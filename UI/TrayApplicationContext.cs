using System.Diagnostics;
using Microsoft.AspNetCore.Builder;

namespace LedImageUpdaterService.UI;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly string[] _args;
    private readonly NotifyIcon _trayIcon;
    private SettingsForm? _settingsForm;
    private CashierForm? _cashierForm;
    private UpdateForm? _updateForm;
    private UpdateService.UpdateInfo? _availableUpdate;
    private WebApplication? _host;
    private string _status = "Запускается...";
    private readonly System.Threading.SynchronizationContext _uiContext;
    private readonly WifiWatchdog _wifiWatchdog = new();

    public TrayApplicationContext(string[] args)
    {
        _args = args;
        _uiContext = System.Threading.SynchronizationContext.Current
            ?? new System.Threading.SynchronizationContext();

        _trayIcon = new NotifyIcon
        {
            Icon = CreateAppIcon(),
            Text = "eCash Tablo",
            Visible = true,
        };
        _trayIcon.DoubleClick += (_, _) => ShowCashier();

        RebuildMenu();

        // Standalone "no link to board" notice: appears after a failed push when the board
        // is unreachable, even if the cashier window is closed.
        _wifiWatchdog.Start();

        _ = StartHostAsync();
    }

    private void RebuildMenu()
    {
        var menu = new ContextMenuStrip { Font = new Font("Segoe UI", 9f) };

        var header = new ToolStripMenuItem($"eCash Tablo  v{AppVersion}") { Enabled = false, Font = new Font("Segoe UI", 9f, FontStyle.Bold) };
        menu.Items.Add(header);

        var statusItem = new ToolStripMenuItem(_status) { Enabled = false, ForeColor = StatusColor() };
        menu.Items.Add(statusItem);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Управление курсами...", null, (_, _) => ShowCashier());
        menu.Items.Add("Настройки (администратор)...", null, (_, _) => ShowSettingsWithPassword());
        menu.Items.Add(new ToolStripSeparator());

        // When an update was detected at startup, surface it prominently.
        if (_availableUpdate is { } upd)
        {
            var updItem = new ToolStripMenuItem($"🔄 Обновить до {upd.Tag}...", null, (_, _) => ShowUpdate())
            {
                ForeColor = Color.FromArgb(0, 150, 230),
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            };
            menu.Items.Add(updItem);
        }
        menu.Items.Add("Проверить обновления...", null, (_, _) => ShowUpdate());

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Перезапустить сервис", null, (_, _) => _ = RestartHostAsync());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => ExitApp());

        _trayIcon.ContextMenuStrip = menu;
    }

    internal static string AppVersion =>
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";

    private Color StatusColor() => _status switch
    {
        "Работает" => Color.Green,
        "Ошибка" => Color.Red,
        _ => Color.DarkOrange,
    };

    // Simplified cashier window: refresh rates + push to board. No password.
    internal void ShowCashier()
    {
        if (_cashierForm == null || _cashierForm.IsDisposed)
            _cashierForm = new CashierForm();
        else
            _cashierForm.ReloadConfig();

        if (!_cashierForm.Visible)
            _cashierForm.Show();

        if (_cashierForm.WindowState == FormWindowState.Minimized)
            _cashierForm.WindowState = FormWindowState.Normal;

        _cashierForm.ShowInTaskbar = true;
        _cashierForm.BringToFront();
        _cashierForm.Activate();
    }

    // Full settings window, gated behind the admin password.
    private void ShowSettingsWithPassword()
    {
        if (!AdminGate.Authenticate(_settingsForm)) return;
        ShowSettings();
    }

    internal void ShowSettings()
    {
        if (_settingsForm == null || _settingsForm.IsDisposed)
            _settingsForm = new SettingsForm(OnRestartRequested, ExitApp);

        if (!_settingsForm.Visible)
            _settingsForm.Show();

        if (_settingsForm.WindowState == FormWindowState.Minimized)
            _settingsForm.WindowState = FormWindowState.Normal;

        _settingsForm.ShowInTaskbar = true;
        _settingsForm.BringToFront();
        _settingsForm.Activate();
    }

    // For points without permanent internet the board is updated manually, so the
    // program must be reachable in the taskbar rather than hidden in the tray.
    // On startup we surface the cashier window (refresh + send) when
    // PermanentInternet=false, so the operator can push to the board on demand.
    private void MaybeAutoShowForManualPoint()
    {
        bool manual;
        try { manual = !AppSettingsManager.Load().PermanentInternet; }
        catch { return; }

        if (!manual) return;

        _uiContext.Post(_ => ShowCashier(), null);
    }

    private void OnRestartRequested() => _ = RestartHostAsync();

    // Background check on startup. If a newer GitHub release exists, surface it via a
    // balloon tip and a highlighted tray-menu item. Never throws (CheckAsync fails soft).
    private async Task CheckForUpdatesAsync()
    {
        try
        {
            var info = await UpdateService.CheckAsync();
            if (info is null) return;

            _uiContext.Post(_ =>
            {
                _availableUpdate = info;
                RebuildMenu();
                try
                {
                    _trayIcon.ShowBalloonTip(8000, "eCash Tablo",
                        $"Доступна версия {info.Tag}. Откройте меню → «Обновить».",
                        ToolTipIcon.Info);
                }
                catch { }
            }, null);
        }
        catch { /* update check must never disrupt startup */ }
    }

    private void ShowUpdate()
    {
        if (_updateForm == null || _updateForm.IsDisposed)
            _updateForm = new UpdateForm(ExitApp);

        if (!_updateForm.Visible) _updateForm.Show();
        if (_updateForm.WindowState == FormWindowState.Minimized)
            _updateForm.WindowState = FormWindowState.Normal;
        _updateForm.BringToFront();
        _updateForm.Activate();
    }

    private async Task StartHostAsync()
    {
        try
        {
            _host = Program.BuildWebApp(_args);
            await _host.StartAsync();
            SetStatus("Работает");
            MaybeAutoShowForManualPoint();
            _ = CheckForUpdatesAsync();
        }
        catch (Exception ex)
        {
            SetStatus("Ошибка");
            MessageBox.Show($"Ошибка запуска сервиса:\n\n{ex.Message}",
                "eCash Tablo", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task StopHostAsync()
    {
        if (_host != null)
        {
            SetStatus("Останавливается...");
            try { await _host.StopAsync(TimeSpan.FromSeconds(5)); } catch { }
            try { await _host.DisposeAsync(); } catch { }
            _host = null;
            SetStatus("Остановлен");
        }
    }

    private async Task RestartHostAsync()
    {
        await StopHostAsync();
        // Give the OS a moment to release the listening socket before re-binding,
        // otherwise the new host can fail with "address already in use".
        await Task.Delay(400);
        SetStatus("Запускается...");
        await StartHostAsync();
    }

    private void SetStatus(string status)
    {
        _status = status;
        _trayIcon.Text = $"eCash Tablo — {status}";
        _uiContext.Post(_ => RebuildMenu(), null);
    }

    private bool _exiting;

    private void ExitApp()
    {
        if (_exiting) return;
        _exiting = true;

        try { _wifiWatchdog.Dispose(); } catch { }
        _trayIcon.Visible = false;
        try { _trayIcon.Dispose(); } catch { }

        // Safety net: if graceful shutdown stalls, force-terminate anyway.
        _ = Task.Delay(TimeSpan.FromSeconds(7))
            .ContinueWith(_ => ForceTerminate());

        // Stop the host (releases port 5050 + background workers), then exit hard.
        _ = Task.Run(async () =>
        {
            try { await StopHostAsync(); } catch { }
            ForceTerminate();
        });
    }

    /// <summary>
    /// Kills any orphaned child processes (isolated SDK senders) and terminates the
    /// whole process tree. Environment.Exit guarantees the main process leaves Task
    /// Manager even if non-background threads (native SDK, Kestrel) are still alive.
    /// </summary>
    private static void ForceTerminate()
    {
        try
        {
            var me = Process.GetCurrentProcess();
            foreach (var p in Process.GetProcessesByName(me.ProcessName))
            {
                if (p.Id == me.Id) continue;
                try { p.Kill(entireProcessTree: true); } catch { }
                p.Dispose();
            }
        }
        catch { }

        Environment.Exit(0);
    }

    internal static Icon CreateAppIcon()
    {
        // Prefer the multi-resolution .ico (sharpest in tray/taskbar)
        var icoPath = Path.Combine(AppContext.BaseDirectory, "content", "common", "logo.ico");
        if (File.Exists(icoPath))
        {
            try { return new Icon(icoPath); }
            catch { }
        }

        // Fall back to rasterizing a PNG logo
        foreach (var candidate in new[]
        {
            Path.Combine(AppContext.BaseDirectory, "content", "common", "logo.png"),
            Path.Combine(AppContext.BaseDirectory, "content", "common", "ecash.png"),
            Path.Combine(AppContext.BaseDirectory, "content", "common", "overlays", "logo.png"),
        })
        {
            if (File.Exists(candidate))
            {
                try
                {
                    using var bmp = new Bitmap(candidate);
                    return IconFromBitmap(bmp);
                }
                catch { }
            }
        }

        return BuildGeneratedIcon();
    }

    private static Icon IconFromBitmap(Bitmap source)
    {
        using var resized = new Bitmap(32, 32);
        using var g = Graphics.FromImage(resized);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        g.DrawImage(source, 0, 0, 32, 32);
        return Icon.FromHandle(resized.GetHicon());
    }

    private static Icon BuildGeneratedIcon()
    {
        var bmp = new Bitmap(32, 32, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using var bgBrush = new SolidBrush(Color.FromArgb(0, 102, 204));
            g.FillEllipse(bgBrush, 1, 1, 30, 30);
            using var font = new Font("Arial", 17f, FontStyle.Bold, GraphicsUnit.Pixel);
            var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString("e", font, Brushes.White, new RectangleF(1, 1, 30, 30), sf);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }
        base.Dispose(disposing);
    }
}
