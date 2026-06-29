namespace LedImageUpdaterService.Models;

/// <summary>
/// Result of one "board link" evaluation produced by
/// <c>BoardLinkMonitor</c>: whether the PC is currently on the LED card's
/// Wi-Fi (an "island" AP with no internet), which card IP was found, the
/// connectivity probe results, and the concrete recommendation/action taken.
///
/// This is the structured payload behind <c>GET /api/led/board-link</c> and the
/// tray/UI banner. It is intentionally a plain snapshot (no behaviour) so it can
/// be serialized as-is and stored as the "latest" report in <see cref="BoardLinkState"/>.
/// </summary>
public sealed record BoardLinkReport(
    DateTimeOffset CheckedAt,
    bool OnBoardWifi,
    string? InterfaceName,
    string? LocalIp,
    string? Gateway,
    bool HasInternet,
    string? DiscoveredCardId,
    string? CandidateCardIp,
    bool PingOk,
    bool TcpOk,
    bool HelloOk,
    string? ConfiguredCardIp,
    bool CardIpMatches,
    string? AppliedCardIp,
    int? ConfiguredWidth,
    int? ConfiguredHeight,
    int? DetectedWidth,
    int? DetectedHeight,
    string Verdict,
    string Recommendation,
    IReadOnlyList<string> Steps)
{
    public static BoardLinkReport NotOnBoardWifi(IReadOnlyList<string> steps) => new(
        CheckedAt: DateTimeOffset.UtcNow,
        OnBoardWifi: false,
        InterfaceName: null, LocalIp: null, Gateway: null, HasInternet: true,
        DiscoveredCardId: null, CandidateCardIp: null,
        PingOk: false, TcpOk: false, HelloOk: false,
        ConfiguredCardIp: null, CardIpMatches: false, AppliedCardIp: null,
        ConfiguredWidth: null, ConfiguredHeight: null, DetectedWidth: null, DetectedHeight: null,
        Verdict: "idle",
        Recommendation: "ПК не подключён к Wi-Fi табло — мониторинг ждёт переключения сети.",
        Steps: steps);
}

/// <summary>
/// Process-wide, thread-safe holder for the latest <see cref="BoardLinkReport"/> and
/// the runtime overrides the monitor applies on the fly.
///
/// The running <c>HuiduLedController</c> reads <see cref="OverrideCardIp"/> /
/// <see cref="OverrideScreen"/> so an auto-applied card IP or panel size takes effect
/// immediately, without waiting for an <c>IOptions</c> reload or a service restart
/// (the file is patched separately so the change also survives a restart).
/// </summary>
public sealed class BoardLinkState
{
    private readonly object _lock = new();
    private BoardLinkReport? _report;

    /// <summary>Card IP applied at runtime by the monitor (null = use configured value).</summary>
    public string? OverrideCardIp { get; private set; }

    /// <summary>Panel size applied at runtime by the monitor (null = use configured value).</summary>
    public (int Width, int Height)? OverrideScreen { get; private set; }

    public BoardLinkReport? Latest
    {
        get { lock (_lock) return _report; }
    }

    public void SetReport(BoardLinkReport report)
    {
        lock (_lock) _report = report;
    }

    public void SetCardIpOverride(string? ip)
    {
        lock (_lock) OverrideCardIp = string.IsNullOrWhiteSpace(ip) ? null : ip;
    }

    public void SetScreenOverride(int width, int height)
    {
        lock (_lock) OverrideScreen = (width, height);
    }
}
