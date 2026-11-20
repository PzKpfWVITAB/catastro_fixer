using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.ExceptionServices;
using System.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using CatastroFixer.Models;
using PdfiumViewer;

namespace CatastroFixer.Services
{
    public class ClaudeApiException : Exception
    {
        public ClaudeApiException(string message) : base(message) { }
    }

    public class ClaudeGeoProcessor
    {
        private static readonly object _pdfiumLock = new object();
        private static readonly HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        private static readonly Random _jitterRng = new Random();
        private static readonly object _rngLock = new object();

        private readonly CatalogRepository _catalogs;
        private readonly EnvConfig _config;
        private readonly Action<string> _logger;
        private readonly Action<long> _onCacheHit;

        public ClaudeGeoProcessor(
            CatalogRepository catalogs,
            EnvConfig config,
            Action<string> logger = null,
            Action<long> onCacheHit = null)
        {
            _catalogs = catalogs;
            _config = config;
            _logger = logger ?? (_ => { });
            _onCacheHit = onCacheHit;
        }

        private class PageExtraction
        {
            public bool EsPaginaEnBlanco { get; set; }
            public long? MunicipioId { get; set; }
            public string MunicipioTexto { get; set; }
            public List<string> Ubicaciones { get; set; } = new List<string>();
            public string Evidencia { get; set; }
        }

        public async Task<DetectionResult> ProcessPdfAsync(string pdfPath, CancellationToken ct = default)
        {
            var sw = Stopwatch.StartNew();
            string fileName = Path.GetFileName(pdfPath);
            var result = new DetectionResult
            {
                FilePath = pdfPath,
                FileName = fileName,
                Estado = "Sin Municipio"
            };

            string fileToOpen = pdfPath;
            string tempLocalFile = null;

            try
            {
                if (IsNetworkPath(pdfPath))
                {
                    try
                    {
                        tempLocalFile = Path.Combine(Path.GetTempPath(), $"CatastroFixer_{Guid.NewGuid():N}_{fileName}");
                        File.Copy(pdfPath, tempLocalFile, true);
                        fileToOpen = tempLocalFile;
                    }
                    catch (Exception ex)
                    {
                        _logger($"[Red] No se pudo copiar '{fileName}' a %TEMP%: {ex.Message}. Leyendo directo de red.");
                        fileToOpen = pdfPath;
                        tempLocalFile = null;
                    }
                }

                var pageExtractions = new List<PageExtraction>();
                var muniVotes = new Dictionary<long, int>();
                var allUbicaciones = new List<string>();
                var evidencias = new List<string>();

                using (var doc = SafeLoadPdf(fileToOpen, out int totalPages))
                {
                    if (doc == null || totalPages == 0)
                    {
                        result.Estado = "Error";
                        result.ErrorDetail = "No se pudo abrir el PDF o no tiene páginas renderizables.";
                        result.Evidencia = result.ErrorDetail;
                        return result;
                    }

                    result.TotalPages = totalPages;
                    int maxPages = Math.Min(totalPages, Math.Max(1, _config.MaxPages));

                    for (int pageNum = 1; pageNum <= maxPages; pageNum++)
                    {
                        ct.ThrowIfCancellationRequested();
                        result.PagesScanned = pageNum;

                        byte[] jpegBytes = SafeRenderPageToJpegBytes(doc, pageNum);
                        if (jpegBytes == null)
                        {
                            _logger($"  [Pág {pageNum}] No se pudo renderizar '{fileName}'.");
                            continue;
                        }

                        var extraction = await ExtractPageGeoAsync(jpegBytes, pageNum, fileName, ct);
                        if (extraction == null) continue;

                        if (extraction.EsPaginaEnBlanco)
                        {
                            _logger($"  [Pág {pageNum}] Página en blanco / reverso con traspaso de tinta omitida. [{fileName}]");
                            continue;
                        }

                        pageExtractions.Add(extraction);

                        var resolvedMuni = _catalogs.ResolveMunicipio(extraction.MunicipioId, extraction.MunicipioTexto);
                        if (resolvedMuni != null)
                        {
                            if (!muniVotes.ContainsKey(resolvedMuni.Id)) muniVotes[resolvedMuni.Id] = 0;
                            muniVotes[resolvedMuni.Id]++;
                        }

                        foreach (var u in extraction.Ubicaciones)
                        {
                            if (!string.IsNullOrWhiteSpace(u) &&
                                !allUbicaciones.Any(x => string.Equals(x, u, StringComparison.OrdinalIgnoreCase)))
                            {
                                allUbicaciones.Add(u);
                            }
                        }

                        if (!string.IsNullOrWhiteSpace(extraction.Evidencia))
                        {
                            evidencias.Add($"P{pageNum}: {extraction.Evidencia}");
                        }

                        // Salida anticipada: si ya tenemos municipio válido + al menos una pista de ubicación clara
                        if (muniVotes.Count > 0 && allUbicaciones.Count > 0)
                        {
                            _logger($"  [Salida Anticipada] Municipio y ubicación detectados en pág {pageNum}/{maxPages}. [{fileName}]");
                            break;
                        }
                    }
                }

                // Consolidar municipio ganador por votación (sin sesgo hacia ningún municipio)
                Municipio winningMuni = null;
                if (muniVotes.Count > 0)
                {
                    long winId = muniVotes.OrderByDescending(kv => kv.Value).First().Key;
                    winningMuni = _catalogs.GetMunicipioById(winId);
                }

                result.UbicacionExtraida = allUbicaciones.Count > 0 ? string.Join(" | ", allUbicaciones) : "";

                if (winningMuni == null)
                {
                    result.Estado = "Sin Municipio";
                    result.Evidencia = evidencias.Count > 0
                        ? string.Join(" ; ", evidencias)
                        : $"No se detectó municipio en las primeras {result.PagesScanned} páginas.";
                    return result;
                }

                result.MunicipioId = winningMuni.Id;
                result.MunicipioNombre = winningMuni.Nombre;
                result.MunicipioClave = winningMuni.ClaveFormateada;

                // Etapa 2: Triangulación de Localidad enviando el catálogo completo del municipio a Claude (Prompt Cache)
                Localidad matchedLoc = null;
                string locMotivo = null;

                if (allUbicaciones.Count > 0 && _catalogs.GetLocalidadesCount(winningMuni.Id) > 0)
                {
                    var locMatch = await MatchLocalidadWithClaudeAsync(winningMuni, allUbicaciones, fileName, ct);
                    if (locMatch.Localidad != null)
                    {
                        matchedLoc = locMatch.Localidad;
                        locMotivo = locMatch.Motivo;
                    }
                    else if (!string.IsNullOrWhiteSpace(locMatch.Motivo))
                    {
                        locMotivo = locMatch.Motivo;
                    }
                }

                if (matchedLoc != null)
                {
                    result.LocalidadId = matchedLoc.Id;
                    result.LocalidadNombre = matchedLoc.Nombre;
                    result.LocalidadClave = matchedLoc.ClaveFormateada;
                    result.IsLocalidadFallback = false;
                    result.Estado = "Detectado";
                }
                else
                {
                    // Fallback obligatorio a cabecera municipal (clave 0001) marcado como (Fallback)
                    var cabecera = _catalogs.GetCabeceraMunicipal(winningMuni.Id);
                    if (cabecera != null)
                    {
                        result.LocalidadId = cabecera.Id;
                        result.LocalidadNombre = cabecera.Nombre + " (Fallback)";
                        result.LocalidadClave = cabecera.ClaveFormateada;
                    }
                    else
                    {
                        result.LocalidadNombre = "Cabecera 0001 (Fallback)";
                        result.LocalidadClave = "0001";
                    }
                    result.IsLocalidadFallback = true;
                    result.Estado = "Detectado (Fallback)";
                }

                if (!string.IsNullOrWhiteSpace(locMotivo))
                {
                    evidencias.Add($"Triangulación: {locMotivo}");
                }
                result.Evidencia = string.Join(" ; ", evidencias);

                return result;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (ClaudeApiException ex)
            {
                result.IsClaudeApiError = true;
                result.Estado = "Error API Claude";
                result.ErrorDetail = ex.Message;
                result.Evidencia = ex.Message;
                return result;
            }
            catch (Exception ex)
            {
                result.Estado = "Error";
                result.ErrorDetail = ex.Message;
                result.Evidencia = ex.Message;
                return result;
            }
            finally
            {
                sw.Stop();
                result.ElapsedMs = sw.ElapsedMilliseconds;
                if (!string.IsNullOrEmpty(tempLocalFile))
                {
                    try { if (File.Exists(tempLocalFile)) File.Delete(tempLocalFile); } catch { }
                }
            }
        }

        private async Task<PageExtraction> ExtractPageGeoAsync(byte[] jpegBytes, int pageNum, string fileName, CancellationToken ct)
        {
            string systemPrompt = @"Eres un especialista en digitalización y lectura visual de documentos catastrales, solicitudes de inspección, recibos de predial, planos y escrituras del Estado de Chiapas, México.

=== REGLAS DE LECTURA ===
1. ORIENTACIÓN OMNIDIRECCIONAL: La página puede estar vertical, horizontal o rotada (90°, 180°, 270°). Lee el texto en su sentido correcto.
2. REVERSOS EN BLANCO / TINTA EN ESPEJO: Si la página está en blanco o solo contiene tinta traspasada en espejo del reverso (bleed-through), responde ""es_pagina_en_blanco"": true y deja los demás campos vacíos/null.
3. MUNICIPIO: Identifica el municipio oficial de Chiapas buscando en encabezados (ej. 'H. AYUNTAMIENTO MUNICIPAL DE...', 'MUNICIPIO DE...'), logotipos, sellos, dominios web (ej. 'www.tuxtla.gob.mx'), casillas o texto legal. Tolera errores tipográficos del documento (ej. 'TUXTLA GUTIERREZZ' -> TUXTLA GUTIÉRREZ, 'TGZ' -> TUXTLA GUTIÉRREZ, 'SCLC' -> SAN CRISTÓBAL DE LAS CASAS). Selecciona el 'municipio_id' exacto del catálogo adjunto. Si no hay evidencia de municipio, devuelve null.
4. UBICACIONES / LOCALIDADES (INCLUYENDO MANUSCRITOS): Extrae TODAS las menciones de ubicación geográfica del predio: localidad, ejido, ranchería, colonia, barrio, paraje, finca, fraccionamiento, cantón, congregación, poblado o campo 'UBICACION' / 'DOMICILIO' / 'LOCALIDAD' / 'LUGAR'.
   - Lee con máximo cuidado la letra manuscrita (ej. 'Cruz. El Mojón', 'Ej. Copoya', 'Rch. San Juan').
   - Transcribe cada ubicación tal como aparece en el documento dentro del arreglo 'ubicaciones'. Si una palabra manuscrita es ligeramente ambigua, incluye tu lectura más fiel.
   - NO incluyas nombres de personas ni números de teléfono ni fechas en 'ubicaciones'.

=== CATÁLOGO OFICIAL DE MUNICIPIOS DE CHIAPAS (id|clave|nombre) ===
" + _catalogs.GetMunicipiosCompactList() + @"
=== FORMATO DE SALIDA ESTRICTO ===
Responde ÚNICAMENTE con un objeto JSON válido comenzando con '{' y terminando con '}', sin texto adicional ni markdown:
{
  ""es_pagina_en_blanco"": false,
  ""municipio_id"": 100,
  ""municipio_texto"": ""..."",
  ""ubicaciones"": [""...""],
  ""evidencia"": ""breve fuente visual (máx 15 palabras)""
}";

            var payload = new Dictionary<string, object>
            {
                { "model", _config.ClaudeModel },
                { "max_tokens", 600 },
                { "system", new object[] {
                    new Dictionary<string, object> {
                        { "type", "text" },
                        { "text", systemPrompt },
                        { "cache_control", new Dictionary<string, object> { { "type", "ephemeral" } } }
                    }
                }},
                { "messages", new object[] {
                    new Dictionary<string, object> {
                        { "role", "user" },
                        { "content", new object[] {
                            new Dictionary<string, object> {
                                { "type", "image" },
                                { "source", new Dictionary<string, object> {
                                    { "type", "base64" },
                                    { "media_type", "image/jpeg" },
                                    { "data", Convert.ToBase64String(jpegBytes) }
                                }}
                            },
                            new Dictionary<string, object> {
                                { "type", "text" },
                                { "text", "Extrae el municipio y todas las ubicaciones/localidades visibles en esta página en JSON estricto." }
                            }
                        }}
                    }
                }}
            };

            string rawText = await SendClaudeRequestAsync(payload, $"Etapa1-P{pageNum} [{fileName}]", ct);
            var parsed = ParseJsonObject(rawText);
            if (parsed == null) return null;

            var ext = new PageExtraction
            {
                EsPaginaEnBlanco = GetBool(parsed, "es_pagina_en_blanco"),
                MunicipioId = GetNullableLong(parsed, "municipio_id"),
                MunicipioTexto = GetString(parsed, "municipio_texto"),
                Evidencia = GetString(parsed, "evidencia")
            };

            if (parsed.ContainsKey("ubicaciones") && parsed["ubicaciones"] is IEnumerable ubList)
            {
                foreach (var item in ubList)
                {
                    string s = item?.ToString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(s) && !s.Equals("null", StringComparison.OrdinalIgnoreCase))
                    {
                        ext.Ubicaciones.Add(s);
                    }
                }
            }

            return ext;
        }

        private async Task<(Localidad Localidad, string Motivo)> MatchLocalidadWithClaudeAsync(
            Municipio municipio,
            List<string> ubicaciones,
            string fileName,
            CancellationToken ct)
        {
            string locsCatalog = _catalogs.GetLocalidadesCompactList(municipio.Id);
            if (string.IsNullOrWhiteSpace(locsCatalog))
            {
                return (null, "Municipio sin localidades en catálogo.");
            }

            string systemPrompt = $@"Eres un experto en geografía catastral de Chiapas, México.
Tu única tarea es elegir el ID exacto de la localidad oficial que corresponde a las pistas de ubicación extraídas de un documento del municipio **{municipio.Nombre}** (clave {municipio.ClaveFormateada}, id {municipio.Id}).

=== REGLAS DE EMPAREJAMIENTO ===
1. Compara las pistas de ubicación contra el catálogo oficial adjunto (formato: id|clave|nombre).
2. Considera variaciones comunes de documentos y manuscritos:
   - Artículos o preposiciones omitidos/agregados (ej. 'Cruz. El Mojón' o 'El Mojón' corresponde a 'LA CRUZ DEL MOJÓN').
   - Prefijos abreviados o intercambiados ('Col.', 'Ej.', 'Ejido', 'Rch.', 'Ranchería', 'Barrio', 'Paraje', 'Finca', 'Fracc.', 'Cantón', 'San'/'Sn', 'Santa'/'Sta').
   - Errores leves de OCR o letra manuscrita (ej. 'Ciud. El Mojón' -> 'LA CRUZ DEL MOJÓN' si es el único/claro match con 'MOJÓN').
3. Si las pistas corresponden claramente a una localidad del catálogo, devuelve su 'localidad_id' exacto.
4. Si las pistas son únicamente una calle urbana dentro de la cabecera municipal (sin coincidir con una localidad específica distinta) o NO existe ninguna coincidencia razonable en el catálogo, devuelve ""localidad_id"": null (NO inventes un ID que no corresponda).

=== CATÁLOGO COMPLETO DE LOCALIDADES DE {municipio.Nombre} (id|clave|nombre) ===
{locsCatalog}
=== FORMATO DE SALIDA ESTRICTO ===
Responde ÚNICAMENTE con un objeto JSON válido comenzando con '{{' y terminando con '}}':
{{
  ""localidad_id"": 21874,
  ""confianza"": ""alta|media|baja"",
  ""motivo"": ""explicación breve en máx 15 palabras""
}}";

            string userPrompt = "Pistas de ubicación extraídas del documento:\n- " + string.Join("\n- ", ubicaciones);

            var payload = new Dictionary<string, object>
            {
                { "model", _config.ClaudeModel },
                { "max_tokens", 300 },
                { "system", new object[] {
                    new Dictionary<string, object> {
                        { "type", "text" },
                        { "text", systemPrompt },
                        { "cache_control", new Dictionary<string, object> { { "type", "ephemeral" } } }
                    }
                }},
                { "messages", new object[] {
                    new Dictionary<string, object> {
                        { "role", "user" },
                        { "content", userPrompt }
                    }
                }}
            };

            string rawText = await SendClaudeRequestAsync(payload, $"Etapa2-Loc [{fileName}]", ct);
            var parsed = ParseJsonObject(rawText);
            if (parsed == null) return (null, "No se pudo interpretar respuesta de triangulación.");

            long? locId = GetNullableLong(parsed, "localidad_id");
            string motivo = GetString(parsed, "motivo");

            if (locId.HasValue)
            {
                var loc = _catalogs.GetLocalidadByIdForMunicipio(locId.Value, municipio.Id);
                if (loc != null)
                {
                    return (loc, motivo);
                }
                return (null, $"ID {locId.Value} devuelto por IA no pertenece a {municipio.Nombre}.");
            }

            return (null, motivo);
        }

        private async Task<string> SendClaudeRequestAsync(Dictionary<string, object> payload, string tag, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(_config.ClaudeApiKey))
            {
                throw new ClaudeApiException("La CLAUDE_API_KEY no está configurada en .env.");
            }

            const int maxIntentos = 2;
            var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            string jsonBody = serializer.Serialize(payload);

            for (int intento = 1; intento <= maxIntentos; intento++)
            {
                ct.ThrowIfCancellationRequested();
                var sw = Stopwatch.StartNew();

                try
                {
                    using (var request = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages"))
                    {
                        request.Headers.Add("x-api-key", _config.ClaudeApiKey);
                        request.Headers.Add("anthropic-version", "2023-06-01");
                        request.Headers.Add("anthropic-beta", "prompt-caching-2024-07-31");
                        request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

                        using (var response = await _httpClient.SendAsync(request, ct))
                        {
                            sw.Stop();
                            string respBody = await response.Content.ReadAsStringAsync();

                            if (response.IsSuccessStatusCode)
                            {
                                var root = serializer.Deserialize<Dictionary<string, object>>(respBody);
                                LogCacheUsage(root, tag, sw.ElapsedMilliseconds);

                                if (root != null && root.ContainsKey("content") && root["content"] is IEnumerable contentList)
                                {
                                    foreach (var item in contentList)
                                    {
                                        if (item is Dictionary<string, object> block &&
                                            block.TryGetValue("type", out var tObj) && tObj?.ToString() == "text" &&
                                            block.TryGetValue("text", out var txtObj))
                                        {
                                            return txtObj?.ToString() ?? "";
                                        }
                                    }
                                }
                                throw new ClaudeApiException($"Respuesta inesperada de Claude en {tag}.");
                            }

                            if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
                            {
                                throw new ClaudeApiException($"API Key inválida o sin autorización (HTTP {(int)response.StatusCode}).");
                            }

                            _logger($"  [{tag}] HTTP {(int)response.StatusCode} (Intento {intento}/{maxIntentos}): {Truncate(respBody, 200)}");

                            if (intento < maxIntentos)
                            {
                                int delayMs = GetJitterDelayMs(response);
                                await Task.Delay(delayMs, ct);
                                continue;
                            }

                            throw new ClaudeApiException($"Error HTTP {(int)response.StatusCode} en Claude API: {Truncate(respBody, 250)}");
                        }
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (ClaudeApiException) { throw; }
                catch (Exception ex)
                {
                    _logger($"  [{tag}] Excepción red/API (Intento {intento}/{maxIntentos}): {ex.Message}");
                    if (intento < maxIntentos)
                    {
                        await Task.Delay(GetJitterDelayMs(null), ct);
                        continue;
                    }
                    throw new ClaudeApiException($"Error de comunicación con Claude API: {ex.Message}");
                }
            }

            throw new ClaudeApiException("No se obtuvo respuesta de Claude API.");
        }

        private void LogCacheUsage(Dictionary<string, object> root, string tag, long elapsedMs)
        {
            if (root == null || !root.TryGetValue("usage", out var uObj) || !(uObj is Dictionary<string, object> usage))
            {
                _logger($"  [{tag}] {elapsedMs} ms");
                return;
            }

            long cacheRead = GetNullableLong(usage, "cache_read_input_tokens") ?? 0;
            long cacheCreate = GetNullableLong(usage, "cache_creation_input_tokens") ?? 0;

            if (cacheRead > 0)
            {
                _onCacheHit?.Invoke(cacheRead);
                _logger($"  [{tag}] {elapsedMs} ms | Cache HIT ({cacheRead} tok)");
            }
            else if (cacheCreate > 0)
            {
                _logger($"  [{tag}] {elapsedMs} ms | Cache WRITE ({cacheCreate} tok)");
            }
            else
            {
                _logger($"  [{tag}] {elapsedMs} ms");
            }
        }

        private static int GetJitterDelayMs(HttpResponseMessage response)
        {
            int jitter;
            lock (_rngLock) { jitter = _jitterRng.Next(150, 600); }
            int delayMs = 1000 + jitter;
            if (response?.Headers?.RetryAfter?.Delta != null)
            {
                delayMs = (int)Math.Max(delayMs, response.Headers.RetryAfter.Delta.Value.TotalMilliseconds);
            }
            return delayMs;
        }

        [HandleProcessCorruptedStateExceptions]
        [SecurityCritical]
        private PdfDocument SafeLoadPdf(string pdfPath, out int pageCount)
        {
            pageCount = 0;
            try
            {
                lock (_pdfiumLock)
                {
                    var doc = PdfDocument.Load(pdfPath);
                    if (doc != null) pageCount = doc.PageCount;
                    return doc;
                }
            }
            catch (Exception ex)
            {
                _logger($"  [PdfiumViewer ERROR] No se pudo abrir '{Path.GetFileName(pdfPath)}': {ex.Message}");
                return null;
            }
        }

        [HandleProcessCorruptedStateExceptions]
        [SecurityCritical]
        private byte[] SafeRenderPageToJpegBytes(PdfDocument doc, int pageNum)
        {
            try
            {
                lock (_pdfiumLock)
                {
                    // Configuración rescatada de CatastroMover3000: 240 DPI, max 1568px, JPEG 95 (SIN rotación ciega)
                    using (var bitmap = (Bitmap)doc.Render(pageNum - 1, 240, 240, PdfRenderFlags.Annotations))
                    {
                        const int maxDimension = 1568;
                        Bitmap targetBitmap = bitmap;
                        bool disposeTarget = false;

                        if (bitmap.Width > maxDimension || bitmap.Height > maxDimension)
                        {
                            float scale = (float)maxDimension / Math.Max(bitmap.Width, bitmap.Height);
                            int newWidth = Math.Max(1, (int)(bitmap.Width * scale));
                            int newHeight = Math.Max(1, (int)(bitmap.Height * scale));

                            var scaled = new Bitmap(newWidth, newHeight, PixelFormat.Format24bppRgb);
                            using (var g = Graphics.FromImage(scaled))
                            {
                                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                                g.SmoothingMode = SmoothingMode.HighQuality;
                                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                                g.DrawImage(bitmap, 0, 0, newWidth, newHeight);
                            }
                            targetBitmap = scaled;
                            disposeTarget = true;
                        }

                        try
                        {
                            using (var ms = new MemoryStream())
                            {
                                var jpegCodec = GetJpegEncoder();
                                if (jpegCodec != null)
                                {
                                    using (var encoderParams = new EncoderParameters(1))
                                    {
                                        encoderParams.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 95L);
                                        targetBitmap.Save(ms, jpegCodec, encoderParams);
                                    }
                                }
                                else
                                {
                                    targetBitmap.Save(ms, ImageFormat.Jpeg);
                                }
                                return ms.ToArray();
                            }
                        }
                        finally
                        {
                            if (disposeTarget && targetBitmap != null) targetBitmap.Dispose();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger($"  [PdfiumViewer ERROR] Fallo al renderizar pág {pageNum}: {ex.Message}");
                return null;
            }
        }

        private static ImageCodecInfo GetJpegEncoder()
        {
            foreach (var codec in ImageCodecInfo.GetImageEncoders())
            {
                if (codec.FormatID == ImageFormat.Jpeg.Guid) return codec;
            }
            return null;
        }

        private static bool IsNetworkPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            return path.StartsWith(@"\\") || path.StartsWith("//");
        }

        private static Dictionary<string, object> ParseJsonObject(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            string s = raw.Trim();
            int start = s.IndexOf('{');
            int end = s.LastIndexOf('}');
            if (start < 0 || end <= start) return null;
            string json = s.Substring(start, end - start + 1);
            try
            {
                var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                return serializer.Deserialize<Dictionary<string, object>>(json);
            }
            catch
            {
                return null;
            }
        }

        private static string GetString(Dictionary<string, object> dict, string key)
        {
            if (dict == null || !dict.TryGetValue(key, out var val) || val == null) return null;
            string s = val.ToString().Trim();
            return s.Equals("null", StringComparison.OrdinalIgnoreCase) ? null : s;
        }

        private static bool GetBool(Dictionary<string, object> dict, string key)
        {
            if (dict == null || !dict.TryGetValue(key, out var val) || val == null) return false;
            if (val is bool b) return b;
            return bool.TryParse(val.ToString(), out bool parsed) && parsed;
        }

        private static long? GetNullableLong(Dictionary<string, object> dict, string key)
        {
            if (dict == null || !dict.TryGetValue(key, out var val) || val == null) return null;
            if (val is long l) return l;
            if (val is int i) return i;
            if (long.TryParse(val.ToString().Trim(), out long parsed)) return parsed;
            return null;
        }

        private static string Truncate(string s, int maxLen)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= maxLen ? s : s.Substring(0, maxLen) + "...";
        }
    }
}
