using System.Net.Http.Json;
using LedImageUpdaterService.Models;
using Microsoft.Extensions.Options;

namespace LedImageUpdaterService.Services;

/// <summary>
/// Sends operational notifications to Telegram via the Bot API.
///
/// Designed to be completely "harmless" to its callers: every send is wrapped in a
/// try/catch with a short timeout, so a slow or broken network can never throw into
/// (or stall) the background send loop. When notifications are disabled or not
/// configured, calls become silent no-ops.
/// </summary>
public sealed class TelegramNotifier
{
    private readonly ILogger<TelegramNotifier> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TelegramOptions _options;
    private readonly InMemoryLogStore _logStore;

    public TelegramNotifier(
        ILogger<TelegramNotifier> logger,
        IHttpClientFactory httpClientFactory,
        IOptions<TelegramOptions> options,
        InMemoryLogStore logStore)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logStore = logStore;
    }

    /// <summary>True when a token and chat id are configured and notifications are enabled.</summary>
    public bool IsConfigured =>
        _options.Enabled
        && !string.IsNullOrWhiteSpace(_options.BotToken)
        && !string.IsNullOrWhiteSpace(_options.ChatId);

    /// <summary>
    /// Sends a plain-text message. Never throws. Returns true on a successful send.
    /// </summary>
    public async Task<bool> NotifyAsync(string text, CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            Log(LogLevel.Debug, "[Telegram] Notification skipped (disabled or not configured).");
            return false;
        }

        try
        {
            using var http = _httpClientFactory.CreateClient("telegram");
            http.Timeout = TimeSpan.FromSeconds(10);

            var url = $"https://api.telegram.org/bot{_options.BotToken}/sendMessage";
            var payload = new
            {
                chat_id = _options.ChatId,
                text,
                disable_web_page_preview = true,
            };

            using var resp = await http.PostAsJsonAsync(url, payload, ct);
            if (resp.IsSuccessStatusCode)
            {
                Log(LogLevel.Information, "[Telegram] Notification sent.");
                return true;
            }

            var body = await resp.Content.ReadAsStringAsync(ct);
            Log(LogLevel.Warning, $"[Telegram] Send failed: HTTP {(int)resp.StatusCode}. {body}");
            return false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Host shutting down — not an error worth logging loudly.
            return false;
        }
        catch (Exception ex)
        {
            Log(LogLevel.Warning, $"[Telegram] Send failed: {ex.Message}");
            return false;
        }
    }

    private void Log(LogLevel level, string message)
    {
        _logger.Log(level, "{Message}", message);
        _logStore.Add(level, nameof(TelegramNotifier), message);
    }
}
