namespace LedImageUpdaterService.UI;

internal sealed class SettingsForm : Form
{
    private readonly Action _onRestart;
    // Full app exit + restart used by the GitHub self-update flow (releases file locks).
    private readonly Action _onExitForUpdate;
    private AppConfig _cfg = new();

    // Header
    private ComboBox _cmbPoint = null!;

    // Tab: Валюты
    private const int MaxColumns = 3;
    private readonly ListBox[] _lstColumns = new ListBox[MaxColumns];
    private readonly GroupBox[] _grpColumns = new GroupBox[MaxColumns];
    private NumericUpDown _numColumnCount = null!;
    private ComboBox _cmbAddTarget = null!;

    // Tab: Заголовки (per-column buy/sell labels)
    private readonly TextBox[] _txtBuyLabels = new TextBox[MaxColumns];
    private readonly TextBox[] _txtSellLabels = new TextBox[MaxColumns];
    private readonly GroupBox[] _grpHeaderCols = new GroupBox[MaxColumns];

    // Tab: Табло
    private NumericUpDown _numW = null!, _numH = null!;

    // Tab: Дизайн
    private LayoutEditorControl _editor = null!;
    private NumericUpDown _numFszValue = null!, _numFszCode = null!, _numFszHdr = null!, _numFszArrow = null!;
    private NumericUpDown _numFlagW = null!, _numFlagH = null!, _numLogoW = null!, _numLogoH = null!;
    private NumericUpDown _numRowsStartY = null!, _numRowH = null!;
    // Per-column X placement (free column layout, e.g. centred logo with rates on both sides)
    private readonly NumericUpDown[] _numColX = new NumericUpDown[MaxColumns];
    private CheckBox _chkManualColX = null!;
    private bool _suppressSync;
    private TabControl _tabs = null!;
    private TabPage _designTab = null!;
    private CheckBox _chkAutoPreview = null!;
    private CheckBox _chkPermanentInternet = null!;
    private Label _lblPreviewStatus = null!;
    private Label _lblSendStatus = null!;
    private System.Windows.Forms.Timer _previewDebounce = null!;
    private bool _previewBusy;

    // Tab: Сервис
    private ComboBox _cmbRunMode = null!, _cmbPublishMode = null!;
    private NumericUpDown _numPoll = null!, _numRatesFetch = null!;
    private CheckBox _chkLayout = null!, _chkAutoSend = null!, _chkSkipUnchanged = null!;
    private CheckBox _chkForceCompose = null!;

    // Tab: Подключение
    private TextBox _txtIp = null!, _txtFtpUser = null!, _txtFtpPass = null!;
    private TextBox _txtRatesUrl = null!, _txtReloadUrl = null!, _txtApiPort = null!;
    private TextBox _txtHuiduCardIp = null!, _txtSsid = null!, _txtHuiduDeviceId = null!;
    private NumericUpDown _numCtrlPort = null!, _numFtpPort = null!, _numDevice = null!;
    private NumericUpDown _numHuiduListenPort = null!;
    private NumericUpDown _numHuiduUdpPort = null!, _numHuiduCardPort = null!;
    private ComboBox _cmbModel = null!;
    private ComboBox _cmbHuiduModel = null!;
    private ComboBox _cmbConnMode = null!;
    private ComboBox _cmbFamily = null!;
    private ComboBox _cmbTransport = null!;
    // Rows hidden/shown based on selected family / transport.
    private Label? _lblCtrlPort, _lblModel, _lblDevice, _lblConnMode;
    private Label? _lblIp, _lblHuiduCardIp, _lblHuiduListenPort, _lblHuiduNote;
    private Label? _lblHuiduModel, _lblHuiduDeviceId;
    private Label? _lblHuiduUdpPort, _lblHuiduCardPort;
    private Label? _lblTransport, _lblSsid;
    private Label _lblPowerStatus = null!;
    private Label _lblConnTestResult = null!;
    private Button _btnHuiduDiag = null!;
    private CheckBox _chkTls = null!;

    // Tab: Дополнительно
    private CheckBox _chkOnbonEnabled = null!, _chkIsolated = null!;
    private CheckBox _chkSkipDup = null!, _chkRejectSize = null!, _chkWifiOnly = null!, _chkPrivate = null!;
    private NumericUpDown _numRetry = null!, _numRetryMs = null!, _numConnTimeout = null!, _numOnbonPoll = null!;
    private TextBox _txtOnbonUser = null!, _txtOnbonPass = null!;
    // Telegram notifications (token/chatId/enabled live in appsettings.json, edited by hand)
    private Button _btnTelegramTest = null!;

    // Tab: Журнал
    private RichTextBox _rtbLog = null!;

    // Tab: Вики
    private ListBox _lstWikiNav = null!;
    private RichTextBox _rtbWiki = null!;

    private static readonly Font UIFont = new("Segoe UI", 9f);
    private static readonly Font BoldFont = new("Segoe UI", 9f, FontStyle.Bold);

    public SettingsForm(Action onRestart, Action onExitForUpdate)
    {
        _onRestart = onRestart;
        _onExitForUpdate = onExitForUpdate;
        InitializeComponent();
        _cfg = AppSettingsManager.Load();
        PopulateForm();
    }

    private void InitializeComponent()
    {
        SuspendLayout();

        Font = UIFont;
        Text = "eCash Tablo — Настройки";
        Size = new Size(700, 620);
        MinimumSize = new Size(680, 580);
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;
        Icon = TrayApplicationContext.CreateAppIcon();
        FormClosing += (_, e) =>
        {
            e.Cancel = true;
            Hide();
        };

        BackColor = UITheme.Bg;

        // ─── Header panel ──────────────────────────────────────────────────
        var header = new Panel
        {
            Dock = DockStyle.Top,
            Height = 68,
            BackColor = UITheme.Bg,
        };
        UITheme.PaintHeader(header);

        int textX = 18;
        var logoPath = Path.Combine(AppContext.BaseDirectory, "content", "common", "logo.png");
        if (File.Exists(logoPath))
        {
            try
            {
                // Load via bytes so the PNG file isn't locked for the form's lifetime
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

        var lblTitle = new Label
        {
            Text = "eCash Tablo",
            ForeColor = UITheme.Accent,
            Font = new Font("Segoe UI Semibold", 16f, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(textX, 12),
            BackColor = Color.Transparent,
        };
        var lblSub = new Label
        {
            Text = "СИСТЕМА УПРАВЛЕНИЯ ТАБЛО · КУРСЫ ВАЛЮТ",
            ForeColor = Color.FromArgb(150, 200, 230),
            Font = new Font("Segoe UI", 8f),
            AutoSize = true,
            Location = new Point(textX + 2, 44),
            BackColor = Color.Transparent,
        };
        header.Controls.AddRange([lblTitle, lblSub]);

        // ─── Point selector row ────────────────────────────────────────────
        var pointRow = new Panel { Dock = DockStyle.Top, Height = 36, Padding = new Padding(8, 6, 8, 0) };
        var lblPoint = new Label { Text = "Активная точка:", AutoSize = true, Location = new Point(8, 10) };
        _cmbPoint = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Location = new Point(130, 6),
            Width = 180,
        };
        _cmbPoint.Items.AddRange(AppSettingsManager.GetAvailablePoints());
        _cmbPoint.SelectedIndexChanged += (_, _) =>
        {
            _cfg.ActivePointId = _cmbPoint.SelectedItem?.ToString() ?? _cfg.ActivePointId;
            var newCfg = AppSettingsManager.Load();
            newCfg.ActivePointId = _cfg.ActivePointId;
            _cfg = newCfg;
            PopulateForm();
        };
        pointRow.Controls.AddRange([lblPoint, _cmbPoint]);

        // ─── Tab control ───────────────────────────────────────────────────
        var tabs = new TabControl { Dock = DockStyle.Fill, Font = UIFont };
        _tabs = tabs;
        UITheme.StyleTabs(tabs);
        tabs.TabPages.Add(BuildCurrenciesTab());
        tabs.TabPages.Add(BuildHeadersTab());
        tabs.TabPages.Add(BuildDisplayTab());
        _designTab = BuildDesignTab();
        tabs.TabPages.Add(_designTab);
        tabs.TabPages.Add(BuildServiceTab());
        tabs.TabPages.Add(BuildConnectionTab());
        tabs.TabPages.Add(BuildAdvancedTab());
        tabs.TabPages.Add(BuildLogTab());
        tabs.TabPages.Add(BuildWikiTab());
        tabs.SelectedIndexChanged += (_, _) =>
        {
            if (tabs.SelectedTab == _designTab)
                EnterDesignTab();
        };

        // ─── Footer ────────────────────────────────────────────────────────
        var footer = new Panel { Dock = DockStyle.Bottom, Height = 48, BackColor = UITheme.Panel };
        footer.Controls.Add(new Label
        {
            Height = 2,
            Dock = DockStyle.Top,
            BackColor = UITheme.Accent,
        });

        var btnSaveRestart = MakeButton("⟳ Сохранить и перезапустить", UITheme.Accent2, Color.White);
        btnSaveRestart.Location = new Point(8, 9);
        btnSaveRestart.Width = 230;
        btnSaveRestart.Click += (_, _) =>
        {
            if (CollectForm())
            {
                AppSettingsManager.Save(_cfg);
                Hide();
                _onRestart();
            }
        };

        var btnSave = MakeButton("💾 Сохранить", Color.FromArgb(0, 168, 132), Color.White);
        btnSave.Location = new Point(246, 9);
        btnSave.Width = 120;
        btnSave.Click += (_, _) =>
        {
            if (CollectForm())
            {
                AppSettingsManager.Save(_cfg);
                MessageBox.Show("Настройки сохранены.\nПерезапустите сервис для применения изменений.",
                    "eCash Tablo", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        };

        var btnClose = MakeButton("Закрыть", UITheme.Input, UITheme.Text);
        btnClose.Location = new Point(374, 9);
        btnClose.Width = 90;
        btnClose.Click += (_, _) => Hide();

        footer.Controls.AddRange([btnSaveRestart, btnSave, btnClose]);

        Controls.Add(tabs);
        Controls.Add(pointRow);
        Controls.Add(header);
        Controls.Add(footer);

        // Apply the dark futuristic theme to the whole control tree
        UITheme.Apply(this);

        ResumeLayout();
    }

    // ─── Tab: Валюты ──────────────────────────────────────────────────────────

    private TabPage BuildCurrenciesTab()
    {
        var tab = new TabPage("Валюты") { Padding = new Padding(8) };

        var note = new Label
        {
            Text = "Выберите валюты. Порядок в списке — порядок на табло. Выберите валюту слева, укажите колонку и нажмите «Добавить».",
            Dock = DockStyle.Top,
            Height = 24,
            ForeColor = Color.Gray,
        };

        // Top strip: column count + add target
        var strip = new Panel { Dock = DockStyle.Top, Height = 34 };
        var lblCols = new Label { Text = "Колонок:", AutoSize = true, Location = new Point(4, 9) };
        _numColumnCount = MakeNumeric(1, MaxColumns);
        _numColumnCount.Location = new Point(70, 5);
        _numColumnCount.Width = 50;
        _numColumnCount.ValueChanged += (_, _) => ApplyColumnCount((int)_numColumnCount.Value);

        var lblTarget = new Label { Text = "Добавить в колонку:", AutoSize = true, Location = new Point(140, 9) };
        _cmbAddTarget = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 60, Location = new Point(270, 5) };
        strip.Controls.AddRange([lblCols, _numColumnCount, lblTarget, _cmbAddTarget]);

        // All currencies panel (left side)
        var grpAll = new GroupBox { Text = "Все доступные", Dock = DockStyle.Left, Width = 210 };
        var lstAll = new ListBox { Dock = DockStyle.Fill, Font = UIFont, SelectionMode = SelectionMode.MultiExtended };
        grpAll.Controls.Add(lstAll);

        // Add/remove buttons
        var btnPanel = new Panel { Dock = DockStyle.Left, Width = 80 };
        var btnAdd = MakeArrow("→ Доб", Color.FromArgb(0, 102, 204), Color.White);
        btnAdd.Location = new Point(10, 40);
        var btnRem = MakeArrow("← Уб", Color.Salmon, Color.White);
        btnRem.Location = new Point(10, 80);
        btnPanel.Controls.AddRange([btnAdd, btnRem]);

        // Column listboxes host
        var columnsHost = new Panel { Dock = DockStyle.Fill };
        for (int i = MaxColumns - 1; i >= 0; i--)
        {
            int idx = i;
            var grp = new GroupBox
            {
                Text = $"Колонка {idx + 1}",
                Dock = idx == 0 ? DockStyle.Fill : DockStyle.Left,
                Width = 150,
            };
            var lst = new ListBox { Dock = DockStyle.Fill, Font = UIFont };
            var upDown = MakeUpDownPanel(lst);
            grp.Controls.Add(lst);
            grp.Controls.Add(upDown);
            _lstColumns[idx] = lst;
            _grpColumns[idx] = grp;
            columnsHost.Controls.Add(grp);
        }

        // Populate lstAll
        var allCurrencies = AppSettingsManager.GetAvailableCurrencies();
        if (allCurrencies.Length == 0)
            allCurrencies = AppSettingsManager.KnownCurrencies.Keys.ToArray();
        foreach (var code in allCurrencies.Union(AppSettingsManager.KnownCurrencies.Keys).Distinct().OrderBy(c => c))
        {
            var name = AppSettingsManager.KnownCurrencies.TryGetValue(code, out var n) ? n : code;
            lstAll.Items.Add($"{code}  {name}");
        }

        btnAdd.Click += (_, _) =>
        {
            int target = Math.Clamp((_cmbAddTarget.SelectedIndex >= 0 ? _cmbAddTarget.SelectedIndex : 0), 0, (int)_numColumnCount.Value - 1);
            var lst = _lstColumns[target];
            foreach (var item in lstAll.SelectedItems.Cast<string>())
            {
                var code = item.Split(' ')[0];
                if (!lst.Items.Cast<string>().Any(s => s.StartsWith(code)))
                    lst.Items.Add(item);
            }
        };

        btnRem.Click += (_, _) =>
        {
            foreach (var lst in _lstColumns)
                foreach (var item in lst.SelectedItems.Cast<string>().ToList())
                    lst.Items.Remove(item);
        };

        var mainPanel = new Panel { Dock = DockStyle.Fill };
        mainPanel.Controls.Add(columnsHost);
        mainPanel.Controls.Add(btnPanel);
        mainPanel.Controls.Add(grpAll);

        tab.Controls.Add(mainPanel);
        tab.Controls.Add(strip);
        tab.Controls.Add(note);

        return tab;
    }

    // Shows/hides column listboxes and rebuilds the add-target dropdown.
    private void ApplyColumnCount(int count)
    {
        count = Math.Clamp(count, 1, MaxColumns);
        for (int i = 0; i < MaxColumns; i++)
            _grpColumns[i].Visible = i < count;

        // Rebuild target combo
        var prev = _cmbAddTarget.SelectedIndex;
        _cmbAddTarget.Items.Clear();
        for (int i = 0; i < count; i++)
            _cmbAddTarget.Items.Add((i + 1).ToString());
        _cmbAddTarget.SelectedIndex = prev >= 0 && prev < count ? prev : 0;

        // Header tab columns follow the same count
        for (int i = 0; i < MaxColumns; i++)
            if (_grpHeaderCols[i] != null) _grpHeaderCols[i].Visible = i < count;

        // Column-X numerics follow the active column count too.
        ApplyColManualEnabled();
    }

    // ─── Tab: Заголовки ───────────────────────────────────────────────────────

    private TabPage BuildHeadersTab()
    {
        var tab = new TabPage("Заголовки") { Padding = new Padding(8) };

        var note = new Label
        {
            Text = "Заголовки «Покупаем/Продаём» для каждой колонки. По строке на язык (напр. казахский / русский / английский).",
            Dock = DockStyle.Top,
            Height = 36,
            ForeColor = Color.Gray,
        };

        var host = new Panel { Dock = DockStyle.Fill };
        for (int i = MaxColumns - 1; i >= 0; i--)
        {
            int idx = i;
            var grp = new GroupBox
            {
                Text = $"Колонка {idx + 1}",
                Dock = idx == 0 ? DockStyle.Fill : DockStyle.Left,
                Width = 230,
                Padding = new Padding(6),
            };

            var lblBuy = new Label { Text = "Покупаем:", Dock = DockStyle.Top, Height = 18 };
            var txtBuy = new TextBox { Dock = DockStyle.Top, Multiline = true, Height = 70, ScrollBars = ScrollBars.Vertical, Font = UIFont };
            var lblSell = new Label { Text = "Продаём:", Dock = DockStyle.Top, Height = 18 };
            var txtSell = new TextBox { Dock = DockStyle.Top, Multiline = true, Height = 70, ScrollBars = ScrollBars.Vertical, Font = UIFont };

            // Dock=Top stacks last-added on top
            grp.Controls.Add(txtSell);
            grp.Controls.Add(lblSell);
            grp.Controls.Add(txtBuy);
            grp.Controls.Add(lblBuy);

            _txtBuyLabels[idx] = txtBuy;
            _txtSellLabels[idx] = txtSell;
            _grpHeaderCols[idx] = grp;
            host.Controls.Add(grp);
        }

        tab.Controls.Add(host);
        tab.Controls.Add(note);
        return tab;
    }

    private static Panel MakeUpDownPanel(ListBox lst)
    {
        var pnl = new Panel { Dock = DockStyle.Bottom, Height = 30 };
        var btnUp = new RoundedButton { Text = "▲", Width = 34, Height = 26, Location = new Point(2, 2), BackColor = UITheme.Input, ForeColor = UITheme.Text, CornerRadius = 7 };
        var btnDn = new RoundedButton { Text = "▼", Width = 34, Height = 26, Location = new Point(38, 2), BackColor = UITheme.Input, ForeColor = UITheme.Text, CornerRadius = 7 };
        btnUp.Click += (_, _) => MoveItem(lst, -1);
        btnDn.Click += (_, _) => MoveItem(lst, 1);
        pnl.Controls.AddRange([btnUp, btnDn]);
        return pnl;
    }

    private static void MoveItem(ListBox lst, int dir)
    {
        var idx = lst.SelectedIndex;
        if (idx < 0) return;
        var newIdx = idx + dir;
        if (newIdx < 0 || newIdx >= lst.Items.Count) return;
        var item = lst.Items[idx];
        lst.Items.RemoveAt(idx);
        lst.Items.Insert(newIdx, item);
        lst.SelectedIndex = newIdx;
    }

    // ─── Tab: Табло ───────────────────────────────────────────────────────────

    private TabPage BuildDisplayTab()
    {
        var tab = new TabPage("Размер табло") { Padding = new Padding(12) };
        var grp = new GroupBox { Text = "Размер холста (пикселей)", Dock = DockStyle.Top, Height = 130, Padding = new Padding(12) };

        _numW = MakeNumeric(8, 4096);
        _numH = MakeNumeric(8, 4096);

        var rows = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, AutoSize = true };
        rows.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));
        rows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddRow(rows, "Ширина (px):", _numW);
        AddRow(rows, "Высота (px):", _numH);

        grp.Controls.Add(rows);

        var hint = new Label
        {
            Text = "Размер должен совпадать с ScreenWidth/ScreenHeight в настройках подключения и с физическими размерами табло.",
            Dock = DockStyle.Top,
            ForeColor = Color.Gray,
            Height = 36,
        };
        tab.Controls.Add(grp);
        tab.Controls.Add(hint);
        return tab;
    }

    // ─── Tab: Дизайн ──────────────────────────────────────────────────────────

    private TabPage BuildDesignTab()
    {
        var tab = new TabPage("Дизайн") { Padding = new Padding(0) };

        // Editor on the left
        _editor = new LayoutEditorControl { Dock = DockStyle.Fill };
        _editor.GeometryChanged += (_, _) =>
        {
            SyncDesignNumericsFromConfig();
            ScheduleLivePreview();
        };

        // Side panel on the right
        var side = new Panel { Dock = DockStyle.Right, Width = 248, Padding = new Padding(8), AutoScroll = true };

        var btnAuto = MakeButton("⊞ Раскидка по размеру", Color.FromArgb(0, 102, 204), Color.White);
        btnAuto.Dock = DockStyle.Top;
        btnAuto.Height = 34;
        btnAuto.Click += (_, _) =>
        {
            PullCurrencies();
            PullDisplay();
            _editor.Bind(_cfg);
            _editor.AutoLayout();
            SyncDesignNumericsFromConfig();
        };

        var btnPreview = MakeButton("👁 Обновить превью", Color.FromArgb(0, 153, 76), Color.White);
        btnPreview.Dock = DockStyle.Top;
        btnPreview.Height = 34;
        btnPreview.Click += (_, _) => _ = RenderPreviewAsync(silent: false);

        var btnApi = MakeButton("⟳ Загрузить курсы из API", Color.FromArgb(120, 80, 160), Color.White);
        btnApi.Dock = DockStyle.Top;
        btnApi.Height = 34;
        btnApi.Click += (_, _) => _ = FetchRatesAndPreviewAsync();

        _chkAutoPreview = new CheckBox
        {
            Text = "Авто-обновление при правках",
            Dock = DockStyle.Top,
            Height = 24,
            Checked = true,
            ForeColor = Color.White,
        };

        // ── Manual send to board (for points without permanent internet) ───────
        var btnSend = MakeButton("📤 Отправить на табло", Color.FromArgb(0, 168, 132), Color.White);
        btnSend.Dock = DockStyle.Top;
        btnSend.Height = 34;
        btnSend.Click += (_, _) => _ = SendToBoardAsync();

        _chkPermanentInternet = new CheckBox
        {
            Text = "Интернет постоянный (автоотправка)",
            Dock = DockStyle.Top,
            Height = 24,
            Checked = true,
            ForeColor = Color.White,
        };

        _lblSendStatus = new Label
        {
            Dock = DockStyle.Top,
            Height = 20,
            ForeColor = Color.Gray,
            Text = "",
        };

        var sendHint = new Label
        {
            Dock = DockStyle.Top,
            Height = 50,
            ForeColor = Color.Gray,
            Text = "Снимите галочку для точек без постоянного интернета: курсы и картинка обновляются, " +
                   "а на табло отправляйте вручную кнопкой выше. Изменение галочки применяется после " +
                   "«Сохранить и перезапустить».",
        };

        _lblPreviewStatus = new Label
        {
            Dock = DockStyle.Top,
            Height = 20,
            ForeColor = Color.Gray,
            Text = "",
        };

        var spacer = new Panel { Dock = DockStyle.Top, Height = 6 };

        // Debounce timer for live preview while editing
        _previewDebounce = new System.Windows.Forms.Timer { Interval = 500 };
        _previewDebounce.Tick += (_, _) =>
        {
            _previewDebounce.Stop();
            _ = RenderPreviewAsync(silent: true);
        };

        // Numeric controls
        var grpFonts = new GroupBox { Text = "Размеры шрифтов", Dock = DockStyle.Top, Height = 130, Padding = new Padding(6) };
        var fonts = NumGrid();
        _numFszValue = MakeNumeric(1, 200);
        _numFszCode = MakeNumeric(1, 200);
        _numFszHdr = MakeNumeric(1, 200);
        _numFszArrow = MakeNumeric(1, 200);
        AddRow(fonts, "Курс (цифры):", _numFszValue);
        AddRow(fonts, "Код валюты:", _numFszCode);
        AddRow(fonts, "Заголовок:", _numFszHdr);
        AddRow(fonts, "Стрелка:", _numFszArrow);
        grpFonts.Controls.Add(fonts);

        var grpFlag = new GroupBox { Text = "Флаг и лого", Dock = DockStyle.Top, Height = 130, Padding = new Padding(6) };
        var flag = NumGrid();
        _numFlagW = MakeNumeric(2, 4096);
        _numFlagH = MakeNumeric(2, 4096);
        _numLogoW = MakeNumeric(2, 4096);
        _numLogoH = MakeNumeric(2, 4096);
        AddRow(flag, "Флаг ширина:", _numFlagW);
        AddRow(flag, "Флаг высота:", _numFlagH);
        AddRow(flag, "Лого ширина:", _numLogoW);
        AddRow(flag, "Лого высота:", _numLogoH);
        grpFlag.Controls.Add(flag);

        var grpRows = new GroupBox { Text = "Строки", Dock = DockStyle.Top, Height = 86, Padding = new Padding(6) };
        var rowsG = NumGrid();
        _numRowsStartY = MakeNumeric(0, 4096);
        _numRowH = MakeNumeric(2, 4096);
        AddRow(rowsG, "Старт строк Y:", _numRowsStartY);
        AddRow(rowsG, "Высота строки:", _numRowH);
        grpRows.Controls.Add(rowsG);

        // ── Размещение колонок (X) — свободная раскладка ──────────────────────
        var grpColX = new GroupBox { Text = "Размещение колонок (X)", Dock = DockStyle.Top, Height = 170, Padding = new Padding(6) };
        var colxG = NumGrid();
        _chkManualColX = new CheckBox { Text = "Задавать X колонок вручную", AutoSize = true };
        _chkManualColX.CheckedChanged += (_, _) => { ApplyColManualEnabled(); ScheduleLivePreview(); };
        colxG.Controls.Add(_chkManualColX, 0, colxG.RowCount);
        colxG.SetColumnSpan(_chkManualColX, 2);
        colxG.RowCount++;
        for (int i = 0; i < MaxColumns; i++)
        {
            _numColX[i] = MakeNumeric(0, 4096);
            _numColX[i].ValueChanged += (_, _) => { if (!_suppressSync) { PullColumnX(); ScheduleLivePreview(); } };
            AddRow(colxG, $"Колонка {i + 1} X:", _numColX[i]);
        }
        var btnLogoCenter = MakeButton("⊞ Лого по центру (широкое)", UITheme.Input, UITheme.Text);
        btnLogoCenter.Width = 220;
        btnLogoCenter.Click += (_, _) => ArrangeLogoCenter();
        colxG.Controls.Add(btnLogoCenter, 0, colxG.RowCount);
        colxG.SetColumnSpan(btnLogoCenter, 2);
        colxG.RowCount++;
        grpColX.Controls.Add(colxG);

        var hint = new Label
        {
            Dock = DockStyle.Top,
            Height = 70,
            ForeColor = Color.Gray,
            Text = "Перетаскивайте блоки мышью. Уголок выделенного блока — изменение размера. «Обновить превью» рисует реальное изображение по текущим курсам.",
        };

        // Wire numerics → config (added in reverse dock order)
        foreach (var n in new[] { _numFszValue, _numFszCode, _numFszHdr, _numFszArrow,
                                  _numFlagW, _numFlagH, _numLogoW, _numLogoH,
                                  _numRowsStartY, _numRowH })
        {
            n.ValueChanged += (_, _) => OnDesignNumericChanged();
        }

        // Dock order: add bottom-most first (Dock=Top stacks last-added on top)
        side.Controls.Add(hint);
        side.Controls.Add(grpColX);
        side.Controls.Add(grpRows);
        side.Controls.Add(grpFlag);
        side.Controls.Add(grpFonts);
        side.Controls.Add(spacer);
        side.Controls.Add(_lblPreviewStatus);
        side.Controls.Add(_chkAutoPreview);
        side.Controls.Add(btnPreview);
        side.Controls.Add(btnApi);
        side.Controls.Add(btnAuto);
        // Manual-send section pinned to the very top of the panel
        side.Controls.Add(sendHint);
        side.Controls.Add(_lblSendStatus);
        side.Controls.Add(_chkPermanentInternet);
        side.Controls.Add(btnSend);

        tab.Controls.Add(_editor);
        tab.Controls.Add(side);
        return tab;
    }

    private static TableLayoutPanel NumGrid()
    {
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return t;
    }

    // Brings the Design tab to the front. Used when auto-opening the window for
    // points without permanent internet so the manual "Отправить на табло" button
    // is immediately in reach.
    internal void SelectDesignTab()
    {
        if (_tabs != null && _designTab != null)
            _tabs.SelectedTab = _designTab;
    }

    private void EnterDesignTab()
    {
        // Bring in latest canvas size + currency count so the editor reflects them
        PullCurrencies();
        PullDisplay();
        PullColumnX();
        _editor.Bind(_cfg);
        SyncDesignNumericsFromConfig();

        // Show a live preview immediately when entering the tab
        if (_chkAutoPreview.Checked)
            _ = RenderPreviewAsync(silent: true);
    }

    private void OnDesignNumericChanged()
    {
        if (_suppressSync) return;
        _cfg.FszValue = (int)_numFszValue.Value;
        _cfg.FszCode = (int)_numFszCode.Value;
        _cfg.FszHdr = (int)_numFszHdr.Value;
        _cfg.FszArrow = (int)_numFszArrow.Value;
        _cfg.ColFlagW = (int)_numFlagW.Value;
        _cfg.ColFlagH = (int)_numFlagH.Value;
        _cfg.LogoW = (int)_numLogoW.Value;
        _cfg.LogoH = (int)_numLogoH.Value;
        _cfg.RowsStartY = (int)_numRowsStartY.Value;
        _cfg.RowH = (int)_numRowH.Value;
        _editor.Invalidate();
        ScheduleLivePreview();
    }

    // Enables column-X numerics only for active columns when manual mode is on.
    private void ApplyColManualEnabled()
    {
        if (_chkManualColX is null || _numColX[0] is null) return; // not built yet
        bool manual = _chkManualColX.Checked;
        int active = Math.Clamp((int)_numColumnCount.Value, 1, MaxColumns);
        for (int i = 0; i < MaxColumns; i++)
            _numColX[i].Enabled = manual && i < active;
        if (!_suppressSync) PullColumnX();
    }

    // Collects per-column X from the UI into the config. Empty list = auto placement.
    private void PullColumnX()
    {
        if (!_chkManualColX.Checked)
        {
            _cfg.ColumnX = [];
            return;
        }

        int active = Math.Clamp((int)_numColumnCount.Value, 1, MaxColumns);
        _cfg.ColumnX = [];
        for (int i = 0; i < active; i++)
            _cfg.ColumnX.Add((int)_numColX[i].Value);
    }

    // Preset for wide boards: logo centred, columns pushed to the sides.
    private void ArrangeLogoCenter()
    {
        PullCurrencies();
        int cnt = Math.Clamp((int)_numColumnCount.Value, 1, MaxColumns);
        int w = (int)_numW.Value;
        int h = (int)_numH.Value;
        int logoW = (int)_numLogoW.Value;
        int logoH = (int)_numLogoH.Value;
        int colW = Math.Max(1, _cfg.ColSellX + _cfg.ColSellW); // approx column content width

        var xs = new List<int>();
        if (cnt == 1)
        {
            xs.Add(0);
        }
        else
        {
            int leftCount = cnt / 2 + (cnt % 2);   // ceil → extra column goes left
            int rightCount = cnt - leftCount;
            for (int i = 0; i < leftCount; i++) xs.Add(i * colW);
            for (int j = 0; j < rightCount; j++) xs.Add(Math.Max(0, w - (rightCount - j) * colW));
        }

        _suppressSync = true;
        _chkManualColX.Checked = true;
        for (int i = 0; i < MaxColumns; i++)
            _numColX[i].Value = Math.Clamp(xs.ElementAtOrDefault(i), 0, 4096);
        _suppressSync = false;

        // Centre the logo on the canvas.
        _cfg.LogoX = Math.Max(0, (w - logoW) / 2);
        _cfg.LogoY = Math.Max(0, (h - logoH) / 2);

        ApplyColManualEnabled();
        PullColumnX();
        _editor.Bind(_cfg);
        _editor.Invalidate();
        ScheduleLivePreview();
    }

    private void ScheduleLivePreview()
    {
        if (_suppressSync) return;
        if (_chkAutoPreview == null || !_chkAutoPreview.Checked) return;
        _previewDebounce.Stop();
        _previewDebounce.Start();
    }

    private void SyncDesignNumericsFromConfig()
    {
        _suppressSync = true;
        SetNum(_numFszValue, _cfg.FszValue);
        SetNum(_numFszCode, _cfg.FszCode);
        SetNum(_numFszHdr, _cfg.FszHdr);
        SetNum(_numFszArrow, _cfg.FszArrow);
        SetNum(_numFlagW, _cfg.ColFlagW);
        SetNum(_numFlagH, _cfg.ColFlagH);
        SetNum(_numLogoW, _cfg.LogoW);
        SetNum(_numLogoH, _cfg.LogoH);
        SetNum(_numRowsStartY, _cfg.RowsStartY);
        SetNum(_numRowH, _cfg.RowH);
        _suppressSync = false;
    }

    private static void SetNum(NumericUpDown n, int v)
        => n.Value = Math.Clamp(v, (int)n.Minimum, (int)n.Maximum);

    private async Task RenderPreviewAsync(bool silent)
    {
        if (_previewBusy) return;
        _previewBusy = true;
        try
        {
            // Bring current edits into _cfg and persist only the layout so the
            // composer renders exactly what is on screen.
            PullCurrencies();
            PullDisplay();
            if (_cfg.AllCurrencies.Count == 0)
            {
                SetPreviewStatus("Нет выбранных валют", true);
                return;
            }

            SetPreviewStatus("Рендеринг…", false);
            // Render to a temp compose/output so production files and the board
            // are not touched until the user explicitly saves.
            var (composePath, _) = AppSettingsManager.WriteTempCompose(_cfg);
            var ratesPath = System.IO.Path.Combine(
                AppContext.BaseDirectory, "content", "points", _cfg.ActivePointId, "rates.json");

            var (image, error) = await PreviewRenderer.RenderAsync(composePath, ratesPath);
            if (error != null)
            {
                SetPreviewStatus("Ошибка рендера", true);
                if (!silent)
                    MessageBox.Show(error, "Превью", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            _editor.SetBackground(image);
            SetPreviewStatus($"Обновлено {DateTime.Now:HH:mm:ss}", false);
        }
        finally
        {
            _previewBusy = false;
        }
    }

    private async Task FetchRatesAndPreviewAsync()
    {
        SetPreviewStatus("Запрос курсов…", false);
        var apiUrl = string.IsNullOrWhiteSpace(_txtRatesUrl.Text) ? _cfg.RatesApiUrl : _txtRatesUrl.Text.Trim();

        var err = await RatesApiClient.FetchAsync(_cfg.ActivePointId, apiUrl);
        if (err != null)
        {
            SetPreviewStatus("Ошибка API", true);
            MessageBox.Show(err, "Курсы из API", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        SetPreviewStatus("Курсы загружены", false);
        await RenderPreviewAsync(silent: false);
    }

    private void SetPreviewStatus(string text, bool error)
    {
        _lblPreviewStatus.Text = text;
        _lblPreviewStatus.ForeColor = error ? Color.Salmon : Color.LightGray;
    }

    // Manually pushes the latest rendered image to the board via the local REST API.
    // Works regardless of the "Интернет постоянный" checkbox, so operators of points
    // without permanent internet can update the board on demand.
    private async Task SendToBoardAsync()
    {
        SetSendStatus("Отправка на табло…", false);
        var (ok, msg) = await LedControlClient.SendToBoardAsync(_cfg.Urls);
        SetSendStatus((ok ? "✓ " : "✗ ") + msg, !ok);
    }

    private void SetSendStatus(string text, bool error)
    {
        _lblSendStatus.Text = text;
        _lblSendStatus.ForeColor = error ? Color.Salmon : UITheme.Accent;
    }

    // ─── Tab: Сервис ──────────────────────────────────────────────────────────

    private TabPage BuildServiceTab()
    {
        var tab = new TabPage("Сервис") { Padding = new Padding(12) };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 200));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _cmbRunMode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
        _cmbRunMode.Items.AddRange(["RenderOnly", "Uploader", "Full"]);

        _cmbPublishMode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
        _cmbPublishMode.Items.AddRange(["WifiFtp", "WifiRelay"]);

        _numPoll = MakeNumeric(5, 3600);
        _numRatesFetch = MakeNumeric(1, 120);

        _chkLayout = new CheckBox { Text = "Режим тестирования (не отправлять на табло)", AutoSize = true };
        _chkAutoSend = new CheckBox { Text = "Автоотправка на табло", AutoSize = true };
        _chkSkipUnchanged = new CheckBox { Text = "Пропускать без изменений", AutoSize = true };
        _chkForceCompose = new CheckBox { Text = "Перерисовывать каждый цикл", AutoSize = true };

        AddRow(layout, "Режим работы:", _cmbRunMode);
        AddRow(layout, "Режим публикации:", _cmbPublishMode);
        AddRow(layout, "Интервал опроса (сек):", _numPoll);
        AddRow(layout, "Обновление курсов (мин):", _numRatesFetch);
        AddRow(layout, "", _chkLayout);
        AddRow(layout, "", _chkAutoSend);
        AddRow(layout, "", _chkSkipUnchanged);
        AddRow(layout, "", _chkForceCompose);

        tab.Controls.Add(layout);
        return tab;
    }

    // ─── Tab: Подключение ─────────────────────────────────────────────────────

    private TabPage BuildConnectionTab()
    {
        var tab = new TabPage("Подключение") { Padding = new Padding(12) };
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        var layout = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, AutoSize = true };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 200));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _txtIp = new TextBox { Width = 160 };
        _numCtrlPort = MakeNumeric(1, 65535);
        _numDevice = MakeNumeric(1000, 65535);
        _cmbModel = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 280 };
        foreach (var m in ControllerCatalog.Models)
            _cmbModel.Items.Add(ControllerCatalog.DisplayName(m));
        _cmbModel.Items.Add(ControllerCatalog.CustomLabel);
        _cmbModel.SelectedIndexChanged += (_, _) => OnModelSelected();
        _cmbConnMode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 280 };
        foreach (var m in ConnectionModeCatalog.Modes)
            _cmbConnMode.Items.Add(m.Label);
        _cmbFamily = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 280 };
        foreach (var f in ControllerFamilyCatalog.Families)
            _cmbFamily.Items.Add(f.Label);
        _cmbTransport = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 280 };
        _cmbTransport.Items.AddRange(["TCP (HDPlayer — карта по UDP/IP)", "FTP (загрузка по IP)"]);
        _cmbTransport.SelectedIndexChanged += (_, _) => ApplyFamilyVisibility();
        _txtSsid = new TextBox { Width = 160 };
        _txtHuiduCardIp = new TextBox { Width = 160 };
        _txtHuiduDeviceId = new TextBox { Width = 160 };
        _numHuiduListenPort = MakeNumeric(1, 65535);
        _numHuiduUdpPort = MakeNumeric(1, 65535);
        _numHuiduCardPort = MakeNumeric(1, 65535);
        _cmbHuiduModel = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 280 };
        foreach (var m in HuiduControllerCatalog.Models)
            _cmbHuiduModel.Items.Add(HuiduControllerCatalog.DisplayName(m));
        _cmbHuiduModel.Items.Add(HuiduControllerCatalog.CustomLabel);
        _cmbHuiduModel.SelectedIndexChanged += (_, _) => OnHuiduModelSelected();
        _txtFtpUser = new TextBox { Width = 160 };
        _txtFtpPass = new TextBox { Width = 160, PasswordChar = '●' };
        _numFtpPort = MakeNumeric(1, 65535);
        _chkTls = new CheckBox { Text = "Использовать TLS", AutoSize = true };
        _txtRatesUrl = new TextBox { Width = 350 };
        _txtReloadUrl = new TextBox { Width = 350 };
        _txtApiPort = new TextBox { Width = 160 };

        // ── Connection test ────────────────────────────────────────────────
        var sep0 = new Label { Text = "───── ПРОВЕРКА СОЕДИНЕНИЯ ─────", ForeColor = Color.Gray, Height = 20 };
        layout.Controls.Add(sep0, 0, layout.RowCount);
        layout.SetColumnSpan(sep0, 2);

        var testRow = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 2, 0, 2) };
        var btnTest = MakeButton("🔍 Проверить соединение", Color.FromArgb(0, 102, 204), Color.White);
        btnTest.Width = 200;
        btnTest.Click += (_, _) => _ = TestConnectionAsync();
        testRow.Controls.Add(btnTest);

        _btnHuiduDiag = MakeButton("🔬 Диагностика Huidu", Color.FromArgb(120, 80, 160), Color.White);
        _btnHuiduDiag.Width = 200;
        _btnHuiduDiag.Click += (_, _) => _ = RunHuiduDiagnosticAsync();
        testRow.Controls.Add(_btnHuiduDiag);

        layout.Controls.Add(testRow, 0, layout.RowCount);
        layout.SetColumnSpan(testRow, 2);

        _lblConnTestResult = new Label { Text = "", AutoSize = true, ForeColor = UITheme.TextDim, Margin = new Padding(2, 0, 0, 6) };
        layout.Controls.Add(_lblConnTestResult, 0, layout.RowCount);
        layout.SetColumnSpan(_lblConnTestResult, 2);

        // ── Power control (operational, calls the local REST API) ──────────
        var sep_pwr = new Label { Text = "───── ПИТАНИЕ ТАБЛО ─────", ForeColor = Color.Gray, Height = 20 };
        layout.Controls.Add(sep_pwr, 0, layout.RowCount);
        layout.SetColumnSpan(sep_pwr, 2);

        var powerRow = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 2, 0, 2) };
        var btnOn = MakeButton("🔆 Включить", Color.FromArgb(0, 168, 132), Color.White);
        btnOn.Width = 120;
        var btnOff = MakeButton("⏻ Выключить", Color.FromArgb(170, 60, 70), Color.White);
        btnOff.Width = 120;
        var btnReboot = MakeButton("↻ Перезагрузить", UITheme.Input, UITheme.Text);
        btnReboot.Width = 140;
        btnOn.Click += (_, _) => _ = PowerAsync(true);
        btnOff.Click += (_, _) => _ = PowerAsync(false);
        btnReboot.Click += (_, _) => _ = RebootBoardAsync();
        powerRow.Controls.AddRange([btnOn, btnOff, btnReboot]);
        layout.Controls.Add(powerRow, 0, layout.RowCount);
        layout.SetColumnSpan(powerRow, 2);

        _lblPowerStatus = new Label { Text = "", AutoSize = true, ForeColor = UITheme.TextDim, Margin = new Padding(2, 0, 0, 4) };
        layout.Controls.Add(_lblPowerStatus, 0, layout.RowCount);
        layout.SetColumnSpan(_lblPowerStatus, 2);

        // ── Controller family ──────────────────────────────────────────────
        var grpCtrl = MakeGroupBox("Контроллер LED", layout);
        AddRow(layout, "Семейство (подключение):", _cmbFamily);

        // Transport (Huidu only) + board Wi-Fi SSID
        _lblTransport = AddRow(layout, "Транспорт отправки:", _cmbTransport);
        _lblSsid = AddRow(layout, "SSID Wi-Fi табло:", _txtSsid);

        // IP field — controller IP for Onbon, board FTP IP for Huidu+FTP (label set in ApplyFamilyVisibility)
        _lblIp = AddRow(layout, "IP-адрес контроллера:", _txtIp);
        _lblCtrlPort = AddRow(layout, "Порт контроллера:", _numCtrlPort);
        _lblModel = AddRow(layout, "Модель контроллера:", _cmbModel);
        _lblDevice = AddRow(layout, "Код устройства:", _numDevice);
        _lblConnMode = AddRow(layout, "Тип подключения:", _cmbConnMode);

        // Huidu fields (hidden for Onbon)
        _lblHuiduNote = new Label
        {
            Text = "Huidu (HDPlayer): сервис подключается к карте по TCP.\n" +
                   "IP карты: вручную или авто-поиск по UDP. В режиме AP карты её адрес обычно 192.168.43.1.\n" +
                   "UDP порт: для A-серии (A3L и др.) — 10001, для C-серии (C16L и др.) — 9527.\n" +
                   "TCP порт карты: стандартный 10001 (HDPlayer).",
            ForeColor = Color.FromArgb(140, 200, 255),
            AutoSize = true,
            Margin = new Padding(0, 4, 0, 4),
        };
        layout.Controls.Add(_lblHuiduNote, 0, layout.RowCount);
        layout.SetColumnSpan(_lblHuiduNote, 2);
        layout.RowCount++;

        _lblHuiduModel = AddRow(layout, "Модель табло (Huidu):", _cmbHuiduModel);
        _lblHuiduCardIp = AddRow(layout, "IP карты (прямое / авто):", _txtHuiduCardIp);
        _lblHuiduDeviceId = AddRow(layout, "ID карты (серийный, необяз.):", _txtHuiduDeviceId);
        _lblHuiduUdpPort = AddRow(layout, "UDP порт (поиск карты):", _numHuiduUdpPort);
        _lblHuiduCardPort = AddRow(layout, "TCP порт карты:", _numHuiduCardPort);
        _lblHuiduListenPort = AddRow(layout, "Порт прослушивания (этот ПК):", _numHuiduListenPort);

        _cmbFamily.SelectedIndexChanged += (_, _) => ApplyFamilyVisibility();

        // ── API ──────────────────────────────────────────────────────────
        var sep2 = new Label { Text = "───── API ─────", ForeColor = Color.Gray, Height = 20 };
        layout.Controls.Add(sep2, 0, layout.RowCount);
        layout.SetColumnSpan(sep2, 2);

        AddRow(layout, "URL API курсов:", _txtRatesUrl);
        AddRow(layout, "URL перезагрузки:", _txtReloadUrl);
        AddRow(layout, "Адрес сервиса (URL):", _txtApiPort);

        scroll.Controls.Add(layout);
        tab.Controls.Add(scroll);
        return tab;
    }

    private async Task TestConnectionAsync()
    {
        _lblConnTestResult.Text = "Проверяю…";
        _lblConnTestResult.ForeColor = Color.LightGray;

        var (isOnline, details) = await LedControlClient.CheckConnectionAsync(_cfg.Urls);

        _lblConnTestResult.Text = (isOnline ? "✓ Онлайн  " : "✗ Оффлайн  ") + details;
        _lblConnTestResult.ForeColor = isOnline ? UITheme.Accent : Color.Salmon;
    }

    // Runs the read-only Huidu card probe (HuiduDiagnostics) on a background thread,
    // streams progress into the result label, then opens the saved report file.
    private async Task RunHuiduDiagnosticAsync()
    {
        _btnHuiduDiag.Enabled = false;
        _lblConnTestResult.ForeColor = Color.LightGray;
        _lblConnTestResult.Text = "Идёт диагностика Huidu (~30 c)…";

        void Live(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            if (IsHandleCreated)
                BeginInvoke(() => { _lblConnTestResult.Text = line.Trim(); });
        }

        try
        {
            var path = await Task.Run(() => Services.HuiduDiagnostics.RunToReportAsync(Live));
            _lblConnTestResult.ForeColor = UITheme.Accent;
            _lblConnTestResult.Text = $"✓ Диагностика готова: {path}";
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); }
            catch { /* ignore */ }
        }
        catch (Exception ex)
        {
            _lblConnTestResult.ForeColor = Color.Salmon;
            _lblConnTestResult.Text = $"Ошибка диагностики: {ex.Message}";
        }
        finally
        {
            _btnHuiduDiag.Enabled = true;
        }
    }

    // When a known model is picked, auto-fill the device code and lock the field.
    // "Другой (вручную)" unlocks the code field for custom controllers.
    private void OnModelSelected()
    {
        if (_suppressSync) return;
        var idx = _cmbModel.SelectedIndex;
        if (idx >= 0 && idx < ControllerCatalog.Models.Count)
        {
            var model = ControllerCatalog.Models[idx];
            _numDevice.Value = Math.Clamp(model.DeviceType, (int)_numDevice.Minimum, (int)_numDevice.Maximum);
            _numDevice.Enabled = false;
        }
        else
        {
            // Custom
            _numDevice.Enabled = true;
        }
    }

    // Selects the dropdown entry matching the current device code (or "Другой").
    private void SyncModelFromDeviceType()
    {
        _suppressSync = true;
        var model = ControllerCatalog.FindByDeviceType(_cfg.DeviceType);
        if (model != null)
        {
            _cmbModel.SelectedIndex = ControllerCatalog.Models.ToList().IndexOf(model);
            _numDevice.Enabled = false;
        }
        else
        {
            _cmbModel.SelectedIndex = _cmbModel.Items.Count - 1; // "Другой"
            _numDevice.Enabled = true;
        }
        _suppressSync = false;
    }

    // When a Huidu model with a known default size is picked, offer to pre-fill the
    // panel size. Huidu cards have no device code, so the model is informational; the
    // resolution depends on the physical panel, hence only a confirmed prompt.
    private void OnHuiduModelSelected()
    {
        if (_suppressSync) return;
        var idx = _cmbHuiduModel.SelectedIndex;
        if (idx < 0 || idx >= HuiduControllerCatalog.Models.Count) return; // "Другая модель"
        var model = HuiduControllerCatalog.Models[idx];

        // Auto-fill UDP discovery port based on the selected model.
        if (_numHuiduUdpPort != null)
            _numHuiduUdpPort.Value = Math.Clamp(model.DefaultUdpPort, 1, 65535);

        if (model.DefaultWidth <= 0 || model.DefaultHeight <= 0) return;
        if (model.DefaultWidth == (int)_numW.Value && model.DefaultHeight == (int)_numH.Value) return;

        var ok = MessageBox.Show(
            $"Для {model.Name} известен размер по умолчанию {model.DefaultWidth}×{model.DefaultHeight}.\n" +
            "Подставить его как размер табло? (всё равно проверьте по физической панели)",
            "Размер табло", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (ok == DialogResult.Yes)
        {
            _numW.Value = Math.Clamp(model.DefaultWidth, (int)_numW.Minimum, (int)_numW.Maximum);
            _numH.Value = Math.Clamp(model.DefaultHeight, (int)_numH.Minimum, (int)_numH.Maximum);
        }
    }

    // Selects the Huidu model dropdown entry matching the saved model name.
    private void SyncHuiduModelFromName()
    {
        _suppressSync = true;
        var model = HuiduControllerCatalog.FindByName(_cfg.HuiduModel);
        _cmbHuiduModel.SelectedIndex = model != null
            ? HuiduControllerCatalog.Models.ToList().IndexOf(model)
            : _cmbHuiduModel.Items.Count - 1; // "Другая модель (вручную)"
        _suppressSync = false;
    }

    // ─── Board power control ──────────────────────────────────────────────────

    private async Task PowerAsync(bool on)
    {
        SetPowerStatus(on ? "Включаю табло…" : "Выключаю табло…", false);
        var (ok, msg) = await LedControlClient.SetPowerAsync(_cfg.Urls, on);
        SetPowerStatus((ok ? "✓ " : "✗ ") + msg, !ok);
    }

    private async Task RebootBoardAsync()
    {
        if (MessageBox.Show("Перезагрузить контроллер табло?", "Перезагрузка",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;
        SetPowerStatus("Перезагружаю контроллер…", false);
        var (ok, msg) = await LedControlClient.RebootAsync(_cfg.Urls);
        SetPowerStatus((ok ? "✓ " : "✗ ") + msg, !ok);
    }

    private void SetPowerStatus(string text, bool error)
    {
        _lblPowerStatus.Text = text;
        _lblPowerStatus.ForeColor = error ? Color.Salmon : UITheme.Accent;
    }

    // ─── Tab: Дополнительно ───────────────────────────────────────────────────

    private TabPage BuildAdvancedTab()
    {
        var tab = new TabPage("Дополнительно") { Padding = new Padding(12) };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _chkOnbonEnabled = new CheckBox { Text = "SDK включён (Windows)", AutoSize = true };
        _txtOnbonUser = new TextBox { Width = 140 };
        _txtOnbonPass = new TextBox { Width = 140, PasswordChar = '●' };
        _numRetry = MakeNumeric(0, 10);
        _numRetryMs = MakeNumeric(100, 30000);
        _numConnTimeout = MakeNumeric(100, 30000);
        _numOnbonPoll = MakeNumeric(5, 3600);
        _chkIsolated = new CheckBox { Text = "Изолированный процесс SDK", AutoSize = true };
        _chkSkipDup = new CheckBox { Text = "Пропускать дублирующиеся изображения", AutoSize = true };
        _chkRejectSize = new CheckBox { Text = "Отклонять несовпадение размера", AutoSize = true };
        _chkWifiOnly = new CheckBox { Text = "Только через Wi-Fi адаптер", AutoSize = true };
        _chkPrivate = new CheckBox { Text = "Требовать приватный IP", AutoSize = true };

        AddRow(layout, "", _chkOnbonEnabled);
        AddRow(layout, "Логин SDK:", _txtOnbonUser);
        AddRow(layout, "Пароль SDK:", _txtOnbonPass);
        AddRow(layout, "Повторных попыток:", _numRetry);
        AddRow(layout, "Задержка попытки (мс):", _numRetryMs);
        AddRow(layout, "Таймаут подключения (мс):", _numConnTimeout);
        AddRow(layout, "Интервал опроса SDK (сек):", _numOnbonPoll);
        AddRow(layout, "", _chkIsolated);
        AddRow(layout, "", _chkSkipDup);
        AddRow(layout, "", _chkRejectSize);
        AddRow(layout, "", _chkWifiOnly);
        AddRow(layout, "", _chkPrivate);

        // ─── Telegram notifications ────────────────────────────────────────
        // Token / Chat ID / Enabled are configured by hand in appsettings.json (section
        // "Telegram"). Here we only offer a test button that reads those saved values.
        _btnTelegramTest = MakeButton("✈ Тест Telegram", Color.FromArgb(0, 136, 204), Color.White);
        _btnTelegramTest.Width = 200;
        _btnTelegramTest.Click += async (_, _) => await SendTelegramTestAsync();

        AddRow(layout, "", new Label
        {
            Text = "— Telegram-уведомления —",
            AutoSize = true,
            ForeColor = UITheme.Accent,
            Padding = new Padding(0, 10, 0, 2),
        });
        AddRow(layout, "", new Label
        {
            Text = "Token, Chat ID и включение задаются в appsettings.json (раздел \"Telegram\").",
            AutoSize = true,
            ForeColor = UITheme.TextDim,
        });
        AddRow(layout, "", _btnTelegramTest);

        // ─── Обновление приложения ─────────────────────────────────────────
        AddRow(layout, "", new Label
        {
            Text = "— Обновление приложения —",
            AutoSize = true,
            ForeColor = UITheme.Accent,
            Padding = new Padding(0, 12, 0, 2),
        });
        AddRow(layout, "", new Label
        {
            Text = $"Текущая версия: v{UpdateService.CurrentVersion.ToString(3)}. " +
                   "Обновление сохраняет настройки и разметку.",
            AutoSize = true,
            ForeColor = UITheme.TextDim,
        });

        var btnCheckUpdate = MakeButton("🔄 Проверить обновления", UITheme.Accent2, Color.White);
        btnCheckUpdate.Width = 220;
        btnCheckUpdate.Click += (_, _) =>
        {
            using var dlg = new UpdateForm(_onExitForUpdate);
            dlg.ShowDialog(this);
        };
        AddRow(layout, "", btnCheckUpdate);

        tab.Controls.Add(layout);
        return tab;
    }

    /// <summary>
    /// Sends a test message using the token/chat id saved in appsettings.json, so the admin
    /// can verify Telegram works. Does not require a restart.
    /// </summary>
    private async Task SendTelegramTestAsync()
    {
        // Read freshly from appsettings.json so the test uses the hand-edited values.
        var cfg = AppSettingsManager.Load();
        var token = cfg.TelegramBotToken.Trim();
        var chatId = cfg.TelegramChatId.Trim();
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(chatId))
        {
            MessageBox.Show(
                "В appsettings.json (раздел \"Telegram\") не заполнены BotToken и/или ChatId.",
                "Telegram", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _btnTelegramTest.Enabled = false;
        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var url = $"https://api.telegram.org/bot{token}/sendMessage";
            var payload = new { chat_id = chatId, text = $"✅ eCash Tablo (Huidu): тестовое сообщение ({_cfg.ActivePointId})." };
            using var resp = await http.PostAsJsonAsync(url, payload);
            if (resp.IsSuccessStatusCode)
                MessageBox.Show("Сообщение отправлено. Проверьте Telegram.", "Telegram",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            else
            {
                var body = await resp.Content.ReadAsStringAsync();
                MessageBox.Show($"Не удалось отправить: HTTP {(int)resp.StatusCode}.\n{body}", "Telegram",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Ошибка отправки: {ex.Message}", "Telegram",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _btnTelegramTest.Enabled = true;
        }
    }

    // ─── Tab: Журнал ──────────────────────────────────────────────────────────

    private TabPage BuildLogTab()
    {
        var tab = new TabPage("Журнал") { Padding = new Padding(8) };

        var toolbar = new Panel { Dock = DockStyle.Top, Height = 32 };
        var btnRefresh = MakeButton("Обновить журнал", Color.FromArgb(0, 102, 204), Color.White);
        btnRefresh.Width = 160;
        btnRefresh.Location = new Point(4, 4);
        btnRefresh.Click += (_, _) => _ = RefreshLogAsync();
        toolbar.Controls.Add(btnRefresh);

        _rtbLog = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BackColor = Color.FromArgb(15, 15, 15),
            ForeColor = Color.LightGreen,
            Font = new Font("Consolas", 8.5f),
            ScrollBars = RichTextBoxScrollBars.Both,
            WordWrap = false,
        };

        tab.Controls.Add(_rtbLog);
        tab.Controls.Add(toolbar);
        return tab;
    }

    private async Task RefreshLogAsync()
    {
        try
        {
            // Determine current API port from Urls setting
            var uri = _cfg.Urls.TrimEnd('/') + "/api/led/logs?count=200";
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var json = await http.GetStringAsync(uri);
            _rtbLog.Text = json;
        }
        catch (Exception ex)
        {
            _rtbLog.Text = $"Не удалось загрузить журнал:\n{ex.Message}\n\nУбедитесь, что сервис запущен.";
        }
    }

    // ─── Tab: Вики ────────────────────────────────────────────────────────────

    // A built-in help/wiki: a navigation list of section headings on the left and
    // a formatted text pane on the right. Selecting a heading jumps to that block.
    private TabPage BuildWikiTab()
    {
        var tab = new TabPage("📖 Вики") { Padding = new Padding(8) };

        var navHost = new Panel { Dock = DockStyle.Left, Width = 190 };
        var navTitle = new Label
        {
            Text = "РАЗДЕЛЫ",
            Dock = DockStyle.Top,
            Height = 22,
            ForeColor = UITheme.Accent,
            Font = BoldFont,
        };
        _lstWikiNav = new ListBox
        {
            Dock = DockStyle.Fill,
            Font = UIFont,
            BorderStyle = BorderStyle.FixedSingle,
            IntegralHeight = false,
        };
        foreach (var section in WikiSections)
            _lstWikiNav.Items.Add(section.Title);
        _lstWikiNav.SelectedIndexChanged += (_, _) => ShowWikiSection(_lstWikiNav.SelectedIndex);
        navHost.Controls.Add(_lstWikiNav);
        navHost.Controls.Add(navTitle);

        var spacer = new Panel { Dock = DockStyle.Left, Width = 8 };

        _rtbWiki = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = UITheme.Panel,
            ForeColor = UITheme.Text,
            Font = new Font("Segoe UI", 9.5f),
            ScrollBars = RichTextBoxScrollBars.Vertical,
            DetectUrls = false,
        };

        tab.Controls.Add(_rtbWiki);
        tab.Controls.Add(spacer);
        tab.Controls.Add(navHost);

        if (_lstWikiNav.Items.Count > 0)
            _lstWikiNav.SelectedIndex = 0;
        return tab;
    }

    private void ShowWikiSection(int index)
    {
        if (index < 0 || index >= WikiSections.Length) return;
        RenderWikiMarkup(_rtbWiki, WikiSections[index].Title, WikiSections[index].Body);
    }

    // Tiny markup renderer:
    //   "## "  → sub-heading (bold accent)
    //   "- "   → bullet
    //   blank  → spacing
    private static void RenderWikiMarkup(RichTextBox rtb, string title, string body)
    {
        rtb.Clear();

        var headFont = new Font("Segoe UI Semibold", 14f, FontStyle.Bold);
        var subFont = new Font("Segoe UI Semibold", 10.5f, FontStyle.Bold);
        var bodyFont = new Font("Segoe UI", 9.5f);

        void Append(string text, Font font, Color color)
        {
            rtb.SelectionStart = rtb.TextLength;
            rtb.SelectionLength = 0;
            rtb.SelectionFont = font;
            rtb.SelectionColor = color;
            rtb.AppendText(text);
        }

        Append(title + "\n\n", headFont, UITheme.Accent);

        foreach (var raw in body.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0)
            {
                Append("\n", bodyFont, UITheme.Text);
            }
            else if (line.StartsWith("## "))
            {
                Append("\n" + line[3..] + "\n", subFont, UITheme.Accent2);
            }
            else if (line.StartsWith("- "))
            {
                Append("   •  " + line[2..] + "\n", bodyFont, UITheme.Text);
            }
            else
            {
                Append(line + "\n", bodyFont, UITheme.Text);
            }
        }

        rtb.SelectionStart = 0;
        rtb.ScrollToCaret();
    }

    private readonly record struct WikiSection(string Title, string Body);

    private static readonly WikiSection[] WikiSections =
    [
        new("Обзор",
            """
            eCash Tablo (Huidu) — программа для автоматического вывода курсов валют на
            светодиодное табло на контроллерах Huidu (протокол HDPlayer).

            Приложение живёт в системном трее (значок у часов). В фоне работает
            сервис, который по расписанию берёт свежие курсы, рисует картинку по
            вашей разметке и отправляет её на карту табло по сети.

            ## Как это работает (поток данных)
            - 1. Курсы валют берутся из API (или из файла rates.json).
            - 2. По разметке рисуется изображение final.jpg.
            - 3. Изображение отправляется на карту Huidu (HDPlayer, порт карты).
            - 4. Цикл повторяется через заданный интервал.

            ## Особенность Huidu (сеть-«остров»)
            Карта Huidu обычно поднимает свою точку доступа Wi-Fi. Программа сама
            определяет, что ПК подключён к сети табло, находит карту по UDP-поиску
            (порт 9527) и при необходимости подставляет её IP — отдельно настраивать
            адрес чаще всего не нужно.

            ## Где что настраивается
            Каждая вкладка вверху отвечает за свой этап: «Валюты» и «Заголовки» —
            содержимое; «Размер табло» и «Дизайн» — внешний вид; «Сервис»,
            «Подключение», «Дополнительно» — работа сервиса и связь с табло;
            «Журнал» — что происходит сейчас.
            """),

        new("С чего начать (по шагам)",
            """
            Минимальная настройка нового табло — сверху вниз по вкладкам:

            ## Шаг 1 — Валюты
            Выберите нужные валюты из списка слева и кнопкой «→ Доб» добавьте их в
            колонку. Порядок в списке = порядок на табло (меняется стрелками ▲▼).

            ## Шаг 2 — Заголовки
            Задайте подписи «Покупаем» и «Продаём» (можно на нескольких языках —
            по строке на язык).

            ## Шаг 3 — Размер табло
            Укажите ширину и высоту в пикселях — ровно как у физического табло.

            ## Шаг 4 — Дизайн
            Нажмите «Раскидка по размеру», поправьте блоки мышью при необходимости
            и нажмите «Обновить превью».

            ## Шаг 5 — Подключение
            Подключите компьютер к сети Wi-Fi табло. IP карты обычно определяется
            автоматически; при необходимости укажите его вручную и нажмите
            «Проверить соединение».

            ## Шаг 6 — Сохранить
            Внизу окна нажмите «Сохранить и перезапустить».
            """),

        new("Активная точка",
            """
            Выпадающий список вверху окна.

            Одно приложение может обслуживать несколько точек (несколько табло).
            Каждая точка хранит свои настройки и свою разметку отдельно.

            При переключении точки всё окно перезагружает её конфиг — вы
            редактируете именно выбранную точку. Сохранение тоже относится только
            к активной точке.
            """),

        new("Валюты",
            """
            Что показывать на табло и в каком порядке.

            ## Колонки
            Поле «Колонок» (1–3) задаёт число колонок на табло. Слева — все
            доступные валюты, справа — колонки с выбранными валютами.

            ## Как добавить/убрать
            - Выделите валюту слева, выберите номер колонки в «Добавить в колонку»
              и нажмите «→ Доб».
            - Чтобы убрать — выделите валюту в колонке и нажмите «← Уб».
            - Порядок внутри колонки меняется стрелками ▲▼ (это порядок строк на
              табло).
            """),

        new("Заголовки",
            """
            Подписи столбцов курса для каждой колонки: «Покупаем» и «Продаём».

            Для каждой колонки — два поля. В каждом поле можно указать несколько
            строк — по строке на язык (например: казахский / русский / английский).
            На табло они выводятся над соответствующим столбцом.

            Число колонок здесь совпадает с числом на вкладке «Валюты».
            """),

        new("Размер табло",
            """
            Размер холста (изображения) в пикселях.

            Ширина и высота должны точно совпадать с физическим разрешением табло
            и с настройками карты. Если размер не совпадает, картинка отобразится
            неправильно или будет отклонена картой.
            """),

        new("Дизайн",
            """
            Визуальный редактор разметки и предпросмотр.

            ## Редактор (слева)
            Перетаскивайте блоки мышью (лого, флаги, коды, столбцы курса).
            Уголок выделенного блока — изменение размера.

            ## Кнопки (справа)
            - «Раскидка по размеру» — автоматически расставляет блоки под текущий
              размер табло и число валют.
            - «Обновить превью» — рисует реальное изображение по текущим курсам.
            - «Загрузить курсы из API» — тянет свежие курсы, чтобы превью было
              актуальным.
            - «Авто-обновление при правках» — превью перерисовывается само при
              изменениях.

            ## Шрифты, флаг/лого, строки
            Числовые поля точно настраивают размеры шрифтов (курс, код, заголовок,
            стрелка), размеры флага и лого, старт и высоту строк.

            ## Отправить на табло
            Кнопка «Отправить на табло» вручную отправляет текущую картинку на
            карту. Нужна для точек без постоянного интернета: снимите галочку
            «Интернет постоянный (автоотправка)» и отправляйте вручную.

            Важно: превью рисуется во временные файлы и НЕ трогает табло, пока вы
            не нажмёте «Сохранить» или «Отправить на табло».
            """),

        new("Сервис",
            """
            Как работает фоновый сервис.

            ## Режим работы
            - RenderOnly — только рисует картинку, на табло не отправляет.
            - Uploader — только отправляет уже готовую картинку.
            - Full — полный цикл: рисует и отправляет.

            ## Интервалы
            - «Интервал опроса» — как часто выполняется рабочий цикл (сек).
            - «Обновление курсов» — как часто тянутся свежие курсы (мин).

            ## Галочки
            - «Режим тестирования» — ничего не отправлять на табло.
            - «Автоотправка на табло» — отправлять без ручного подтверждения.
            - «Пропускать без изменений» — не слать кадр, если он не изменился.
            - «Перерисовывать каждый цикл» — всегда заново рисовать картинку.
            """),

        new("Подключение",
            """
            Связь с картой табло Huidu.

            ## Проверка соединения
            Кнопка «Проверить соединение» опрашивает карту и показывает, доступна
            она или нет.

            ## IP карты (UDP, необязательно)
            Обычно адрес карты определяется автоматически: программа ищет карту по
            UDP-поиску в сети табло и подставляет найденный IP. Поле «IP карты»
            нужно заполнять только если автоопределение не срабатывает.

            ## Порт карты
            TCP-порт карты Huidu (HDPlayer). Менять только если он нестандартный.

            ## API
            - URL API курсов — откуда брать курсы.
            - Порт REST API — локальный адрес сервиса (для превью, журнала,
              управления).

            ## Если связи нет
            Если после отправки появляется окно «Курсы на табло не обновляются» —
            подключите компьютер к сети Wi-Fi табло. Окно само закроется, как
            только курс снова уйдёт на карту.
            """),

        new("Дополнительно",
            """
            Тонкие настройки надёжности и проверок.

            ## Надёжность
            - Повторных попыток, задержка попытки, таймаут подключения.
            - «Пропускать дублирующиеся изображения».
            - «Отклонять несовпадение размера» — защита от неверного разрешения.
            - «Только через Wi-Fi адаптер» / «Требовать приватный IP» — ограничивают
              отправку безопасной локальной сетью табло.
            """),

        new("Журнал",
            """
            Что сервис делает прямо сейчас.

            Кнопка «Обновить журнал» загружает последние записи лога через
            локальный REST API сервиса. Здесь видно циклы рендера, отправку на
            табло, автоопределение карты ([BoardLink]) и ошибки связи.

            Если журнал не загружается — убедитесь, что сервис запущен (значок в
            трее) и порт REST API на вкладке «Подключение» указан верно.
            """),

        new("Сохранение и перезапуск",
            """
            Кнопки внизу окна.

            - «Сохранить и перезапустить» — сохраняет настройки и перезапускает
              сервис, чтобы изменения вступили в силу немедленно.
            - «Сохранить» — только сохраняет; изменения применятся после
              перезапуска сервиса.
            - «Закрыть» — прячет окно (приложение продолжает работать в трее).

            Большинство настроек требуют перезапуска сервиса, поэтому обычно
            используйте «Сохранить и перезапустить».
            """),
    ];

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private void PopulateForm()
    {
        // Point
        var idx = _cmbPoint.Items.IndexOf(_cfg.ActivePointId);
        if (idx >= 0) _cmbPoint.SelectedIndex = idx;
        else if (_cmbPoint.Items.Count > 0) _cmbPoint.SelectedIndex = 0;

        // Currencies
        _cfg.NormalizeColumns();
        _numColumnCount.Value = Math.Clamp(_cfg.ColumnCount, 1, MaxColumns);
        ApplyColumnCount(_cfg.ColumnCount);
        for (int i = 0; i < MaxColumns; i++)
        {
            _lstColumns[i].Items.Clear();
            if (i < _cfg.Columns.Count)
                foreach (var code in _cfg.Columns[i])
                    _lstColumns[i].Items.Add(FormatCurrency(code));
        }

        // Header labels (per column)
        for (int i = 0; i < MaxColumns; i++)
        {
            _txtBuyLabels[i].Text = string.Join(Environment.NewLine,
                i < _cfg.ColumnBuyLabels.Count ? _cfg.ColumnBuyLabels[i] : AppConfig.DefaultBuyLabels);
            _txtSellLabels[i].Text = string.Join(Environment.NewLine,
                i < _cfg.ColumnSellLabels.Count ? _cfg.ColumnSellLabels[i] : AppConfig.DefaultSellLabels);
        }

        // Per-column X placement
        _suppressSync = true;
        _chkManualColX.Checked = _cfg.ColumnX.Any(x => x.HasValue);
        for (int i = 0; i < MaxColumns; i++)
            _numColX[i].Value = Math.Clamp(_cfg.ColumnX.ElementAtOrDefault(i) ?? 0, 0, 4096);
        _suppressSync = false;
        ApplyColManualEnabled();

        // Display
        _numW.Value = Math.Clamp(_cfg.CanvasWidth, 8, 4096);
        _numH.Value = Math.Clamp(_cfg.CanvasHeight, 8, 4096);

        // Service
        _cmbRunMode.SelectedItem = _cfg.RunMode;
        if (_cmbRunMode.SelectedIndex < 0) _cmbRunMode.SelectedIndex = 0;
        _cmbPublishMode.SelectedItem = _cfg.PublishMode;
        if (_cmbPublishMode.SelectedIndex < 0) _cmbPublishMode.SelectedIndex = 0;
        _numPoll.Value = Math.Clamp(_cfg.PollSeconds, 5, 3600);
        _numRatesFetch.Value = Math.Clamp(_cfg.RatesFetchIntervalMinutes, 1, 120);
        _chkLayout.Checked = _cfg.LayoutTestMode;
        _chkPermanentInternet.Checked = _cfg.PermanentInternet;
        _chkAutoSend.Checked = _cfg.AutoSend;
        _chkSkipUnchanged.Checked = _cfg.SkipIfUnchanged;
        _chkForceCompose.Checked = _cfg.ForceComposeEveryPoll;

        // Connection
        _cmbFamily.SelectedIndex = ControllerFamilyCatalog.IndexOfKey(_cfg.ControllerFamily);
        _cmbTransport.SelectedIndex =
            string.Equals(_cfg.BoardTransport, "Ftp", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        _txtSsid.Text = _cfg.WifiSsid;
        ApplyFamilyVisibility();
        _txtIp.Text = _cfg.ControllerIp;
        _numCtrlPort.Value = Math.Clamp(_cfg.ControllerPort, 1, 65535);
        _numHuiduListenPort.Value = Math.Clamp(_cfg.HuiduListenPort, 1, 65535);
        _numHuiduUdpPort.Value = Math.Clamp(_cfg.HuiduUdpDiscoveryPort, 1, 65535);
        _numHuiduCardPort.Value = Math.Clamp(_cfg.HuiduCardPort, 1, 65535);
        _txtHuiduCardIp.Text = _cfg.HuiduCardIp;
        _txtHuiduDeviceId.Text = _cfg.HuiduDeviceId;
        SyncHuiduModelFromName();
        _numDevice.Value = Math.Clamp(_cfg.DeviceType, 1000, 65535);
        SyncModelFromDeviceType();
        _cmbConnMode.SelectedIndex = ConnectionModeCatalog.Modes.ToList()
            .IndexOf(ConnectionModeCatalog.FindByKey(_cfg.ConnectionMode));
        _txtFtpUser.Text = _cfg.FtpUser;
        _txtFtpPass.Text = _cfg.FtpPassword;
        _numFtpPort.Value = Math.Clamp(_cfg.FtpPort, 1, 65535);
        _chkTls.Checked = _cfg.UseTls;
        _txtRatesUrl.Text = _cfg.RatesApiUrl;
        _txtReloadUrl.Text = _cfg.ControllerReloadUrl;
        _txtApiPort.Text = _cfg.Urls;

        // Advanced
        _chkOnbonEnabled.Checked = _cfg.OnbonEnabled;
        _txtOnbonUser.Text = _cfg.OnbonUserName;
        _txtOnbonPass.Text = _cfg.OnbonPassword;
        _numRetry.Value = Math.Clamp(_cfg.RetryCount, 0, 10);
        _numRetryMs.Value = Math.Clamp(_cfg.RetryDelayMs, 100, 30000);
        _numConnTimeout.Value = Math.Clamp(_cfg.ConnectionTimeoutMs, 100, 30000);
        _numOnbonPoll.Value = Math.Clamp(_cfg.OnbonPollSeconds, 5, 3600);
        _chkIsolated.Checked = _cfg.UseIsolatedSender;
        _chkSkipDup.Checked = _cfg.SkipDuplicateUploads;
        _chkRejectSize.Checked = _cfg.RejectSizeMismatchBeforePublish;
        _chkWifiOnly.Checked = _cfg.EnforceWifiOnly;
        _chkPrivate.Checked = _cfg.RequirePrivateAddress;

        // Design
        SyncDesignNumericsFromConfig();
        _editor.SetBackground(null);
        _editor.Bind(_cfg);
    }

    private void PullCurrencies()
    {
        _cfg.ColumnCount = Math.Clamp((int)_numColumnCount.Value, 1, MaxColumns);
        _cfg.Columns = [];
        for (int i = 0; i < _cfg.ColumnCount; i++)
            _cfg.Columns.Add(_lstColumns[i].Items.Cast<string>().Select(s => s.Split(' ')[0]).ToList());

        // Per-column header labels (split textbox lines, drop blanks)
        _cfg.ColumnBuyLabels = [];
        _cfg.ColumnSellLabels = [];
        for (int i = 0; i < _cfg.ColumnCount; i++)
        {
            _cfg.ColumnBuyLabels.Add(SplitLabelLines(_txtBuyLabels[i].Text, AppConfig.DefaultBuyLabels));
            _cfg.ColumnSellLabels.Add(SplitLabelLines(_txtSellLabels[i].Text, AppConfig.DefaultSellLabels));
        }
        _cfg.NormalizeColumns();
    }

    private static List<string> SplitLabelLines(string text, List<string> fallback)
    {
        var lines = (text ?? "")
            .Replace("\r\n", "\n").Split('\n')
            .Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        return lines.Count > 0 ? lines : fallback;
    }

    private void PullDisplay()
    {
        _cfg.CanvasWidth = (int)_numW.Value;
        _cfg.CanvasHeight = (int)_numH.Value;
        _cfg.ScreenWidth = _cfg.CanvasWidth;
        _cfg.ScreenHeight = _cfg.CanvasHeight;
    }

    private bool CollectForm()
    {
        // Currencies
        PullCurrencies();

        if (_cfg.AllCurrencies.Count == 0)
        {
            MessageBox.Show("Выберите хотя бы одну валюту.", "eCash Tablo", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        // Display
        PullDisplay();
        PullColumnX();

        // Service
        _cfg.RunMode = _cmbRunMode.SelectedItem?.ToString() ?? "RenderOnly";
        _cfg.PublishMode = _cmbPublishMode.SelectedItem?.ToString() ?? "WifiFtp";
        _cfg.PollSeconds = (int)_numPoll.Value;
        _cfg.RatesFetchIntervalMinutes = (int)_numRatesFetch.Value;
        _cfg.LayoutTestMode = _chkLayout.Checked;
        _cfg.PermanentInternet = _chkPermanentInternet.Checked;
        _cfg.AutoSend = _chkAutoSend.Checked;
        _cfg.SkipIfUnchanged = _chkSkipUnchanged.Checked;
        _cfg.ForceComposeEveryPoll = _chkForceCompose.Checked;

        // Connection
        if (_cmbFamily.SelectedIndex >= 0)
            _cfg.ControllerFamily = ControllerFamilyCatalog.Families[_cmbFamily.SelectedIndex].Key;
        _cfg.BoardTransport = _cmbTransport.SelectedIndex == 1 ? "Ftp" : "Tcp";
        _cfg.WifiSsid = _txtSsid.Text.Trim();
        _cfg.ControllerIp = _txtIp.Text.Trim();
        _cfg.ControllerPort = (int)_numCtrlPort.Value;
        _cfg.HuiduListenPort = (int)_numHuiduListenPort.Value;
        _cfg.HuiduUdpDiscoveryPort = (int)_numHuiduUdpPort.Value;
        _cfg.HuiduCardPort = (int)_numHuiduCardPort.Value;
        _cfg.HuiduCardIp = _txtHuiduCardIp.Text.Trim();
        _cfg.HuiduDeviceId = _txtHuiduDeviceId.Text.Trim();
        _cfg.HuiduModel = _cmbHuiduModel.SelectedIndex >= 0
            && _cmbHuiduModel.SelectedIndex < HuiduControllerCatalog.Models.Count
            ? HuiduControllerCatalog.Models[_cmbHuiduModel.SelectedIndex].Name
            : "";
        _cfg.DeviceType = (int)_numDevice.Value;
        if (_cmbConnMode.SelectedIndex >= 0)
            _cfg.ConnectionMode = ConnectionModeCatalog.Modes[_cmbConnMode.SelectedIndex].Key;
        _cfg.FtpUser = _txtFtpUser.Text;
        _cfg.FtpPassword = _txtFtpPass.Text;
        _cfg.FtpPort = (int)_numFtpPort.Value;
        _cfg.UseTls = _chkTls.Checked;
        _cfg.RatesApiUrl = _txtRatesUrl.Text.Trim();
        _cfg.ControllerReloadUrl = _txtReloadUrl.Text.Trim();
        _cfg.Urls = _txtApiPort.Text.Trim();

        // Advanced
        _cfg.OnbonEnabled = _chkOnbonEnabled.Checked;
        _cfg.OnbonUserName = _txtOnbonUser.Text;
        _cfg.OnbonPassword = _txtOnbonPass.Text;
        _cfg.RetryCount = (int)_numRetry.Value;
        _cfg.RetryDelayMs = (int)_numRetryMs.Value;
        _cfg.ConnectionTimeoutMs = (int)_numConnTimeout.Value;
        _cfg.OnbonPollSeconds = (int)_numOnbonPoll.Value;
        _cfg.UseIsolatedSender = _chkIsolated.Checked;
        _cfg.SkipDuplicateUploads = _chkSkipDup.Checked;
        _cfg.RejectSizeMismatchBeforePublish = _chkRejectSize.Checked;
        _cfg.EnforceWifiOnly = _chkWifiOnly.Checked;
        _cfg.RequirePrivateAddress = _chkPrivate.Checked;

        return true;
    }

    private static string FormatCurrency(string code)
    {
        var name = AppSettingsManager.KnownCurrencies.TryGetValue(code, out var n) ? n : code;
        return $"{code}  {name}";
    }

    private static NumericUpDown MakeNumeric(int min, int max)
        => new() { Minimum = min, Maximum = max, Width = 100 };

    private static Button MakeButton(string text, Color bg, Color fg)
    {
        return new RoundedButton
        {
            Text = text,
            Height = 32,
            BackColor = bg,
            ForeColor = fg,
            Font = new Font("Segoe UI Semibold", 9f),
            CornerRadius = 9,
        };
    }

    private static Button MakeArrow(string text, Color bg, Color fg)
    {
        var btn = MakeButton(text, bg, fg);
        btn.Width = 60;
        btn.Height = 26;
        return btn;
    }

    private static GroupBox MakeGroupBox(string text, Control parent)
    {
        var grp = new GroupBox { Text = text, Dock = DockStyle.Top, AutoSize = true };
        parent.Controls.Add(grp);
        return grp;
    }

    private static Label? AddRow(TableLayoutPanel tbl, string label, Control ctrl)
    {
        int row = tbl.RowCount;
        tbl.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        tbl.RowCount = row + 1;

        Label? lbl = null;
        if (!string.IsNullOrEmpty(label))
        {
            lbl = new Label
            {
                Text = label,
                AutoSize = true,
                Anchor = AnchorStyles.Left | AnchorStyles.Top,
                Padding = new Padding(0, 5, 0, 0),
            };
            tbl.Controls.Add(lbl, 0, row);
        }

        ctrl.Anchor = AnchorStyles.Left | AnchorStyles.Top;
        tbl.Controls.Add(ctrl, 1, row);
        return lbl;
    }

    // Show/hide family-specific fields based on the selected controller family.
    private void ApplyFamilyVisibility()
    {
        bool huidu = _cmbFamily.SelectedIndex >= 0
            && string.Equals(
                ControllerFamilyCatalog.Families[_cmbFamily.SelectedIndex].Key,
                ControllerFamilyCatalog.Huidu, StringComparison.OrdinalIgnoreCase);

        void SetRow(Label? lbl, Control ctrl, bool visible)
        {
            if (lbl != null) lbl.Visible = visible;
            ctrl.Visible = visible;
        }

        // FTP transport is a Huidu-family option (index 1). Onbon always uses FTP/screen.xml.
        bool ftp = huidu && _cmbTransport.SelectedIndex == 1;

        // Transport selector + board SSID: Huidu family only.
        SetRow(_lblTransport, _cmbTransport, huidu);
        SetRow(_lblSsid, _txtSsid, huidu);

        // IP field: controller IP for Onbon, board FTP IP for Huidu+FTP; hidden for Huidu+TCP.
        bool showIp = !huidu || ftp;
        if (_lblIp != null) _lblIp.Text = huidu ? "IP табло (FTP):" : "IP-адрес контроллера:";
        SetRow(_lblIp, _txtIp, showIp);

        // Onbon-only rows
        SetRow(_lblCtrlPort, _numCtrlPort, !huidu);
        SetRow(_lblModel, _cmbModel, !huidu);
        SetRow(_lblDevice, _numDevice, !huidu);
        SetRow(_lblConnMode, _cmbConnMode, !huidu);

        // Model selector: Huidu (informational) for the Huidu family, Onbon code for Onbon.
        SetRow(_lblHuiduModel, _cmbHuiduModel, huidu);

        // Huidu TCP-only rows (direct/auto card connection — not used by the FTP transport)
        bool huiduTcp = huidu && !ftp;
        if (_lblHuiduNote != null) _lblHuiduNote.Visible = huiduTcp;
        SetRow(_lblHuiduListenPort, _numHuiduListenPort, huiduTcp);
        SetRow(_lblHuiduCardIp, _txtHuiduCardIp, huiduTcp);
        SetRow(_lblHuiduDeviceId, _txtHuiduDeviceId, huiduTcp);
        if (_lblHuiduUdpPort != null && _numHuiduUdpPort != null)
            SetRow(_lblHuiduUdpPort, _numHuiduUdpPort, huiduTcp);
        if (_lblHuiduCardPort != null && _numHuiduCardPort != null)
            SetRow(_lblHuiduCardPort, _numHuiduCardPort, huiduTcp);
    }
}
