using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace LedImageUpdaterService.UI;

/// <summary>
/// On-demand rates fetcher for the preview. Mirrors RatesFetcherService:
/// reads the active point's depCode from layout/points/index.json, calls the
/// quiq.kz API, and writes content/points/{id}/rates.json so the composer can
/// render a true-to-life preview.
/// </summary>
internal static class RatesApiClient
{
    private static string AppDir => AppContext.BaseDirectory;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly JsonSerializerOptions WriteOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    public static async Task<string?> FetchAsync(string pointId, string apiUrl)
    {
        var (depCode, ratesPath) = ResolvePoint(pointId);
        if (string.IsNullOrWhiteSpace(depCode))
            return $"Для точки '{pointId}' не задан depCode в layout/points/index.json.";

        List<DeptDto>? depts;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            await using var stream = await http.GetStreamAsync(apiUrl);
            depts = await JsonSerializer.DeserializeAsync<List<DeptDto>>(stream, JsonOpts);
        }
        catch (Exception ex)
        {
            return $"Не удалось получить данные с API:\n{ex.Message}";
        }

        if (depts is null || depts.Count == 0)
            return "API вернул пустой список отделений.";

        var dept = depts.FirstOrDefault(d =>
            string.Equals(d.Depcode, depCode, StringComparison.OrdinalIgnoreCase));
        if (dept is null)
            return $"Отделение '{depCode}' не найдено в ответе API.\nДоступные: {string.Join(", ", depts.Select(d => d.Depcode))}";

        var currencies = new Dictionary<string, (decimal buy, decimal sell)>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in dept.Currencies)
        {
            if (!c.IsActive) continue;
            var r = c.RateList.FirstOrDefault();
            if (r is null || r.Buy <= 0) continue;
            currencies[c.Code] = (r.Buy, r.Sale);
        }

        if (currencies.Count == 0)
            return "API не вернул активных курсов для этой точки.";

        WriteRates(ratesPath, currencies);
        return null; // success
    }

    private static (string depCode, string ratesPath) ResolvePoint(string pointId)
    {
        var defaultPath = Path.Combine(AppDir, "content", "points", pointId, "rates.json");
        var indexPath = Path.Combine(AppDir, "layout", "points", "index.json");
        if (!File.Exists(indexPath)) return ("", defaultPath);

        try
        {
            var root = JsonNode.Parse(File.ReadAllText(indexPath))!.AsObject();
            var points = root["points"]?.AsArray();
            if (points == null) return ("", defaultPath);

            foreach (var p in points)
            {
                var obj = p?.AsObject();
                if (obj == null) continue;
                if (!string.Equals(obj["id"]?.GetValue<string>(), pointId, StringComparison.OrdinalIgnoreCase))
                    continue;

                var depCode = obj["depCode"]?.GetValue<string>() ?? "";
                var rp = obj["ratesJsonPath"]?.GetValue<string>();
                var ratesPath = string.IsNullOrWhiteSpace(rp)
                    ? defaultPath
                    : (Path.IsPathRooted(rp) ? rp : Path.Combine(AppDir, rp));
                return (depCode, ratesPath);
            }
        }
        catch { }
        return ("", defaultPath);
    }

    private static void WriteRates(string ratesPath, Dictionary<string, (decimal buy, decimal sell)> currencies)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ratesPath) ?? ".");

        // Preserve existing labels and previous values for change arrows
        JsonNode? labels = null;
        var prev = new Dictionary<string, (decimal buy, decimal sell)>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(ratesPath))
        {
            try
            {
                var existing = JsonNode.Parse(File.ReadAllText(ratesPath))!.AsObject();
                labels = existing["labels"]?.DeepClone();
                var curs = existing["currencies"]?.AsObject();
                if (curs != null)
                    foreach (var kv in curs)
                    {
                        var b = kv.Value?["buy"]?.GetValue<decimal>() ?? 0;
                        var s = kv.Value?["sell"]?.GetValue<decimal>() ?? 0;
                        prev[kv.Key] = (b, s);
                    }
            }
            catch { }
        }

        labels ??= new JsonObject
        {
            ["buy"] = new JsonArray("Сатып аламыз", "Покупаем", "We buy"),
            ["sell"] = new JsonArray("Сатамыз", "Продаем", "We sell"),
        };

        var cursOut = new JsonObject();
        foreach (var kv in currencies)
        {
            var pv = prev.GetValueOrDefault(kv.Key);
            cursOut[kv.Key] = new JsonObject
            {
                ["buy"] = kv.Value.buy,
                ["sell"] = kv.Value.sell,
                ["prevBuy"] = pv.buy > 0 ? pv.buy : kv.Value.buy,
                ["prevSell"] = pv.sell > 0 ? pv.sell : kv.Value.sell,
            };
        }

        var output = new JsonObject { ["labels"] = labels, ["currencies"] = cursOut };
        File.WriteAllText(ratesPath, output.ToJsonString(WriteOpts));
    }

    // ─── DTO ────────────────────────────────────────────────────────────────
    private sealed class DeptDto
    {
        public string Depcode { get; init; } = "";
        public List<CurrencyDto> Currencies { get; init; } = [];
    }

    private sealed class CurrencyDto
    {
        public string Code { get; init; } = "";
        public bool IsActive { get; init; }
        public List<RateDto> RateList { get; init; } = [];
    }

    private sealed class RateDto
    {
        public decimal Buy { get; init; }
        public decimal Sale { get; init; }
    }
}
