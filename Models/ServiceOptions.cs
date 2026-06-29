using System.ComponentModel.DataAnnotations;

namespace LedImageUpdaterService.Models;

public sealed class ServiceOptions
{
    public const string SectionName = "LedUpdater";

    public const string RunModeUploader = "Uploader";
    public const string RunModeRenderOnly = "RenderOnly";
    // Render image from rates + template, then immediately publish to controller.
    public const string RunModeFull = "Full";

    public const string PublishModeWifiFtp = "WifiFtp";
    public const string PublishModeWifiRelay = "WifiRelay";

    [Required]
    public string RunMode { get; init; } = RunModeUploader;

    [Required]
    public string PublishMode { get; init; } = PublishModeWifiFtp;

    [Required]
    public required string WatchFolder { get; init; }

    [Required]
    public required string ScreenXmlPath { get; init; }

    [Range(5, 3600)]
    public int PollSeconds { get; init; } = 10;

    [Required]
    public required string FtpUser { get; init; }

    [Required]
    public required string FtpPassword { get; init; }

    public bool UseTls { get; init; }

    [Range(1, 65535)]
    public int FtpPort { get; init; } = 21;

    // If true, upload only when the newest file changed since last successful publish.
    public bool SkipIfUnchanged { get; init; } = true;

    // If true, compose/publish loops ignore SkipIfUnchanged and process every poll cycle.
    // Useful for fast layout tuning when you want constant re-render.
    public bool ForceComposeEveryPoll { get; init; }

    // If true, force RenderOnly behavior for safe layout testing and disable board auto-send.
    public bool LayoutTestMode { get; init; }

    // If true (default), the point has permanent (wired) internet: rates are fetched,
    // the image is rendered AND it is auto-sent to the board on the poll timer (current behavior).
    // If false, the point has no stable internet: rates are still fetched and the image is still
    // rendered, but the board is NOT auto-sent on a timer — the operator pushes it manually with the
    // "Отправить на табло" button on the Design tab. This avoids the crash loop on flaky connections.
    public bool PermanentInternet { get; init; } = true;

    // The board's Wi-Fi SSID (the access point the operator joins on points without a wired
    // link). Used only to gate the standalone "no link" tray notice: it appears solely when an
    // SSID is configured AND PermanentInternet is on. Empty = never show the notice.
    public string WifiSsid { get; init; } = "";

    // Optional fixed remote root (example: NET_00000199). If empty, service takes value from screen.xml.
    public string? ForceRemoteRoot { get; init; }

    // Enforce that route to controller goes via Wi-Fi adapter.
    public bool EnforceWifiOnly { get; init; } = true;

    // Additional safety: block non-private target addresses.
    public bool RequirePrivateAddress { get; init; } = true;

    // Used by WifiRelay mode. Usually this is LedYQ/ftp_temp.
    public string? RelayOutRoot { get; init; }

    // Optional: GET this URL after each successful FTP publish to trigger controller reload.
    // Example for Onbon BX-Y2: "http://192.168.22.2/cmd?action=updatefile"
    // Leave empty to skip (controller will detect file changes on its own).
    public string? ControllerReloadUrl { get; init; }

    // Optional path to network.json with all known controller IPs for auto-discovery.
    // If set, the service will probe all IPs and pick the reachable one automatically.
    public string? NetworkJsonPath { get; init; }

    // Used by RenderOnly mode.
    public string ComposeConfigPath { get; init; } = "layout/points/megapark.compose.json";
    public string RatesJsonPath { get; init; } = "content/points/megapark/rates.json";

    // Live rate fetching from quiq.kz API.
    // All points are updated from the same API call; see layout/points/index.json for depCode mapping.
    public string RatesApiUrl { get; init; } = "https://api.quiq.kz/Department/getDepsLandingInfo";
    public int RatesFetchIntervalMinutes { get; init; } = 10;
}
