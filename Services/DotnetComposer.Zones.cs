using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Color = SixLabors.ImageSharp.Color;
using Font = SixLabors.Fonts.Font;
using FontStyle = SixLabors.Fonts.FontStyle;
using HorizontalAlignment = SixLabors.Fonts.HorizontalAlignment;
using Image = SixLabors.ImageSharp.Image;
using Point = SixLabors.ImageSharp.Point;
using Rectangle = SixLabors.ImageSharp.Rectangle;
using Size = SixLabors.ImageSharp.Size;
using VerticalAlignment = SixLabors.Fonts.VerticalAlignment;

namespace LedImageUpdaterService.Services;

// ─────────────────────────────────────────────────────────────────────────────
// Zones mode ("как в аэропорту"): the board is cut into N equal parts along its long side
// (left→right on a wide board, top→bottom on a tall one). Each part is one of:
//   rates  – fixed currencies          logo   – the point's logo
//   scroll – the other currencies, scrolling      image – QR code / advertising picture
// ─────────────────────────────────────────────────────────────────────────────
public sealed partial class DotnetComposer
{
    private sealed class ZonesCfg
    {
        public bool Enabled { get; init; }
        public List<ZoneDef> Items { get; init; } = [];
        /// <summary>Speed / mode of the scrolling zone (same fields as gridLayout.scroll).</summary>
        public ScrollCfg? Scroll { get; init; }
    }

    private sealed class ZoneDef
    {
        /// <summary>"rates" | "logo" | "scroll" | "image".</summary>
        public string Type { get; init; } = "rates";
        /// <summary>rates: fixed codes. scroll: codes to scroll (default: all the others).</summary>
        public List<string>? Codes { get; init; }
        /// <summary>image: picture file (default: gridLayout.overlays[0].file).</summary>
        public string? File { get; init; }
        public bool? Sharp { get; init; }
        /// <summary>Relative size of the zone. Default 1 (all zones equal).</summary>
        public int? Weight { get; init; }
    }

    /// <summary>Row geometry and fonts of one zone, all in render (oversampled) pixels.</summary>
    private sealed class ZoneRowStyle
    {
        public bool Narrow;
        public int FlagX, FlagY, FlagW, FlagH;
        public int CodeX, CodeCY;
        public Font CodeFont = null!, ValFont = null!;
        public int BuyCX, SellCX, ValCY;
        public int BuyArrowX, SellArrowX;
        public int ArrowSize;       // 1× px
        public bool Arrows;
    }

    private static Font FitFont(string sample, int maxW, int sizePx, FontStyle style, float scaleX)
    {
        sizePx = Math.Max(2, sizePx);
        var f = ResolveFont(sizePx, style);
        while (sizePx > 2 &&
               TextMeasurer.MeasureSize(sample, new RichTextOptions(f)).Width * scaleX > maxW)
            f = ResolveFont(--sizePx, style);
        return f;
    }

    private static ZoneRowStyle BuildZoneRowStyle(int w, int h, string sample, int os, float fsx)
    {
        var s = new ZoneRowStyle();
        int pad = Math.Max(os, w / 60);
        s.Narrow = w < 100 * os;

        if (!s.Narrow)
        {
            int flagH = Math.Max(2 * os, Math.Min(h * 55 / 100, w / 5));
            int flagW = flagH * 145 / 100;
            s.FlagX = pad; s.FlagY = (h - flagH) / 2; s.FlagW = flagW; s.FlagH = flagH;
            int codeW = w * 22 / 100;
            s.CodeX = s.FlagX + flagW + pad;
            s.CodeCY = h / 2;
            int restX = s.CodeX + codeW;
            int colW = Math.Max(4 * os, (w - restX - pad) / 2);
            s.Arrows = true;
            int arrowPx = Math.Max(4 * os, h * 28 / 100);
            s.ArrowSize = Math.Max(1, arrowPx / os);
            int arrowW = arrowPx * 8 / 10;
            s.ValFont = FitFont(sample, colW - arrowW - pad, h * 42 / 100, FontStyle.Bold, fsx);
            s.CodeFont = FitFont("MMM", codeW, h * 40 / 100, FontStyle.Bold, fsx);
            s.BuyCX = restX + (colW - arrowW) / 2;
            s.SellCX = restX + colW + (colW - arrowW) / 2;
            s.BuyArrowX = restX + colW - pad;
            s.SellArrowX = restX + 2 * colW - pad;
            s.ValCY = h / 2;
        }
        else
        {
            // Narrow zone (tall board): flag + code on the top line, buy | sell under it.
            int topH = h * 45 / 100, botH = h - topH;
            int flagH = Math.Max(2 * os, topH * 80 / 100);
            int flagW = Math.Min(flagH * 145 / 100, w * 45 / 100);
            s.FlagX = pad; s.FlagY = (topH - flagH) / 2; s.FlagW = flagW; s.FlagH = flagH;
            s.CodeX = s.FlagX + flagW + pad;
            s.CodeCY = topH / 2;
            s.CodeFont = FitFont("MMM", w - s.CodeX - pad, topH * 70 / 100, FontStyle.Bold, fsx);
            s.ValFont = FitFont(sample, w / 2 - 2 * pad, botH * 72 / 100, FontStyle.Bold, fsx);
            s.BuyCX = w / 4; s.SellCX = w * 3 / 4;
            s.ValCY = topH + botH / 2;
            s.Arrows = false;
        }
        return s;
    }

    private async Task DrawZoneRowAsync(
        Image<Rgba32> canvas, int x, int y, ZoneRowStyle s, string code, CurrencyRate rate,
        string flagsDir, string flagFile, int os, float fsx, float strokePx, CancellationToken ct)
    {
        await DrawFlagAsync(canvas, flagsDir, flagFile, x + s.FlagX, y + s.FlagY, s.FlagW, s.FlagH, ct);

        PlaceTextStretched(canvas, code, x + s.CodeX, y + s.CodeCY,
            HorizontalAlignment.Left, VerticalAlignment.Center, s.CodeFont, CsCode,
            fsx, strokePx, verticalScale: 1.3f, inkCenter: true);

        PlaceTextStretched(canvas, FmtRate(rate.Buy, code), x + s.BuyCX, y + s.ValCY,
            HorizontalAlignment.Center, VerticalAlignment.Center, s.ValFont, CsBuy,
            fsx, strokePx, verticalScale: 1.3f, inkCenter: true);
        PlaceTextStretched(canvas, FmtRate(rate.Sell, code), x + s.SellCX, y + s.ValCY,
            HorizontalAlignment.Center, VerticalAlignment.Center, s.ValFont, CsSell,
            fsx, strokePx, verticalScale: 1.3f, inkCenter: true);

        if (s.Arrows)
        {
            DrawArrow(canvas, x + s.BuyArrowX, y + s.ValCY, Direction(rate.PrevBuy, rate.Buy), true, os, s.ArrowSize, fsx);
            DrawArrow(canvas, x + s.SellArrowX, y + s.ValCY, Direction(rate.PrevSell, rate.Sell), false, os, s.ArrowSize, fsx);
        }
    }

    private static string LongestRateText(RatesConfig rates, IEnumerable<string> codes)
    {
        string best = "888.88";
        foreach (var c in codes)
        {
            if (!rates.Currencies.TryGetValue(c, out var r)) continue;
            foreach (var t in new[] { FmtRate(r.Buy, c), FmtRate(r.Sell, c) })
                if (t.Length > best.Length) best = t;
        }
        return best;
    }

    private async Task<string> RenderZonesAsync(
        Image<Rgba32> canvas, GridLayout gl, RatesConfig rates, string sourceDir, string flagsDir,
        int outW, int outH, int os, string outPath, CancellationToken ct)
    {
        var zc = gl.Zones!;
        var items = zc.Items.Take(8).ToList();
        bool horizontal = outW >= outH;
        int total = horizontal ? outW : outH;
        int weightSum = items.Sum(i => Math.Max(1, i.Weight ?? 1));

        var rects = new List<Rectangle>();
        int pos = 0, acc = 0;
        for (int i = 0; i < items.Count; i++)
        {
            acc += Math.Max(1, items[i].Weight ?? 1);
            int end = i == items.Count - 1 ? total : total * acc / weightSum;
            rects.Add(horizontal ? new Rectangle(pos, 0, end - pos, outH) : new Rectangle(0, pos, outW, end - pos));
            pos = end;
        }

        bool IsGold(string c) => c.StartsWith("GOLD", StringComparison.OrdinalIgnoreCase);
        var preferred = (gl.Left.Count > 0 ? gl.Left : gl.Columns?.FirstOrDefault()?.Codes ?? [])
            .Concat(rates.Currencies.Keys.Where(c => !IsGold(c)))
            .Where(c => rates.Currencies.ContainsKey(c)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        // Fixed codes per rates zone (explicit, or the next unused ones from the point's list).
        var fixedCodes = new Dictionary<int, List<string>>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < items.Count; i++)
            if (IsType(items[i], "rates") && items[i].Codes is { Count: > 0 } cs)
            {
                var l = cs.Where(rates.Currencies.ContainsKey).ToList();
                fixedCodes[i] = l; foreach (var c in l) used.Add(c);
            }
        for (int i = 0; i < items.Count; i++)
            if (IsType(items[i], "rates") && !fixedCodes.ContainsKey(i))
            {
                var l = preferred.Where(c => !used.Contains(c)).Take(3).ToList();
                fixedCodes[i] = l; foreach (var c in l) used.Add(c);
            }

        float fsx = gl.FontScaleX ?? 1f;
        float strokePx = (gl.TextStroke ?? 0) * os;
        var odd = ParseColor(gl.RowBgOdd);
        var even = ParseColor(gl.RowBgEven);
        bool scrollDone = false;

        for (int i = 0; i < items.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var it = items[i];
            var r = rects[i];
            int rx = r.X * os, ry = r.Y * os, rw = r.Width * os, rh = r.Height * os;

            if (IsType(it, "logo"))
            {
                int p = 2 * os;
                await TryDrawLogoAsync(canvas, sourceDir, gl.LogoFile ?? "logo.png",
                    rx + p, ry + p, Math.Max(1, rw - 2 * p), Math.Max(1, rh - 2 * p), ct);
            }
            else if (IsType(it, "rates"))
            {
                var codes = fixedCodes[i];
                if (codes.Count == 0) continue;
                int rowH = rh / codes.Count;
                int top = ry + (rh - rowH * codes.Count) / 2;
                var style = BuildZoneRowStyle(rw, rowH, LongestRateText(rates, codes), os, fsx);
                for (int j = 0; j < codes.Count; j++)
                {
                    int y = top + j * rowH;
                    var fill = (j % 2 == 0 ? odd : even) ?? (j % 2 == 0 ? even : odd);
                    if (fill is { } f)
                        canvas.Mutate(c => c.Fill(f, new SixLabors.ImageSharp.RectangleF(rx, y, rw, rowH)));
                    var code = codes[j];
                    var flagFile = gl.FlagFiles.TryGetValue(code, out var ff) ? ff : $"{code.ToLower()}.png";
                    await DrawZoneRowAsync(canvas, rx, y, style, code, rates.Currencies[code],
                        flagsDir, flagFile, os, fsx, strokePx, ct);
                }
            }
            else if (IsType(it, "scroll"))
            {
                if (scrollDone)
                {
                    _logger.LogWarning("Зоны: прокручиваться может только одна зона, остальные пропущены.");
                    continue;
                }
                scrollDone = true;
                FlushFlags(canvas);   // queued logos/flags must not land on the strip
                var list = (it.Codes is { Count: > 0 }
                        ? it.Codes
                        : rates.Currencies.Keys.Where(c => !IsGold(c) && !used.Contains(c)).ToList())
                    .Where(rates.Currencies.ContainsKey).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                await BuildZoneScrollStripAsync(zc.Scroll, gl, rates, list, flagsDir, r, os, fsx, strokePx, ct);
            }
        }

        FlushFlags(canvas);
        canvas.Mutate(x => x.Resize(new ResizeOptions
        {
            Size = new Size(outW, outH),
            Mode = ResizeMode.Stretch,
            Sampler = KnownResamplers.Lanczos3
        }));

        // Pictures and separators go on top at 1× (crisp QR, survive scrolling and animation).
        var sepColor = ParseColor(gl.GridLineColor);
        for (int i = 0; i < items.Count; i++)
        {
            var r = rects[i];
            if (IsType(items[i], "image"))
            {
                var file = items[i].File;
                if (string.IsNullOrWhiteSpace(file))
                    file = gl.Overlays?.FirstOrDefault()?.File;
                if (string.IsNullOrWhiteSpace(file)) continue;
                try
                {
                    int p = 2;
                    int w = Math.Max(1, r.Width - 2 * p), h = Math.Max(1, r.Height - 2 * p);
                    bool sharp = items[i].Sharp ?? gl.Overlays?.FirstOrDefault()?.Sharp ?? false;
                    var img = await LoadOverlayImageAsync(ResolvePath(_overlayRoot, file), w, h, sharp, ct);
                    _topOverlays.Add((img, new Point(r.X + p, r.Y + p)));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning("Зона-картинка {File} не загружена: {Err}", file, ex.Message);
                }
            }
            if (i > 0 && sepColor is { } sc && (gl.GridLineWidth ?? 1) > 0)
            {
                int t = Math.Max(1, gl.GridLineWidth ?? 1);
                var sep = horizontal ? new Image<Rgba32>(t, outH, sc) : new Image<Rgba32>(outW, t, sc);
                _topOverlays.Add((sep, new Point(r.X, r.Y)));
            }
        }

        // FinalizeScrollAsync reads gl.Scroll for speed/mode; zones carry their own.
        if (_scrollStrip is not null)
            gl.Scroll = zc.Scroll is { } z ? z : new ScrollCfg { Enabled = true };

        return await FinalizeAsync(canvas, gl, rates, outW, outH, outPath, ct);
    }

    private static bool IsType(ZoneDef z, string t) =>
        string.Equals(z.Type, t, StringComparison.OrdinalIgnoreCase);

    private async Task BuildZoneScrollStripAsync(
        ScrollCfg? sc, GridLayout gl, RatesConfig rates, List<string> list, string flagsDir,
        Rectangle zone, int os, float fsx, float strokePx, CancellationToken ct)
    {
        if (list.Count == 0) return;
        bool narrow = zone.Width < 100;
        int target = narrow ? 36 : 27;
        int v = Math.Max(1, (int)Math.Round(zone.Height / (double)target));
        int rowH = Math.Max(4, zone.Height / v);
        v = Math.Max(1, zone.Height / rowH);

        bool continuous = !string.Equals(sc?.Mode, "pages", StringComparison.OrdinalIgnoreCase);
        if (continuous && list.Count > 1 && list.Count % 2 == 1) list = list.Concat(list).ToList();
        int pages = (list.Count + v - 1) / v;
        int totalRows = continuous ? list.Count + v : pages * v + v;
        string CodeAt(int j) => continuous
            ? list[j % list.Count]
            : (j < pages * v ? list[j % list.Count] : list[j - pages * v]);

        var odd = ParseColor(gl.RowBgOdd);
        var even = ParseColor(gl.RowBgEven);
        var style = BuildZoneRowStyle(zone.Width * os, rowH * os, LongestRateText(rates, list), os, fsx);
        int flagRectsBefore = _flagRects.Count;

        var strip = new Image<Rgba32>(zone.Width * os, totalRows * rowH * os, CsBg);
        try
        {
            for (int j = 0; j < totalRows; j++)
            {
                ct.ThrowIfCancellationRequested();
                int top = j * rowH * os;
                int slot = continuous ? j : j % v;
                var fill = (slot % 2 == 0 ? odd : even) ?? (slot % 2 == 0 ? even : odd);
                if (fill is { } f)
                    strip.Mutate(c => c.Fill(f, new SixLabors.ImageSharp.RectangleF(0, top, zone.Width * os, rowH * os)));
                var code = CodeAt(j);
                var flagFile = gl.FlagFiles.TryGetValue(code, out var ff) ? ff : $"{code.ToLower()}.png";
                await DrawZoneRowAsync(strip, 0, top, style, code, rates.Currencies[code],
                    flagsDir, flagFile, os, fsx, strokePx, ct);
            }
            FlushFlags(strip);
            if (_flagRects.Count > flagRectsBefore)
                _flagRects.RemoveRange(flagRectsBefore, _flagRects.Count - flagRectsBefore);

            strip.Mutate(x => x.Resize(new ResizeOptions
            {
                Size = new Size(zone.Width, totalRows * rowH),
                Mode = ResizeMode.Stretch,
                Sampler = KnownResamplers.Lanczos3
            }));
        }
        catch { strip.Dispose(); throw; }

        _scrollStrip?.Dispose();
        _scrollStrip = strip;
        _scrollRect = new Rectangle(zone.X, zone.Y + (zone.Height - v * rowH) / 2, zone.Width, v * rowH);
        _scrollRowsPerPage = v;
        _scrollPages = pages;
        _scrollCycleRows = continuous ? list.Count : pages * v;
        _scrollContinuous = continuous;
    }
}
