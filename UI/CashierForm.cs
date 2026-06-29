namespace LedImageUpdaterService.UI;

/// <summary>
/// Simplified operator window for cashiers. Exposes the everyday actions —
/// refresh the rates from the API, push the rendered image to the board, and
/// turn the board on/off — plus a preview of the image being sent and a short
/// built-in help ("wiki"). The board layout/design is fixed and can only be
/// changed from the admin <see cref="SettingsForm"/>.
/// </summary>
internal sealed class CashierForm : Form
{
    private AppConfig _cfg = AppSettingsManager.Load();

    private Label _lblPoint = null!;
    private Label _lblStatus = null!;
    private PictureBox _preview = null!;
    private Button _btnRefresh = null!;
    private Button _btnSend = null!;
    private Button _btnPowerOn = null!;
    private Button _btnPowerOff = null!;
    private Button _btnHelp = null!;

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
        Size = new Size(520, 680);
        MinimumSize = new Size(480, 620);
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
        var pointRow = new Panel { Dock = DockStyle.Top, Height = 36, Padding = new Padding(12, 6, 12, 0) };
        _btnHelp = new RoundedButton
        {
            Text = "❔  Как это работает",
            Dock = DockStyle.Right,
            Width = 170,
            BackColor = UITheme.Accent2,
            ForeColor = Color.White,
            Font = new Font("Segoe UI Semibold", 9f),
            CornerRadius = 8,
        };
        _btnHelp.Click += (_, _) => ShowHelp();
        _lblPoint = new Label
        {
            Dock = DockStyle.Fill,
            ForeColor = UITheme.Text,
            Font = new Font("Segoe UI Semibold", 10f),
            TextAlign = ContentAlignment.MiddleLeft,
            Text = "Точка: —",
        };
        pointRow.Controls.Add(_lblPoint);
        pointRow.Controls.Add(_btnHelp);

        // ─── Action buttons + status (bottom) ──────────────────────────────
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 196, BackColor = UITheme.Panel, Padding = new Padding(12, 10, 12, 10) };
        bottom.Controls.Add(new Label { Height = 2, Dock = DockStyle.Top, BackColor = UITheme.Accent });

        _btnRefresh = MakeBigButton("⟳  Обновить курсы", Color.FromArgb(120, 80, 160));
        _btnRefresh.Location = new Point(12, 16);
        _btnRefresh.Click += async (_, _) => await RefreshRatesAsync();

        _btnSend = MakeBigButton("📤  Отправить на табло", Color.FromArgb(0, 168, 132));
        _btnSend.Location = new Point(12, 64);
        _btnSend.Click += async (_, _) => await SendToBoardAsync();

        // Power on/off sit side by side on one row.
        _btnPowerOn = MakeBigButton("💡  Включить табло", Color.FromArgb(46, 160, 67));
        _btnPowerOn.Location = new Point(12, 112);
        _btnPowerOn.Click += async (_, _) => await SetPowerAsync(true);

        _btnPowerOff = MakeBigButton("⏻  Выключить табло", Color.FromArgb(176, 76, 76));
        _btnPowerOff.Location = new Point(12, 112);
        _btnPowerOff.Click += async (_, _) => await SetPowerAsync(false);

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
        bottom.Controls.Add(_btnPowerOn);
        bottom.Controls.Add(_btnPowerOff);
        bottom.Controls.Add(_lblStatus);

        // Keep the buttons sized to the window: the two top buttons span the full
        // width; the power buttons split the row in half.
        bottom.Resize += (_, _) =>
        {
            int w = bottom.ClientSize.Width - 24;
            _btnRefresh.Width = w;
            _btnSend.Width = w;

            int half = (w - 8) / 2;
            _btnPowerOn.Width = half;
            _btnPowerOn.Location = new Point(12, 112);
            _btnPowerOff.Width = w - half - 8;
            _btnPowerOff.Location = new Point(12 + half + 8, 112);
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

    // Turns the LED panel on or off via the in-process service. The image stays
    // loaded on the card — "off" only blanks the panel, "on" lights it back up.
    private async Task SetPowerAsync(bool on)
    {
        SetBusy(true);
        SetStatus(on ? "Включаю табло…" : "Выключаю табло…", false);
        try
        {
            var (ok, msg) = await LedControlClient.SetPowerAsync(_cfg.Urls, on);
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
        _btnPowerOn.Enabled = !busy;
        _btnPowerOff.Enabled = !busy;
        Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
    }

    private void SetStatus(string text, bool error)
    {
        _lblStatus.Text = text;
        _lblStatus.ForeColor = error ? Color.Salmon : UITheme.Accent;
    }

    // ─── Help ("wiki") ────────────────────────────────────────────────────────

    // Short built-in instruction window for the cashier. Mirrors the lightweight
    // markdown-ish formatting used by the admin SettingsForm wiki ("## " headings,
    // "- " bullets), but kept to just the everyday actions.
    private const string HelpText =
        """
        Это окно показывает курсы валют, которые сейчас выводятся на табло, и
        позволяет обновить их и управлять самим табло. Дизайн и состав валют
        настраивает администратор — кассиру это менять не нужно.

        ## Кнопки
        - ⟳ Обновить курсы — берёт свежие курсы из системы и заново рисует
          картинку. Снизу появляется превью — это ровно то, что уйдёт на табло.
          Само табло при этом ещё НЕ меняется.
        - 📤 Отправить на табло — отправляет текущую картинку (превью) на табло.
          Нажимайте после «Обновить курсы», чтобы новые цифры появились на экране.
        - 💡 Включить табло — зажигает экран табло (например, утром в начале смены).
        - ⏻ Выключить табло — гасит экран табло (например, на ночь). Курсы при этом
          не теряются — после «Включить» снова покажется последняя картинка.

        ## Обычный порядок работы
        - 1. Нажмите «Обновить курсы» — проверьте цифры в превью снизу.
        - 2. Нажмите «Отправить на табло» — курсы появятся на экране.
        - 3. В начале дня — «Включить табло», в конце дня — «Выключить табло».

        ## Строка статуса
        Внизу окна показывается результат последнего действия:
        - ✓ зелёным — действие выполнено успешно.
        - ✗ красным — не удалось. Проверьте, что компьютер подключён к Wi-Fi табло,
          и повторите. Если не помогает — обратитесь к администратору.

        ## Если что-то не так
        - На табло старые курсы — нажмите «Обновить курсы», затем «Отправить на табло».
        - Табло не реагирует — проверьте подключение к сети Wi-Fi табло.
        - Нужно поменять валюты, цвета или размер — это делает администратор
          в окне настроек.
        """;

    private void ShowHelp()
    {
        using var dlg = new Form
        {
            Text = "eCash Tablo — Как это работает",
            Size = new Size(560, 640),
            MinimumSize = new Size(420, 400),
            StartPosition = FormStartPosition.CenterParent,
            BackColor = UITheme.Bg,
            Icon = TrayApplicationContext.CreateAppIcon(),
            ShowInTaskbar = false,
            MinimizeBox = false,
        };

        var rtb = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            BackColor = UITheme.Panel,
            ForeColor = UITheme.Text,
            Font = new Font("Segoe UI", 10f),
            DetectUrls = false,
            Margin = new Padding(0),
        };

        var host = new Panel { Dock = DockStyle.Fill, Padding = new Padding(14), BackColor = UITheme.Bg };
        host.Controls.Add(rtb);

        var btnClose = new RoundedButton
        {
            Text = "Закрыть",
            Dock = DockStyle.Right,
            Width = 120,
            BackColor = UITheme.Accent2,
            ForeColor = Color.White,
            Font = new Font("Segoe UI Semibold", 10f),
            CornerRadius = 8,
        };
        btnClose.Click += (_, _) => dlg.Close();
        var footer = new Panel { Dock = DockStyle.Bottom, Height = 52, BackColor = UITheme.Panel, Padding = new Padding(14, 8, 14, 8) };
        footer.Controls.Add(btnClose);

        dlg.Controls.Add(host);
        dlg.Controls.Add(footer);

        RenderHelp(rtb, HelpText);
        dlg.ShowDialog(this);
    }

    private static void RenderHelp(RichTextBox rtb, string body)
    {
        var bodyFont = new Font("Segoe UI", 10f);
        var headFont = new Font("Segoe UI Semibold", 12f, FontStyle.Bold);

        void Append(string text, Font font, Color color)
        {
            rtb.SelectionStart = rtb.TextLength;
            rtb.SelectionLength = 0;
            rtb.SelectionFont = font;
            rtb.SelectionColor = color;
            rtb.AppendText(text);
        }

        foreach (var raw in body.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0)
                Append("\n", bodyFont, UITheme.Text);
            else if (line.StartsWith("## "))
                Append("\n" + line[3..] + "\n", headFont, UITheme.Accent);
            else if (line.StartsWith("- "))
                Append("   •  " + line[2..] + "\n", bodyFont, UITheme.Text);
            else
                Append(line + "\n", bodyFont, UITheme.Text);
        }

        rtb.SelectionStart = 0;
        rtb.ScrollToCaret();
    }
}
