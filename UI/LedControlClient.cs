using System.Net.Http;
using System.Net.Http.Json;

namespace LedImageUpdaterService.UI;

/// <summary>
/// Thin client for the in-process REST API used by the settings UI to issue
/// operational commands to the LED board (power on/off, reboot).
/// </summary>
internal static class LedControlClient
{
    private static string BaseUrl(string? urls)
    {
        var url = (urls ?? "").Split(';').FirstOrDefault(u => !string.IsNullOrWhiteSpace(u))?.Trim();
        if (string.IsNullOrWhiteSpace(url)) url = "http://localhost:5050";
        return url.Replace("0.0.0.0", "localhost").Replace("+", "localhost").TrimEnd('/');
    }

    public static async Task<(bool isOnline, string details)> CheckConnectionAsync(string? urls)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var resp = await http.GetAsync($"{BaseUrl(urls)}/api/led/connection");
            var text = await resp.Content.ReadAsStringAsync();
            // Parse {"isOnline":true,"details":"..."}
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(text);
                var root = doc.RootElement;
                var online = root.TryGetProperty("isOnline", out var p) && p.GetBoolean();
                var details = root.TryGetProperty("details", out var d) ? d.GetString() ?? "" : text;
                return (online, details);
            }
            catch
            {
                return (resp.IsSuccessStatusCode, text);
            }
        }
        catch (Exception ex)
        {
            return (false, $"Сервис недоступен: {ex.Message}");
        }
    }

    /// <summary>
    /// Forces a board-link re-scan (<c>POST /api/led/board-link/recheck</c>) and returns the
    /// card IP the service found on the table's Wi-Fi, the card's discovery id (carries the
    /// model, e.g. "C16L-24-0F5A3"), and the human-readable recommendation.
    /// <paramref name="ip"/> is the discovered/applied card IP (null if nothing was found).
    /// </summary>
    public static async Task<(bool ok, string? ip, string? cardId, string message)> DetectCardIpAsync(string? urls)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var resp = await http.PostAsync($"{BaseUrl(urls)}/api/led/board-link/recheck", content: null);
            var text = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
                return (false, null, null, string.IsNullOrWhiteSpace(text) ? resp.StatusCode.ToString() : text);

            using var doc = System.Text.Json.JsonDocument.Parse(text);
            var root = doc.RootElement;

            string? Str(string name) =>
                root.TryGetProperty(name, out var p) && p.ValueKind == System.Text.Json.JsonValueKind.String
                    ? p.GetString() : null;

            // Prefer the applied IP (already active at runtime), else the candidate the scan found.
            var ip = Str("appliedCardIp") ?? Str("candidateCardIp");
            var cardId = Str("discoveredCardId");
            var message = Str("recommendation") ?? text;
            bool tcpOk = root.TryGetProperty("tcpOk", out var t) && t.ValueKind == System.Text.Json.JsonValueKind.True;

            // "ok" even without a fresh IP if the configured one already matches and is reachable.
            return (ip is not null || tcpOk, ip, cardId, message);
        }
        catch (Exception ex)
        {
            return (false, null, null, $"Сервис недоступен: {ex.Message}");
        }
    }

    public static Task<(bool ok, string message)> SetPowerAsync(string? urls, bool on)
        => PostAsync(urls, "api/led/power", new { on });

    public static Task<(bool ok, string message)> RebootAsync(string? urls)
        => PostAsync(urls, "api/led/reboot", new { });

    /// <summary>
    /// Manually pushes the latest rendered image to the LED board (no auto-send timer needed).
    /// Used by the "Отправить на табло" button on the Design tab for points without permanent internet.
    /// </summary>
    public static Task<(bool ok, string message)> SendToBoardAsync(string? urls)
        => PostAsync(urls, "api/led/update", new { });

    private static async Task<(bool ok, string message)> PostAsync(string? urls, string path, object body)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
            var resp = await http.PostAsJsonAsync($"{BaseUrl(urls)}/{path}", body);
            var text = await resp.Content.ReadAsStringAsync();
            return (resp.IsSuccessStatusCode, string.IsNullOrWhiteSpace(text) ? resp.StatusCode.ToString() : text);
        }
        catch (Exception ex)
        {
            return (false, $"Сервис недоступен: {ex.Message}");
        }
    }
}
