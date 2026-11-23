using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CatastroFixer.Models;
using MySqlConnector;

namespace CatastroFixer.Services
{
    public class CatalogRepository
    {
        public List<Municipio> Municipios { get; private set; } = new List<Municipio>();
        public List<Localidad> Localidades { get; private set; } = new List<Localidad>();

        private Dictionary<long, Municipio> _municipiosById = new Dictionary<long, Municipio>();
        private Dictionary<long, Localidad> _localidadesById = new Dictionary<long, Localidad>();
        private Dictionary<long, List<Localidad>> _localidadesByMunicipio = new Dictionary<long, List<Localidad>>();

        private string _municipiosCompactCache;
        private readonly ConcurrentDictionary<long, string> _localidadesCompactCache = new ConcurrentDictionary<long, string>();

        public bool IsLoaded => Municipios.Count > 0 && Localidades.Count > 0;

        public async Task TestConnectionAsync(EnvConfig config, CancellationToken ct = default)
        {
            using (var conn = new MySqlConnection(config.BuildConnectionString()))
            {
                await conn.OpenAsync(ct);
                using (var cmd = new MySqlCommand("SELECT COUNT(*) FROM stellmunicipios", conn))
                {
                    await cmd.ExecuteScalarAsync(ct);
                }
            }
        }

        public async Task LoadCatalogsAsync(EnvConfig config, Action<string> logger = null, CancellationToken ct = default)
        {
            var munis = new List<Municipio>();
            var locs = new List<Localidad>();

            using (var conn = new MySqlConnection(config.BuildConnectionString()))
            {
                await conn.OpenAsync(ct);

                logger?.Invoke("[MySQL] Cargando stellmunicipios...");
                using (var cmd = new MySqlCommand("SELECT id, estado, nombre, clave FROM stellmunicipios ORDER BY clave, id", conn))
                using (var reader = await cmd.ExecuteReaderAsync(ct))
                {
                    while (await reader.ReadAsync(ct))
                    {
                        munis.Add(new Municipio
                        {
                            Id = reader.GetInt64(0),
                            Estado = reader.GetInt64(1),
                            Nombre = reader.IsDBNull(2) ? "" : reader.GetString(2).Trim(),
                            Clave = reader.IsDBNull(3) ? "" : reader.GetString(3).Trim()
                        });
                    }
                }

                logger?.Invoke("[MySQL] Cargando stelllocalidades...");
                using (var cmd = new MySqlCommand("SELECT id, municipio, nombre, clave FROM stelllocalidades ORDER BY municipio, clave, id", conn))
                using (var reader = await cmd.ExecuteReaderAsync(ct))
                {
                    while (await reader.ReadAsync(ct))
                    {
                        locs.Add(new Localidad
                        {
                            Id = reader.GetInt64(0),
                            MunicipioId = reader.GetInt64(1),
                            Nombre = reader.IsDBNull(2) ? "" : reader.GetString(2).Trim(),
                            Clave = reader.IsDBNull(3) ? "" : reader.GetString(3).Trim()
                        });
                    }
                }
            }

            Municipios = munis;
            Localidades = locs;
            _municipiosById = munis.ToDictionary(m => m.Id, m => m);
            _localidadesById = locs.ToDictionary(l => l.Id, l => l);
            _localidadesByMunicipio = locs
                .GroupBy(l => l.MunicipioId)
                .ToDictionary(g => g.Key, g => g.OrderBy(l => l.ClaveFormateada).ThenBy(l => l.Id).ToList());

            _municipiosCompactCache = BuildMunicipiosCompact();
            _localidadesCompactCache.Clear();

            logger?.Invoke($"[MySQL] Catálogos cargados: {Municipios.Count} municipios, {Localidades.Count} localidades.");
        }

        private string BuildMunicipiosCompact()
        {
            var sb = new StringBuilder(Municipios.Count * 28);
            foreach (var m in Municipios)
            {
                sb.Append(m.Id).Append('|').Append(m.ClaveFormateada).Append('|').Append(m.Nombre).Append('\n');
            }
            return sb.ToString();
        }

        public string GetMunicipiosCompactList()
        {
            return _municipiosCompactCache ?? "";
        }

        public string GetLocalidadesCompactList(long municipioId)
        {
            return _localidadesCompactCache.GetOrAdd(municipioId, id =>
            {
                if (!_localidadesByMunicipio.TryGetValue(id, out var list) || list.Count == 0)
                    return "";

                var sb = new StringBuilder(list.Count * 30);
                foreach (var l in list)
                {
                    sb.Append(l.Id).Append('|').Append(l.ClaveFormateada).Append('|').Append(l.Nombre).Append('\n');
                }
                return sb.ToString();
            });
        }

        public int GetLocalidadesCount(long municipioId)
        {
            return _localidadesByMunicipio.TryGetValue(municipioId, out var list) ? list.Count : 0;
        }

        public Municipio GetMunicipioById(long id)
        {
            return _municipiosById.TryGetValue(id, out var m) ? m : null;
        }

        public Localidad GetLocalidadByIdForMunicipio(long localidadId, long municipioId)
        {
            if (_localidadesById.TryGetValue(localidadId, out var loc) && loc.MunicipioId == municipioId)
                return loc;
            return null;
        }

        public Localidad GetCabeceraMunicipal(long municipioId)
        {
            if (!_localidadesByMunicipio.TryGetValue(municipioId, out var list) || list.Count == 0)
                return null;

            return list.FirstOrDefault(l => l.ClaveFormateada == "0001")
                   ?? list.FirstOrDefault(l => l.Clave == "1" || l.Clave == "0001")
                   ?? list.First();
        }

        public Municipio GetDefaultFallbackMunicipio()
        {
            return Municipios.FirstOrDefault(m => m.ClaveFormateada == "101")
                   ?? Municipios.FirstOrDefault(m => m.Nombre != null && m.Nombre.IndexOf("TUXTLA GUTI", StringComparison.OrdinalIgnoreCase) >= 0)
                   ?? Municipios.FirstOrDefault(m => m.Id == 100);
        }

        public Municipio ResolveMunicipio(long? suggestedId, string rawText)
        {
            if (suggestedId.HasValue && _municipiosById.TryGetValue(suggestedId.Value, out var direct))
            {
                return direct;
            }

            if (string.IsNullOrWhiteSpace(rawText) || Municipios.Count == 0)
            {
                return null;
            }

            string trimmed = rawText.Trim();
            // Coincidencia directa por clave de 3 dígitos si vino numérica
            if (trimmed.Length <= 3 && int.TryParse(trimmed, out int numClave))
            {
                string padded = numClave.ToString().PadLeft(3, '0');
                var byClave = Municipios.FirstOrDefault(m => m.ClaveFormateada == padded);
                if (byClave != null) return byClave;
            }

            Municipio best = null;
            double bestScore = 0.0;
            foreach (var m in Municipios)
            {
                double score = StringSimilarityHelper.CalculateSimilarity(m.Nombre, rawText);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = m;
                }
            }

            return bestScore >= 0.55 ? best : null;
        }
    }
}
