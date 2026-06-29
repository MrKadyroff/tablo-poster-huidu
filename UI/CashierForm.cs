namespace LedImageUpdaterService.UI;

/// <summary>
/// Simplified operator window for cashiers. Exposes exactly two actions —
/// refresh the rates from the API and push the rendered image to the board —
/// plus a preview of the image being sent. The board layout/design is fixed
/// and can only be changed from the admin <see cref="SettingsForm"/>.
/// </summary>
internal sealed class CashierForm : Form
{
    private AppConfig _cfg = AppSettingsManager.Load();

    private Label _lblPoint = null!;
    private Label _lblStatus = null!;
    private PictureBox _preview = null!;
    private Button _btnRefresh = null!;
    private Button _btnSend = null!;

    private static readonly Font UIFont = new("Segoe UI", 9f);

    public CashierForm()
    {
        InitializeComponent();
        ReloadConfig();
        LoadExistingPreview();
    }

    private void InitializeComponent()
    {
        SuspendLayout();

        Font = UIFont;
        Text = "eCash Tablo — Курсы";
        Size = new Size(520, 620);
        MinimumSize = new Size(480, 560);
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;
        Icon = TrayApplicationContext.CreateAppIcon();
        BackColor = UITheme.Bg;
        FormClosing += (_, e) =>
        {
            e.Cancel = true;
            Hide();
        };

        // ─── Header ────────────────────────────────────────────────────────
        var header = new Panel { Dock = DockStyle.Top, Height = 68, BackColor = UITheme.Bg };
        UITheme.PaintHeader(header);

        int textX = 18;
        var logoPath = Path.Combine(AppContext.BaseDirectory, "content", "common", "logo.png");
        if (File.Exists(logoPath))
        {
            try
            {
                using var loaded = Image.FromStream(new MemoryStream(File.ReadAllBytes(logoPath)));
                var logoBox = new PictureBox
                {
                    Image = new Bitmap(loaded),
                    SizeMode = PictureBoxSizeMode.Zoom,
                    Size = new Size(48, 48),
                    Location = new Point(16, 10),
                    BackColor = Color.Transparent,
                };
                header.Controls.Add(logoBox);
                textX = 76;
            }
            catch { }
        }

        header.Controls.Add(new Label
        {
            Text = "eCash Tablo",
            ForeColor = UITheme.Accent,
            Font = new Font("Segoe UI Semibold", 16f, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(textX, 12),
            BackColor = Color.Transparent,
        });
        header.Controls.Add(new Label
        {
            Text = "ОБНОВЛЕНИЕ КУРСОВ · ОТПРАВКА НА ТАБЛО",
            ForeColor = Color.FromArgb(150, 200, 230),
            Font = new Font("Segoe UI", 8f),
            AutoSize = true,
            Location = new Point(textX + 2, 44),
            BackColor = Color.Transparent,
        });

        // ─── Active point row ──────────────────────────────────────────────
        var pointRow = new Panel { Dock = DockStyle.Top, Height = 32, Padding = new Padding(12, 6, 12, 0) };
        _lblPoint = new Label
        {
            Dock = DockStyle.Fill,
            ForeColor = UITheme.Text,
            Font = new Font("Segoe UI Semibold", 10f),
            TextAlign = ContentAlignment.MiddleLeft,
            Text = "Точка: —",
        };
        pointRow.Controls.Add(_lblPoint);

        // ─── Action buttons + status (bottom) ──────────────────────────────
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 132, BackColor = UITheme.Panel, Padding = new Padding(12, 10, 12, 10) };
        bottom.Controls.Add(new Label { Height = 2, Dock = DockStyle.Top, BackColor = UITheme.Accent });

        _btnRefresh = MakeBigButton("⟳  Обновить курсы из API", Color.FromArgb(120, 80, 160));
        _btnRefresh.Location = new Point(12, 16);
        _btnRefresh.Click += async (_, _) => await RefreshRatesAsync();

        _btnSend = MakeBigButton("📤  Загрузить на табло", Color.FromArgb(0, 168, 132));
        _btnSend.Location = new Point(12, 64);
        _btnSend.Click += async (_, _) => await SendToBoardAsync();

        _lblStatus = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 22,
            ForeColor = UITheme.TextDim,
            TextAlign = ContentAlignment.MiddleLeft,
            Text = "Готово.",
        };

        bottom.Controls.Add(_btnRefresh);
        bottom.Controls.Add(_btnSend);
        bottom.Controls.Add(_lblStatus);

        // Keep the buttons full-width when the window is resized.
        bottom.Resize += (_, _) =>
        {
            int w = bottom.ClientSize.Width - 24;
            _btnRefresh.Width = w;
            _btnSend.Width = w;
        };

        // ─── Preview (fill) ────────────────────────────────────────────────
        var previewHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12), BackColor = UITheme.Bg };
        _preview = new PictureBox
        {
            Dock = DockStyle.Fill,
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.Black,
            BorderStyle = BorderStyle.FixedSingle,
        };
        previewHost.Controls.Add(_preview);

        Controls.Add(previewHost);
        Controls.Add(bottom);
        Controls.Add(pointRow);
        Controls.Add(header);

        UITheme.Apply(this);

        ResumeLayout();
    }

    private static Button MakeBigButton(string text, Color bg) => new RoundedButton
    {
        Text = text,
        Height = 42,
        Width = 460,
        BackColor = bg,
        ForeColor = Color.White,
        Font = new Font("Segoe UI Semibold", 11f),
        CornerRadius = 10,
    };

    /// <summary>Re-reads settings (point can change while the window is hidden).</summary>
    internal void ReloadConfig()
    {
        _cfg = AppSettingsManager.Load();
        _lblPoint.Text = $"Точка: {_cfg.ActivePointId}";
    }

    // ─── Actions ────────────────────────────────────────────────────────────

    private async Task RefreshRatesAsync()
    {
        SetBusy(true);
        SetStatus("Запрос курсов из API…", false);
        try
        {
            var err = await RatesApiClient.FetchAsync(_cfg.ActivePointId, _cfg.RatesApiUrl);
            if (err != null)
            {
                SetStatus("✗ Ошибка получения курсов", true);
                MessageBox.Show(err, "Курсы из API", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Render the production image so the preview matches exactly what
            // "Загрузить на табло" will push, and the watch-folder file is fresh.
            SetStatus("Курсы получены. Отрисовка…", false);
            var (img, rerr) = await RenderProductionAsync();
            if (img != null) SetPreview(img);
            SetStatus(rerr == null
                ? $"✓ Курсы обновлены {DateTime.Now:HH:mm:ss}. Нажмите «Загрузить на табло»."
                : "Курсы обновлены, но превью не построено.", rerr != null);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task SendToBoardAsync()
    {
        SetBusy(true);
        SetStatus("Отправка на табло…", false);
        try
        {
            var (ok, msg) = await LedControlClient.SendToBoardAsync(_cfg.Urls);
            SetStatus((ok ? "✓ " : "✗ ") + msg, !ok);
        }
        finally
        {
            SetBusy(false);
        }
    }

    // ─── Preview helpers ──────────────────────────────────────────────────────

    private async Task<(Image? image, string? error)> RenderProductionAsync()
    {
        var composePath = Path.Combine(
            AppContext.BaseDirectory, "layout", "points", $"{_cfg.ActivePointId}.compose.json");
        var ratesPath = Path.Combine(
            AppContext.BaseDirectory, "content", "points", _cfg.ActivePointId, "rates.json");
        return await PreviewRenderer.RenderAsync(composePath, ratesPath);
    }

    // Loads the last rendered output image (if any) so the window shows the
    // current board content immediately on open.
    private void LoadExistingPreview()
    {
        try
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "content", "points", _cfg.ActivePointId, "output");
            if (!Directory.Exists(dir)) return;
            var latest = Directory.GetFiles(dir, "*.*")
                .Where(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                         || f.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                         || f.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (latest == null) return;
            using var loaded = Image.FromStream(new MemoryStream(File.ReadAllBytes(latest)));
            SetPreview(new Bitmap(loaded));
        }
        catch { }
    }

    private void SetPreview(Image img)
    {
        var old = _preview.Image;
        _preview.Image = img;
        old?.Dispose();
    }

    // ─── Status helpers ───────────────────────────────────────────────────────

    private void SetBusy(bool busy)
    {
        _btnRefresh.Enabled = !busy;
        _btnSend.Enabled = !busy;
        Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
    }

    private void SetStatus(string text, bool error)
    {
        _lblStatus.Text = text;
        _lblStatus.ForeColor = error ? Color.Salmon : UITheme.Accent;
    }
}
