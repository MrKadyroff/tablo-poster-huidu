using Microsoft.Extensions.Configuration;

namespace LedImageUpdaterService.Services;

/// <summary>
/// Describes the boards (LED screens) this installation drives. One board is the
/// normal case (<c>ActivePointId</c>); an optional second one (<c>SecondPointId</c>) runs
/// next to it as an independent copy of the whole pipeline — its own point config,
/// layout, rates, controller and API port. The two never share state.
/// </summary>
public static class BoardTopology
{
    /// <summary>
    /// API address of the second board: the primary address with the port moved up by one
    /// (http://localhost:5050 → http://localhost:5051). An explicit <c>SecondUrls</c> wins.
    /// </summary>
    public static string SecondUrls(string primaryUrls, string? explicitSecond = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitSecond)) return explicitSecond.Trim();

        var first = (primaryUrls ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? "http://localhost:5050";
        if (!Uri.TryCreate(first.Replace("*", "localhost").Replace("+", "localhost"), UriKind.Absolute, out var uri))
            return "http://localhost:5051";

        int port = uri.IsDefaultPort ? (uri.Scheme == "https" ? 443 : 80) : uri.Port;
        var builder = new UriBuilder(uri) { Port = port + 1 };
        return builder.Uri.GetLeftPart(UriPartial.Authority);
    }

    /// <summary>Controller IP configured for a point (config/points/{id}.json), or null if unknown.</summary>
    public static string? ControllerIpOf(string contentRoot, string pointId)
    {
        try
        {
            var path = Path.Combine(contentRoot, "config", "points", $"{pointId}.json");
            if (!File.Exists(path)) return null;
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path),
                new System.Text.Json.JsonDocumentOptions { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true });
            // Huidu card IP (TCP transport) first, then the FTP/Onbon controller IP.
            if (doc.RootElement.TryGetProperty("HuiduLed", out var hd)
                && hd.TryGetProperty("CardIp", out var card)
                && !string.IsNullOrWhiteSpace(card.GetString()))
                return card.GetString()!.Trim();
            return doc.RootElement.TryGetProperty("OnbonLed", out var ob)
                   && ob.TryGetProperty("ControllerIp", out var ip)
                ? ip.GetString()?.Trim()
                : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Two boards with the same controller IP would hit the same card: the second board's picture
    /// would land on the first board. Returns the clash description, or null when all is well.
    /// </summary>
    public static string? FindIpClash(string contentRoot, string firstPoint, string secondPoint)
    {
        var a = ControllerIpOf(contentRoot, firstPoint);
        var b = ControllerIpOf(contentRoot, secondPoint);
        return !string.IsNullOrEmpty(a) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase)
            ? $"Табло 1 («{firstPoint}») и табло 2 («{secondPoint}») имеют один и тот же IP контроллера {a}."
            : null;
    }

    /// <summary>
    /// Point id of the second board, or null when none is configured (or it is invalid:
    /// empty, same as the first board, or without a point config file).
    /// </summary>
    public static string? ResolveSecondPointId(IConfiguration config, string contentRoot)
    {
        var second = config["SecondPointId"]?.Trim();
        if (string.IsNullOrEmpty(second)) return null;
        if (string.Equals(second, config["ActivePointId"]?.Trim(), StringComparison.OrdinalIgnoreCase)) return null;
        return File.Exists(Path.Combine(contentRoot, "config", "points", $"{second}.json")) ? second : null;
    }
}
