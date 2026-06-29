using System.Security.Cryptography;
using LedImageUpdaterService.Models;
using Microsoft.Extensions.Options;
using ImageSharp = SixLabors.ImageSharp.Image;

namespace LedImageUpdaterService.Services;

/// <summary>
/// <see cref="ILedController"/> implementation for Huidu full-color cards (e.g. BX A3L),
/// driven through the modern HDPlayer JSON-over-TCP protocol (<see cref="HuiduHdPlayerClient"/>).
///
/// It receives the SAME full-screen rendered image as the Onbon path; the difference is
/// only the delivery transport. Active only when <c>Led:Family = "Huidu"</c>.
///
/// Connection model (matches a real HDPlayer ⇄ card capture):
///   • The PC is the TCP client and connects TO the card at <c>CardIp:CardPort</c> (10001).
///     In Wi-Fi AP mode the card is the gateway (e.g. 192.168.43.1). If <c>CardIp</c> is
///     empty we auto-discover it over UDP broadcast (port 9527).
///
/// Send flow (per <see cref="HuiduHdPlayerClient.SendFullScreenImage"/>):
///   1. Connect to the card and complete the version handshake.
///   2. Send a one-program / one-area PlayTask JSON referencing the image by md5/size.
///   3. Run the file-sync upload (the card pulls only bytes it doesn't already have).
///   4. Wait for the card's final kSuccess.
/// </summary>
public sealed class HuiduLedController : ILedController, IDisposable
{
    private readonly ILogger<HuiduLedController> _logger;
    private readonly HuiduOptions _options;
    private readonly InMemoryLogStore _logStore;
    private readonly BoardLinkState _boardLink;
    private readonly object _hashLock = new();

    private string? _lastSentImageHash;
    private string? _discoveredCardIp;
    private bool _disposed;

    public HuiduLedController(
        ILogger<HuiduLedController> logger,
        IOptions<HuiduOptions> options,
        InMemoryLogStore logStore,
        BoardLinkState boardLink)
    {
        _logger = logger;
        _options = options.Value;
        _logStore = logStore;
        _boardLink = boardLink;

        Log(LogLevel.Information,
            $"[Huidu] Initializing. CardIp={(string.IsNullOrWhiteSpace(_options.CardIp) ? "(auto-discover)" : _options.CardIp)}:{_options.CardPort} " +
            $"Screen={_options.ScreenWidth}x{_options.ScreenHeight} Enabled={_options.Enabled}");

        if (!_options.Enabled)
            Log(LogLevel.Warning,
                "[Huidu] Disabled via configuration (HuiduLed:Enabled=false). All operations are no-ops.");
    }

    /// <summary>
    /// Resolves the card's IP: the configured <c>CardIp</c> if set, otherwise UDP discovery
    /// (port 9527). The discovered IP is cached so repeated sends don't re-broadcast.
    /// </summary>
    private string? ResolveCardIp()
    {
        // A card IP auto-applied at runtime by BoardLinkMonitor wins over config so an
        // operator who just switched onto the board Wi-Fi can send without a restart.
        if (!string.IsNullOrWhiteSpace(_boardLink.OverrideCardIp))
            return _boardLink.OverrideCardIp;

        if (!string.IsNullOrWhiteSpace(_options.CardIp))
            return _options.CardIp;

        if (_discoveredCardIp is not null)
            return _discoveredCardIp;

        var deviceId = string.IsNullOrWhiteSpace(_options.DeviceId) ? null : _options.DeviceId;
        var ip = HuiduHdPlayerDiscovery.FindCardIp(deviceId, 1500, msg => Log(LogLevel.Information, msg));
        if (ip is not null)
        {
            _discoveredCardIp = ip;
            Log(LogLevel.Information, $"[Huidu] Discovered card at {ip} (port {_options.CardPort}).");
        }
        return ip;
    }

    /// <summary>Screen size in effect: a panel size auto-applied at runtime, else configured.</summary>
    private (int width, int height) EffectiveScreen()
        => _boardLink.OverrideScreen ?? (_options.ScreenWidth, _options.ScreenHeight);

    // ─── Connectivity ────────────────────────────────────────────────────────

    public Task<ConnectionCheckResult> CheckConnectionAsync(CancellationToken ct = default)
    {
        if (!_options.Enabled)
            return Task.FromResult(new ConnectionCheckResult(false, "Huidu disabled (HuiduLed:Enabled=false)."));

        return Task.Run(() =>
        {
            var ip = ResolveCardIp();
            if (ip is null)
                return new ConnectionCheckResult(false,
                    "No Huidu card found. Set HuiduLed:CardIp (e.g. 192.168.43.1) or ensure the card " +
                    "answers UDP discovery on port 9527.");

            try
            {
                using var client = HuiduHdPlayerClient.Connect(ip, _options.CardPort, _options.IoTimeoutMs,
                    msg => Log(LogLevel.Information, msg));
                return new ConnectionCheckResult(true,
                    $"Huidu card reachable at {ip}:{_options.CardPort} (HDPlayer protocol).");
            }
            catch (Exception ex)
            {
                _discoveredCardIp = null; // force re-discovery next time
                return new ConnectionCheckResult(false,
                    $"Cannot reach Huidu card at {ip}:{_options.CardPort}: {ex.Message}");
            }
        }, ct);
    }

    // ─── Send image (full-screen) ────────────────────────────────────────────

    public async Task<bool> SendImageAsync(string imagePath, CancellationToken ct = default)
        => (await SendImageWithStatusAsync(imagePath, ct)).Success;

    public async Task<LedSendStatus> SendImageWithStatusAsync(string imagePath, CancellationToken ct = default)
    {
        Log(LogLevel.Information, $"[Huidu] SendImage requested: {imagePath}");

        if (!_options.Enabled)
            return LedSendStatus.Fail(LedSendErrorType.SdkNotInitialized,
                "Huidu disabled (HuiduLed:Enabled=false).");

        if (!File.Exists(imagePath))
            return LedSendStatus.Fail(LedSendErrorType.FileNotFound, $"File not found: {imagePath}");

        var sizeValidation = await ValidateImageSizeAsync(imagePath, ct);
        if (sizeValidation is not null)
            return sizeValidation;

        if (_options.SkipDuplicateUploads)
        {
            var hash = await ComputeSha256Async(imagePath, ct);
            lock (_hashLock)
            {
                if (_lastSentImageHash is not null && _lastSentImageHash == hash)
                {
                    Log(LogLevel.Information, "[Huidu] Skipped duplicate image (same hash as previous send).");
                    return LedSendStatus.Ok("Skipped duplicate image (already sent).", duplicateSkipped: true);
                }
            }
        }

        var cardIp = ResolveCardIp();
        if (cardIp is null)
            return LedSendStatus.Fail(LedSendErrorType.SdkSendFailed,
                "No Huidu card found. Set HuiduLed:CardIp or ensure the card answers UDP discovery (port 9527).");

        var result = await Task.Run(() =>
        {
            try
            {
                using var client = HuiduHdPlayerClient.Connect(cardIp, _options.CardPort, _options.IoTimeoutMs,
                    msg => Log(LogLevel.Information, msg));
                Log(LogLevel.Information, $"[Huidu] Connected to {cardIp}:{_options.CardPort}; sending PlayTask…");
                var (w, h) = EffectiveScreen();
                return client.SendFullScreenImage(imagePath, w, h);
            }
            catch (Exception ex)
            {
                _discoveredCardIp = null; // drop a stale discovered IP so the next send re-discovers
                return (ok: false, detail: $"Send to {cardIp}:{_options.CardPort} failed: {ex.Message}");
            }
        }, ct);

        if (result.ok)
        {
            if (_options.SkipDuplicateUploads)
            {
                var hash = await ComputeSha256Async(imagePath, ct);
                lock (_hashLock) _lastSentImageHash = hash;
            }
            Log(LogLevel.Information, "[Huidu] Image sent successfully.");
            return LedSendStatus.Ok("Image sent successfully (Huidu).");
        }

        Log(LogLevel.Error, $"[Huidu] Send failed: {result.detail}");
        return LedSendStatus.Fail(LedSendErrorType.SdkSendFailed, result.detail);
    }

    public Task<bool> ClearScreenAsync(CancellationToken ct = default)
    {
        if (!_options.Enabled) return Task.FromResult(false);

        return Task.Run(() =>
        {
            lock (_hashLock) _lastSentImageHash = null;

            var cardIp = ResolveCardIp();
            if (cardIp is null) return false;

            try
            {
                using var client = HuiduHdPlayerClient.Connect(cardIp, _options.CardPort, _options.IoTimeoutMs,
                    msg => Log(LogLevel.Information, msg));
                var (w, h) = EffectiveScreen();
                return client.ClearScreen(w, h).ok;
            }
            catch (Exception ex)
            {
                _discoveredCardIp = null;
                Log(LogLevel.Error, $"[Huidu] Clear failed: {ex.Message}");
                return false;
            }
        }, ct);
    }

    // ─── Optional control surface ────────────────────────────────────────────
    // The current Huidu SDK port focuses on the full-screen image path. These
    // operations are not implemented for the Huidu transport and degrade
    // gracefully (the REST API already maps null/false to a clear 503/500).

    public Task<ScreenStatusInfo?> GetScreenStatusAsync(CancellationToken ct = default)
        => Task.FromResult<ScreenStatusInfo?>(null);

    public Task<ControllerHardwareInfo?> GetControllerInfoAsync(CancellationToken ct = default)
        => Task.FromResult<ControllerHardwareInfo?>(null);

    public Task<FirmwareInfo?> GetFirmwareAsync(CancellationToken ct = default)
        => Task.FromResult<FirmwareInfo?>(null);

    public Task<bool> SetBrightnessAsync(int brightness, CancellationToken ct = default)
    {
        Log(LogLevel.Warning, "[Huidu] SetBrightness is not supported by the Huidu transport.");
        return Task.FromResult(false);
    }

    public Task<bool> SetPowerAsync(bool on, CancellationToken ct = default)
    {
        Log(LogLevel.Warning, "[Huidu] SetPower is not supported by the Huidu transport.");
        return Task.FromResult(false);
    }

    public Task<bool> RebootControllerAsync(CancellationToken ct = default)
    {
        Log(LogLevel.Warning, "[Huidu] Reboot is not supported by the Huidu transport.");
        return Task.FromResult(false);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private async Task<LedSendStatus?> ValidateImageSizeAsync(string imagePath, CancellationToken ct)
    {
        try
        {
            var info = await ImageSharp.IdentifyAsync(imagePath, ct);
            if (info is null) return null;
            var (screenW, screenH) = EffectiveScreen();
            if (info.Width == screenW && info.Height == screenH)
                return null;

            var msg = $"Image size {info.Width}x{info.Height} does not match screen " +
                      $"{screenW}x{screenH}.";
            if (_options.RejectSizeMismatchBeforePublish)
            {
                Log(LogLevel.Error, $"[Huidu] {msg}");
                return LedSendStatus.Fail(LedSendErrorType.InvalidImageSize, msg);
            }

            Log(LogLevel.Warning, $"[Huidu] {msg} Continuing (RejectSizeMismatchBeforePublish=false).");
            return null;
        }
        catch
        {
            return null; // never block the send on a metadata read failure
        }
    }

    private static string ResolvePath(string path)
        => Path.IsPathRooted(path) ? path : Path.Combine(AppContext.BaseDirectory, path);

    private static async Task<string> ComputeSha256Async(string filePath, CancellationToken ct)
    {
        await using var fs = File.OpenRead(filePath);
        return Convert.ToHexString(await SHA256.HashDataAsync(fs, ct));
    }

    private void Log(LogLevel level, string message)
    {
        _logger.Log(level, "{Message}", message);
        _logStore.Add(level, nameof(HuiduLedController), message);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // The SDK server is process-wide and reused across host restarts; do not stop
        // it on dispose (mirrors the Onbon process-global SDK lifecycle).
    }
}
