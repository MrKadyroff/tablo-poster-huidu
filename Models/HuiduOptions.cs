using System.ComponentModel.DataAnnotations;

namespace LedImageUpdaterService.Models;

/// <summary>
/// Configuration for Huidu full-color controllers (e.g. BX A3L) driven through the
/// HDPlayer / Huidu SDK protocol. Mapped from the "HuiduLed" section of appsettings.json.
///
/// Connection model (server mode — the official Huidu SDK design):
///   The card is configured once (via HDPlayer / HDSet, "SDK server" / "服务器") to dial
///   into this service over TCP. The service therefore LISTENS on <see cref="ListenPort"/>;
///   when the card connects we complete the version handshake, read the card GUID, and can
///   then push programs (a single full-screen image) and read/set parameters.
///
/// This is the only LED transport in this application (Huidu / HDPlayer).
/// </summary>
public sealed class HuiduOptions
{
    public const string SectionName = "HuiduLed";

    /// <summary>Delivery over the HDPlayer protocol — the PC pushes the image to the card over TCP.</summary>
    public const string TransportTcp = "Tcp";
    /// <summary>Delivery over FTP — the image is uploaded to a fixed IP (share/programs/lists layout).</summary>
    public const string TransportFtp = "Ftp";

    /// <summary>Set false to disable all Huidu operations (e.g. macOS development).</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Image delivery transport. "Tcp" (default) pushes the full-screen image to the card
    /// over the HDPlayer protocol (card discovered via UDP or <see cref="CardIp"/>). "Ftp"
    /// uploads to a fixed controller IP via the FtpPublisher share/programs/lists layout —
    /// the target IP/credentials come from the FTP settings + screen.xml (ftp_ip).
    /// </summary>
    public string Transport { get; init; } = TransportTcp;

    /// <summary>Polling interval for the LedBoardService background send loop (seconds).</summary>
    [Range(5, 3600)]
    public int PollSeconds { get; init; } = 10;

    /// <summary>
    /// When false, LedBoardService will NOT automatically send images from the watch
    /// folder. Manual API endpoints (Swagger) and the tray "Send" button remain active.
    /// </summary>
    public bool AutoSend { get; init; } = true;

    /// <summary>
    /// TCP port this service listens on for the card to connect (SDK server port).
    /// Must match the "SDK server" port configured on the card. Huidu default is 6677.
    /// </summary>
    [Range(1, 65535)]
    public int ListenPort { get; init; } = 6677;

    /// <summary>
    /// Optional expected device id / Card ID (the card serial shown in HDPlayer, also
    /// the UDP discovery id). When set, the service only talks to this card; empty =
    /// accept any single card. Settable by hand in the settings window ("ID карты").
    /// </summary>
    public string DeviceId { get; init; } = "";

    /// <summary>
    /// Informational controller model name (e.g. "BX A3L", "BX C16L"). Huidu cards are
    /// not addressed by a numeric code, so this is metadata only — it does not change the
    /// protocol. Used by the settings UI to pre-fill a default panel size where known.
    /// </summary>
    public string Model { get; init; } = "";

    /// <summary>Screen pixel width.</summary>
    [Range(8, 4096)]
    public int ScreenWidth { get; init; } = 128;

    /// <summary>Screen pixel height.</summary>
    [Range(8, 4096)]
    public int ScreenHeight { get; init; } = 256;

    /// <summary>
    /// Path to the program XML template used to build the "SetPrograms" command.
    /// Placeholders are replaced at send time: ##GUID ##WIDTH ##HEIGHT ##FILE.
    /// Relative paths resolve from the application root.
    /// </summary>
    public string ProgramTemplatePath { get; init; } = "config/huidu/program-template.xml";

    /// <summary>
    /// When true (default), the service auto-discovers the card on the LAN over UDP
    /// (port 10001) and tells it to connect to this service's SDK server — no manual
    /// "SDK server" setup on the card is needed. Set false if you configure the card
    /// manually via HDPlayer/HDSet.
    /// </summary>
    public bool AutoConfigureViaUdp { get; init; } = true;

    /// <summary>
    /// Direct IP address of the Huidu card (e.g. "192.168.43.1" — in Wi-Fi AP mode the
    /// card is the gateway). The HDPlayer protocol connects TO the card, so this is the
    /// primary way to reach it. Leave empty to auto-discover via UDP broadcast (port 9527).
    /// </summary>
    public string CardIp { get; init; } = "";

    /// <summary>TCP port the card listens on for the HDPlayer protocol. Huidu default is 10001.</summary>
    [Range(1, 65535)]
    public int CardPort { get; init; } = 10001;

    /// <summary>Seconds to wait for the card to connect before a send is considered failed.</summary>
    [Range(1, 120)]
    public int ConnectWaitSeconds { get; init; } = 15;

    /// <summary>Socket receive/send timeout in milliseconds for a single SDK exchange.</summary>
    [Range(500, 60000)]
    public int IoTimeoutMs { get; init; } = 10000;

    /// <summary>Skip re-sending when the normalized image is identical to the last one sent.</summary>
    public bool SkipDuplicateUploads { get; init; } = true;

    /// <summary>Reject publish when the image size differs from ScreenWidth/ScreenHeight.</summary>
    public bool RejectSizeMismatchBeforePublish { get; init; } = false;

    // ─── Board-link monitor (auto-detect the card on the table's Wi-Fi) ───────

    /// <summary>
    /// When true (default), <c>BoardLinkMonitor</c> watches for the PC joining the
    /// LED card's Wi-Fi (an "island" AP with no internet — the AnyDesk scenario),
    /// auto-discovers the card and runs connectivity diagnostics.
    /// </summary>
    public bool AutoDetectOnApLink { get; init; } = true;

    /// <summary>
    /// When true (default), if the monitor finds the card on the board Wi-Fi at an IP
    /// that differs from <see cref="CardIp"/>, it applies the discovered IP at runtime
    /// and patches appsettings.json. Only fires on an island AP (private IP, no internet,
    /// exactly one card) so a deliberate static LAN IP is never clobbered.
    /// </summary>
    public bool AutoApplyCardIp { get; init; } = true;

    /// <summary>
    /// When true (default), if the monitor can reliably read the panel resolution from
    /// the card and it differs from <see cref="ScreenWidth"/>/<see cref="ScreenHeight"/>,
    /// it applies the detected size. No-op while panel-size read-back is unavailable.
    /// </summary>
    public bool AutoApplyScreenSize { get; init; } = true;

    /// <summary>Re-evaluation interval (seconds) for the board-link monitor's safety poll.</summary>
    [Range(5, 600)]
    public int BoardLinkPollSeconds { get; init; } = 30;
}
