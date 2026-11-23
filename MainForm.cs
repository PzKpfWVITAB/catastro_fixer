using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using CatastroFixer.Models;
using CatastroFixer.Services;

namespace CatastroFixer
{
    public class MainForm : Form
    {
        private EnvConfig _config;
        private readonly CatalogRepository _catalogs = new CatalogRepository();

        // Controles superiores
        private TextBox _txtFolder;
        private Button _btnBrowse;
        private CheckBox _chkSubfolders;
        private Button _btnLoadPdfs;
        private Button _btnConfig;

        // Panel izquierdo (Selección de PDFs)
        private SplitContainer _splitMain;
        private Button _btnToggleSelectAll;
        private TextBox _txtFilter;
        private Label _lblSelectionCount;
        private CheckedListBox _chkListPdfs;
        private readonly List<string> _allPdfPaths = new List<string>();
        private readonly HashSet<string> _checkedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private bool _suppressItemCheck;

        // Panel derecho (Resultados)
        private Button _btnRun;
        private Button _btnPause;
        private Button _btnCancel;
        private Button _btnClearResults;
        private Button _btnExportCsv;
        private DataGridView _dgvResults;
        private readonly List<DetectionResult> _results = new List<DetectionResult>();

        // Panel inferior (Progreso y Log)
        private ProgressBar _progressBar;
        private Label _lblStatus;
        private RichTextBox _rtbLog;

        // Estado de ejecución / concurrencia / Circuit Breaker
        private volatile bool _isRunning;
        private volatile bool _isPaused;
        private CancellationTokenSource _cts;
        private readonly ManualResetEventSlim _pauseEvent = new ManualResetEventSlim(true);
        private readonly object _pauseLock = new object();
        private int _consecutiveClaudeFailures;
        private const int CLAUDE_CIRCUIT_BREAKER_THRESHOLD = 3;
        private long _totalCacheHits;

        private class PdfListItem
        {
            public string FullPath { get; set; }
            public string DisplayName { get; set; }
            public override string ToString() => DisplayName;
        }

        public MainForm()
        {
            InitializeUi();
            Load += MainForm_Load;
            FormClosing += MainForm_FormClosing;
        }

        private void InitializeUi()
        {
            Text = "CatastroFixer — Localizador de Municipio y Localidad (Claude Vision + stellumdb)";
            Size = new Size(1380, 820);
            MinimumSize = new Size(1024, 640);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9.5f);
            BackColor = Color.FromArgb(248, 250, 252);

            // 1. Barra superior
            var topPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 56,
                Padding = new Padding(12, 10, 12, 8),
                BackColor = Color.White
            };

            var lblDir = new Label { Text = "Carpeta PDFs:", AutoSize = true, Left = 12, Top = 18, Font = new Font("Segoe UI", 9.5f, FontStyle.Bold) };
            _txtFolder = new TextBox { Left = 112, Top = 14, Width = 540, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            _btnBrowse = new Button { Text = "Explorar...", Left = 660, Top = 13, Width = 88, Height = 28, Anchor = AnchorStyles.Top | AnchorStyles.Right, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(241, 245, 249) };
            _chkSubfolders = new CheckBox { Text = "Subcarpetas", Left = 758, Top = 17, Width = 104, Anchor = AnchorStyles.Top | AnchorStyles.Right };
            _btnLoadPdfs = new Button { Text = "Cargar PDFs", Left = 868, Top = 12, Width = 112, Height = 30, Anchor = AnchorStyles.Top | AnchorStyles.Right, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(37, 99, 235), ForeColor = Color.White, Font = new Font("Segoe UI", 9f, FontStyle.Bold) };
            _btnLoadPdfs.FlatAppearance.BorderSize = 0;

            _btnConfig = new Button { Text = "Configurar (.env)", Left = 988, Top = 12, Width = 130, Height = 30, Anchor = AnchorStyles.Top | AnchorStyles.Right, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(51, 65, 85), ForeColor = Color.White };
            _btnConfig.FlatAppearance.BorderSize = 0;

            topPanel.Controls.AddRange(new Control[] { lblDir, _txtFolder, _btnBrowse, _chkSubfolders, _btnLoadPdfs, _btnConfig });

            // Reacomodar anclas al ancho real
            topPanel.Resize += (s, e) =>
            {
                int right = topPanel.ClientSize.Width - 12;
                _btnConfig.Left = right - _btnConfig.Width;
                _btnLoadPdfs.Left = _btnConfig.Left - _btnLoadPdfs.Width - 8;
                _chkSubfolders.Left = _btnLoadPdfs.Left - _chkSubfolders.Width - 8;
                _btnBrowse.Left = _chkSubfolders.Left - _btnBrowse.Width - 8;
                _txtFolder.Width = Math.Max(180, _btnBrowse.Left - _txtFolder.Left - 8);
            };

            _btnBrowse.Click += BtnBrowse_Click;
            _btnLoadPdfs.Click += BtnLoadPdfs_Click;
            _btnConfig.Click += BtnConfig_Click;

            // 2. Panel inferior (Progreso + Log)
            var bottomPanel = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 165,
                Padding = new Padding(12, 6, 12, 10),
                BackColor = Color.White
            };

            _lblStatus = new Label
            {
                Text = "Inicializando...",
                Dock = DockStyle.Top,
                Height = 22,
                ForeColor = Color.FromArgb(30, 41, 59),
                Font = new Font("Segoe UI", 9f, FontStyle.Bold)
            };

            _progressBar = new ProgressBar
            {
                Dock = DockStyle.Top,
                Height = 16
            };

            _rtbLog = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                BackColor = Color.FromArgb(15, 23, 42),
                ForeColor = Color.FromArgb(226, 232, 240),
                Font = new Font("Consolas", 8.5f),
                BorderStyle = BorderStyle.FixedSingle
            };

            bottomPanel.Controls.Add(_rtbLog);
            bottomPanel.Controls.Add(_progressBar);
            bottomPanel.Controls.Add(_lblStatus);

            // 3. SplitContainer central (Izquierda: PDFs con checkboxes | Derecha: Resultados)
            _splitMain = new SplitContainer
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(226, 232, 240),
                SplitterWidth = 5
            };
            var split = _splitMain;

            // Panel Izquierdo
            var leftContainer = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10), BackColor = Color.White };

            var leftHeader = new Panel { Dock = DockStyle.Top, Height = 88 };
            var lblLeftTitle = new Label { Text = "PDFs Disponibles (clic para marcar)", Left = 0, Top = 0, Width = 320, Font = new Font("Segoe UI", 9.5f, FontStyle.Bold), ForeColor = Color.FromArgb(30, 58, 138) };

            _btnToggleSelectAll = new Button
            {
                Text = "Seleccionar todos",
                Left = 0,
                Top = 24,
                Width = 175,
                Height = 28,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(224, 231, 255),
                ForeColor = Color.FromArgb(30, 58, 138),
                Font = new Font("Segoe UI", 9f, FontStyle.Bold)
            };
            _btnToggleSelectAll.FlatAppearance.BorderSize = 0;
            _btnToggleSelectAll.Click += BtnToggleSelectAll_Click;

            _lblSelectionCount = new Label
            {
                Text = "Seleccionados: 0 / 0",
                Left = 182,
                Top = 29,
                Width = 160,
                ForeColor = Color.FromArgb(71, 85, 105),
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold)
            };

            _txtFilter = new TextBox
            {
                Left = 0,
                Top = 58,
                Width = 330,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            // Placeholder visual simple
            var lblFilterHint = new Label { Text = "Filtrar por nombre...", Left = 6, Top = 61, AutoSize = true, ForeColor = Color.Gray, BackColor = Color.White };
            lblFilterHint.Click += (s, e) => _txtFilter.Focus();
            _txtFilter.TextChanged += (s, e) =>
            {
                lblFilterHint.Visible = string.IsNullOrEmpty(_txtFilter.Text);
                RefreshPdfListBox();
            };

            leftHeader.Controls.AddRange(new Control[] { lblLeftTitle, _btnToggleSelectAll, _lblSelectionCount, lblFilterHint, _txtFilter });
            lblFilterHint.BringToFront();
            leftHeader.Resize += (s, e) => _txtFilter.Width = Math.Max(120, leftHeader.ClientSize.Width);

            _chkListPdfs = new CheckedListBox
            {
                Dock = DockStyle.Fill,
                CheckOnClick = true,
                IntegralHeight = false,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Segoe UI", 9f)
            };
            _chkListPdfs.ItemCheck += ChkListPdfs_ItemCheck;
            _chkListPdfs.DoubleClick += (s, e) =>
            {
                if (_chkListPdfs.SelectedItem is PdfListItem item) OpenPdfExternally(item.FullPath);
            };

            leftContainer.Controls.Add(_chkListPdfs);
            leftContainer.Controls.Add(leftHeader);
            split.Panel1.Controls.Add(leftContainer);

            // Panel Derecho
            var rightContainer = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10), BackColor = Color.White };

            var actionPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 42,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false
            };

            _btnRun = new Button { Text = "▶ Analizar Seleccionados", Width = 185, Height = 32, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(22, 163, 74), ForeColor = Color.White, Font = new Font("Segoe UI", 9.5f, FontStyle.Bold), Margin = new Padding(0, 2, 8, 2) };
            _btnRun.FlatAppearance.BorderSize = 0;
            _btnRun.Click += BtnRun_Click;

            _btnPause = new Button { Text = "⏸ Pausar", Width = 100, Height = 32, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(245, 158, 11), ForeColor = Color.White, Font = new Font("Segoe UI", 9f, FontStyle.Bold), Enabled = false, Margin = new Padding(0, 2, 8, 2) };
            _btnPause.FlatAppearance.BorderSize = 0;
            _btnPause.Click += BtnPause_Click;

            _btnCancel = new Button { Text = "■ Cancelar", Width = 100, Height = 32, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(220, 38, 38), ForeColor = Color.White, Font = new Font("Segoe UI", 9f, FontStyle.Bold), Enabled = false, Margin = new Padding(0, 2, 16, 2) };
            _btnCancel.FlatAppearance.BorderSize = 0;
            _btnCancel.Click += BtnCancel_Click;

            _btnClearResults = new Button { Text = "Limpiar Tabla", Width = 110, Height = 32, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(241, 245, 249), Margin = new Padding(0, 2, 8, 2) };
            _btnClearResults.Click += (s, e) => { _results.Clear(); _dgvResults.Rows.Clear(); };

            _btnExportCsv = new Button { Text = "Exportar CSV", Width = 115, Height = 32, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(14, 116, 144), ForeColor = Color.White, Font = new Font("Segoe UI", 9f, FontStyle.Bold), Margin = new Padding(0, 2, 0, 2) };
            _btnExportCsv.FlatAppearance.BorderSize = 0;
            _btnExportCsv.Click += BtnExportCsv_Click;

            actionPanel.Controls.AddRange(new Control[] { _btnRun, _btnPause, _btnCancel, _btnClearResults, _btnExportCsv });

            _dgvResults = new DataGridView
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false,
                RowHeadersVisible = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.DisplayedCells,
                BackgroundColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
                EnableHeadersVisualStyles = false
            };

            _dgvResults.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(30, 41, 59);
            _dgvResults.ColumnHeadersDefaultCellStyle.ForeColor = Color.White;
            _dgvResults.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI", 9f, FontStyle.Bold);
            _dgvResults.ColumnHeadersHeight = 30;
            _dgvResults.RowTemplate.Height = 26;

            _dgvResults.Columns.Add("Archivo", "Archivo");
            _dgvResults.Columns.Add("Municipio", "Municipio");
            _dgvResults.Columns.Add("ClaveMuni", "Clave Muni");
            _dgvResults.Columns.Add("Localidad", "Localidad");
            _dgvResults.Columns.Add("IdLocalidad", "ID Loc");
            _dgvResults.Columns.Add("ClaveLoc", "Clave Loc");
            _dgvResults.Columns.Add("Ubicacion", "Ubicación Extraída");
            _dgvResults.Columns.Add("Estado", "Estado");
            _dgvResults.Columns.Add("DestinoPropuesto", "Destino Propuesto");
            _dgvResults.Columns.Add("Evidencia", "Evidencia");
            var colPath = new DataGridViewTextBoxColumn { Name = "Ruta", HeaderText = "Ruta", Visible = false };
            _dgvResults.Columns.Add(colPath);

            _dgvResults.Columns["Evidencia"].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            _dgvResults.CellDoubleClick += DgvResults_CellDoubleClick;

            var lblHintDgv = new Label
            {
                Text = "Doble clic en cualquier fila para abrir el PDF y verificar visualmente.",
                Dock = DockStyle.Bottom,
                Height = 20,
                ForeColor = Color.FromArgb(100, 116, 139),
                Font = new Font("Segoe UI", 8.5f, FontStyle.Italic)
            };

            rightContainer.Controls.Add(_dgvResults);
            rightContainer.Controls.Add(lblHintDgv);
            rightContainer.Controls.Add(actionPanel);
            split.Panel2.Controls.Add(rightContainer);

            Controls.Add(split);
            Controls.Add(topPanel);
            Controls.Add(bottomPanel);
        }

        private async void MainForm_Load(object sender, EventArgs e)
        {
            try
            {
                if (_splitMain != null && _splitMain.Width > 780)
                {
                    _splitMain.Panel1MinSize = 260;
                    _splitMain.Panel2MinSize = 480;
                    _splitMain.SplitterDistance = 360;
                }
            }
            catch { }

            _config = EnvConfig.Load();
            _txtFolder.Text = _config.DefaultPdfDir;

            Log($"[Config] Archivo .env: {_config.EnvFilePath}");
            Log($"[Config] Modelo Claude: {_config.ClaudeModel} | Hilos: {_config.MaxConcurrency} | Máx. páginas: {_config.MaxPages}");

            await LoadCatalogsSafeAsync();

            if (!string.IsNullOrWhiteSpace(_txtFolder.Text) && Directory.Exists(_txtFolder.Text))
            {
                LoadPdfsFromFolder(_txtFolder.Text);
            }
        }

        private async Task LoadCatalogsSafeAsync()
        {
            try
            {
                _lblStatus.Text = "Conectando a MySQL local (stellumdb) y cargando catálogos...";
                await _catalogs.LoadCatalogsAsync(_config, Log);
                _lblStatus.Text = $"Listo | stellumdb: {_catalogs.Municipios.Count} municipios, {_catalogs.Localidades.Count} localidades | Modelo: {_config.ClaudeModel}";
            }
            catch (Exception ex)
            {
                Log($"[ERROR MySQL] No se pudieron cargar los catálogos de stellumdb: {ex.Message}");
                _lblStatus.Text = "Error al conectar a MySQL (abre 'Configurar (.env)' para revisar credenciales).";
            }
        }

        private void BtnBrowse_Click(object sender, EventArgs e)
        {
            using (var dlg = new FolderBrowserDialog())
            {
                dlg.Description = "Seleccione la carpeta que contiene los archivos PDF";
                if (!string.IsNullOrWhiteSpace(_txtFolder.Text) && Directory.Exists(_txtFolder.Text))
                    dlg.SelectedPath = _txtFolder.Text;

                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    _txtFolder.Text = dlg.SelectedPath;
                    LoadPdfsFromFolder(dlg.SelectedPath);
                }
            }
        }

        private void BtnLoadPdfs_Click(object sender, EventArgs e)
        {
            string dir = _txtFolder.Text.Trim();
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            {
                MessageBox.Show($"La carpeta no existe o no es accesible:\n{dir}", "Carpeta inválida", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            LoadPdfsFromFolder(dir);
        }

        private void LoadPdfsFromFolder(string folder)
        {
            try
            {
                Cursor = Cursors.WaitCursor;
                var opt = _chkSubfolders.Checked ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                var files = Directory.GetFiles(folder, "*.pdf", opt)
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                _allPdfPaths.Clear();
                _allPdfPaths.AddRange(files);
                _checkedPaths.Clear();

                RefreshPdfListBox();
                Log($"[Carpeta] Cargados {_allPdfPaths.Count} PDF(s) desde: {folder}");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error al listar PDFs:\n{ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                Cursor = Cursors.Default;
            }
        }

        private void RefreshPdfListBox()
        {
            string filter = (_txtFilter.Text ?? "").Trim();
            string baseDir = (_txtFolder.Text ?? "").Trim().TrimEnd('\\', '/');

            _suppressItemCheck = true;
            _chkListPdfs.BeginUpdate();
            _chkListPdfs.Items.Clear();

            foreach (var path in _allPdfPaths)
            {
                string display = path;
                if (!string.IsNullOrEmpty(baseDir) && path.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase))
                {
                    display = path.Substring(baseDir.Length).TrimStart('\\', '/');
                }
                else
                {
                    display = Path.GetFileName(path);
                }

                if (!string.IsNullOrEmpty(filter) &&
                    display.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                int idx = _chkListPdfs.Items.Add(new PdfListItem { FullPath = path, DisplayName = display });
                if (_checkedPaths.Contains(path))
                {
                    _chkListPdfs.SetItemChecked(idx, true);
                }
            }

            _chkListPdfs.EndUpdate();
            _suppressItemCheck = false;
            UpdateSelectionCounterAndToggleButton();
        }

        private void ChkListPdfs_ItemCheck(object sender, ItemCheckEventArgs e)
        {
            if (_suppressItemCheck) return;
            if (_chkListPdfs.Items[e.Index] is PdfListItem item)
            {
                if (e.NewValue == CheckState.Checked) _checkedPaths.Add(item.FullPath);
                else _checkedPaths.Remove(item.FullPath);
            }
            BeginInvoke(new Action(UpdateSelectionCounterAndToggleButton));
        }

        private void BtnToggleSelectAll_Click(object sender, EventArgs e)
        {
            if (_chkListPdfs.Items.Count == 0) return;

            bool allVisibleChecked = AreAllVisibleChecked();
            bool targetCheck = !allVisibleChecked;

            _suppressItemCheck = true;
            _chkListPdfs.BeginUpdate();
            for (int i = 0; i < _chkListPdfs.Items.Count; i++)
            {
                _chkListPdfs.SetItemChecked(i, targetCheck);
                if (_chkListPdfs.Items[i] is PdfListItem item)
                {
                    if (targetCheck) _checkedPaths.Add(item.FullPath);
                    else _checkedPaths.Remove(item.FullPath);
                }
            }
            _chkListPdfs.EndUpdate();
            _suppressItemCheck = false;

            UpdateSelectionCounterAndToggleButton();
        }

        private bool AreAllVisibleChecked()
        {
            if (_chkListPdfs.Items.Count == 0) return false;
            for (int i = 0; i < _chkListPdfs.Items.Count; i++)
            {
                if (!_chkListPdfs.GetItemChecked(i)) return false;
            }
            return true;
        }

        private void UpdateSelectionCounterAndToggleButton()
        {
            _lblSelectionCount.Text = $"Seleccionados: {_checkedPaths.Count} / {_allPdfPaths.Count}";
            _btnToggleSelectAll.Text = AreAllVisibleChecked() ? "Deseleccionar todos" : "Seleccionar todos";
        }

        private async void BtnConfig_Click(object sender, EventArgs e)
        {
            using (var dlg = new ConfigDialog(_config))
            {
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    Log("[Config] .env actualizado. Recargando catálogos de stellumdb...");
                    await LoadCatalogsSafeAsync();
                }
            }
        }

        private async void BtnRun_Click(object sender, EventArgs e)
        {
            if (_isRunning) return;

            if (!_catalogs.IsLoaded)
            {
                await LoadCatalogsSafeAsync();
                if (!_catalogs.IsLoaded)
                {
                    MessageBox.Show("No se pudieron cargar los catálogos de stellumdb. Revisa 'Configurar (.env)'.", "Catálogos requeridos", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
            }

            if (string.IsNullOrWhiteSpace(_config.ClaudeApiKey))
            {
                MessageBox.Show("Falta configurar CLAUDE_API_KEY en 'Configurar (.env)'.", "API Key requerida", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var selected = _allPdfPaths.Where(p => _checkedPaths.Contains(p)).ToList();
            if (selected.Count == 0)
            {
                MessageBox.Show("Selecciona al menos un PDF en el panel izquierdo (o haz clic en 'Seleccionar todos').", "Sin selección", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            _isRunning = true;
            _isPaused = false;
            _pauseEvent.Set();
            Interlocked.Exchange(ref _consecutiveClaudeFailures, 0);
            Interlocked.Exchange(ref _totalCacheHits, 0);
            _cts = new CancellationTokenSource();

            SetRunningUiState(true);
            _progressBar.Minimum = 0;
            _progressBar.Maximum = selected.Count;
            _progressBar.Value = 0;

            var queue = new ConcurrentQueue<string>(selected);
            int completed = 0;
            int total = selected.Count;
            var swBatch = Stopwatch.StartNew();

            Log($"=== Iniciando análisis de {total} PDF(s) con {_config.MaxConcurrency} hilo(s) | Modelo: {_config.ClaudeModel} ===");

            int workerCount = Math.Min(Math.Max(1, _config.MaxConcurrency), total);
            var tasks = new List<Task>();

            for (int w = 0; w < workerCount; w++)
            {
                tasks.Add(Task.Run(async () =>
                {
                    var processor = new ClaudeGeoProcessor(
                        _catalogs,
                        _config,
                        Log,
                        tokens => Interlocked.Increment(ref _totalCacheHits));

                    while (!queue.IsEmpty)
                    {
                        if (_cts.Token.IsCancellationRequested) break;

                        try { _pauseEvent.Wait(_cts.Token); }
                        catch (OperationCanceledException) { break; }

                        if (!queue.TryDequeue(out string pdfPath)) break;

                        DetectionResult res;
                        try
                        {
                            Log($"Procesando: {Path.GetFileName(pdfPath)}");
                            res = await processor.ProcessPdfAsync(pdfPath, _cts.Token);
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }

                        if (res.IsClaudeApiError)
                        {
                            // Circuit Breaker: reencolar para no perder el archivo
                            queue.Enqueue(pdfPath);
                            int fails = Interlocked.Increment(ref _consecutiveClaudeFailures);
                            Log($"[Aviso Claude API] Fallo en '{res.FileName}' ({fails}/{CLAUDE_CIRCUIT_BREAKER_THRESHOLD}): {res.ErrorDetail}");

                            if (fails >= CLAUDE_CIRCUIT_BREAKER_THRESHOLD)
                            {
                                TriggerCircuitBreaker(res.ErrorDetail);
                            }

                            try { _pauseEvent.Wait(_cts.Token); }
                            catch (OperationCanceledException) { break; }
                            continue;
                        }

                        Interlocked.Exchange(ref _consecutiveClaudeFailures, 0);

                        try
                        {
                            await DatabaseSchemaManager.UpsertResultAsync(_config, res, _cts.Token);
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                        catch (Exception dbEx)
                        {
                            Log($"[Aviso MySQL] No se pudo guardar '{res.FileName}' en {DatabaseSchemaManager.ResultsTableName}: {dbEx.Message} (Usa 'Crear / Actualizar Tablas BD' en Configurar)");
                        }

                        int done = Interlocked.Increment(ref completed);
                        AddResultRowThreadSafe(res, done, total, swBatch.Elapsed.TotalSeconds);
                    }
                }));
            }

            try
            {
                await Task.WhenAll(tasks);
            }
            catch (OperationCanceledException) { }
            finally
            {
                swBatch.Stop();
                _isRunning = false;
                SetRunningUiState(false);

                string summary = _cts.IsCancellationRequested
                    ? $"Cancelado ({completed}/{total} procesados en {swBatch.Elapsed.TotalSeconds:F1}s)"
                    : $"Completado: {completed}/{total} PDF(s) en {swBatch.Elapsed.TotalSeconds:F1}s | Cache Hits: {Interlocked.Read(ref _totalCacheHits)}";

                _lblStatus.Text = summary;
                Log($"=== {summary} ===");
                _cts?.Dispose();
                _cts = null;
            }
        }

        private void TriggerCircuitBreaker(string reason)
        {
            bool newlyPaused = false;
            lock (_pauseLock)
            {
                if (!_isPaused)
                {
                    _isPaused = true;
                    _pauseEvent.Reset();
                    newlyPaused = true;
                }
            }

            if (newlyPaused)
            {
                Log($"[CIRCUIT BREAKER] {CLAUDE_CIRCUIT_BREAKER_THRESHOLD} fallos consecutivos en Claude API. Lote pausado automáticamente.");
                BeginInvoke(new Action(() =>
                {
                    _btnPause.Text = "▶ Reanudar";
                    _lblStatus.Text = "PAUSADO automáticamente (Circuit Breaker Claude API).";
                    MessageBox.Show(
                        $"Se detectaron {CLAUDE_CIRCUIT_BREAKER_THRESHOLD} fallos consecutivos en la API de Claude Vision.\n\n" +
                        $"El proceso se pausó automáticamente y los archivos pendientes siguen en cola.\nMotivo: {reason}\n\n" +
                        "Verifica tu saldo/conexión y presiona 'Reanudar'.",
                        "Circuit Breaker — Pausa Automática",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                }));
            }
        }

        private void BtnPause_Click(object sender, EventArgs e)
        {
            if (!_isRunning) return;
            lock (_pauseLock)
            {
                if (!_isPaused)
                {
                    _isPaused = true;
                    _pauseEvent.Reset();
                    _btnPause.Text = "▶ Reanudar";
                    _lblStatus.Text = "Procesamiento pausado manualmente.";
                    Log("[Pausa] Pausado manualmente por el usuario.");
                }
                else
                {
                    _isPaused = false;
                    Interlocked.Exchange(ref _consecutiveClaudeFailures, 0);
                    _pauseEvent.Set();
                    _btnPause.Text = "⏸ Pausar";
                    Log("[Pausa] Procesamiento reanudado.");
                }
            }
        }

        private void BtnCancel_Click(object sender, EventArgs e)
        {
            if (!_isRunning) return;
            _cts?.Cancel();
            lock (_pauseLock)
            {
                _isPaused = false;
                _pauseEvent.Set();
            }
            Log("[Cancelación] Solicitando cancelación de workers...");
        }

        private void SetRunningUiState(bool running)
        {
            _btnRun.Enabled = !running;
            _btnLoadPdfs.Enabled = !running;
            _btnBrowse.Enabled = !running;
            _btnConfig.Enabled = !running;
            _chkListPdfs.Enabled = !running;
            _btnToggleSelectAll.Enabled = !running;
            _btnPause.Enabled = running;
            _btnCancel.Enabled = running;
            _btnPause.Text = "⏸ Pausar";
        }

        private void AddResultRowThreadSafe(DetectionResult res, int done, int total, double elapsedSec)
        {
            if (IsDisposed) return;
            BeginInvoke(new Action(() =>
            {
                if (IsDisposed) return;
                _results.Add(res);

                int rowIdx = _dgvResults.Rows.Add(
                    res.FileName,
                    res.MunicipioNombre ?? "",
                    res.MunicipioClave ?? "",
                    res.LocalidadNombre ?? "",
                    res.LocalidadId?.ToString() ?? "",
                    res.LocalidadClave ?? "",
                    res.UbicacionExtraida ?? "",
                    res.Estado ?? "",
                    res.DestinoPropuesto ?? "",
                    res.Evidencia ?? "",
                    res.FilePath ?? ""
                );

                var row = _dgvResults.Rows[rowIdx];
                if (res.Estado == "Detectado")
                {
                    row.DefaultCellStyle.BackColor = Color.FromArgb(220, 252, 231);
                }
                else if (res.Estado == "Detectado (Fallback)")
                {
                    row.DefaultCellStyle.BackColor = Color.FromArgb(254, 249, 195);
                }
                else if (res.Estado != null && res.Estado.StartsWith("Fallback Total", StringComparison.OrdinalIgnoreCase))
                {
                    row.DefaultCellStyle.BackColor = Color.FromArgb(255, 237, 213);
                }
                else
                {
                    row.DefaultCellStyle.BackColor = Color.FromArgb(254, 226, 226);
                }

                _progressBar.Value = Math.Min(_progressBar.Maximum, done);
                double avgSec = done > 0 ? elapsedSec / done : 0;
                _lblStatus.Text = $"Procesando: {done}/{total} PDFs | Promedio: {avgSec:F2}s/PDF | Cache Hits: {Interlocked.Read(ref _totalCacheHits)}";
            }));
        }

        private void DgvResults_CellDoubleClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0) return;
            string path = _dgvResults.Rows[e.RowIndex].Cells["Ruta"].Value?.ToString();
            OpenPdfExternally(path);
        }

        private void OpenPdfExternally(string pdfPath)
        {
            if (string.IsNullOrWhiteSpace(pdfPath) || !File.Exists(pdfPath)) return;
            try
            {
                Process.Start(new ProcessStartInfo(pdfPath) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"No se pudo abrir el PDF:\n{ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void BtnExportCsv_Click(object sender, EventArgs e)
        {
            if (_results.Count == 0)
            {
                MessageBox.Show("No hay resultados para exportar todavía.", "Exportar CSV", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using (var dlg = new SaveFileDialog())
            {
                dlg.Filter = "Archivo CSV (*.csv)|*.csv";
                dlg.FileName = $"catastro_fixer_{DateTime.Now:yyyyMMdd_HHmmss}.csv";
                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                try
                {
                    // Respetar el separador de listas de Windows (',' en es-MX, ';' en es-ES) para que Excel separe columnas directo
                    string sep = System.Globalization.CultureInfo.CurrentCulture.TextInfo.ListSeparator;
                    if (sep != "," && sep != ";") sep = ",";

                    var sb = new StringBuilder();
                    sb.Append(string.Join(sep, new[]
                    {
                        "NombreOriginal", "UbicacionOriginal", "CarpetaMuniOriginal",
                        "NombrePropuesto", "DestinoPropuesto",
                        "Municipio", "ClaveMuni", "MunicipioId",
                        "Localidad", "IdLocalidad", "ClaveLoc",
                        "UbicacionExtraida", "Estado", "Evidencia"
                    })).Append("\r\n");

                    foreach (var r in _results)
                    {
                        sb.Append(string.Join(sep,
                            Csv(r.FileName),
                            Csv(r.FilePath),
                            Csv(r.CarpetaMunicipioOriginal),
                            Csv(r.NombrePropuesto),
                            Csv(r.DestinoPropuesto),
                            Csv(r.MunicipioNombre),
                            Csv(r.MunicipioClave),
                            Csv(r.MunicipioId?.ToString()),
                            Csv(r.LocalidadNombre),
                            Csv(r.LocalidadId?.ToString()),
                            Csv(r.LocalidadClave),
                            Csv(r.UbicacionExtraida),
                            Csv(r.Estado),
                            Csv(r.Evidencia))).Append("\r\n");
                    }

                    // UTF-8 con BOM (EF BB BF): garantiza que Excel abra tildes, ñ y rutas intactas al doble clic
                    File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
                    MessageBox.Show($"CSV exportado correctamente en UTF-8 con BOM ({_results.Count} filas):\n{dlg.FileName}", "Exportado", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error al exportar CSV:\n{ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private static string Csv(string val)
        {
            if (string.IsNullOrEmpty(val)) return "\"\"";
            string normalized = val.Normalize(NormalizationForm.FormC);
            return "\"" + normalized.Replace("\"", "\"\"").Replace("\r", " ").Replace("\n", " ") + "\"";
        }

        private void Log(string msg)
        {
            if (IsDisposed) return;
            string line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action(() => AppendLog(line))); } catch { }
            }
            else
            {
                AppendLog(line);
            }
        }

        private void AppendLog(string line)
        {
            if (IsDisposed || _rtbLog == null) return;
            _rtbLog.AppendText(line + Environment.NewLine);
            _rtbLog.SelectionStart = _rtbLog.TextLength;
            _rtbLog.ScrollToCaret();
        }

        private void MainForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (_isRunning)
            {
                var confirm = MessageBox.Show("Hay un análisis en curso. ¿Deseas cancelarlo y salir?", "Confirmar salida", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (confirm != DialogResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }
                _cts?.Cancel();
                _pauseEvent.Set();
            }
        }
    }
}
