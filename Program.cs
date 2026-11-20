using System;
using System.IO;
using System.Net;
using System.Windows.Forms;

namespace CatastroFixer
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            try
            {
                // Optimización de red TCP rescatada de CatastroMover3000 para alta concurrencia con Claude API
                ServicePointManager.DefaultConnectionLimit = 64;
                ServicePointManager.Expect100Continue = false;
                ServicePointManager.UseNagleAlgorithm = false;
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            }
            catch { }

            if (args != null && args.Length > 0 && args[0] == "--selftest")
            {
                RunSelfTest();
                return;
            }

            Application.ThreadException += (s, e) => LogCrash(e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => LogCrash(e.ExceptionObject as Exception);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }

        private static void RunSelfTest()
        {
            string logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
            if (!Directory.Exists(logDir)) Directory.CreateDirectory(logDir);
            string outPath = Path.Combine(logDir, "selftest.txt");
            try
            {
                var cfg = Services.EnvConfig.Load();
                var repo = new Services.CatalogRepository();
                repo.LoadCatalogsAsync(cfg).GetAwaiter().GetResult();
                var m = repo.ResolveMunicipio(null, "TUXTLA GUTIERREZZ");
                var l = m != null ? repo.GetLocalidadByIdForMunicipio(21874, m.Id) : null;
                var cab = m != null ? repo.GetCabeceraMunicipal(m.Id) : null;

                string report =
                    $"OK\r\n" +
                    $"EnvPath={cfg.EnvFilePath}\r\n" +
                    $"Municipios={repo.Municipios.Count}\r\n" +
                    $"Localidades={repo.Localidades.Count}\r\n" +
                    $"MatchTypo={m?.Id}|{m?.ClaveFormateada}|{m?.Nombre}\r\n" +
                    $"Localidad21874={l?.Id}|{l?.ClaveFormateada}|{l?.Nombre}\r\n" +
                    $"Cabecera={cab?.Id}|{cab?.ClaveFormateada}|{cab?.Nombre}\r\n" +
                    $"TuxtlaLocs={repo.GetLocalidadesCount(m?.Id ?? 0)}\r\n";

                File.WriteAllText(outPath, report, System.Text.Encoding.UTF8);
            }
            catch (Exception ex)
            {
                File.WriteAllText(outPath, "FAIL\r\n" + ex, System.Text.Encoding.UTF8);
                Environment.ExitCode = 1;
            }
        }

        private static void LogCrash(Exception ex)
        {
            if (ex == null) return;
            try
            {
                string logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
                if (!Directory.Exists(logDir)) Directory.CreateDirectory(logDir);
                string logPath = Path.Combine(logDir, "crash_log.txt");
                string msg = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] CRASH NO CONTROLADO:\r\n{ex}\r\n\r\n";
                File.AppendAllText(logPath, msg);
                MessageBox.Show(
                    $"Ocurrió un error crítico no controlado.\nDetalle guardado en logs/crash_log.txt:\n\n{ex.Message}",
                    "Error Crítico - CatastroFixer",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            catch { }
        }
    }
}
