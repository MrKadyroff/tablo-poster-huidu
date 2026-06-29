using LedImageUpdaterService.Services;

namespace LedImageUpdaterService.UI;

/// <summary>
/// Tray-level owner of the standalone <see cref="WifiAlertForm"/>. It reacts to the board-send
/// pipeline (see <see cref="LedBoardService"/>): after a failed push to an unreachable board the
/// service raises <see cref="WifiAlertBridge.ShowAlertRequested"/>; once a push succeeds it
/// raises <see cref="WifiAlertBridge.HideAlertRequested"/>.
///
/// The operator may close the notice. If it was closed while the board is still unreachable,
/// the watchdog snoozes for <see cref="SnoozeMinutes"/> minutes and then re-shows it if the
/// board is still unreachable. Requests during the snooze window are ignored.
///
/// Bridge events may arrive on a background thread, so they are marshalled to the UI thread via
/// the captured <see cref="SynchronizationContext"/>.
/// </summary>
internal sealed class WifiWatchdog : IDisposable
{
    private const int SnoozeMinutes = 2;

    private WifiAlertForm? _alert;
    private SynchronizationContext? _ui;
    private string? _lastLogLine;
    private DateTime _snoozeUntil = DateTime.MinValue;
    private readonly System.Windows.Forms.Timer _snoozeTimer =
        new() { Interval = SnoozeMinutes * 60 * 1000 };
    private static readonly string LogPath = Path.Combine(AppContext.BaseDirectory, "logs", "wifi-watchdog.log");

    public void Start()
    {
        // Captured on the UI thread (WifiWatchdog is constructed/started from the tray ctor).
        _ui = SynchronizationContext.Current ?? new SynchronizationContext();
        WifiAlertBridge.ShowAlertRequested += OnShowRequested;
        WifiAlertBridge.HideAlertRequested += OnHideRequested;
        _snoozeTimer.Tick += (_, _) => OnSnoozeElapsed();
        Log($"WifiWatchdog запущен (предупреждение после неудачной отправки; повтор через {SnoozeMinutes} мин, если не решено).");
    }

    public void Stop()
    {
        _snoozeTimer.Stop();
        WifiAlertBridge.ShowAlertRequested -= OnShowRequested;
        WifiAlertBridge.HideAlertRequested -= OnHideRequested;
    }

    private void OnShowRequested()
    {
        Post(() =>
        {
            if (DateTime.Now < _snoozeUntil)
            {
                Log("Предупреждение отложено оператором — показ пропущен.");
                return;
            }
            ShowAlert();
        });
    }

    private void OnHideRequested()
    {
        Post(() =>
        {
            _snoozeUntil = DateTime.MinValue;
            _snoozeTimer.Stop();
            if (_alert is { IsDisposed: false })
            {
                Log("Связь с табло восстановлена — закрываю предупреждение.");
                _alert.ForceClose();
            }
        });
    }

    // Fires SnoozeMinutes after the operator dismissed the notice. Re-shows it only if the
    // board is still unreachable.
    private void OnSnoozeElapsed()
    {
        _snoozeTimer.Stop();
        _snoozeUntil = DateTime.MinValue;

        if (_alert is { IsDisposed: false }) return; // already showing
        if (WifiAlertBridge.IsBoardUnreachable)
        {
            Log($"Прошло {SnoozeMinutes} мин, связи с табло всё ещё нет — показываю предупреждение снова.");
            ShowAlert();
        }
        else
        {
            Log($"Прошло {SnoozeMinutes} мин — связь с табло есть, повтор не требуется.");
        }
    }

    private void Post(Action action)
    {
        var ui = _ui;
        if (ui != null) ui.Post(_ => action(), null);
        else action();
    }

    // Logs to logs/wifi-watchdog.log, skipping identical consecutive lines so a stable
    // state does not grow the file. Never throws.
    private void Log(string message)
    {
        try
        {
            if (message == _lastLogLine) return;
            _lastLogLine = message;
            var dir = Path.GetDirectoryName(LogPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch { /* logging must never break the watchdog */ }
    }

    private void ShowAlert()
    {
        if (_alert is { IsDisposed: false }) return;

        _alert = new WifiAlertForm();
        _alert.FormClosed += (_, _) =>
        {
            bool resolved = _alert?.ResolvedConnected ?? true;
            _alert = null;
            if (!resolved)
            {
                // Operator dismissed it while still unreachable → snooze, then re-check.
                _snoozeUntil = DateTime.Now.AddMinutes(SnoozeMinutes);
                _snoozeTimer.Stop();
                _snoozeTimer.Start();
                Log($"Оператор закрыл предупреждение — повтор через {SnoozeMinutes} мин, если не решено.");
            }
        };
        _alert.Show();
        _alert.Activate();
    }

    public void Dispose()
    {
        Stop();
        try { _snoozeTimer.Dispose(); } catch { }
        if (_alert is { IsDisposed: false }) { _alert.ForceClose(); _alert.Dispose(); _alert = null; }
    }
}
