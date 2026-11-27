using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace LedImageUpdaterService.Services;

/// <summary>
/// Best-effort read of the panel size from the card itself. HDPlayer normally knows the size from
/// its own project, so the network protocols are not documented to report it; this asks the card
/// the ways it might (A-series: JSON "Get" with several tags; C-series: scans the handshake replies)
/// and accepts a result only when width and height are both plausible. Every raw reply is written to
/// <c>steps</c> so a card that does answer can be recognised from the log.
/// </summary>
internal static class HuiduScreenProbe
{
    private static readonly string[] GetTags =
        ["Screen", "ScreenInfo", "ScreenParam", "Display", "DeviceInfo", "Device", "Hardware"];

    private static bool Plausible(int w, int h) => w is >= 16 and <= 16384 && h is >= 16 and <= 16384;

    public static (int w, int h)? Probe(string ip, int port, int timeoutMs, List<string> steps)
    {
        try
        {
            return port == HuiduCSeriesClient.DefaultPort
                ? ProbeCSeries(ip, port, timeoutMs, steps)
                : ProbeHdPlayer(ip, port, timeoutMs, steps);
        }
        catch (Exception ex)
        {
            steps.Add($"Чтение размера панели: ошибка {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static (int w, int h)? ProbeHdPlayer(string ip, int port, int timeoutMs, List<string> steps)
    {
        using var client = HuiduHdPlayerClient.Connect(ip, port, timeoutMs, _ => { });
        foreach (var tag in GetTags)
        {
            string? reply;
            try { reply = client.QueryGet(tag); }
            catch (Exception ex)
            {
                steps.Add($"Get '{tag}': нет ответа ({ex.Message}) — дальше не пробую.");
                break;
            }
            if (reply is null) continue;
            steps.Add($"Get '{tag}': {Trunc(reply, 300)}");
            if (FindInJson(reply) is { } size)
            {
                steps.Add($"✓ Размер панели из ответа карты (Get '{tag}'): {size.w}x{size.h}.");
                return size;
            }
        }
        steps.Add("Карта не сообщила размер панели (HDPlayer-протокол).");
        return null;
    }

    private static (int w, int h)? ProbeCSeries(string ip, int port, int timeoutMs, List<string> steps)
    {
        using var client = HuiduCSeriesClient.Connect(ip, port, timeoutMs, _ => { });
        foreach (var (cmd, body) in client.HandshakeReplies)
        {
            if (body.Length == 0) continue;
            string text = Encoding.UTF8.GetString(body).Replace('\0', ' ');
            steps.Add($"Ответ карты 0x{cmd:x4} ({body.Length} Б): {Trunc(text, 200)}");
            if (FindInText(text) is { } size)
            {
                steps.Add($"✓ Размер панели из ответа карты (0x{cmd:x4}): {size.w}x{size.h}.");
                return size;
            }
        }
        steps.Add("Карта не сообщила размер панели (C-серия).");
        return null;
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    // ── parsing ──────────────────────────────────────────────────────────────

    private static (int w, int h)? FindInJson(string json)
    {
        try { return Walk(JsonNode.Parse(json)); }
        catch { return FindInText(json); }
    }

    private static (int w, int h)? Walk(JsonNode? node)
    {
        if (node is JsonObject o)
        {
            int? w = null, h = null;
            foreach (var kv in o)
            {
                string k = kv.Key.ToLowerInvariant();
                int? v = kv.Value is JsonValue jv && jv.TryGetValue<int>(out var iv) ? iv : null;
                if (v is null) continue;
                if (k is "width" or "screenwidth" or "w") w = v;
                else if (k is "height" or "screenheight" or "h") h = v;
            }
            if (w is not null && h is not null && Plausible(w.Value, h.Value)) return (w.Value, h.Value);
            foreach (var kv in o)
                if (Walk(kv.Value) is { } inner) return inner;
        }
        else if (node is JsonArray a)
        {
            foreach (var item in a)
                if (Walk(item) is { } inner) return inner;
        }
        return null;
    }

    private static readonly Regex WidthHeight = new(
        "(?:width|screenwidth|\\bw)\\W{0,4}(\\d{2,5})\\D{1,40}?(?:height|screenheight|\\bh)\\W{0,4}(\\d{2,5})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static (int w, int h)? FindInText(string text)
    {
        var m = WidthHeight.Match(text);
        if (m.Success && int.TryParse(m.Groups[1].Value, out var w) && int.TryParse(m.Groups[2].Value, out var h) && Plausible(w, h))
            return (w, h);
        return null;
    }
}
