using System;
using System.Drawing;
using System.Net.Http;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using CatastroFixer.Services;

namespace CatastroFixer
{
    public class ConfigDialog : Form
    {
        private readonly EnvConfig _config;

        private TextBox _txtHost;
        private NumericUpDown _numPort;
        private TextBox _txtDatabase;
        private TextBox _txtUser;
        private TextBox _txtPassword;
        private TextBox _txtApiKey;
        private ComboBox _cmbModel;
        private NumericUpDown _numConcurrency;
        private NumericUpDown _numMaxPages;
        private TextBox _txtDefaultDir;
        private Button _btnTestMySql;
        private Button _btnTestClaude;
        private Button _btnSave;
        private Button _btnCancel;

        private static readonly string[] SupportedModels =
        {
            "claude-sonnet-4-6",
            "claude-haiku-4-5-20251001",
            "claude-sonnet-4-5-20250929",
            "claude-opus-4-6"
        };

        public ConfigDialog(EnvConfig config)
        {
            _config = config;
            InitializeUi();
            LoadValues();
        }

        private void InitializeUi()
        {
            Text = "Configuración (.env) — CatastroFixer";
            Size = new Size(560, 520);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            Font = new Font("Segoe UI", 9.5f);
            BackColor = Color.White;

            int y = 16;

            var lblDb = new Label { Text = "MySQL Local (stellumdb)", Font = new Font("Segoe UI", 10f, FontStyle.Bold), ForeColor = Color.FromArgb(30, 58, 138), Left = 20, Top = y, Width = 500 };
            Controls.Add(lblDb);
            y += 28;

            _txtHost = AddTextRow("Host:", ref y, 220);
            _numPort = AddNumericRow("Puerto:", ref y, 1, 65535);
            _txtDatabase = AddTextRow("Base de Datos:", ref y, 220);
            _txtUser = AddTextRow("Usuario:", ref y, 220);
            _txtPassword = AddTextRow("Contraseña:", ref y, 220, isPassword: true);

            _btnTestMySql = new Button { Text = "Probar MySQL", Left = 160, Top = y, Width = 140, Height = 30, BackColor = Color.FromArgb(241, 245, 249), FlatStyle = FlatStyle.Flat };
            _btnTestMySql.Click += BtnTestMySql_Click;
            Controls.Add(_btnTestMySql);
            y += 42;

            var lblClaude = new Label { Text = "Anthropic Claude Vision", Font = new Font("Segoe UI", 10f, FontStyle.Bold), ForeColor = Color.FromArgb(30, 58, 138), Left = 20, Top = y, Width = 500 };
            Controls.Add(lblClaude);
            y += 28;

            _txtApiKey = AddTextRow("API Key:", ref y, 360, isPassword: true);

            Controls.Add(new Label { Text = "Modelo:", Left = 20, Top = y + 3, Width = 130 });
            _cmbModel = new ComboBox { Left = 160, Top = y, Width = 260, DropDownStyle = ComboBoxStyle.DropDown };
            _cmbModel.Items.AddRange(SupportedModels);
            Controls.Add(_cmbModel);
            y += 32;

            _numConcurrency = AddNumericRow("Hilos concurrentes:", ref y, 1, 32);
            _numMaxPages = AddNumericRow("Máx. páginas/PDF:", ref y, 1, 15);
            _txtDefaultDir = AddTextRow("Carpeta PDF default:", ref y, 360);

            _btnTestClaude = new Button { Text = "Probar Claude", Left = 160, Top = y, Width = 140, Height = 30, BackColor = Color.FromArgb(241, 245, 249), FlatStyle = FlatStyle.Flat };
            _btnTestClaude.Click += BtnTestClaude_Click;
            Controls.Add(_btnTestClaude);
            y += 46;

            _btnSave = new Button { Text = "Guardar .env", Left = 310, Top = y, Width = 110, Height = 34, BackColor = Color.FromArgb(22, 163, 74), ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 9.5f, FontStyle.Bold) };
            _btnSave.FlatAppearance.BorderSize = 0;
            _btnSave.Click += BtnSave_Click;
            Controls.Add(_btnSave);

            _btnCancel = new Button { Text = "Cancelar", Left = 430, Top = y, Width = 90, Height = 34, DialogResult = DialogResult.Cancel, BackColor = Color.FromArgb(226, 232, 240), FlatStyle = FlatStyle.Flat };
            _btnCancel.FlatAppearance.BorderSize = 0;
            Controls.Add(_btnCancel);

            AcceptButton = _btnSave;
            CancelButton = _btnCancel;
        }

        private TextBox AddTextRow(string label, ref int y, int width, bool isPassword = false)
        {
            Controls.Add(new Label { Text = label, Left = 20, Top = y + 3, Width = 130 });
            var txt = new TextBox { Left = 160, Top = y, Width = width, UseSystemPasswordChar = isPassword };
            Controls.Add(txt);
            y += 30;
            return txt;
        }

        private NumericUpDown AddNumericRow(string label, ref int y, int min, int max)
        {
            Controls.Add(new Label { Text = label, Left = 20, Top = y + 3, Width = 130 });
            var num = new NumericUpDown { Left = 160, Top = y, Width = 100, Minimum = min, Maximum = max };
            Controls.Add(num);
            y += 30;
            return num;
        }

        private void LoadValues()
        {
            _txtHost.Text = _config.DbHost;
            _numPort.Value = Math.Max(1, Math.Min(65535, _config.DbPort));
            _txtDatabase.Text = _config.DbDatabase;
            _txtUser.Text = _config.DbUsername;
            _txtPassword.Text = _config.DbPassword;
            _txtApiKey.Text = _config.ClaudeApiKey;
            _cmbModel.Text = string.IsNullOrWhiteSpace(_config.ClaudeModel) ? "claude-sonnet-4-6" : _config.ClaudeModel;
            _numConcurrency.Value = Math.Max(1, Math.Min(32, _config.MaxConcurrency));
            _numMaxPages.Value = Math.Max(1, Math.Min(15, _config.MaxPages));
            _txtDefaultDir.Text = _config.DefaultPdfDir;
        }

        private void ApplyToConfig(EnvConfig target)
        {
            target.DbHost = _txtHost.Text.Trim();
            target.DbPort = (int)_numPort.Value;
            target.DbDatabase = _txtDatabase.Text.Trim();
            target.DbUsername = _txtUser.Text.Trim();
            target.DbPassword = _txtPassword.Text;
            target.ClaudeApiKey = _txtApiKey.Text.Trim();
            target.ClaudeModel = string.IsNullOrWhiteSpace(_cmbModel.Text) ? "claude-sonnet-4-6" : _cmbModel.Text.Trim();
            target.MaxConcurrency = (int)_numConcurrency.Value;
            target.MaxPages = (int)_numMaxPages.Value;
            target.DefaultPdfDir = _txtDefaultDir.Text.Trim();
        }

        private async void BtnTestMySql_Click(object sender, EventArgs e)
        {
            _btnTestMySql.Enabled = false;
            _btnTestMySql.Text = "Probando...";
            try
            {
                var temp = new EnvConfig();
                ApplyToConfig(temp);
                var repo = new CatalogRepository();
                await repo.TestConnectionAsync(temp);
                MessageBox.Show($"¡Conexión exitosa a MySQL ({temp.DbDatabase} en {temp.DbHost}:{temp.DbPort})!", "MySQL OK", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error al conectar con MySQL:\n\n{ex.Message}", "Error MySQL", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                _btnTestMySql.Enabled = true;
                _btnTestMySql.Text = "Probar MySQL";
            }
        }

        private async void BtnTestClaude_Click(object sender, EventArgs e)
        {
            string key = _txtApiKey.Text.Trim();
            string model = string.IsNullOrWhiteSpace(_cmbModel.Text) ? "claude-sonnet-4-6" : _cmbModel.Text.Trim();

            if (string.IsNullOrEmpty(key))
            {
                MessageBox.Show("Ingresa primero tu CLAUDE_API_KEY.", "Validación", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            _btnTestClaude.Enabled = false;
            _btnTestClaude.Text = "Probando...";
            try
            {
                using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) })
                using (var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages"))
                {
                    req.Headers.Add("x-api-key", key);
                    req.Headers.Add("anthropic-version", "2023-06-01");
                    var payload = new
                    {
                        model,
                        max_tokens = 20,
                        messages = new[] { new { role = "user", content = "ping" } }
                    };
                    var serializer = new JavaScriptSerializer();
                    req.Content = new StringContent(serializer.Serialize(payload), Encoding.UTF8, "application/json");

                    using (var resp = await client.SendAsync(req))
                    {
                        string body = await resp.Content.ReadAsStringAsync();
                        if (resp.IsSuccessStatusCode)
                        {
                            MessageBox.Show($"¡Conexión exitosa con Claude API!\nModelo: {model}", "Claude OK", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        }
                        else
                        {
                            MessageBox.Show($"Error HTTP {(int)resp.StatusCode} con Claude API:\n{body}", "Error Claude", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error al conectar con Claude API:\n\n{ex.Message}", "Error Claude", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                _btnTestClaude.Enabled = true;
                _btnTestClaude.Text = "Probar Claude";
            }
        }

        private void BtnSave_Click(object sender, EventArgs e)
        {
            try
            {
                ApplyToConfig(_config);
                _config.Save();
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"No se pudo guardar .env:\n{ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
