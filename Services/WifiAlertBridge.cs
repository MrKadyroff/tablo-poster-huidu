namespace LedImageUpdaterService.Services;

/// <summary>
/// Process-wide bridge between the board-send pipeline (DI host, see
/// <see cref="LedBoardService"/>) and the tray UI alert (see <c>UI.WifiWatchdog</c> /
/// <c>UI.WifiAlertForm</c>).
///
/// Unlike the Onbon build there is no configured Wi-Fi SSID — the Huidu card runs its own
/// access point that the operator joins, and reachability is judged by the send result. After
/// a failed push to an unreachable board the service asks the UI to show a corner notice; once
/// a send succeeds it asks the UI to hide it.
///
/// Handlers may be invoked from background threads — subscribers marshal to the UI thread.
/// </summary>
public static class WifiAlertBridge
{
    /// <summary>True while the board is currently considered unreachable (last push failed).
    /// The watchdog uses this to decide whether a snoozed notice should re-appear.</summary>
    public static bool IsBoardUnreachable { get; private set; }

    /// <summary>The board Wi-Fi SSID to display in the notice (set by the send pipeline). Empty if unknown.</summary>
    public static string Ssid { get; set; } = "";

    /// <summary>Raised after a failed board push when the board is unreachable.</summary>
    public static event Action? ShowAlertRequested;

    /// <summary>Raised once a push succeeds (board reachable again).</summary>
    public static event Action? HideAlertRequested;

    public static void RequestShow()
    {
        IsBoardUnreachable = true;
        try { ShowAlertRequested?.Invoke(); }
        catch { /* UI signalling must never break the send pipeline */ }
    }

    public static void RequestHide()
    {
        IsBoardUnreachable = false;
        try { HideAlertRequested?.Invoke(); }
        catch { /* UI signalling must never break the send pipeline */ }
    }
}
