using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CatastroFixer.Models;
using MySqlConnector;

namespace CatastroFixer.Services
{
    public class DatabaseSchemaManager
    {
        public const string ResultsTableName = "catastro_fixer_resultados";

        private class ColumnDef
        {
            public string Name { get; set; }
            public string Definition { get; set; }
            public ColumnDef(string name, string definition)
            {
                Name = name;
                Definition = definition;
            }
        }

        private class IndexDef
        {
            public string Name { get; set; }
            public string SqlClause { get; set; } // ej. "UNIQUE KEY `uq_...` (`col`)" o "KEY `idx_...` (`col`)"
            public IndexDef(string name, string sqlClause)
            {
                Name = name;
                SqlClause = sqlClause;
            }
        }

        private class TableDef
        {
            public string Name { get; set; }
            public List<ColumnDef> Columns { get; set; } = new List<ColumnDef>();
            public List<IndexDef> Indexes { get; set; } = new List<IndexDef>();
        }

        /// <summary>
        /// Registro declarativo de todas las tablas gestionadas por CatastroFixer.
        /// Cualquier tabla o columna nueva agregada aquí se creará o actualizará automáticamente
        /// con el botón "Crear / Actualizar Tablas BD" en Configuración.
        /// </summary>
        private static List<TableDef> GetManagedTables()
        {
            return new List<TableDef>
            {
                new TableDef
                {
                    Name = ResultsTableName,
                    Columns = new List<ColumnDef>
                    {
                        new ColumnDef("id", "BIGINT UNSIGNED NOT NULL AUTO_INCREMENT PRIMARY KEY"),
                        new ColumnDef("ubicacion_original_hash", "CHAR(64) NOT NULL"),
                        new ColumnDef("nombre_archivo_original", "VARCHAR(255) NOT NULL"),
                        new ColumnDef("ubicacion_original", "VARCHAR(1000) NOT NULL"),
                        new ColumnDef("carpeta_municipio_original", "VARCHAR(50) NULL"),
                        new ColumnDef("nombre_propuesto", "VARCHAR(255) NULL"),
                        new ColumnDef("destino_propuesto", "VARCHAR(1000) NULL"),
                        new ColumnDef("municipio_id", "BIGINT UNSIGNED NULL"),
                        new ColumnDef("municipio_clave", "VARCHAR(10) NULL"),
                        new ColumnDef("municipio_nombre", "VARCHAR(255) NULL"),
                        new ColumnDef("localidad_id", "BIGINT UNSIGNED NULL"),
                        new ColumnDef("localidad_clave", "VARCHAR(10) NULL"),
                        new ColumnDef("localidad_nombre", "VARCHAR(255) NULL"),
                        new ColumnDef("ubicacion_extraida", "TEXT NULL"),
                        new ColumnDef("evidencia", "TEXT NULL"),
                        new ColumnDef("estado", "VARCHAR(60) NOT NULL DEFAULT 'Pendiente'"),
                        new ColumnDef("es_fallback_municipio", "TINYINT(1) NOT NULL DEFAULT 0"),
                        new ColumnDef("es_fallback_localidad", "TINYINT(1) NOT NULL DEFAULT 0"),
                        new ColumnDef("modelo_claude", "VARCHAR(100) NULL"),
                        new ColumnDef("paginas_escaneadas", "INT NOT NULL DEFAULT 0"),
                        new ColumnDef("latencia_ms", "BIGINT NOT NULL DEFAULT 0"),
                        new ColumnDef("created_at", "TIMESTAMP NULL DEFAULT CURRENT_TIMESTAMP"),
                        new ColumnDef("updated_at", "TIMESTAMP NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP")
                    },
                    Indexes = new List<IndexDef>
                    {
                        new IndexDef("uq_cf_ubicacion_hash", "UNIQUE KEY `uq_cf_ubicacion_hash` (`ubicacion_original_hash`)"),
                        new IndexDef("idx_cf_municipio_clave", "KEY `idx_cf_municipio_clave` (`municipio_clave`)"),
                        new IndexDef("idx_cf_localidad_id", "KEY `idx_cf_localidad_id` (`localidad_id`)"),
                        new IndexDef("idx_cf_estado", "KEY `idx_cf_estado` (`estado`)")
                    }
                }
            };
        }

        public static async Task<string> EnsureOrUpdateAllTablesAsync(EnvConfig config, CancellationToken ct = default)
        {
            var report = new StringBuilder();
            var tables = GetManagedTables();

            using (var conn = new MySqlConnection(config.BuildConnectionString()))
            {
                await conn.OpenAsync(ct);

                foreach (var table in tables)
                {
                    bool exists = await TableExistsAsync(conn, config.DbDatabase, table.Name, ct);

                    if (!exists)
                    {
                        var parts = new List<string>();
                        parts.AddRange(table.Columns.Select(c => $"`{c.Name}` {c.Definition}"));
                        parts.AddRange(table.Indexes.Select(i => i.SqlClause));

                        string createSql =
                            $"CREATE TABLE `{table.Name}` (\n  " +
                            string.Join(",\n  ", parts) +
                            "\n) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;";

                        using (var cmd = new MySqlCommand(createSql, conn))
                        {
                            await cmd.ExecuteNonQueryAsync(ct);
                        }

                        report.AppendLine($"• `{table.Name}`: CREADA ({table.Columns.Count} columnas, {table.Indexes.Count} índices).");
                    }
                    else
                    {
                        var existingCols = await GetExistingColumnsAsync(conn, config.DbDatabase, table.Name, ct);
                        var existingIndexes = await GetExistingIndexesAsync(conn, config.DbDatabase, table.Name, ct);

                        int colsAdded = 0;
                        int idxAdded = 0;
                        string prevColName = null;

                        foreach (var col in table.Columns)
                        {
                            if (!existingCols.Contains(col.Name))
                            {
                                string defWithoutPk = col.Definition;
                                string afterClause = !string.IsNullOrEmpty(prevColName) && existingCols.Contains(prevColName)
                                    ? $" AFTER `{prevColName}`"
                                    : "";

                                string alterSql = $"ALTER TABLE `{table.Name}` ADD COLUMN `{col.Name}` {defWithoutPk}{afterClause};";
                                using (var cmd = new MySqlCommand(alterSql, conn))
                                {
                                    await cmd.ExecuteNonQueryAsync(ct);
                                }
                                existingCols.Add(col.Name);
                                colsAdded++;
                            }
                            prevColName = col.Name;
                        }

                        foreach (var idx in table.Indexes)
                        {
                            if (!existingIndexes.Contains(idx.Name))
                            {
                                string alterIdxSql = $"ALTER TABLE `{table.Name}` ADD {idx.SqlClause};";
                                using (var cmd = new MySqlCommand(alterIdxSql, conn))
                                {
                                    await cmd.ExecuteNonQueryAsync(ct);
                                }
                                existingIndexes.Add(idx.Name);
                                idxAdded++;
                            }
                        }

                        if (colsAdded == 0 && idxAdded == 0)
                        {
                            report.AppendLine($"• `{table.Name}`: Ya existía y está completamente al día.");
                        }
                        else
                        {
                            report.AppendLine($"• `{table.Name}`: ACTUALIZADA (+{colsAdded} columna(s), +{idxAdded} índice(s)).");
                        }
                    }
                }
            }

            return report.ToString().Trim();
        }

        public static async Task UpsertResultAsync(EnvConfig config, DetectionResult r, CancellationToken ct = default)
        {
            if (r == null || string.IsNullOrWhiteSpace(r.FilePath)) return;

            string normPath = r.FilePath.Trim().Normalize(NormalizationForm.FormC);
            string hash = ComputeSha256Hex(normPath.ToLowerInvariant());

            string sql = $@"
INSERT INTO `{ResultsTableName}` (
    ubicacion_original_hash,
    nombre_archivo_original,
    ubicacion_original,
    carpeta_municipio_original,
    nombre_propuesto,
    destino_propuesto,
    municipio_id,
    municipio_clave,
    municipio_nombre,
    localidad_id,
    localidad_clave,
    localidad_nombre,
    ubicacion_extraida,
    evidencia,
    estado,
    es_fallback_municipio,
    es_fallback_localidad,
    modelo_claude,
    paginas_escaneadas,
    latencia_ms
) VALUES (
    @hash,
    @nombre_orig,
    @ubic_orig,
    @carpeta_muni_orig,
    @nombre_prop,
    @destino_prop,
    @muni_id,
    @muni_clave,
    @muni_nombre,
    @loc_id,
    @loc_clave,
    @loc_nombre,
    @ubic_extraida,
    @evidencia,
    @estado,
    @fb_muni,
    @fb_loc,
    @modelo,
    @paginas,
    @latencia
)
ON DUPLICATE KEY UPDATE
    nombre_archivo_original = VALUES(nombre_archivo_original),
    ubicacion_original = VALUES(ubicacion_original),
    carpeta_municipio_original = VALUES(carpeta_municipio_original),
    nombre_propuesto = VALUES(nombre_propuesto),
    destino_propuesto = VALUES(destino_propuesto),
    municipio_id = VALUES(municipio_id),
    municipio_clave = VALUES(municipio_clave),
    municipio_nombre = VALUES(municipio_nombre),
    localidad_id = VALUES(localidad_id),
    localidad_clave = VALUES(localidad_clave),
    localidad_nombre = VALUES(localidad_nombre),
    ubicacion_extraida = VALUES(ubicacion_extraida),
    evidencia = VALUES(evidencia),
    estado = VALUES(estado),
    es_fallback_municipio = VALUES(es_fallback_municipio),
    es_fallback_localidad = VALUES(es_fallback_localidad),
    modelo_claude = VALUES(modelo_claude),
    paginas_escaneadas = VALUES(paginas_escaneadas),
    latencia_ms = VALUES(latencia_ms),
    updated_at = CURRENT_TIMESTAMP;";

            using (var conn = new MySqlConnection(config.BuildConnectionString()))
            {
                await conn.OpenAsync(ct);
                using (var cmd = new MySqlCommand(sql, conn))
                {
                    cmd.Parameters.AddWithValue("@hash", hash);
                    cmd.Parameters.AddWithValue("@nombre_orig", (r.FileName ?? "").Normalize(NormalizationForm.FormC));
                    cmd.Parameters.AddWithValue("@ubic_orig", normPath);
                    cmd.Parameters.AddWithValue("@carpeta_muni_orig", (object)r.CarpetaMunicipioOriginal ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@nombre_prop", (object)(r.NombrePropuesto?.Normalize(NormalizationForm.FormC)) ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@destino_prop", (object)(r.DestinoPropuesto?.Normalize(NormalizationForm.FormC)) ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@muni_id", (object)r.MunicipioId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@muni_clave", (object)r.MunicipioClave ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@muni_nombre", (object)r.MunicipioNombre ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@loc_id", (object)r.LocalidadId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@loc_clave", (object)r.LocalidadClave ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@loc_nombre", (object)r.LocalidadNombre ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@ubic_extraida", (object)r.UbicacionExtraida ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@evidencia", (object)r.Evidencia ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@estado", r.Estado ?? "Pendiente");
                    cmd.Parameters.AddWithValue("@fb_muni", r.IsMunicipioFallback ? 1 : 0);
                    cmd.Parameters.AddWithValue("@fb_loc", r.IsLocalidadFallback ? 1 : 0);
                    cmd.Parameters.AddWithValue("@modelo", (object)r.ModeloClaude ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@paginas", r.PagesScanned);
                    cmd.Parameters.AddWithValue("@latencia", r.ElapsedMs);

                    await cmd.ExecuteNonQueryAsync(ct);
                }
            }
        }

        private static async Task<bool> TableExistsAsync(MySqlConnection conn, string dbName, string tableName, CancellationToken ct)
        {
            const string sql = "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = @db AND TABLE_NAME = @tbl;";
            using (var cmd = new MySqlCommand(sql, conn))
            {
                cmd.Parameters.AddWithValue("@db", dbName);
                cmd.Parameters.AddWithValue("@tbl", tableName);
                var count = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
                return count > 0;
            }
        }

        private static async Task<HashSet<string>> GetExistingColumnsAsync(MySqlConnection conn, string dbName, string tableName, CancellationToken ct)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            const string sql = "SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = @db AND TABLE_NAME = @tbl;";
            using (var cmd = new MySqlCommand(sql, conn))
            {
                cmd.Parameters.AddWithValue("@db", dbName);
                cmd.Parameters.AddWithValue("@tbl", tableName);
                using (var reader = await cmd.ExecuteReaderAsync(ct))
                {
                    while (await reader.ReadAsync(ct))
                    {
                        set.Add(reader.GetString(0));
                    }
                }
            }
            return set;
        }

        private static async Task<HashSet<string>> GetExistingIndexesAsync(MySqlConnection conn, string dbName, string tableName, CancellationToken ct)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            const string sql = "SELECT DISTINCT INDEX_NAME FROM INFORMATION_SCHEMA.STATISTICS WHERE TABLE_SCHEMA = @db AND TABLE_NAME = @tbl;";
            using (var cmd = new MySqlCommand(sql, conn))
            {
                cmd.Parameters.AddWithValue("@db", dbName);
                cmd.Parameters.AddWithValue("@tbl", tableName);
                using (var reader = await cmd.ExecuteReaderAsync(ct))
                {
                    while (await reader.ReadAsync(ct))
                    {
                        set.Add(reader.GetString(0));
                    }
                }
            }
            return set;
        }

        private static string ComputeSha256Hex(string input)
        {
            using (var sha = SHA256.Create())
            {
                byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(input));
                var sb = new StringBuilder(64);
                foreach (byte b in bytes) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }
    }
}
