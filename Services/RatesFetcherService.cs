using System.Text.Json;
using LedImageUpdaterService.Models;
using Microsoft.Extensions.Options;

namespace LedImageUpdaterService.Services;

/// <summary>
/// Background service that fetches live exchange rates from quiq.kz every
/// <see cref="ServiceOptions.RatesFetchIntervalMinutes"/> minutes.
///
/// One HTTP request fetches ALL departments from the API.
/// Each enabled point in layout/points/index.json is matched by its <c>depCode</c>
/// and its own <c>rates.json</c> is written independently.
/// </summary>
public sealed class RatesFetcherService : BackgroundService
{
    private readonly ILogger<RatesFetcherService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ServiceOptions _options;

    // Loaded once at startup from layout/points/index.json
    private IReadOnlyList<PointEntry> _points = [];

    public RatesFetcherService(
        ILogger<RatesFetcherService> logger,
        IHttpClientFactory httpClientFactory,
        IOptions<ServiceOptions> options)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _points = LoadPointRegistry();

        if (_points.Count == 0)
        {
            _logger.LogWarning("RatesFetcherService: no enabled points found in layout/points/index.json — rates fetching is disabled.");
            return;
        }

        _logger.LogInformation("RatesFetcherService: managing rates for {Count} point(s): {Ids}",
            _points.Count, string.Join(", ", _points.Select(p => p.Id)));

        // Fetch immediately on start, then on a fixed interval.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await FetchAndSaveAllAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(
                    "API курсов недоступно (нет интернета или сервер не отвечает). " +
                    "URL: {Url}. Ошибка: {Msg}. " +
                    "Повтор через {Min} мин. Старые курсы продолжат использоваться.",
                    _options.RatesApiUrl, ex.Message, _options.RatesFetchIntervalMinutes);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Ошибка получения курсов. Повтор через {Min} мин.",
                    _options.RatesFetchIntervalMinutes);
            }

            await Task.Delay(
                TimeSpan.FromMinutes(_options.RatesFetchIntervalMinutes),
                stoppingToken);
        }
    }

    private async Task FetchAndSaveAllAsync(CancellationToken ct)
    {
        _logger.LogInformation("Запрашиваю курсы: {Url}", _options.RatesApiUrl);

        using var http = _httpClientFactory.CreateClient("rates");
        using var response = await http.GetAsync(_options.RatesApiUrl, ct);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}. " +
                $"Сервер {_options.RatesApiUrl} вернул ошибку.");

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        List<DeptDto>? depts;
        try
        {
            depts = await JsonSerializer.DeserializeAsync<List<DeptDto>>(stream, JsonOpts, ct);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                $"API вернул неожиданный формат данных. Детали: {ex.Message}");
        }

        if (depts is null || depts.Count == 0)
            throw new InvalidDataException("API вернул пустой список отделений.");

        // Build lookup by depCode
        var deptMap = depts.ToDictionary(
            d => d.Depcode,
            d => d,
            StringComparer.OrdinalIgnoreCase);

        foreach (var point in _points)
        {
            if (!deptMap.TryGetValue(point.DepCode, out var dept))
            {
                _logger.LogWarning(
                    "Точка '{Id}': отделение '{DepCode}' не найдено в API. " +
                    "Доступные: {Available}. Rates.json не обновлён.",
                    point.Id, point.DepCode,
                    string.Join(", ", deptMap.Keys.Select(k => $"'{k}'")));
                continue;
            }

            await WritePointRatesAsync(point, dept, ct);
        }
    }

    private async Task WritePointRatesAsync(PointEntry point, DeptDto dept, CancellationToken ct)
    {
        // Build currency map from API response (active && has rates)
        var currencies = new Dictionary<string, RatePair>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in dept.Currencies)
        {
            if (!c.IsActive) continue;
            var r = c.RateList.FirstOrDefault();
            if (r is null || r.Buy <= 0) continue;
            currencies[c.Code] = new RatePair(r.Buy, r.Sale);
        }

        _logger.LogInformation("Точка '{Id}' ({DepCode}): {Count} активных курсов",
            point.Id, point.DepCode, currencies.Count);

        var ratesPath = ResolvePath(point.RatesJsonPath);
        Directory.CreateDirectory(Path.GetDirectoryName(ratesPath) ?? ".");

        var existingLabels = TryReadLabels(ratesPath);
        var existingRates = TryReadRates(ratesPath);

        var output = new
        {
            labels = existingLabels,
            currencies = currencies.ToDictionary(
                kv => kv.Key,
                kv =>
                {
                    var prev = existingRates.GetValueOrDefault(kv.Key);
                    return new
                    {
                        buy = kv.Value.Buy,
                        sell = kv.Value.Sell,
                        prevBuy = prev.Buy > 0 ? prev.Buy : kv.Value.Buy,
                        prevSell = prev.Sell > 0 ? prev.Sell : kv.Value.Sell
                    };
                })
        };

        var tmpPath = ratesPath + ".tmp";
        await using (var fs = File.Open(tmpPath, FileMode.Create, FileAccess.Write))
        {
            await JsonSerializer.SerializeAsync(fs, output, WriterOpts, ct);
        }

        File.Move(tmpPath, ratesPath, overwrite: true);
        _logger.LogInformation("rates.json обновлён → {Path}", ratesPath);
    }

    // ─── Point registry ───────────────────────────────────────────────────────

    private IReadOnlyList<PointEntry> LoadPointRegistry()
    {
        var indexPath = ResolvePath("layout/points/index.json");
        if (!File.Exists(indexPath))
        {
            _logger.LogWarning("layout/points/index.json не найден — rates fetching отключён.");
            return [];
        }

        try
        {
            var txt = File.ReadAllText(indexPath);
            using var doc = JsonDocument.Parse(txt, DocOpts);
            var root = doc.RootElement;

            if (!root.TryGetProperty("points", out var pointsEl))
                return [];

            var result = new List<PointEntry>();
            foreach (var p in pointsEl.EnumerateArray())
            {
                var enabled = !p.TryGetProperty("enabled", out var en) || en.GetBoolean();
                if (!enabled) continue;

                var id = p.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
                var depCode = p.TryGetProperty("depCode", out var dcEl) ? dcEl.GetString() ?? "" : "";
                var ratesPath = p.TryGetProperty("ratesJsonPath", out var rpEl)
                    ? rpEl.GetString() ?? $"content/points/{id}/rates.json"
                    : $"content/points/{id}/rates.json";

                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(depCode))
                {
                    _logger.LogWarning("index.json: пропущена точка без id или depCode.");
                    continue;
                }

                result.Add(new PointEntry(id, depCode, ratesPath));
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка чтения layout/points/index.json.");
            return [];
        }
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private static Dictionary<string, RatePair> TryReadRates(string path)
    {
        try
        {
            if (!File.Exists(path)) return [];
            var txt = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(txt, DocOpts);
            if (!doc.RootElement.TryGetProperty("currencies", out var curs)) return [];
            var result = new Dictionary<string, RatePair>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in curs.EnumerateObject())
            {
                var e = prop.Value;
                if (e.TryGetProperty("buy", out var b) &&
                    e.TryGetProperty("sell", out var s))
                    result[prop.Name] = new RatePair(b.GetDecimal(), s.GetDecimal());
            }
            return result;
        }
        catch { return []; }
    }

    private static object TryReadLabels(string path)
    {
        try
        {
            if (!File.Exists(path)) return DefaultLabels();
            var txt = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(txt, DocOpts);
            if (doc.RootElement.TryGetProperty("labels", out var lblElem))
            {
                return lblElem.Deserialize<LabelsDto>(JsonOpts) ?? DefaultLabels();
            }
        }
        catch { /* fall through */ }
        return DefaultLabels();
    }

    private static LabelsDto DefaultLabels() => new()
    {
        Buy = ["Сатып аламыз", "Покупаем", "We buy"],
        Sell = ["Сатамыз", "Продаем", "We sell"]
    };

    private static string ResolvePath(string p) =>
        Path.IsPathRooted(p) ? p : Path.GetFullPath(
            Path.Combine(Directory.GetCurrentDirectory(), p));

    // ─── DTO models ───────────────────────────────────────────────────────────

    private sealed record PointEntry(string Id, string DepCode, string RatesJsonPath);

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
        public decimal Sale { get; init; }   // API uses "sale" (not "sell")
    }

    private sealed class LabelsDto
    {
        public List<string> Buy { get; set; } = [];
        public List<string> Sell { get; set; } = [];
    }

    private readonly record struct RatePair(decimal Buy, decimal Sell);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static readonly JsonSerializerOptions WriterOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly JsonDocumentOptions DocOpts = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };
}

