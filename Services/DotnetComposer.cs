using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
// Disambiguate types that conflict with System.Drawing / System.Windows.Forms
using Brushes = SixLabors.ImageSharp.Drawing.Processing.Brushes;
using Color = SixLabors.ImageSharp.Color;
using Font = SixLabors.Fonts.Font;
using FontStyle = SixLabors.Fonts.FontStyle;
using HorizontalAlignment = SixLabors.Fonts.HorizontalAlignment;
using Image = SixLabors.ImageSharp.Image;
using Pens = SixLabors.ImageSharp.Drawing.Processing.Pens;
using Point = SixLabors.ImageSharp.Point;
using PointF = SixLabors.ImageSharp.PointF;
using Size = SixLabors.ImageSharp.Size;
using SystemFonts = SixLabors.Fonts.SystemFonts;

namespace LedImageUpdaterService.Services;

// ─────────────────────────────────────────────────────────────────────────────
//  DotnetComposer – generates LED board JPEG sized from compose config canvas.
//
//  Layout (1× output pixels, oversample for quality then downscale):
//
//   ┌───────────────────────────────────────────────────────────────────────┐
//   │  canvas.width × canvas.height  (e.g. 560 × 80)                       │
//   │  ┌──────────────────────┬──────────┬──────────────────────────┐      │
//   │  │  LEFT  section       │ LOGO(60) │  RIGHT section           │      │
//   │  │  w = (outW−60)/2     │          │  w = (outW−60)/2         │      │
//   │  │  hdr: [BUY][SELL]    │          │  hdr: [BUY][SELL]        │      │
//   │  │  [flag][CODE][B][S]  │          │  [flag][CODE][B][S]      │      │
//   │  │  × 4 rows            │          │  × 4 rows                │      │
//   │  └──────────────────────┴──────────┴──────────────────────────┘      │
//   │   x=0              secW    secW+60                           outW    │
//   └───────────────────────────────────────────────────────────────────────┘
// ─────────────────────────────────────────────────────────────────────────────
public sealed class DotnetComposer
{
    private readonly ILogger<DotnetComposer> _logger;

    public DotnetComposer(ILogger<DotnetComposer> logger)
    {
        _logger = logger;
    }

    public async Task<string> ComposeAsync(string configPath, string ratesPath, CancellationToken ct)
    {
        var cfg = await ReadJsonAsync<ComposeConfig>(configPath, ct)
            ?? throw new InvalidOperationException($"Invalid compose config: {configPath}");
        var ratesCfg = await ReadJsonAsync<RatesConfig>(ratesPath, ct)
            ?? throw new InvalidOperationException($"Invalid rates json: {ratesPath}");

        var root = Directory.GetCurrentDirectory();
        var outPath = ResolvePath(root, cfg.OutputFile);
        Directory.CreateDirectory(Path.GetDirectoryName(outPath) ?? root);

        return await RenderGridAsync(cfg, ratesCfg, root, outPath, ct);
    }

    // ─── Grid layout defaults (1× = output pixel; everything ×os at render) ──
    // These are the fallback values used when GridLayout / breakpoint fields are null.

    // Logo strip (centred): fixed width, position derived from canvas at runtime
    private const int DefaultLogoW = 60;

    // Row geometry: header 12px + 4 rows × 17px = 80px ✓
    private const int DefaultHeaderH = 12;
    private const int DefaultRowH = 17;

    // Column offsets inside one section (from its left edge, in px).
    // Columns must fit within secW = (outW − logoW) / 2.
    // For 560px canvas: secW = (560−60)/2 = 250 px.
    private const int DefaultColFlagX = 2, DefaultColFlagW = 20, DefaultColFlagH = 13;
    private const int DefaultColCodeX = 24, DefaultColCodeW = 28;
    private const int DefaultColBuyX = 54, DefaultColBuyW = 104;
    private const int DefaultColSellX = 160, DefaultColSellW = 90;   // 160+90 = 250 ✓

    // Font sizes at 1× (multiplied by oversample for rendering)
    private const int DefaultFszHdr = 5;    // column header labels
    private const int DefaultFszCode = 14;  // currency code
    private const int DefaultFszValue = 19; // rate value — large & bold
    private const int DefaultFszArrow = 12; // change-direction arrow

    // Colors
    private static readonly Color CsBg = Color.Black;
    private static readonly Color CsHdr = Color.FromRgb(160, 160, 160);
    private static readonly Color CsCode = Color.White;
    private static readonly Color CsBuy = Color.White;
    private static readonly Color CsSell = Color.White;
    private static readonly Color CsArrowRed = Color.FromRgb(255, 60, 60);
    private static readonly Color CsArrowGreen = Color.FromRgb(50, 220, 70);

    // ─────────────────────────────────────────────────────────────────────────

    private async Task<string> RenderGridAsync(
        ComposeConfig cfg,
        RatesConfig ratesCfg,
        string root,
        string outPath,
        CancellationToken ct)
    {
        // Resolve active GridLayout: merge the first matching breakpoint on top of the base.
        var baseGl = cfg.GridLayout ?? new GridLayout();
        var gl = ResolveBreakpoint(cfg, baseGl);
        int os = Math.Clamp(gl.Oversample, 1, 8);

        // Resolve layout values — breakpoint/gridLayout fields win over defaults.
        int logoW = gl.LogoW ?? DefaultLogoW;
        int headerH = gl.HeaderH ?? DefaultHeaderH;
        int rowH = gl.RowH ?? DefaultRowH;
        int colFlagX = gl.ColFlagX ?? DefaultColFlagX;
        int colFlagW = gl.ColFlagW ?? DefaultColFlagW;
        int colFlagH = gl.ColFlagH ?? DefaultColFlagH;
        int colCodeX = gl.ColCodeX ?? DefaultColCodeX;
        int colBuyX = gl.ColBuyX ?? DefaultColBuyX;
        int colBuyW = gl.ColBuyW ?? DefaultColBuyW;
        int colSellX = gl.ColSellX ?? DefaultColSellX;
        int colSellW = gl.ColSellW ?? DefaultColSellW;
        int fszHdr = gl.FszHdr ?? DefaultFszHdr;
        int fszCode = gl.FszCode ?? DefaultFszCode;
        int fszValue = gl.FszValue ?? DefaultFszValue;
        int fszArrow = gl.FszArrow ?? DefaultFszArrow;
        int valueShiftX = gl.ValueShiftX ?? 0;

        // Derive dimensions and section geometry from canvas config
        int outW = cfg.Canvas.Width;
        int outH = cfg.Canvas.Height;
        int secW = (outW - logoW) / 2;
        int logoX = secW;                  // logo starts at left-section right edge
        int secRX = secW + logoW;          // right section start

        int rw = outW * os;
        int rh = outH * os;

        var sourceDir = ResolvePath(root, cfg.SourceDir);
        var flagsDir = ResolvePath(sourceDir, gl.FlagsDir ?? "../flags");

        using var canvas = new Image<Rgba32>(rw, rh, CsBg);

        // New unified multi-column mode (1..3 columns, free logo, per-column headers)
        if (string.Equals(gl.Mode, "columns", StringComparison.OrdinalIgnoreCase) ||
            gl.Columns is { Count: > 0 })
        {
            var rendered = await RenderColumnsAsync(
                canvas, gl, ratesCfg, sourceDir, flagsDir, outW, outH, os,
                rowH, colFlagX, colFlagW, colFlagH,
                colCodeX, colBuyX, colBuyW, colSellX, colSellW,
                fszHdr, fszCode, fszValue, fszArrow, ct);

            await SaveJpegWithRetryAsync(rendered, outPath, ct);
            _logger.LogInformation("Multi-column board composed → {Out}", outPath);
            return outPath;
        }

        if (string.Equals(gl.Mode, "singleColumn", StringComparison.OrdinalIgnoreCase))
        {
            var rendered = await RenderSingleColumnAsync(
                canvas, gl, ratesCfg, sourceDir, flagsDir, outW, outH, os, rh,
                headerH, rowH, colFlagX, colFlagW, colFlagH,
                colCodeX, colBuyX, colBuyW, colSellX, colSellW,
                fszHdr, fszCode, fszValue, fszArrow, ct);

            await SaveJpegWithRetryAsync(rendered, outPath, ct);
            _logger.LogInformation("Single-column board composed → {Out}", outPath);
            return outPath;
        }

        // ── logo ─────────────────────────────────────────────────────────
        await TryDrawLogoAsync(canvas, sourceDir, gl.LogoFile ?? "logo.svg",
            logoX * os, 0, logoW * os, rh, ct);

        // ── left + right sections ─────────────────────────────────────────
        foreach (var (sectX, codes) in new[] { (0, gl.Left), (secRX, gl.Right) })
        {
            DrawSectionHeaders(canvas, sectX, ratesCfg.Labels, os,
                colBuyX, colBuyW, colSellX, colSellW, fszHdr);

            for (var i = 0; i < codes.Count && i < 4; i++)
            {
                var code = codes[i];
                if (!ratesCfg.Currencies.TryGetValue(code, out var rate))
                    continue;

                var flagFile = gl.FlagFiles.TryGetValue(code, out var ff)
                    ? ff
                    : $"{code.ToLower()}.png";

                await DrawRowAsync(canvas,
                    sectX * os,
                    (headerH + i * rowH) * os,
                    rowH * os,
                    code, rate, flagsDir, flagFile, os,
                    colFlagX, colFlagW, colFlagH,
                    colCodeX, colBuyX, colBuyW, colSellX, colSellW,
                    fszCode, fszValue, fszArrow, valueShiftX, ct, outW,
                    gl.FontScaleX ?? 1f, (gl.TextStroke ?? 0) * os);
            }
        }


        // ── downscale to output dimensions with Lanczos3 ──────────────────
        canvas.Mutate(x => x.Resize(new ResizeOptions
        {
            Size = new Size(outW, outH),
            Mode = ResizeMode.Stretch,
            Sampler = KnownResamplers.Lanczos3
        }));

        await SaveJpegWithRetryAsync(canvas, outPath, ct);
        _logger.LogInformation("Grid board composed → {Out}", outPath);
        return outPath;
    }

    private void PlaceTextStretched(
    Image<Rgba32> canvas,
    string text,
    int x, int y,
    HorizontalAlignment ha,
    VerticalAlignment va,
    Font font,
    Color color,
    float scaleX = 1f,
    float strokePx = 0f,
    float verticalScale = 1.3f)
{
    if (string.IsNullOrWhiteSpace(text)) return;

    var measureOpts = new RichTextOptions(font)
    {
        Origin = new PointF(0, 0),
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Top,
    };
    var size = TextMeasurer.MeasureSize(text, measureOpts);

    // Generous bottom padding so descenders/overflow are never clipped
    const int padL = 2, padT = 2, padR = 2, padB = 14;
    int layerW = (int)(size.Width + padL + padR);
    int layerH = (int)(size.Height + padT + padB);

    using var textLayer = new Image<Rgba32>(layerW, layerH);
    var drawOpts = new RichTextOptions(font)
    {
        Origin = new PointF(padL, padT),
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Top,
    };
    textLayer.Mutate(ctx =>
    {
        if (strokePx > 0)
            ctx.DrawText(new DrawingOptions(), drawOpts, text,
                Brushes.Solid(Color.Transparent),
                Pens.Solid(Color.Black, strokePx));
        ctx.DrawText(drawOpts, text, color);
    });

    // Scale the whole layer so padding stays proportional and nothing gets clipped
    int newW = (int)(layerW * scaleX);
    int newH = (int)(layerH * verticalScale);
    textLayer.Mutate(ctx => ctx.Resize(new ResizeOptions
    {
        Size = new Size(newW, newH),
        Mode = ResizeMode.Stretch,
        Sampler = KnownResamplers.Bicubic
    }));

    // Find where the text visually sits inside the resized layer
    float pX = (float)newW / layerW;
    float pY = (float)newH / layerH;

    int drawX = ha switch
    {
        HorizontalAlignment.Center => x - (int)((padL + size.Width / 2f) * pX),
        HorizontalAlignment.Right  => x - (int)((padL + size.Width) * pX),
        _                          => x - (int)(padL * pX),
    };
    int drawY = va switch
    {
        VerticalAlignment.Center => y - (int)((padT + size.Height / 2f) * pY),
        VerticalAlignment.Bottom => y - (int)((padT + size.Height) * pY),
        _                        => y - (int)(padT * pY),
    };

    canvas.Mutate(ctx => ctx.DrawImage(textLayer, new Point(drawX, drawY), 1f));
}

    private async Task SaveJpegWithRetryAsync(Image<Rgba32> image, string outPath, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(outPath) ?? Directory.GetCurrentDirectory();
        Directory.CreateDirectory(dir);

        // Write to a temp file first, then overwrite target.
        // This minimizes partially-written outputs and handles short file locks.
        var tempPath = Path.Combine(dir, $".{Path.GetFileName(outPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await image.SaveAsJpegAsync(tempPath, new JpegEncoder { Quality = 95 }, ct);

            const int maxAttempts = 10;
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    File.Copy(tempPath, outPath, overwrite: true);
                    return;
                }
                catch (IOException ioEx) when (attempt < maxAttempts)
                {
                    _logger.LogWarning(
                        "Output file is locked, retry {Attempt}/{Max}: {Path}. Details: {Error}",
                        attempt, maxAttempts, outPath, ioEx.Message);
                    await Task.Delay(TimeSpan.FromMilliseconds(150 * attempt), ct);
                }
            }

            // Last attempt with explicit error if still locked.
            File.Copy(tempPath, outPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { /* ignore temp cleanup */ }
            }
        }
    }

    // private async Task<Image<Rgba32>> RenderSingleColumnAsync(
    //     Image<Rgba32> canvas,
    //     GridLayout gl,
    //     RatesConfig ratesCfg,
    //     string sourceDir,
    //     string flagsDir,
    //     int outW,
    //     int outH,
    //     int os,
    //     int rh,
    //     int headerH,
    //     int rowH,
    //     int colFlagX,
    //     int colFlagW,
    //     int colFlagH,
    //     int colCodeX,
    //     int colBuyX,
    //     int colBuyW,
    //     int colSellX,
    //     int colSellW,
    //     int fszHdr,
    //     int fszCode,
    //     int fszValue,
    //     int fszArrow,
    //     CancellationToken ct)
    // {
    //     int logoW = gl.LogoW ?? 42;
    //     int logoX = gl.LogoX ?? (gl.SingleLeftMargin ?? 2);
    //     int logoY = gl.LogoY ?? 1;
    //     int logoH = gl.LogoH ?? Math.Max(10, Math.Min(logoW, headerH - 4));

    //     // Backward compatibility:
    //     // - legacy single-column uses table anchored after logo (singleLeftMargin + logoW + singleHeaderGap)
    //     // - if any absolute anchor is set, columns are interpreted as absolute X positions
    //     bool hasAbsoluteAnchors =
    //         gl.LogoX.HasValue || gl.LogoY.HasValue || gl.LogoH.HasValue ||
    //         gl.HeaderBuyX.HasValue || gl.HeaderBuyY.HasValue ||
    //         gl.HeaderSellX.HasValue || gl.HeaderSellY.HasValue ||
    //         gl.RowsStartY.HasValue;

    //     int legacyTableX = (gl.SingleLeftMargin ?? 2) + logoW + (gl.SingleHeaderGap ?? 6);
    //     int tableX = hasAbsoluteAnchors ? 0 : legacyTableX;

    //     await TryDrawLogoAsync(canvas, sourceDir, gl.LogoFile ?? "logo.png",
    //         logoX * os, logoY * os, logoW * os, logoH * os, ct);

    //     int buyHeaderX = gl.HeaderBuyX
    //         ?? (hasAbsoluteAnchors ? colBuyX + colBuyW / 2 : legacyTableX + colBuyX + colBuyW / 2);
    //     int buyHeaderY = gl.HeaderBuyY ?? 1;

    //     int sellHeaderX = gl.HeaderSellX
    //         ?? (hasAbsoluteAnchors ? colSellX + colSellW / 2 : legacyTableX + colSellX + colSellW / 2);
    //     int sellHeaderY = gl.HeaderSellY ?? buyHeaderY;

    //     int buyCX = buyHeaderX * os;
    //     int sellCX = sellHeaderX * os;
    //     var hdrFont = ResolveFont(fszHdr * os, FontStyle.Bold);
    //     int lineGap = 1;
    //     int lineStep = fszHdr + lineGap;

    //     // Three-line header as in the reference design: local + RU + EN.
    //     var buyL0 = ratesCfg.Labels.Buy.ElementAtOrDefault(0) ?? "Сатып аламыз";
    //     var buyL1 = ratesCfg.Labels.Buy.ElementAtOrDefault(1) ?? "Покупаем";
    //     var buyL2 = ratesCfg.Labels.Buy.ElementAtOrDefault(2) ?? "We buy";
    //     var sellL0 = ratesCfg.Labels.Sell.ElementAtOrDefault(0) ?? "Сатамыз";
    //     var sellL1 = ratesCfg.Labels.Sell.ElementAtOrDefault(1) ?? "Продаем";
    //     var sellL2 = ratesCfg.Labels.Sell.ElementAtOrDefault(2) ?? "We sell";

    //     PlaceText(canvas, buyL0, buyCX, buyHeaderY * os, HorizontalAlignment.Center, VerticalAlignment.Top, hdrFont, CsHdr);
    //     PlaceText(canvas, buyL1, buyCX, (buyHeaderY + lineStep) * os, HorizontalAlignment.Center, VerticalAlignment.Top, hdrFont, CsHdr);
    //     PlaceText(canvas, buyL2, buyCX, (buyHeaderY + lineStep * 2) * os, HorizontalAlignment.Center, VerticalAlignment.Top, hdrFont, CsHdr);
    //     PlaceText(canvas, sellL0, sellCX, sellHeaderY * os, HorizontalAlignment.Center, VerticalAlignment.Top, hdrFont, CsHdr);
    //     PlaceText(canvas, sellL1, sellCX, (sellHeaderY + lineStep) * os, HorizontalAlignment.Center, VerticalAlignment.Top, hdrFont, CsHdr);
    //     PlaceText(canvas, sellL2, sellCX, (sellHeaderY + lineStep * 2) * os, HorizontalAlignment.Center, VerticalAlignment.Top, hdrFont, CsHdr);

    //     int rowsStartY = hasAbsoluteAnchors
    //         ? (gl.RowsStartY ?? (gl.SingleTopOffset ?? (Math.Max(buyHeaderY, sellHeaderY) + lineStep * 3 + 1)))
    //         : ((gl.SingleTopOffset ?? 0) + headerH);
    //     int rows = gl.SingleRows ?? 5;
    //     var codes = gl.Left.Count > 0 ? gl.Left : ["USD", "EUR", "RUB", "CNY", "KGS"];

    //     for (var i = 0; i < codes.Count && i < rows; i++)
    //     {
    //         var code = codes[i];
    //         if (!ratesCfg.Currencies.TryGetValue(code, out var rate))
    //             continue;

    //         var flagFile = gl.FlagFiles.TryGetValue(code, out var ff)
    //             ? ff
    //             : $"{code.ToLower()}.png";

    //         await DrawRowAsync(canvas,
    //             tableX * os,
    //             (rowsStartY + i * rowH) * os,
    //             rowH * os,
    //             code, rate, flagsDir, flagFile, os,
    //             colFlagX, colFlagW, colFlagH,
    //             colCodeX, colBuyX, colBuyW, colSellX, colSellW,
    //             fszCode, fszValue, fszArrow, gl.ValueShiftX ?? 0, ct);
    //     }

    //     canvas.Mutate(x => x.Resize(new ResizeOptions
    //     {
    //         Size = new Size(outW, outH),
    //         Mode = ResizeMode.Stretch,
    //         Sampler = KnownResamplers.Lanczos3
    //     }));

    //     return canvas;
    // }
private async Task<Image<Rgba32>> RenderSingleColumnAsync(
    Image<Rgba32> canvas,
    GridLayout gl,
    RatesConfig ratesCfg,
    string sourceDir,
    string flagsDir,
    int outW,
    int outH,
    int os,
    int rh,
    int headerH,
    int rowH,
    int colFlagX, int colFlagW, int colFlagH,
    int colCodeX, int colBuyX, int colBuyW, int colSellX, int colSellW,
    int fszHdr, int fszCode, int fszValue, int fszArrow,
    CancellationToken ct)
{
    int logoW = gl.LogoW ?? 34;
    int logoX = gl.LogoX ?? 2;
    int logoY = gl.LogoY ?? 2;
    int logoH = gl.LogoH ?? 26;

    await TryDrawLogoAsync(canvas, sourceDir, gl.LogoFile ?? "logo.png",
        logoX * os, logoY * os, logoW * os, logoH * os, ct);

    // Заголовки
    int buyHeaderX = gl.HeaderBuyX ?? 67;
    int sellHeaderX = gl.HeaderSellX ?? 104;
    int buyHeaderY = gl.HeaderBuyY ?? 3;
    int sellHeaderY = gl.HeaderSellY ?? 3;

    if (outW <= 160)
    {
        buyHeaderX = 67;
        sellHeaderX = 104;
        fszHdr = Math.Min(fszHdr, 9);
    }

    var hdrFont = ResolveFont(fszHdr * os, FontStyle.Bold);
    int lineStep = fszHdr + 1;

    // Трёхстрочный заголовок
    var buyL0 = ratesCfg.Labels.Buy.ElementAtOrDefault(0) ?? "Сатып аламыз";
    var buyL1 = ratesCfg.Labels.Buy.ElementAtOrDefault(1) ?? "Покупаем";
    var buyL2 = ratesCfg.Labels.Buy.ElementAtOrDefault(2) ?? "We buy";
    var sellL0 = ratesCfg.Labels.Sell.ElementAtOrDefault(0) ?? "Сатамыз";
    var sellL1 = ratesCfg.Labels.Sell.ElementAtOrDefault(1) ?? "Продаем";
    var sellL2 = ratesCfg.Labels.Sell.ElementAtOrDefault(2) ?? "We sell";

    int buyCX = buyHeaderX * os;
    int sellCX = sellHeaderX * os;

    float fontScaleX = gl.FontScaleX ?? 1f;
    float strokePx = (gl.TextStroke ?? 0) * os;

    PlaceText(canvas, buyL0, buyCX, buyHeaderY * os, HorizontalAlignment.Center, VerticalAlignment.Top, hdrFont, CsHdr, fontScaleX);
    PlaceText(canvas, buyL1, buyCX, (buyHeaderY + lineStep) * os, HorizontalAlignment.Center, VerticalAlignment.Top, hdrFont, CsHdr, fontScaleX);
    PlaceText(canvas, buyL2, buyCX, (buyHeaderY + lineStep * 2) * os, HorizontalAlignment.Center, VerticalAlignment.Top, hdrFont, CsHdr, fontScaleX);

    PlaceText(canvas, sellL0, sellCX, sellHeaderY * os, HorizontalAlignment.Center, VerticalAlignment.Top, hdrFont, CsHdr, fontScaleX);
    PlaceText(canvas, sellL1, sellCX, (sellHeaderY + lineStep) * os, HorizontalAlignment.Center, VerticalAlignment.Top, hdrFont, CsHdr, fontScaleX);
    PlaceText(canvas, sellL2, sellCX, (sellHeaderY + lineStep * 2) * os, HorizontalAlignment.Center, VerticalAlignment.Top, hdrFont, CsHdr, fontScaleX);

    int rowsStartY = gl.RowsStartY ?? 36;
    int rows = gl.SingleRows ?? 6;
    var codes = gl.Left;

    for (int i = 0; i < codes.Count && i < rows; i++)
    {
        var code = codes[i];
        if (!ratesCfg.Currencies.TryGetValue(code, out var rate)) continue;

        var flagFile = gl.FlagFiles.TryGetValue(code, out var ff) ? ff : $"{code.ToLower()}.png";

        await DrawRowAsync(canvas, 0, (rowsStartY + i * rowH) * os, rowH * os,
            code, rate, flagsDir, flagFile, os,
            colFlagX, colFlagW, colFlagH, colCodeX, colBuyX, colBuyW, colSellX, colSellW,
            fszCode, fszValue, fszArrow, gl.ValueShiftX ?? -5, ct, outW,
            fontScaleX, strokePx);
    }

    canvas.Mutate(x => x.Resize(new ResizeOptions
    {
        Size = new Size(outW, outH),
        Mode = ResizeMode.Stretch,
        Sampler = KnownResamplers.Lanczos3
    }));

    return canvas;
}

    /// <summary>
    /// Unified multi-column renderer (1..3 columns). The logo is drawn once as a
    /// free overlay at gl.LogoX/Y/W/H. Each column repeats the same internal row
    /// geometry (flag/code/buy/sell + headers) at a horizontal offset, and carries
    /// its own currency list and buy/sell header labels.
    /// </summary>
    private async Task<Image<Rgba32>> RenderColumnsAsync(
        Image<Rgba32> canvas,
        GridLayout gl,
        RatesConfig ratesCfg,
        string sourceDir,
        string flagsDir,
        int outW,
        int outH,
        int os,
        int rowH,
        int colFlagX, int colFlagW, int colFlagH,
        int colCodeX, int colBuyX, int colBuyW, int colSellX, int colSellW,
        int fszHdr, int fszCode, int fszValue, int fszArrow,
        CancellationToken ct)
    {
        // Build the list of columns. Fall back to Left/Right for back-compat.
        var columns = gl.Columns is { Count: > 0 }
            ? gl.Columns
            : new List<ColumnDef>
            {
                new() { Codes = gl.Left },
                new() { Codes = gl.Right },
            }.Where(c => c.Codes.Count > 0).ToList();

        if (columns.Count == 0)
            columns = [new ColumnDef { Codes = gl.Left }];

        int count = Math.Clamp(gl.ColumnCount ?? columns.Count, 1, 3);
        if (columns.Count > count) columns = columns.Take(count).ToList();

        int pitch = outW / count;   // column width in 1× pixels

        float fontScaleX = gl.FontScaleX ?? 1f;
        float strokePx = (gl.TextStroke ?? 0) * os;

        // ── logo: free-floating overlay ────────────────────────────────────
        int logoX = gl.LogoX ?? 2, logoY = gl.LogoY ?? 2;
        int logoW = gl.LogoW ?? 40, logoH = gl.LogoH ?? 31;
        await TryDrawLogoAsync(canvas, sourceDir, gl.LogoFile ?? "logo.png",
            logoX * os, logoY * os, logoW * os, logoH * os, ct);

        // Header geometry (offsets within a column)
        int buyHeaderX = gl.HeaderBuyX ?? 67;
        int sellHeaderX = gl.HeaderSellX ?? 104;
        int buyHeaderY = gl.HeaderBuyY ?? 3;
        int sellHeaderY = gl.HeaderSellY ?? 3;
        var hdrFont = ResolveFont(fszHdr * os, FontStyle.Bold);
        int lineStep = fszHdr + 1;

        int rowsStartY = gl.RowsStartY ?? 36;
        int maxRows = gl.SingleRows ?? 6;

        for (int c = 0; c < columns.Count; c++)
        {
            var col = columns[c];
            // Per-column absolute X wins; otherwise auto-place evenly across the canvas.
            int xOff = col.X ?? (c * pitch);

            // Per-column header labels (fall back to shared rates.json labels)
            var buy = col.Buy is { Count: > 0 } ? col.Buy : ratesCfg.Labels.Buy;
            var sell = col.Sell is { Count: > 0 } ? col.Sell : ratesCfg.Labels.Sell;

            DrawColumnHeader(canvas, buy, sell, (xOff + buyHeaderX) * os, (xOff + sellHeaderX) * os,
                buyHeaderY, sellHeaderY, lineStep, os, hdrFont, fontScaleX);

            for (int i = 0; i < col.Codes.Count && i < maxRows; i++)
            {
                var code = col.Codes[i];
                if (!ratesCfg.Currencies.TryGetValue(code, out var rate)) continue;

                var flagFile = gl.FlagFiles.TryGetValue(code, out var ff) ? ff : $"{code.ToLower()}.png";

                await DrawRowAsync(canvas, xOff * os, (rowsStartY + i * rowH) * os, rowH * os,
                    code, rate, flagsDir, flagFile, os,
                    colFlagX, colFlagW, colFlagH, colCodeX, colBuyX, colBuyW, colSellX, colSellW,
                    fszCode, fszValue, fszArrow, gl.ValueShiftX ?? -5, ct, outW,
                    fontScaleX, strokePx);
            }
        }

        canvas.Mutate(x => x.Resize(new ResizeOptions
        {
            Size = new Size(outW, outH),
            Mode = ResizeMode.Stretch,
            Sampler = KnownResamplers.Lanczos3
        }));

        return canvas;
    }

    /// <summary>Draws a 3-line buy/sell header pair for one column.</summary>
    private void DrawColumnHeader(
        Image<Rgba32> canvas, List<string> buy, List<string> sell,
        int buyCX, int sellCX, int buyHeaderY, int sellHeaderY,
        int lineStep, int os, Font hdrFont, float fontScaleX)
    {
        for (int line = 0; line < 3; line++)
        {
            var bt = buy.ElementAtOrDefault(line);
            if (!string.IsNullOrWhiteSpace(bt))
                PlaceText(canvas, bt, buyCX, (buyHeaderY + lineStep * line) * os,
                    HorizontalAlignment.Center, VerticalAlignment.Top, hdrFont, CsHdr, fontScaleX);

            var st = sell.ElementAtOrDefault(line);
            if (!string.IsNullOrWhiteSpace(st))
                PlaceText(canvas, st, sellCX, (sellHeaderY + lineStep * line) * os,
                    HorizontalAlignment.Center, VerticalAlignment.Top, hdrFont, CsHdr, fontScaleX);
        }
    }
    // ─────────────────────────────────────────────────────────────────────────

    // ─── Breakpoint resolution ────────────────────────────────────────────────

    /// <summary>
    /// Finds the first breakpoint in <paramref name="cfg"/> whose canvas constraints
    /// match the actual canvas dimensions, then merges its GridLayout on top of
    /// <paramref name="base"/> (breakpoint wins for every non-null field).
    /// </summary>
    private static GridLayout ResolveBreakpoint(ComposeConfig cfg, GridLayout @base)
    {
        if (cfg.Breakpoints is not { Count: > 0 })
            return @base;

        int w = cfg.Canvas.Width;
        int h = cfg.Canvas.Height;

        var bp = cfg.Breakpoints.FirstOrDefault(b =>
            (b.MinWidth is null || w >= b.MinWidth) &&
            (b.MaxWidth is null || w <= b.MaxWidth) &&
            (b.MinHeight is null || h >= b.MinHeight) &&
            (b.MaxHeight is null || h <= b.MaxHeight));

        if (bp?.GridLayout is null)
            return @base;

        var ov = bp.GridLayout;
        // Merge: breakpoint field wins when non-null, otherwise keep base value.
        return new GridLayout
        {
            Oversample = ov.Oversample != 0 ? ov.Oversample : @base.Oversample,
            Mode = ov.Mode ?? @base.Mode,
            FlagsDir = ov.FlagsDir ?? @base.FlagsDir,
            LogoFile = ov.LogoFile ?? @base.LogoFile,
            Left = ov.Left.Count > 0 ? ov.Left : @base.Left,
            Right = ov.Right.Count > 0 ? ov.Right : @base.Right,
            Columns = ov.Columns is { Count: > 0 } ? ov.Columns : @base.Columns,
            ColumnCount = ov.ColumnCount ?? @base.ColumnCount,
            FlagFiles = ov.FlagFiles.Count > 0 ? ov.FlagFiles : @base.FlagFiles,
            SingleRows = ov.SingleRows ?? @base.SingleRows,
            SingleLeftMargin = ov.SingleLeftMargin ?? @base.SingleLeftMargin,
            SingleHeaderGap = ov.SingleHeaderGap ?? @base.SingleHeaderGap,
            SingleTopOffset = ov.SingleTopOffset ?? @base.SingleTopOffset,
            LogoX = ov.LogoX ?? @base.LogoX,
            LogoY = ov.LogoY ?? @base.LogoY,
            LogoW = ov.LogoW ?? @base.LogoW,
            LogoH = ov.LogoH ?? @base.LogoH,
            HeaderBuyX = ov.HeaderBuyX ?? @base.HeaderBuyX,
            HeaderBuyY = ov.HeaderBuyY ?? @base.HeaderBuyY,
            HeaderSellX = ov.HeaderSellX ?? @base.HeaderSellX,
            HeaderSellY = ov.HeaderSellY ?? @base.HeaderSellY,
            RowsStartY = ov.RowsStartY ?? @base.RowsStartY,
            HeaderH = ov.HeaderH ?? @base.HeaderH,
            RowH = ov.RowH ?? @base.RowH,
            ColFlagX = ov.ColFlagX ?? @base.ColFlagX,
            ColFlagW = ov.ColFlagW ?? @base.ColFlagW,
            ColFlagH = ov.ColFlagH ?? @base.ColFlagH,
            ColCodeX = ov.ColCodeX ?? @base.ColCodeX,
            ColBuyX = ov.ColBuyX ?? @base.ColBuyX,
            ColBuyW = ov.ColBuyW ?? @base.ColBuyW,
            ColSellX = ov.ColSellX ?? @base.ColSellX,
            ColSellW = ov.ColSellW ?? @base.ColSellW,
            FszHdr = ov.FszHdr ?? @base.FszHdr,
            FszCode = ov.FszCode ?? @base.FszCode,
            FszValue = ov.FszValue ?? @base.FszValue,
            FszArrow = ov.FszArrow ?? @base.FszArrow,
            ValueShiftX = ov.ValueShiftX ?? @base.ValueShiftX,
            FontScaleX = ov.FontScaleX ?? @base.FontScaleX,
            TextStroke = ov.TextStroke ?? @base.TextStroke,
        };
    }

    // ─────────────────────────────────────────────────────────────────────────

    private static void DrawSectionHeaders(
        Image<Rgba32> canvas,
        int sectX,
        LabelsConfig labels,
        int os,
        int colBuyX, int colBuyW,
        int colSellX, int colSellW,
        int fszHdr)
    {
        // Column header centers (output-pixel positions, then ×os)
        int buyCX = (sectX + colBuyX + colBuyW / 2) * os;
        int sellCX = (sectX + colSellX + colSellW / 2) * os;

        int fsz = fszHdr * os;
        var f = ResolveFont(fsz);

        // Two-line header: Russian / English (fits compact 12px header)
        int gap = os;
        int line1Y = os;
        int line2Y = line1Y + fsz + gap;

        var buyL1 = labels.Buy.ElementAtOrDefault(1) ?? "Покупаем";
        var buyL2 = labels.Buy.ElementAtOrDefault(2) ?? "We buy";
        var sellL1 = labels.Sell.ElementAtOrDefault(1) ?? "Продаём";
        var sellL2 = labels.Sell.ElementAtOrDefault(2) ?? "We sell";

        PlaceText(canvas, buyL1, buyCX, line1Y, HorizontalAlignment.Center, VerticalAlignment.Top, f, CsHdr);
        PlaceText(canvas, buyL2, buyCX, line2Y, HorizontalAlignment.Center, VerticalAlignment.Top, f, CsHdr);
        PlaceText(canvas, sellL1, sellCX, line1Y, HorizontalAlignment.Center, VerticalAlignment.Top, f, CsHdr);
        PlaceText(canvas, sellL2, sellCX, line2Y, HorizontalAlignment.Center, VerticalAlignment.Top, f, CsHdr);
    }

    // private async Task DrawRowAsync(
    //     Image<Rgba32> canvas,
    //     int sectXPx,
    //     int rowTopPx,
    //     int rowHPx,
    //     string code,
    //     CurrencyRate rate,
    //     string flagsDir,
    //     string flagFile,
    //     int os,
    //     int colFlagX, int colFlagW, int colFlagH,
    //     int colCodeX, int colBuyX, int colBuyW, int colSellX, int colSellW,
    //     int fszCode, int fszValue, int fszArrow, int valueShiftX,
    //     CancellationToken ct)
    // {
    //     // ── flag ──────────────────────────────────────────────────────────
    //     int fw = colFlagW * os, fh = colFlagH * os;
    //     int fx = sectXPx + colFlagX * os;
    //     int fy = rowTopPx + (rowHPx - fh) / 2;

    //     try
    //     {
    //         using var flag = await LoadImageAsync(flagsDir, flagFile, fw, fh, ct);
    //         canvas.Mutate(x => x.DrawImage(flag, new Point(fx, fy), 1f));
    //     }
    //     catch (Exception ex)
    //     {
    //         _logger.LogDebug("Flag load failed ({F}): {E}. Trying fallback usd.png", flagFile, ex.Message);
    //         try
    //         {
    //             using var fallbackFlag = await LoadImageAsync(flagsDir, "usd.png", fw, fh, ct);
    //             canvas.Mutate(x => x.DrawImage(fallbackFlag, new Point(fx, fy), 1f));
    //         }
    //         catch (Exception fallbackEx)
    //         {
    //             _logger.LogDebug("Flag fallback skip (usd.png): {E}", fallbackEx.Message);
    //         }
    //     }

    //     int midY = rowTopPx + rowHPx / 2;

    //     // ── currency code ─────────────────────────────────────────────────
    //     PlaceText(canvas, code,
    //         sectXPx + colCodeX * os, midY,
    //         HorizontalAlignment.Left, VerticalAlignment.Center,
    //         ResolveFont(fszCode * os, FontStyle.Bold), CsCode);

    //     // ── buy value ────────────────────────────────────────────────────────────────
    //     PlaceText(canvas, FmtRate(rate.Buy),
    //         sectXPx + (colBuyX + colBuyW / 2 + valueShiftX) * os, midY,
    //         HorizontalAlignment.Center, VerticalAlignment.Center,
    //         ResolveFont(fszValue * os, FontStyle.Bold), CsBuy);
    //     DrawArrow(canvas, sectXPx + (colBuyX + colBuyW - 5) * os, midY,
    //         Direction(rate.PrevBuy, rate.Buy), isBuy: true, os, fszArrow);

    //     // ── sell value ───────────────────────────────────────────────────────────────
    //     PlaceText(canvas, FmtRate(rate.Sell),
    //         sectXPx + (colSellX + colSellW / 2 + valueShiftX) * os, midY,
    //         HorizontalAlignment.Center, VerticalAlignment.Center,
    //         ResolveFont(fszValue * os, FontStyle.Bold), CsSell);
    //     DrawArrow(canvas, sectXPx + (colSellX + colSellW - 5) * os, midY,
    //         Direction(rate.PrevSell, rate.Sell), isBuy: false, os, fszArrow);
    // }

    private async Task DrawRowAsync(
    Image<Rgba32> canvas,
    int sectXPx,
    int rowTopPx,
    int rowHPx,
    string code,
    CurrencyRate rate,
    string flagsDir,
    string flagFile,
    int os,
    int colFlagX, int colFlagW, int colFlagH,
    int colCodeX, int colBuyX, int colBuyW, int colSellX, int colSellW,
    int fszCode, int fszValue, int fszArrow, int valueShiftX,
    CancellationToken ct,
    int outW,
    float fontScaleX = 1f,
    float strokePx = 0f)
{
    int midY = rowTopPx + rowHPx / 2;

    // Flag
    int fx = sectXPx + colFlagX * os;
    int fy = rowTopPx + (rowHPx - colFlagH * os) / 2;
    try
    {
        using var flag = await LoadImageAsync(flagsDir, flagFile, colFlagW * os, colFlagH * os, ct);
        canvas.Mutate(x => x.DrawImage(flag, new Point(fx, fy), 1f));
    }
    catch { /* fallback if needed */ }

    // Code
    PlaceTextStretched(canvas, code,
        sectXPx + colCodeX * os, midY,
        HorizontalAlignment.Left, VerticalAlignment.Center,
        ResolveFont(fszCode * os, FontStyle.Bold), CsCode,
        fontScaleX, strokePx,verticalScale: 1.3f);

    // 3-decimal currencies (UZS, VND) print a longer number, so shrink this row's
    // value font by a couple pixels to keep it inside the column.
    int valueFsz = ThreeDecimalCurrencies.Contains(code) ? Math.Max(1, fszValue - 2) : fszValue;

    var valueFont = ResolveFont(valueFsz * os, FontStyle.Bold);

    // Авто-уменьшение шрифта для узких табло
    if (outW <= 160)
    {
        var testFont = ResolveFont((int)(valueFsz * 0.93 * os), FontStyle.Bold);
        var buySize = TextMeasurer.MeasureSize(FmtRate(rate.Buy, code), new RichTextOptions(testFont));
        if (buySize.Width * fontScaleX / os > colBuyW - 6)
            valueFont = testFont;
    }

    // Buy
    PlaceTextStretched(canvas, FmtRate(rate.Buy, code),
        sectXPx + (colBuyX + colBuyW / 2 + valueShiftX) * os, midY,
        HorizontalAlignment.Center, VerticalAlignment.Center, valueFont, CsBuy,
        fontScaleX, strokePx, verticalScale: 1.3f);

    DrawArrow(canvas, sectXPx + (colBuyX + colBuyW - 4) * os, midY,
        Direction(rate.PrevBuy, rate.Buy), true, os, fszArrow, fontScaleX);

    // Sell
    PlaceTextStretched(canvas, FmtRate(rate.Sell, code),
        sectXPx + (colSellX + colSellW / 2 + valueShiftX) * os, midY,
        HorizontalAlignment.Center, VerticalAlignment.Center, valueFont, CsSell,
        fontScaleX, strokePx, verticalScale: 1.3f);

    DrawArrow(canvas, sectXPx + (colSellX + colSellW - 4) * os, midY,
        Direction(rate.PrevSell, rate.Sell), false, os, fszArrow, fontScaleX);
}

    private static int Direction(decimal prev, decimal current) =>
        prev <= 0 || prev == current ? 0 : current > prev ? 1 : -1;

    private void DrawArrow(
        Image<Rgba32> canvas, int x, int y, int dir, bool isBuy, int os, int fszArrow, float scaleX = 1f)
    {
        if (dir == 0) return;
        bool up = dir > 0;
        // Buy: up = bad (red ▲), down = good (green ▼)
        // Sell: up = good (green ▲), down = bad (red ▼)
        Color color = isBuy
            ? (up ? CsArrowRed : CsArrowGreen)
            : (up ? CsArrowGreen : CsArrowRed);
        PlaceText(canvas, up ? "\u25b2" : "\u25bc",
            x, y, HorizontalAlignment.Right, VerticalAlignment.Center,
            ResolveFont(fszArrow * os), color, scaleX);
    }

    private static void PlaceText(
        Image<Rgba32> canvas,
        string text,
        int x, int y,
        HorizontalAlignment ha,
        VerticalAlignment va,
        Font font,
        Color color,
        float scaleX = 1f,
        float strokePx = 0f)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var textOpts = new RichTextOptions(font)
        {
            Origin = new PointF(x, y),
            HorizontalAlignment = ha,
            VerticalAlignment = va,
        };
        var drawOpts = scaleX != 1f
            ? new DrawingOptions { Transform = Matrix3x2.CreateScale(scaleX, 1f, new Vector2(x, y)) }
            : new DrawingOptions();
        canvas.Mutate(ctx =>
        {
            if (strokePx > 0)
                ctx.DrawText(drawOpts, textOpts, text,
                    Brushes.Solid(Color.Transparent),
                    Pens.Solid(Color.Black, strokePx));
            ctx.DrawText(drawOpts, textOpts, text,
                Brushes.Solid(color),
                Pens.Solid(Color.Transparent, 0.01f));
        });
    }

    private async Task TryDrawLogoAsync(
        Image<Rgba32> canvas,
        string sourceDir, string logoFile,
        int x, int y, int w, int h,
        CancellationToken ct)
    {
        try
        {
            using var logo = await LoadSvgOrRasterAsync(sourceDir, logoFile, w, h, ct);
            canvas.Mutate(ctx => ctx.DrawImage(logo, new Point(x, y), 1f));
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Logo not loaded: {Err}", ex.Message);
        }
    }

    // ─── helpers ──────────────────────────────────────────────────────────────

    // Currencies whose per-unit value is tiny (e.g. UZS, VND) are shown with 3 decimals
    // so the rate stays readable.
    private static readonly HashSet<string> ThreeDecimalCurrencies =
        new(StringComparer.OrdinalIgnoreCase) { "UZS", "VND" };

    private static string FmtRate(decimal v, string code)
    {
        if (ThreeDecimalCurrencies.Contains(code))
            return v.ToString("0.000", CultureInfo.InvariantCulture);

        return v.ToString(v == decimal.Floor(v) ? "0" : "0.##", CultureInfo.InvariantCulture);
    }

    private static Font ResolveFont(int size, FontStyle style = FontStyle.Regular)
    {
        size = Math.Max(1, size);
        foreach (var name in new[] { "Arial", "Helvetica Neue", "Helvetica", "FreeSans" })
            if (SystemFonts.TryGet(name, out var fam))
                return fam.CreateFont(size, style);

        var all = SystemFonts.Collection.Families.ToList();
        return all.Count > 0
            ? all[0].CreateFont(size, style)
            : SystemFonts.Get("Arial").CreateFont(size, FontStyle.Regular);
    }

    private static async Task<Image<Rgba32>> LoadImageAsync(
        string dir, string file, int w, int h, CancellationToken ct)
    {
        var path = ResolvePath(dir, file);
        await using var fs = File.OpenRead(path);
        using var raw = await Image.LoadAsync<Rgba32>(fs, ct);
        return raw.Clone(ctx => ctx.Resize(new ResizeOptions
        {
            Size = new Size(Math.Max(1, w), Math.Max(1, h)),
            Mode = ResizeMode.Pad,
            Position = AnchorPositionMode.Center,
            PadColor = Color.Transparent
        }));
    }

    private static async Task<Image<Rgba32>> LoadSvgOrRasterAsync(
        string dir, string file, int w, int h, CancellationToken ct)
    {
        var path = ResolvePath(dir, file);
        if (!Path.GetExtension(path).Equals(".svg", StringComparison.OrdinalIgnoreCase))
            return await LoadImageAsync(dir, file, w, h, ct);

        var tmp = await ConvertSvgAsync(path, ct);
        try
        {
            await using var s = File.OpenRead(tmp);
            using var raw = await Image.LoadAsync<Rgba32>(s, ct);
            return raw.Clone(ctx => ctx.Resize(new ResizeOptions
            {
                Size = new Size(Math.Max(1, w), Math.Max(1, h)),
                Mode = ResizeMode.Pad,
                Position = AnchorPositionMode.Center,
                PadColor = Color.Transparent
            }));
        }
        finally { TryDelete(tmp); }
    }

    private static async Task<string> ConvertSvgAsync(string path, CancellationToken ct)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"led-{Guid.NewGuid():N}.png");
        string[][] cmds =
        [
            ["inkscape", path, "--export-type=png", $"--export-filename={tmp}"],
            ["rsvg-convert", path, "-o", tmp],
            ["sips", "-s", "format", "png", path, "--out", tmp],
        ];
        foreach (var args in cmds)
        {
            try
            {
                var psi = new ProcessStartInfo(args[0])
                { UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true };
                foreach (var a in args.Skip(1)) psi.ArgumentList.Add(a);
                using var p = Process.Start(psi);
                if (p is null) continue;
                await p.WaitForExitAsync(ct);
                if (p.ExitCode == 0 && File.Exists(tmp)) return tmp;
            }
            catch { /* try next */ }
        }
        throw new InvalidOperationException("SVG decode failed. Install inkscape or rsvg-convert.");
    }

    private static void TryDelete(string p) { try { if (File.Exists(p)) File.Delete(p); } catch { } }

    private static async Task<T?> ReadJsonAsync<T>(string path, CancellationToken ct)
    {
        await using var fs = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(fs, JsonOpts, ct);
    }

    private static string ResolvePath(string basePath, string path) =>
        Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(basePath, path));

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    // ─── JSON model ───────────────────────────────────────────────────────────

    private sealed class ComposeConfig
    {
        public required CanvasCfg Canvas { get; init; }
        public required string SourceDir { get; init; }
        public required string OutputFile { get; init; }
        public GridLayout? GridLayout { get; init; }
        /// <summary>
        /// Optional breakpoints. The first one whose min/max canvas constraints match
        /// the actual canvas size is merged on top of the base GridLayout.
        /// </summary>
        public List<ComposeBreakpoint>? Breakpoints { get; init; }
    }

    private sealed class CanvasCfg
    {
        public int Width { get; init; }
        public int Height { get; init; }
    }

    private sealed class ComposeBreakpoint
    {
        /// <summary>Human-readable identifier, e.g. "narrow", "wide".</summary>
        public string Id { get; init; } = "";
        public int? MinWidth { get; init; }
        public int? MaxWidth { get; init; }
        public int? MinHeight { get; init; }
        public int? MaxHeight { get; init; }
        /// <summary>Layout overrides applied when this breakpoint is active.</summary>
        public GridLayout? GridLayout { get; init; }
    }

    private sealed class GridLayout
    {
        public int Oversample { get; init; } = 4;
        public string? Mode { get; init; }
        public string? FlagsDir { get; init; } = "../flags";
        public string? LogoFile { get; init; } = "logo.svg";
        public List<string> Left { get; init; } = [];
        public List<string> Right { get; init; } = [];
        public Dictionary<string, string> FlagFiles { get; init; } =
            new(StringComparer.OrdinalIgnoreCase);

        // Multi-column mode (mode = "columns"): up to 3 independent columns,
        // each with its own currency list and buy/sell header labels.
        public List<ColumnDef>? Columns { get; init; }
        public int? ColumnCount { get; init; }

        // Single-column mode settings (mode = "singleColumn")
        public int? SingleRows { get; init; }
        public int? SingleLeftMargin { get; init; }
        public int? SingleHeaderGap { get; init; }
        public int? SingleTopOffset { get; init; }

        // Absolute anchors for independent positioning in single-column mode.
        public int? LogoX { get; init; }
        public int? LogoY { get; init; }
        public int? LogoH { get; init; }
        public int? HeaderBuyX { get; init; }
        public int? HeaderBuyY { get; init; }
        public int? HeaderSellX { get; init; }
        public int? HeaderSellY { get; init; }
        public int? RowsStartY { get; init; }

        // ── Nullable layout tuning fields ─────────────────────────────────
        // When null the corresponding Default* constant is used.
        // Set these in gridLayout or in a breakpoint's gridLayout to override.

        /// <summary>Logo strip width in output pixels.</summary>
        public int? LogoW { get; init; }
        /// <summary>Header row height in output pixels.</summary>
        public int? HeaderH { get; init; }
        /// <summary>Currency row height in output pixels.</summary>
        public int? RowH { get; init; }

        // Column offsets from section left edge (output pixels)
        public int? ColFlagX { get; init; }
        public int? ColFlagW { get; init; }
        public int? ColFlagH { get; init; }
        public int? ColCodeX { get; init; }
        public int? ColBuyX { get; init; }
        public int? ColBuyW { get; init; }
        public int? ColSellX { get; init; }
        public int? ColSellW { get; init; }

        // Font sizes at 1× (multiplied by oversample during render)
        public int? FszHdr { get; init; }
        public int? FszCode { get; init; }
        public int? FszValue { get; init; }
        public int? FszArrow { get; init; }

        // Horizontal shift for BUY/SELL numeric values in pixels.
        // Negative shifts left, positive shifts right.
        public int? ValueShiftX { get; init; }

        /// <summary>Horizontal scale factor for text rendering (e.g. 0.91 = 9% narrower). Default: 1.0</summary>
        public float? FontScaleX { get; init; }
        /// <summary>Outline stroke width for value/code text in output pixels. 0 = no stroke.</summary>
        public int? TextStroke { get; init; }
    }

    private sealed class ColumnDef
    {
        public List<string> Codes { get; init; } = [];
        public List<string>? Buy { get; init; }
        public List<string>? Sell { get; init; }

        /// <summary>
        /// Optional absolute X offset of this column in 1× pixels. When null the column
        /// is auto-placed at index × (canvasWidth / columnCount). Set per column to lay
        /// columns out freely — e.g. a centred logo with rates on both sides on a wide board.
        /// </summary>
        public int? X { get; init; }
    }

    private sealed class RatesConfig
    {
        public LabelsConfig Labels { get; init; } = new();
        public Dictionary<string, CurrencyRate> Currencies { get; init; } =
            new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class LabelsConfig
    {
        public List<string> Buy { get; init; } = [];
        public List<string> Sell { get; init; } = [];
    }

    private sealed class CurrencyRate
    {
        public decimal Buy { get; init; }
        public decimal Sell { get; init; }
        public decimal PrevBuy { get; init; }
        public decimal PrevSell { get; init; }
    }
}
