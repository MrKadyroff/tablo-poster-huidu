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
        var ip = HuiduHdPlayerDiscovery.FindCardIp(deviceId, 1500, msg => Log(LogLevel.Information, msg), _options.UdpDiscoveryPort);
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

    /// <summary>
    /// True when the card speaks the C-series binary protocol (e.g. C16L) rather than the
    /// A-series JSON "PlayTask" protocol. C-series cards serve on TCP 9527; A-series on 10001.
    /// A C-prefixed device id / model is also taken as C-series.
    /// </summary>
    private bool UseCSeriesProtocol()
    {
        if (_options.CardPort == HuiduCSeriesClient.DefaultPort) return true;
        return IsCSeriesName(_options.DeviceId) || IsCSeriesName(_options.Model);
    }

    private static bool IsCSeriesName(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        // "C16L-24-0F5A3", "BX C16L" → C-series.  "A3L-25-…", "BX A3L" → A-series.
        var token = s.Trim().Replace("BX ", "").TrimStart();
        return token.StartsWith("C", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Resolves the (DeviceModel, cardId) to stamp into the C-series scene XML. Prefers a live
    /// UDP discovery of the card (its id carries the model, e.g. "C16L-24-0F5A3"), falling back
    /// to the configured DeviceId/Model. Never throws — identity is best-effort metadata.
    /// </summary>
    private (string model, string? cardId) ResolveCardIdentity(string cardIp)
    {
        try
        {
            var cards = HuiduHdPlayerDiscovery.Search(800, _ => { }, _options.UdpDiscoveryPort);
            var match = cards.FirstOrDefault(c => c.Ip.ToString() == cardIp);
            var id = !string.IsNullOrWhiteSpace(match.Id) ? match.Id
                   : (cards.Count == 1 ? cards[0].Id : null);
            if (!string.IsNullOrWhiteSpace(id))
                return (ModelFromId(id) ?? CleanModel(_options.Model), id);
        }
        catch { /* fall back to config */ }

        var cfgId = string.IsNullOrWhiteSpace(_options.DeviceId) ? null : _options.DeviceId;
        return (ModelFromId(cfgId) ?? CleanModel(_options.Model), cfgId);
    }

    /// <summary>"C16L-24-0F5A3" → "C16L"; null/blank → null.</summary>
    private static string? ModelFromId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        var prefix = id.Split('-')[0].Trim();
        return prefix.Length > 0 ? prefix : null;
    }

    /// <summary>"BX C16L" → "C16L"; blank → "" .</summary>
    private static string CleanModel(string? model)
        => string.IsNullOrWhiteSpace(model) ? "" : model.Replace("BX ", "").Trim();

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
                if (UseCSeriesProtocol())
                {
                    using var c = HuiduCSeriesClient.Connect(ip, _options.CardPort, _options.IoTimeoutMs,
                        msg => Log(LogLevel.Information, msg));
                    return new ConnectionCheckResult(true,
                        $"Huidu card reachable at {ip}:{_options.CardPort} (C-series protocol).");
                }
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
                var (w, h) = EffectiveScreen();
                if (UseCSeriesProtocol())
                {
                    var (model, cardId) = ResolveCardIdentity(cardIp);
                    using var c = HuiduCSeriesClient.Connect(cardIp, _options.CardPort, _options.IoTimeoutMs,
                        msg => Log(LogLevel.Information, msg));
                    Log(LogLevel.Information,
                        $"[Huidu] Connected to {cardIp}:{_options.CardPort} (C-series, model={model}, id={cardId ?? "?"}); uploading scene…");
                    return c.SendFullScreenImage(imagePath, w, h, model, cardId);
                }

                using var client = HuiduHdPlayerClient.Connect(cardIp, _options.CardPort, _options.IoTimeoutMs,
                    msg => Log(LogLevel.Information, msg));
                Log(LogLevel.Information, $"[Huidu] Connected to {cardIp}:{_options.CardPort}; sending PlayTask…");
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

            if (UseCSeriesProtocol())
            {
                // The C-series binary protocol has no separate "clear" in the capture; clearing
                // is done by sending a new (blank) scene. Not implemented yet — log and no-op
                // rather than speak the wrong protocol to the card.
                Log(LogLevel.Warning, "[Huidu] ClearScreen is not supported on C-series cards yet.");
                return false;
            }

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
