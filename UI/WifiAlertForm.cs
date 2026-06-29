using System.Media;
using LedImageUpdaterService.Services;

namespace LedImageUpdaterService.UI;

/// <summary>
/// Compact, always-on-top notice shown in the bottom-right corner after a failed push to the
/// board (the card is unreachable). The operator CAN close it; the tray watchdog
/// (<see cref="WifiWatchdog"/>) re-shows it ~2 minutes later if the board is still unreachable.
/// It is closed automatically (via <see cref="ForceClose"/>) once a push succeeds.
///
/// The Huidu card runs its own Wi-Fi access point, so there is no fixed SSID to display — the
/// message simply tells the operator to join the board's Wi-Fi. This window is owned by the
/// tray app, so it appears even when the cashier window is closed.
/// </summary>
internal sealed class WifiAlertForm : Form
{
    private bool _connected;

    /// <summary>
    /// True when the window closed because the board became reachable again (so the watchdog
    /// should NOT schedule a re-show).
    /// </summary>
    internal bool ResolvedConnected => _connected;

    private static readonly Color WarnColor = Color.FromArgb(235, 110, 90);

    public WifiAlertForm()
    {
        InitializeComponent();
        Load += (_, _) =>
        {
            PositionBottomRight();
            try { SystemSounds.Exclamation.Play(); } catch { }
        };
    }

    private void InitializeComponent()
    {
        SuspendLayout();

        Text = "Нет связи с табло";
        Size = new Size(330, 144);
        FormBorderStyle = FormBorderStyle.FixedToolWindow; // small title bar, has a close button
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = UITheme.Bg;
        ForeColor = UITheme.Text;
        Font = new Font("Segoe UI", 9.5f);
        try { Icon = TrayApplicationContext.CreateAppIcon(); } catch { }

        var title = new Label
        {
            Text = "Курсы на табло не обновляются",
            Font = new Font("Segoe UI Semibold", 11f, FontStyle.Bold),
            ForeColor = WarnColor,
            AutoSize = false,
            Dock = DockStyle.Top,
            Height = 46,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(14, 0, 10, 0),
        };

        var ssid = WifiAlertBridge.Ssid;
        var wifiLine = string.IsNullOrWhiteSpace(ssid)
            ? "Подключитесь к сети Wi-Fi табло."
            : $"Подключитесь к сети Wi-Fi: {ssid}";

        var body = new Label
        {
            Text = $"Нет связи с табло.\n{wifiLine}",
            ForeColor = UITheme.Text,
            AutoSize = false,
            Dock = DockStyle.Top,
            Height = 50,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(14, 0, 10, 0),
        };

        // Dock(Top) stacks last-added on top → title, then body.
        Controls.Add(body);
        Controls.Add(title);

        ResumeLayout();
    }

    private void PositionBottomRight()
    {
        var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        Location = new Point(wa.Right - Width - 12, wa.Bottom - Height - 12);
    }

    /// <summary>
    /// Closes the notice programmatically (used by the watchdog when the board becomes
    /// reachable again, so it never lingers).
    /// </summary>
    public void ForceClose()
    {
        _connected = true; // marks the close as "resolved" → no re-show
        try { Close(); } catch { /* ignore if already disposing */ }
    }
}
