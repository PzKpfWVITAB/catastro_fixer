using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace CatastroFixer.Services
{
    public class EnvConfig
    {
        public string EnvFilePath { get; private set; }

        public string DbHost { get; set; } = "127.0.0.1";
        public int DbPort { get; set; } = 3306;
        public string DbDatabase { get; set; } = "stellumdb";
        public string DbUsername { get; set; } = "root";
        public string DbPassword { get; set; } = "";

        public string ClaudeApiKey { get; set; } = "";
        public string ClaudeModel { get; set; } = "claude-sonnet-4-6";

        public int MaxConcurrency { get; set; } = 4;
        public int MaxPages { get; set; } = 3;
        public string DefaultPdfDir { get; set; } = @"\\172.40.5.84\ssdirec\REVISANDO CATASTRO\ENTREGA 3\2DA PARTE\CATASTRO SIN CLAVE\001";

        public string BuildConnectionString()
        {
            return $"Server={DbHost};Port={DbPort};Database={DbDatabase};User ID={DbUsername};Password={DbPassword};CharSet=utf8mb4;ConnectionTimeout=15;DefaultCommandTimeout=60;";
        }

        public static string ResolveEnvPath()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string direct = Path.Combine(baseDir, ".env");
            if (File.Exists(direct)) return direct;

            // Subir hasta 4 niveles por si se ejecuta desde bin\Debug\net472\
            var dir = new DirectoryInfo(baseDir);
            for (int i = 0; i < 4 && dir?.Parent != null; i++)
            {
                dir = dir.Parent;
                string candidate = Path.Combine(dir.FullName, ".env");
                if (File.Exists(candidate)) return candidate;
            }

            return direct;
        }

        public static EnvConfig Load()
        {
            var cfg = new EnvConfig();
            cfg.EnvFilePath = ResolveEnvPath();

            if (!File.Exists(cfg.EnvFilePath))
            {
                return cfg;
            }

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rawLine in File.ReadAllLines(cfg.EnvFilePath, Encoding.UTF8))
            {
                string line = rawLine.Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith("#")) continue;

                int idx = line.IndexOf('=');
                if (idx <= 0) continue;

                string key = line.Substring(0, idx).Trim();
                string val = line.Substring(idx + 1).Trim();

                if ((val.StartsWith("\"") && val.EndsWith("\"") && val.Length >= 2) ||
                    (val.StartsWith("'") && val.EndsWith("'") && val.Length >= 2))
                {
                    val = val.Substring(1, val.Length - 2);
                }

                map[key] = val;
            }

            if (map.TryGetValue("DB_HOST", out string dbHost) && !string.IsNullOrWhiteSpace(dbHost)) cfg.DbHost = dbHost;
            if (map.TryGetValue("DB_PORT", out string dbPort) && int.TryParse(dbPort, out int port) && port > 0) cfg.DbPort = port;
            if (map.TryGetValue("DB_DATABASE", out string dbName) && !string.IsNullOrWhiteSpace(dbName)) cfg.DbDatabase = dbName;
            if (map.TryGetValue("DB_USERNAME", out string dbUser) && !string.IsNullOrWhiteSpace(dbUser)) cfg.DbUsername = dbUser;
            if (map.TryGetValue("DB_PASSWORD", out string dbPass)) cfg.DbPassword = dbPass;

            if (map.TryGetValue("CLAUDE_API_KEY", out string apiKey)) cfg.ClaudeApiKey = apiKey.Trim();
            if (map.TryGetValue("CLAUDE_MODEL", out string model) && !string.IsNullOrWhiteSpace(model)) cfg.ClaudeModel = model.Trim();

            if (map.TryGetValue("MAX_CONCURRENCY", out string conc) && int.TryParse(conc, out int cVal) && cVal >= 1 && cVal <= 32)
                cfg.MaxConcurrency = cVal;

            if (map.TryGetValue("MAX_PAGES", out string maxP) && int.TryParse(maxP, out int pVal) && pVal >= 1 && pVal <= 15)
                cfg.MaxPages = pVal;

            if (map.TryGetValue("DEFAULT_PDF_DIR", out string defDir))
                cfg.DefaultPdfDir = defDir.Trim();

            return cfg;
        }

        public void Save()
        {
            if (string.IsNullOrWhiteSpace(EnvFilePath))
            {
                EnvFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".env");
            }

            var lines = new[]
            {
                "# Conexión MySQL local (stellumdb)",
                $"DB_HOST={DbHost}",
                $"DB_PORT={DbPort}",
                $"DB_DATABASE={DbDatabase}",
                $"DB_USERNAME={DbUsername}",
                $"DB_PASSWORD={DbPassword}",
                "",
                "# Anthropic Claude Vision API",
                $"CLAUDE_API_KEY={ClaudeApiKey}",
                $"CLAUDE_MODEL={ClaudeModel}",
                "",
                "# Procesamiento",
                $"MAX_CONCURRENCY={MaxConcurrency}",
                $"MAX_PAGES={MaxPages}",
                $"DEFAULT_PDF_DIR={DefaultPdfDir}"
            };

            File.WriteAllLines(EnvFilePath, lines, new UTF8Encoding(false));

            // Si estamos ejecutando desde bin\Debug\net472 y existe otro .env junto al .exe, mantenerlo sincronizado
            string exeEnv = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".env");
            if (!string.Equals(Path.GetFullPath(exeEnv), Path.GetFullPath(EnvFilePath), StringComparison.OrdinalIgnoreCase))
            {
                try { File.WriteAllLines(exeEnv, lines, new UTF8Encoding(false)); } catch { }
            }
        }
    }
}
