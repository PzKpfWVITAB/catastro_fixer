using System;

namespace CatastroFixer.Models
{
    public class Municipio
    {
        public long Id { get; set; }
        public long Estado { get; set; }
        public string Nombre { get; set; }
        public string Clave { get; set; }

        public string ClaveFormateada => (Clave ?? "").Trim().PadLeft(3, '0');
    }

    public class Localidad
    {
        public long Id { get; set; }
        public long MunicipioId { get; set; }
        public string Nombre { get; set; }
        public string Clave { get; set; }

        public string ClaveFormateada => (Clave ?? "").Trim().PadLeft(4, '0');
    }

    public class DetectionResult
    {
        public string FilePath { get; set; }
        public string FileName { get; set; }
        public int PagesScanned { get; set; }
        public int TotalPages { get; set; }

        public long? MunicipioId { get; set; }
        public string MunicipioNombre { get; set; }
        public string MunicipioClave { get; set; }

        public long? LocalidadId { get; set; }
        public string LocalidadNombre { get; set; }
        public string LocalidadClave { get; set; }
        public bool IsLocalidadFallback { get; set; }

        public string UbicacionExtraida { get; set; }
        public string Evidencia { get; set; }
        public string Estado { get; set; }
        public bool IsClaudeApiError { get; set; }
        public string ErrorDetail { get; set; }
        public long ElapsedMs { get; set; }
    }
}
