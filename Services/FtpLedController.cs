using System.Net.Sockets;
using System.Security.Cryptography;
using LedImageUpdaterService.Models;
using Microsoft.Extensions.Options;

namespace LedImageUpdaterService.Services;

/// <summary>
/// <see cref="ILedController"/> implementation that delivers the full-screen image over FTP to a
/// fixed controller IP, using the existing <see cref="FtpPublisher"/> (share/programs/lists
/// layout). It is the alternative transport selected by <c>HuiduLed:Transport = "Ftp"</c>.
///
/// The target IP, FTP credentials, port and remote root come from the FTP settings and
/// <c>config/screen.xml</c> (read via <see cref="ScreenModelReader"/>): the settings UI writes the
/// chosen IP into <c>ftp_ip</c>. Connectivity / power / status operations are not part of the FTP
/// transport and degrade gracefully (mirroring the Huidu TCP controller's optional surface).
/// </summary>
public sealed class FtpLedController : ILedController
{
    private readonly ILogger<FtpLedController> _logger;
    private readonly ServiceOptions _options;
    private readonly HuiduOptions _huidu;
    private readonly ScreenModelReader _screenReader;
    private readonly LedPayloadBuilder _payloadBuilder;
    private readonly FtpPublisher _ftp;
    private readonly InMemoryLogStore _logStore;
    private readonly object _hashLock = new();

    private string? _lastSentImageHash;

    public FtpLedController(
        ILogger<FtpLedController> logger,
        IOptions<ServiceOptions> options,
        IOptions<HuiduOptions> huidu,
        ScreenModelReader screenReader,
        LedPayloadBuilder payloadBuilder,
        IEnumerable<IPublishStrategy> strategies,
        InMemoryLogStore logStore)
    {
        _logger = logger;
        _options = options.Value;
        _huidu = huidu.Value;
        _screenReader = screenReader;
        _payloadBuilder = payloadBuilder;
        _ftp = strategies.OfType<FtpPublisher>().FirstOrDefault()
               ?? throw new InvalidOperationException("FtpPublisher is not registered.");
        _logStore = logStore;

        Log(LogLevel.Information,
            $"[FTP-LED] Initializing FTP transport. ScreenXml={_options.ScreenXmlPath} FtpPort={_options.FtpPort} User='{_options.FtpUser}'");
    }

    private ScreenInfo ReadScreen()
    {
        var screenXml = ResolvePath(_options.ScreenXmlPath);
        var networkJson = string.IsNullOrWhiteSpace(_options.NetworkJsonPath)
            ? null
            : ResolvePath(_options.NetworkJsonPath);
        return _screenReader.Read(screenXml, _options.ForceRemoteRoot, networkJson);
    }

    // ─── Connectivity ─────────────────────────────────────────────────────────

    public Task<ConnectionCheckResult> CheckConnectionAsync(CancellationToken ct = default)
    {
        return Task.Run(async () =>
        {
            ScreenInfo screen;
            try
            {
                screen = ReadScreen();
            }
            catch (Exception ex)
            {
                return new ConnectionCheckResult(false,
                    $"FTP: не удалось прочитать screen.xml ({_options.ScreenXmlPath}): {ex.Message}");
            }

            foreach (var ip in screen.FtpIpCandidates.Where(s => !string.IsNullOrWhiteSpace(s)))
            {
                try
                {
                    using var tcp = new TcpClient();
                    var connect = tcp.ConnectAsync(ip, _options.FtpPort, ct).AsTask();
                    var done = await Task.WhenAny(connect, Task.Delay(3000, ct));
                    if (done == connect && tcp.Connected)
                        return new ConnectionCheckResult(true, $"FTP-контроллер доступен на {ip}:{_options.FtpPort}.");
                }
                catch { /* try next candidate */ }
            }

            return new ConnectionCheckResult(false,
                $"FTP-контроллер недоступен. Кандидаты: [{string.Join(", ", screen.FtpIpCandidates)}], порт {_options.FtpPort}.");
        }, ct);
    }

    // ─── Send image (full-screen) ─────────────────────────────────────────────

    public async Task<bool> SendImageAsync(string imagePath, CancellationToken ct = default)
        => (await SendImageWithStatusAsync(imagePath, ct)).Success;

    public async Task<LedSendStatus> SendImageWithStatusAsync(string imagePath, CancellationToken ct = default)
    {
        Log(LogLevel.Information, $"[FTP-LED] SendImage requested: {imagePath}");

        if (!File.Exists(imagePath))
            return LedSendStatus.Fail(LedSendErrorType.FileNotFound, $"File not found: {imagePath}");

        if (_huidu.SkipDuplicateUploads)
        {
            var hash = await ComputeSha256Async(imagePath, ct);
            lock (_hashLock)
            {
                if (_lastSentImageHash is not null && _lastSentImageHash == hash)
                {
                    Log(LogLevel.Information, "[FTP-LED] Skipped duplicate image (same hash as previous send).");
                    return LedSendStatus.Ok("Skipped duplicate image (already sent).", duplicateSkipped: true);
                }
            }
        }

        ScreenInfo screen;
        try
        {
            screen = ReadScreen();
        }
        catch (Exception ex)
        {
            return LedSendStatus.Fail(LedSendErrorType.SdkSendFailed,
                $"FTP: не удалось прочитать screen.xml ({_options.ScreenXmlPath}): {ex.Message}");
        }

        PublishPayload payload;
        try
        {
            payload = _payloadBuilder.Build(screen, imagePath);
        }
        catch (Exception ex)
        {
            return LedSendStatus.Fail(LedSendErrorType.SdkSendFailed, $"FTP: ошибка подготовки данных: {ex.Message}");
        }

        try
        {
            await _ftp.PublishAsync(screen, _options, payload, ct);
        }
        catch (Exception ex)
        {
            Log(LogLevel.Error, $"[FTP-LED] Send failed: {ex.Message}");
            return LedSendStatus.Fail(LedSendErrorType.SdkSendFailed, ex.Message);
        }

        if (_huidu.SkipDuplicateUploads)
        {
            var hash = await ComputeSha256Async(imagePath, ct);
            lock (_hashLock) _lastSentImageHash = hash;
        }

        Log(LogLevel.Information, "[FTP-LED] Image sent successfully (FTP).");
        return LedSendStatus.Ok("Image sent successfully (FTP).");
    }

    public Task<bool> ClearScreenAsync(CancellationToken ct = default)
    {
        lock (_hashLock) _lastSentImageHash = null;
        Log(LogLevel.Warning, "[FTP-LED] ClearScreen is not supported by the FTP transport.");
        return Task.FromResult(false);
    }

    // ─── Optional control surface (not supported over FTP) ─────────────────────

    public Task<ScreenStatusInfo?> GetScreenStatusAsync(CancellationToken ct = default)
        => Task.FromResult<ScreenStatusInfo?>(null);

    public Task<ControllerHardwareInfo?> GetControllerInfoAsync(CancellationToken ct = default)
        => Task.FromResult<ControllerHardwareInfo?>(null);

    public Task<FirmwareInfo?> GetFirmwareAsync(CancellationToken ct = default)
        => Task.FromResult<FirmwareInfo?>(null);

    public Task<bool> SetBrightnessAsync(int brightness, CancellationToken ct = default)
        => Task.FromResult(false);

    public Task<bool> SetPowerAsync(bool on, CancellationToken ct = default)
        => Task.FromResult(false);

    public Task<bool> RebootControllerAsync(CancellationToken ct = default)
        => Task.FromResult(false);

    // ─── Helpers ───────────────────────────────────────────────────────────────

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
        _logStore.Add(level, nameof(FtpLedController), message);
    }
}
