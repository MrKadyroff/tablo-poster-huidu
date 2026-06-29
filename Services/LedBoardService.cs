using LedImageUpdaterService.Models;
using Microsoft.Extensions.Options;

namespace LedImageUpdaterService.Services;

/// <summary>
/// Background service that periodically checks the watch folder for a new image
/// and sends it to the Huidu LED controller via <see cref="HuiduLedController"/>.
///
/// This service complements the existing FTP-based <see cref="Worker"/> — use it
/// when you want direct SDK delivery alongside (or instead of) FTP publishing.
///
/// Configuration:
///   LedUpdater:WatchFolder  — folder to watch for latest image
///   HuiduLed:PollSeconds    — how often to check (default 10 s)
///   HuiduLed:Enabled        — set false to disable on macOS
/// </summary>
public sealed class LedBoardService : BackgroundService
{
    private readonly ILogger<LedBoardService> _logger;
    private readonly ILedController _controller;
    private readonly ServiceOptions _serviceOptions;
    private readonly HuiduOptions _huiduOptions;
    private readonly InMemoryLogStore _logStore;
    private readonly TelegramNotifier _telegram;

    private string? _lastSentFilePath;
    private DateTime _lastSentWriteUtc;
    private long _tick;
    private DateTimeOffset? _lastTickAt;
    private DateTimeOffset? _lastAttemptAt;
    private DateTimeOffset? _lastSuccessAt;
    private DateTimeOffset? _lastFailureAt;
    private string? _lastAttemptImage;
    private string? _lastFailureErrorType;
    private string? _lastFailureDetails;
    // Notify Telegram once per failure episode (avoids spamming every poll).
    private bool _failureNotified;

    public LedBoardService(
        ILogger<LedBoardService> logger,
        ILedController controller,
        IOptions<ServiceOptions> serviceOptions,
        IOptions<HuiduOptions> huiduOptions,
        InMemoryLogStore logStore,
        TelegramNotifier telegram)
    {
        _logger = logger;
        _controller = controller;
        _serviceOptions = serviceOptions.Value;
        _huiduOptions = huiduOptions.Value;
        _logStore = logStore;
        _telegram = telegram;
    }

    // " (192.168.x.x)" when a card IP is configured, else "" — for clearer notifications.
    private string CardSuffix() =>
        string.IsNullOrWhiteSpace(_huiduOptions.CardIp) ? "" : $" ({_huiduOptions.CardIp})";

    private async Task NotifyAsync(string text, CancellationToken ct)
    {
        try { await _telegram.NotifyAsync(text, ct); }
        catch (Exception ex)
        {
            // TelegramNotifier already swallows errors; final safety net so notifications
            // can never disrupt the send loop.
            Log(LogLevel.Debug, $"[LedBoardService] Telegram notify error (ignored): {ex.Message}");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_serviceOptions.LayoutTestMode)
        {
            Log(LogLevel.Information,
                "[LedBoardService] LayoutTestMode=true — SDK auto-send disabled for safe layout testing.");
            return;
        }

        if (!_serviceOptions.PermanentInternet)
        {
            Log(LogLevel.Information,
                "[LedBoardService] PermanentInternet=false — таймерная автоотправка на табло отключена. " +
                "Курсы обновляются и изображение генерируется как обычно; отправка на табло — вручную " +
                "кнопкой «Отправить на табло» на вкладке «Дизайн» (или через API).");
            return;
        }

        if (!_huiduOptions.AutoSend)
        {
            Log(LogLevel.Information,
                "[LedBoardService] AutoSend=false — background polling disabled. " +
                "Use Swagger API endpoints to send images manually.");
            return;
        }

        var watchDirResolved = Path.GetFullPath(_serviceOptions.WatchFolder);
        var watchDirExists = Directory.Exists(watchDirResolved);
        
        Log(LogLevel.Information,
            $"[LedBoardService] Started. WatchFolder={_serviceOptions.WatchFolder} " +
            $"(resolved: {watchDirResolved}, exists={watchDirExists}) " +
            $"PollInterval={_huiduOptions.PollSeconds}s");

        // Give other services time to fully start before the first send attempt
        await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _tick++;
                _lastTickAt = DateTimeOffset.UtcNow;
                Log(LogLevel.Information,
                    $"[LedBoardService] Tick #{_tick}. Polling watch folder...");

                await PollAndSendAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log(LogLevel.Error, $"[LedBoardService] Unhandled error in poll loop: {ex.Message}");
            }

            await Task.Delay(TimeSpan.FromSeconds(_huiduOptions.PollSeconds), stoppingToken);
        }

        Log(LogLevel.Information, "[LedBoardService] Stopped.");
    }

    // ─── Core poll logic ─────────────────────────────────────────────────────

    private async Task PollAndSendAsync(CancellationToken ct)
    {
        var watchDir = _serviceOptions.WatchFolder;
        var watchDirResolved = Path.GetFullPath(watchDir);

        if (!Directory.Exists(watchDirResolved))
        {
            Log(LogLevel.Warning, $"[LedBoardService] WatchFolder does not exist: {watchDir} (resolved: {watchDirResolved})");
            return;
        }

        var newest = GetNewestImage(watchDirResolved);

        if (newest is null)
        {
            Log(LogLevel.Information, $"[LedBoardService] No images found in {watchDirResolved}");
            return;
        }

        // Skip if the file has not changed since the last successful send
        if (_serviceOptions.SkipIfUnchanged
            && newest.FullName == _lastSentFilePath
            && newest.LastWriteTimeUtc == _lastSentWriteUtc)
        {
            Log(LogLevel.Debug, $"[LedBoardService] Image unchanged, skipping: {newest.Name}");
            return;
        }

        Log(LogLevel.Information, $"[LedBoardService] New image detected: {newest.FullName}");
        _lastAttemptAt = DateTimeOffset.UtcNow;
        _lastAttemptImage = newest.FullName;

        // Use the same send pipeline as /api/led/upload for identical behavior.
        var status = await _controller.SendImageWithStatusAsync(newest.FullName, ct);

        if (status.Success)
        {
            _lastSentFilePath = newest.FullName;
            _lastSentWriteUtc = newest.LastWriteTimeUtc;
            if (status.DuplicateSkipped)
            {
                Log(LogLevel.Information,
                    $"[LedBoardService] Duplicate skipped: {newest.Name}. Details: {status.Message}");
            }
            else
            {
                Log(LogLevel.Information, $"[LedBoardService] Image sent successfully: {newest.Name}");
            }

            _lastSuccessAt = DateTimeOffset.UtcNow;
            _lastFailureAt = null;
            _lastFailureErrorType = null;
            _lastFailureDetails = null;

            // Board reachable again → close any standalone "no link" notice.
            WifiAlertBridge.RequestHide();

            // Notify once that the board recovered after a failure episode.
            if (_failureNotified)
            {
                _failureNotified = false;
                await NotifyAsync(
                    $"✅ eCash Tablo (Huidu){CardSuffix()}: связь восстановлена, курс отправлен на табло.", ct);
            }
        }
        else
        {
            _lastFailureAt = DateTimeOffset.UtcNow;
            _lastFailureErrorType = status.ErrorType.ToString();
            _lastFailureDetails = status.Message;

            var conn = await _controller.CheckConnectionAsync(ct);
            Log(LogLevel.Warning,
                $"[LedBoardService] Failed to send image: {newest.Name}. " +
                $"ErrorType={status.ErrorType}; Details={status.Message}; " +
                $"ConnectionOnline={conn.IsOnline}; ConnectionDetails={conn.Details}");

            // Push failed and the board is not reachable → ask the tray to show the notice.
            if (!conn.IsOnline) WifiAlertBridge.RequestShow();

            // Notify once per failure episode.
            if (!_failureNotified)
            {
                _failureNotified = true;
                await NotifyAsync(
                    $"❌ eCash Tablo (Huidu){CardSuffix()}: курс не отправлен на табло.\n" +
                    $"Причина: {status.ErrorType} — {status.Message}\n" +
                    $"Связь с табло: {(conn.IsOnline ? "есть" : "НЕТ — проверьте Wi-Fi табло")}.", ct);
            }
        }
    }

    /// <summary>Sends a specific image immediately, bypassing the changed-file check.</summary>
    public Task<bool> ForceUpdateAsync(string? imagePath = null, CancellationToken ct = default)
    {
        if (imagePath is not null)
        {
            Log(LogLevel.Information, $"[LedBoardService] ForceUpdate requested: {imagePath}");
            return SendForceViaUploadPipelineAsync(imagePath, ct);
        }

        var newest = GetNewestImage(_serviceOptions.WatchFolder);
        if (newest is null)
        {
            Log(LogLevel.Warning, "[LedBoardService] ForceUpdate: no images found in WatchFolder.");
            return Task.FromResult(false);
        }

        Log(LogLevel.Information, $"[LedBoardService] ForceUpdate: sending latest: {newest.FullName}");
        return SendForceViaUploadPipelineAsync(newest.FullName, ct);
    }

    private async Task<bool> SendForceViaUploadPipelineAsync(string imagePath, CancellationToken ct)
    {
        _lastAttemptAt = DateTimeOffset.UtcNow;
        _lastAttemptImage = imagePath;

        var status = await _controller.SendImageWithStatusAsync(imagePath, ct);
        if (!status.Success)
        {
            _lastFailureAt = DateTimeOffset.UtcNow;
            _lastFailureErrorType = status.ErrorType.ToString();
            _lastFailureDetails = status.Message;

            var conn = await _controller.CheckConnectionAsync(ct);
            Log(LogLevel.Warning,
                $"[LedBoardService] ForceUpdate failed. ErrorType={status.ErrorType}; Details={status.Message}; " +
                $"ConnectionOnline={conn.IsOnline}; ConnectionDetails={conn.Details}");

            // Push failed and the board is not reachable → ask the tray to show the notice.
            if (!conn.IsOnline) WifiAlertBridge.RequestShow();
            return false;
        }

        _lastSuccessAt = DateTimeOffset.UtcNow;
        _lastFailureAt = null;
        _lastFailureErrorType = null;
        _lastFailureDetails = null;

        // Board reachable again → close any standalone "no link" notice.
        WifiAlertBridge.RequestHide();
        return true;
    }

    public LedBoardRuntimeStatus GetRuntimeStatus() => new(
        Enabled: _huiduOptions.Enabled,
        AutoSend: _huiduOptions.AutoSend,
        LayoutTestMode: _serviceOptions.LayoutTestMode,
        PollSeconds: _huiduOptions.PollSeconds,
        Tick: _tick,
        LastTickAt: _lastTickAt,
        LastAttemptAt: _lastAttemptAt,
        LastAttemptImage: _lastAttemptImage,
        LastSuccessAt: _lastSuccessAt,
        LastFailureAt: _lastFailureAt,
        LastFailureErrorType: _lastFailureErrorType,
        LastFailureDetails: _lastFailureDetails,
        WatchFolder: _serviceOptions.WatchFolder,
        LastSentFilePath: _lastSentFilePath,
        LastSentWriteUtc: _lastSentWriteUtc == default ? null : new DateTimeOffset(_lastSentWriteUtc, TimeSpan.Zero));

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static FileInfo? GetNewestImage(string folder)
    {
        if (!Directory.Exists(folder)) return null;

        var files = new DirectoryInfo(folder)
            .EnumerateFiles("*", SearchOption.TopDirectoryOnly)
            .Where(f => f.Extension.ToLowerInvariant() is ".bmp" or ".png" or ".jpg" or ".jpeg")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault();

        return files;
    }

    private void Log(LogLevel level, string message)
    {
        _logger.Log(level, "{Message}", message);
        _logStore.Add(level, nameof(LedBoardService), message);
    }
}

public sealed record LedBoardRuntimeStatus(
    bool Enabled,
    bool AutoSend,
    bool LayoutTestMode,
    int PollSeconds,
    long Tick,
    DateTimeOffset? LastTickAt,
    DateTimeOffset? LastAttemptAt,
    string? LastAttemptImage,
    DateTimeOffset? LastSuccessAt,
    DateTimeOffset? LastFailureAt,
    string? LastFailureErrorType,
    string? LastFailureDetails,
    string WatchFolder,
    string? LastSentFilePath,
    DateTimeOffset? LastSentWriteUtc);
