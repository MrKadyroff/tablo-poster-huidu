namespace LedImageUpdaterService.UI;

/// <summary>
/// Small dialog that checks GitHub for a newer release, shows the version + release notes,
/// and applies the update (download → swap → restart) via <see cref="UpdateService"/>.
/// Used both by the "Проверить обновления" button in Settings and the tray menu.
/// </summary>
internal sealed class UpdateForm : Form
{
    private readonly Action _exitApp;
    private UpdateService.UpdateInfo? _info;

    private Label _lblStatus = null!;
    private TextBox _txtNotes = null!;
    private ProgressBar _progress = null!;
    private RoundedButton _btnUpdate = null!;
    private RoundedButton _btnRecheck = null!;
    private RoundedButton _btnClose = null!;

    public UpdateForm(Action exitApp)
    {
        _exitApp = exitApp;
        InitializeComponent();
        Load += async (_, _) => await CheckAsync();
    }

    private void InitializeComponent()
    {
        SuspendLayout();

        Text = "eCash Tablo — Обновление";
        Size = new Size(540, 460);
        MinimumSize = new Size(520, 420);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = UITheme.Bg;
        ForeColor = UITheme.Text;
        Font = new Font("Segoe UI", 9.5f);
        ShowInTaskbar = true;
        MaximizeBox = false;
        try { Icon = TrayApplicationContext.CreateAppIcon(); } catch { }

        var lblCurrent = new Label
        {
            Text = $"Текущая версия: v{UpdateService.CurrentVersion.ToString(3)}",
            AutoSize = true,
            ForeColor = UITheme.TextDim,
            Location = new Point(18, 18),
        };

        _lblStatus = new Label
        {
            Text = "Проверка обновлений…",
            AutoSize = true,
            ForeColor = UITheme.Accent,
            Font = new Font("Segoe UI Semibold", 11f),
            Location = new Point(18, 44),
        };

        var lblNotesHdr = new Label
        {
            Text = "Что нового:",
            AutoSize = true,
            ForeColor = UITheme.TextDim,
            Location = new Point(18, 82),
        };

        _txtNotes = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BackColor = UITheme.Input,
            ForeColor = UITheme.Text,
            BorderStyle = BorderStyle.FixedSingle,
            Location = new Point(18, 104),
            Size = new Size(490, 230),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
        };

        _progress = new ProgressBar
        {
            Location = new Point(18, 344),
            Size = new Size(490, 18),
            Visible = false,
            Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
        };

        _btnUpdate = new RoundedButton
        {
            Text = "⬇  Обновить и перезапустить",
            BackColor = UITheme.Accent2,
            ForeColor = Color.White,
            Font = new Font("Segoe UI Semibold", 9.5f),
            Size = new Size(250, 34),
            CornerRadius = 9,
            Enabled = false,
            Location = new Point(18, 374),
            Anchor = AnchorStyles.Left | AnchorStyles.Bottom,
        };
        _btnUpdate.Click += async (_, _) => await ApplyAsync();

        _btnRecheck = new RoundedButton
        {
            Text = "↻ Проверить снова",
            BackColor = UITheme.Input,
            ForeColor = UITheme.Text,
            Font = new Font("Segoe UI Semibold", 9.5f),
            Size = new Size(150, 34),
            CornerRadius = 9,
            Location = new Point(278, 374),
            Anchor = AnchorStyles.Left | AnchorStyles.Bottom,
        };
        _btnRecheck.Click += async (_, _) => await CheckAsync();

        _btnClose = new RoundedButton
        {
            Text = "Закрыть",
            BackColor = UITheme.Input,
            ForeColor = UITheme.Text,
            Font = new Font("Segoe UI Semibold", 9.5f),
            Size = new Size(80, 34),
            CornerRadius = 9,
            Location = new Point(434, 374),
            Anchor = AnchorStyles.Right | AnchorStyles.Bottom,
        };
        _btnClose.Click += (_, _) => Close();

        Controls.AddRange([lblCurrent, _lblStatus, lblNotesHdr, _txtNotes, _progress, _btnUpdate, _btnRecheck, _btnClose]);

        ResumeLayout();
    }

    private async Task CheckAsync()
    {
        _info = null;
        _btnUpdate.Enabled = false;
        _btnRecheck.Enabled = false;
        _lblStatus.ForeColor = UITheme.Accent;
        _lblStatus.Text = "Проверка обновлений…";
        _txtNotes.Text = "";
        try
        {
            var info = await UpdateService.CheckAsync();
            if (info is null)
            {
                _lblStatus.ForeColor = UITheme.Accent;
                _lblStatus.Text = $"✓ У вас последняя версия (v{UpdateService.CurrentVersion.ToString(3)}).";
                _txtNotes.Text = "Обновлений нет либо нет соединения с GitHub.";
                return;
            }

            _info = info;
            _lblStatus.ForeColor = Color.FromArgb(120, 200, 255);
            _lblStatus.Text = $"Доступна версия {info.Tag}";
            _txtNotes.Text = string.IsNullOrWhiteSpace(info.Notes)
                ? "(описание изменений отсутствует)"
                : info.Notes.Replace("\n", Environment.NewLine);
            _btnUpdate.Enabled = true;
        }
        catch (Exception ex)
        {
            _lblStatus.ForeColor = Color.Salmon;
            _lblStatus.Text = "Не удалось проверить обновления.";
            _txtNotes.Text = ex.Message;
        }
        finally
        {
            _btnRecheck.Enabled = true;
        }
    }

    private async Task ApplyAsync()
    {
        if (_info is null) return;

        var confirm = MessageBox.Show(
            $"Обновить до {_info.Tag}?\n\nПриложение закроется, обновится и запустится заново.\n" +
            "Настройки и разметка точки будут сохранены.",
            "Обновление", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
        if (confirm != DialogResult.Yes) return;

        _btnUpdate.Enabled = false;
        _btnRecheck.Enabled = false;
        _btnClose.Enabled = false;
        _progress.Visible = true;
        _progress.Value = 0;
        _lblStatus.ForeColor = UITheme.Accent;
        _lblStatus.Text = "Скачивание обновления…";

        try
        {
            var progress = new Progress<int>(p =>
            {
                if (IsDisposed) return;
                _progress.Value = Math.Clamp(p, 0, 100);
                _lblStatus.Text = $"Скачивание обновления… {p}%";
            });

            var zip = await UpdateService.DownloadAsync(_info, progress);

            _lblStatus.Text = "Установка и перезапуск…";
            // Hands off to the detached updater and exits the app (releases file locks).
            UpdateService.ApplyAndExit(zip, _exitApp);
        }
        catch (Exception ex)
        {
            _progress.Visible = false;
            _lblStatus.ForeColor = Color.Salmon;
            _lblStatus.Text = "Ошибка обновления.";
            _txtNotes.Text = ex.Message;
            _btnUpdate.Enabled = true;
            _btnRecheck.Enabled = true;
            _btnClose.Enabled = true;
        }
    }
}
