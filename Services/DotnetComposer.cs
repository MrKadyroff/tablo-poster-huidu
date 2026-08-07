using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Processing.Processors.Quantization;
// Disambiguate types that conflict with System.Drawing / System.Windows.Forms
using Brushes = SixLabors.ImageSharp.Drawing.Processing.Brushes;
using Color = SixLabors.ImageSharp.Color;
using Font = SixLabors.Fonts.Font;
using FontFamily = SixLabors.Fonts.FontFamily;
using Rectangle = SixLabors.ImageSharp.Rectangle;
using RectangleF = SixLabors.ImageSharp.RectangleF;
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

    // Colors — defaults for the classic black board. A compose config can override
    // them (bgColor / hdrColor / codeColor / valueColor + the row-stripe fields) to
    // get the light "table" look: grey header band and alternating row stripes.
    private static readonly Color DefaultBg = Color.Black;
    private static readonly Color DefaultHdr = Color.FromRgb(160, 160, 160);
    private static readonly Color CsArrowRed = Color.FromRgb(255, 60, 60);
    private static readonly Color CsArrowGreen = Color.FromRgb(50, 220, 70);

    // Resolved per render from the active GridLayout. Renders are sequential (one
    // worker; the settings preview uses its own composer instance), so plain fields
    // are enough — they are set once at the top of RenderGridAsync.
    private Color CsBg = DefaultBg;

    // ── Flag rendering state (reset at the top of every render) ──────────────
    // Flags are always resized to exactly colFlagW × colFlagH so every flag has the
    // same width regardless of its source aspect ratio, and get rounded corners.
    private string _flagFit = "crop";
    private float _flagRadiusPx;          // corner radius in *render* pixels (1× × oversample)
    private bool _flagOnTop;              // true = draw flags after all other content
    private int _flagOs = 1;              // oversample of the current render

    /// <summary>Flags queued for the top layer: the image and its render-space position.</summary>
    private readonly List<(Image<Rgba32> Image, Point At)> _pendingFlags = new();

    /// <summary>Where each flag ended up, in 1× output pixels — used to place the shine.</summary>
    private readonly List<Rectangle> _flagRects = new();
    private Color CsHdr = DefaultHdr;
    private Color CsCode = Color.White;
    private Color CsBuy = Color.White;
    private Color CsSell = Color.White;

    /// <summary>Parses "#RRGGBB" / "#AARRGGBB" / "rrggbb"; returns null when unset or invalid.</summary>
    private static Color? ParseColor(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var t = s.Trim();
        if (!t.StartsWith('#')) t = "#" + t;
        return Color.TryParseHex(t, out var c) ? c : null;
    }

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

        // Flag look for this render
        ResetFlagState();
        _flagOs = os;
        _flagFit = (gl.FlagFit ?? "crop").ToLowerInvariant();
        _flagRadiusPx = Math.Max(0f, gl.FlagRadius ?? 2f) * os;
        _flagOnTop = gl.FlagOnTop ?? false;

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

        // Palette for this render (config wins over the classic black-board defaults)
        CsBg = ParseColor(gl.BgColor) ?? DefaultBg;
        CsHdr = ParseColor(gl.HdrColor) ?? DefaultHdr;
        CsCode = ParseColor(gl.CodeColor) ?? Color.White;
        CsBuy = ParseColor(gl.ValueColor) ?? Color.White;
        CsSell = CsBuy;

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

            var saved = await FinalizeAsync(rendered, gl, ratesCfg, outW, outH, outPath, ct);
            _logger.LogInformation("Multi-column board composed → {Out}", saved);
            return saved;
        }

        if (string.Equals(gl.Mode, "singleColumn", StringComparison.OrdinalIgnoreCase))
        {
            var rendered = await RenderSingleColumnAsync(
                canvas, gl, ratesCfg, sourceDir, flagsDir, outW, outH, os, rh,
                headerH, rowH, colFlagX, colFlagW, colFlagH,
                colCodeX, colBuyX, colBuyW, colSellX, colSellW,
                fszHdr, fszCode, fszValue, fszArrow, ct);

            var saved = await FinalizeAsync(rendered, gl, ratesCfg, outW, outH, outPath, ct);
            _logger.LogInformation("Single-column board composed → {Out}", saved);
            return saved;
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


        FlushFlags(canvas);

        // ── downscale to output dimensions with Lanczos3 ──────────────────
        canvas.Mutate(x => x.Resize(new ResizeOptions
        {
            Size = new Size(outW, outH),
            Mode = ResizeMode.Stretch,
            Sampler = KnownResamplers.Lanczos3
        }));

        var savedPath = await FinalizeAsync(canvas, gl, ratesCfg, outW, outH, outPath, ct);
        _logger.LogInformation("Grid board composed → {Out}", savedPath);
        return savedPath;
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

    // Padding must scale with the render, not be a fixed pixel count: the font size and
    // the outline stroke are both multiplied by oversample, so a constant 2 px margin is
    // eaten by the stroke alone at os = 4 and shaves the edge glyphs. Side padding covers
    // the stroke plus the side bearing MeasureSize does not report; the bottom also has
    // to hold descenders.
    int pad = (int)MathF.Ceiling(strokePx) + (int)MathF.Ceiling(font.Size * 0.15f) + 2;
    int padL = pad, padT = pad, padR = pad;
    int padB = pad + (int)MathF.Ceiling(font.Size * 0.35f);
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

    private Task SaveJpegWithRetryAsync(Image<Rgba32> image, string outPath, CancellationToken ct) =>
        SaveWithRetryAsync(image, outPath, animated: false, ct);

    /// <summary>
    /// GIF encoder tuned for LED boards: one global palette of a few dozen colours instead of
    /// a 256-colour table per frame. The board art is flat colour, so the picture is unchanged
    /// while the file gets markedly smaller — and a smaller file is a shorter upload, which is
    /// what the card shows its "Loading…" splash for.
    /// </summary>
    private static GifEncoder BuildGifEncoder(int colors) => new()
    {
        ColorTableMode = GifColorTableMode.Global,
        Quantizer = new OctreeQuantizer(new QuantizerOptions
        {
            MaxColors = Math.Clamp(colors, 2, 256),
            Dither = null,   // dithering adds noise the LZW pass cannot compress
        }),
    };

    private async Task SaveWithRetryAsync(
        Image<Rgba32> image, string outPath, bool animated, CancellationToken ct, int gifColors = 64)
    {
        var dir = Path.GetDirectoryName(outPath) ?? Directory.GetCurrentDirectory();
        Directory.CreateDirectory(dir);

        // Write to a temp file first, then overwrite target.
        // This minimizes partially-written outputs and handles short file locks.
        var tempPath = Path.Combine(dir, $".{Path.GetFileName(outPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            if (animated)
                await image.SaveAsGifAsync(tempPath, BuildGifEncoder(gifColors), ct);
            else
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

    /// <summary>
    /// Paints the table chrome behind one column: the header band, alternating row
    /// stripes and the separator lines — the "spreadsheet" look. Every part is opt-in:
    /// with none of the colors configured this draws nothing and the board stays flat.
    /// Coordinates are 1× layout pixels; the method scales them by <paramref name="os"/>.
    /// </summary>
    private static void DrawTableChrome(
        Image<Rgba32> canvas, GridLayout gl, int os,
        int x, int width, int rowsStartY, int rowH, int rowCount, int canvasH)
    {
        var headerBg = ParseColor(gl.HeaderBg);
        var rowOdd = ParseColor(gl.RowBgOdd);
        var rowEven = ParseColor(gl.RowBgEven);
        var lineColor = ParseColor(gl.GridLineColor);
        int lineW = gl.GridLineWidth ?? 1;
        if (headerBg is null && rowOdd is null && rowEven is null && lineColor is null) return;

        int gap = gl.RowGap ?? 0;

        canvas.Mutate(ctx =>
        {
            if (headerBg is { } hb && rowsStartY > 0)
                ctx.Fill(hb, new SixLabors.ImageSharp.RectangleF(x * os, 0, width * os, rowsStartY * os));

            for (int i = 0; i < rowCount; i++)
            {
                // With only one stripe color configured every row uses it.
                var fill = (i % 2 == 0 ? rowOdd : rowEven) ?? (i % 2 == 0 ? rowEven : rowOdd);
                if (fill is null) break;

                int top = (rowsStartY + i * rowH) * os;
                int h = (rowH - gap) * os;
                if (h <= 0 || top >= canvasH) continue;
                ctx.Fill(fill.Value, new SixLabors.ImageSharp.RectangleF(x * os, top, width * os, h));
            }

            if (lineColor is not { } lc || lineW <= 0) return;

            // Separator under the header and between the rows
            for (int i = 0; i <= rowCount; i++)
            {
                int y = (rowsStartY + i * rowH) * os - lineW * os / 2;
                if (y < 0 || y >= canvasH) continue;
                ctx.Fill(lc, new SixLabors.ImageSharp.RectangleF(x * os, y, width * os, Math.Max(1, lineW * os)));
            }
        });
    }

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

    // Table chrome first — everything else is drawn on top of it
    DrawTableChrome(canvas, gl, os, 0, outW,
        gl.RowsStartY ?? 36, rowH,
        Math.Min(gl.Left.Count, gl.SingleRows ?? 6), rh);

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

    // Optional caption over the currency column ("Валюта"), centred in the header band
    if (!string.IsNullOrWhiteSpace(gl.CodeHeader))
    {
        int capX = gl.HeaderCodeX ?? ((colFlagX + colCodeX) / 2);
        int capY = gl.HeaderCodeY ?? ((gl.RowsStartY ?? 36) / 2);
        PlaceText(canvas, gl.CodeHeader, capX * os, capY * os,
            HorizontalAlignment.Center, VerticalAlignment.Center, hdrFont, CsHdr, fontScaleX);
    }

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

    FlushFlags(canvas);

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

            // Table chrome for this column, painted before its content
            DrawTableChrome(canvas, gl, os, xOff, pitch, rowsStartY, rowH,
                Math.Min(col.Codes.Count, maxRows), outH * os);

            // Per-column header labels (fall back to shared rates.json labels)
            var buy = col.Buy is { Count: > 0 } ? col.Buy : ratesCfg.Labels.Buy;
            var sell = col.Sell is { Count: > 0 } ? col.Sell : ratesCfg.Labels.Sell;

            if (!string.IsNullOrWhiteSpace(gl.CodeHeader))
            {
                int capX = xOff + (gl.HeaderCodeX ?? ((colFlagX + colCodeX) / 2));
                int capY = gl.HeaderCodeY ?? (rowsStartY / 2);
                PlaceText(canvas, gl.CodeHeader, capX * os, capY * os,
                    HorizontalAlignment.Center, VerticalAlignment.Center, hdrFont, CsHdr, fontScaleX);
            }

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

        FlushFlags(canvas);

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
            BgColor = ov.BgColor ?? @base.BgColor,
            HeaderBg = ov.HeaderBg ?? @base.HeaderBg,
            RowBgOdd = ov.RowBgOdd ?? @base.RowBgOdd,
            RowBgEven = ov.RowBgEven ?? @base.RowBgEven,
            RowGap = ov.RowGap ?? @base.RowGap,
            GridLineColor = ov.GridLineColor ?? @base.GridLineColor,
            GridLineWidth = ov.GridLineWidth ?? @base.GridLineWidth,
            HdrColor = ov.HdrColor ?? @base.HdrColor,
            CodeColor = ov.CodeColor ?? @base.CodeColor,
            ValueColor = ov.ValueColor ?? @base.ValueColor,
            CodeHeader = ov.CodeHeader ?? @base.CodeHeader,
            HeaderCodeX = ov.HeaderCodeX ?? @base.HeaderCodeX,
            HeaderCodeY = ov.HeaderCodeY ?? @base.HeaderCodeY,
            FlagFit = ov.FlagFit ?? @base.FlagFit,
            FlagRadius = ov.FlagRadius ?? @base.FlagRadius,
            FlagOnTop = ov.FlagOnTop ?? @base.FlagOnTop,
            AnimFrames = ov.AnimFrames ?? @base.AnimFrames,
            AnimDelayMs = ov.AnimDelayMs ?? @base.AnimDelayMs,
            AnimColors = ov.AnimColors ?? @base.AnimColors,
            Ticker = ov.Ticker ?? @base.Ticker,
            Shine = ov.Shine ?? @base.Shine,
        };
    }

    // ─────────────────────────────────────────────────────────────────────────

    private void DrawSectionHeaders(
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
    await DrawFlagAsync(canvas, flagsDir, flagFile, fx, fy, colFlagW * os, colFlagH * os, ct);

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

    // ─── output: static JPEG, or animated GIF with ticker / flag shine ────────

    /// <summary>Orange of the eCash logo — the ticker band default.</summary>
    private static readonly Color DefaultTickerBg = Color.FromRgb(0xF5, 0x82, 0x20);

    /// <summary>
    /// Writes the finished board. With no animation enabled this is the previous
    /// behaviour (one JPEG). With ticker and/or shine on, the board is squeezed under
    /// the ticker band and the whole thing is written as a looping GIF next to it —
    /// the JPEG is removed so the publisher picks up the animation instead.
    /// </summary>
    private async Task<string> FinalizeAsync(
        Image<Rgba32> board,
        GridLayout gl,
        RatesConfig ratesCfg,
        int outW, int outH,
        string outPath,
        CancellationToken ct)
    {
        var tk = gl.Ticker;
        var sh = gl.Shine;
        bool tickerOn = tk is { Enabled: true };
        bool shineOn = sh is { Enabled: true } && _flagRects.Count > 0;

        if (!tickerOn && !shineOn)
        {
            await SaveJpegWithRetryAsync(board, outPath, ct);
            return outPath;
        }

        int delayMs = Math.Clamp(gl.AnimDelayMs ?? 70, 20, 500);
        int maxFrames = Math.Clamp(gl.AnimFrames ?? 80, 4, 200);

        // Band height: explicit ticker.h wins, otherwise ~14 % of the board.
        int tickerH = tickerOn
            ? Math.Clamp(tk!.H ?? (int)Math.Round(outH * 0.14), 6, outH / 2)
            : 0;

        // Board is squeezed into the remaining height so nothing is covered.
        using var baseFrame = new Image<Rgba32>(outW, outH, CsBg);
        int boardH = outH - tickerH;
        using (var squeezed = board.Clone(c => c.Resize(new ResizeOptions
        {
            Size = new Size(outW, boardH),
            Mode = ResizeMode.Stretch,
            Sampler = KnownResamplers.Lanczos3
        })))
        {
            baseFrame.Mutate(c => c.DrawImage(squeezed, new Point(0, tickerH), 1f));
        }

        // Flag slots follow the same vertical squeeze.
        float vScale = (float)boardH / outH;
        var shineRects = _flagRects
            .Select(r => new RectangleF(r.X, tickerH + r.Y * vScale, r.Width, r.Height * vScale))
            .ToList();

        // Ticker strip: one copy of the text, tiled horizontally while it scrolls.
        Image<Rgba32>? strip = null;
        Color bandColor = ParseColor(tk?.BgColor) ?? DefaultTickerBg;
        int frames = Math.Clamp(gl.AnimFrames ?? 24, 4, maxFrames);

        try
        {
            if (tickerOn)
            {
                strip = BuildTickerStrip(tk!, ratesCfg, tickerH);
                // One loop must cover exactly one strip width, otherwise the scroll
                // jumps when the GIF restarts. Frames follow from the wanted speed.
                float speed = Math.Clamp(tk!.Speed ?? 3f, 0.5f, 20f);
                frames = Math.Clamp((int)Math.Round(strip.Width / speed), 8, maxFrames);

                float actual = (float)strip.Width / frames;
                if (actual > speed * 1.5f)
                {
                    _logger.LogWarning(
                        "Бегущая строка: текст {W}px не помещается в {F} кадров — прокрутка {A:0.#} px/кадр " +
                        "вместо {S:0.#}. Укоротите ticker.text/langs, уменьшите ticker.fontSize " +
                        "или поднимите gridLayout.animFrames.",
                        strip.Width, frames, actual, speed);
                }
            }

            // Which flags glow this render, and where each one is in its sweep.
            var rnd = new Random();
            var shinePhases = new Dictionary<int, float>();
            if (shineOn)
            {
                int count = Math.Clamp(sh!.Count ?? 3, 1, shineRects.Count);
                var picked = Enumerable.Range(0, shineRects.Count)
                    .OrderBy(_ => rnd.Next())
                    .Take(count)
                    .ToList();
                for (int i = 0; i < picked.Count; i++)
                    shinePhases[picked[i]] = (float)i / picked.Count;
            }

            float shineStrength = Math.Clamp(sh?.Strength ?? 0.75f, 0.05f, 1f);
            float shineWidth = Math.Clamp(sh?.Width ?? 0.35f, 0.05f, 2f);

            Image<Rgba32>? anim = null;
            try
            {
                for (int f = 0; f < frames; f++)
                {
                    ct.ThrowIfCancellationRequested();
                    using var frame = baseFrame.Clone();

                    if (tickerOn && strip is not null)
                    {
                        float step = (float)strip.Width / frames;
                        float offset = -(f * step);
                        frame.Mutate(c =>
                        {
                            c.Fill(bandColor, new RectangleF(0, 0, outW, tickerH));
                            for (float x = offset; x < outW; x += strip.Width)
                                c.DrawImage(strip, new Point((int)MathF.Round(x), 0), 1f);
                        });
                    }

                    foreach (var (idx, phase) in shinePhases)
                    {
                        float t = ((float)f / frames + phase) % 1f;
                        DrawShine(frame, shineRects[idx], t, _flagRadiusPx / Math.Max(1, _flagOs),
                            shineStrength, shineWidth);
                    }

                    if (anim is null)
                    {
                        anim = frame.Clone();
                    }
                    else
                    {
                        anim.Frames.AddFrame(frame.Frames.RootFrame);
                    }
                }

                if (anim is null)
                {
                    await SaveJpegWithRetryAsync(board, outPath, ct);
                    return outPath;
                }

                anim.Metadata.GetGifMetadata().RepeatCount = 0;   // loop forever
                foreach (var fr in anim.Frames)
                    fr.Metadata.GetGifMetadata().FrameDelay = Math.Max(2, delayMs / 10);

                var gifPath = Path.ChangeExtension(outPath, ".gif");
                await SaveWithRetryAsync(anim, gifPath, animated: true, ct, gl.AnimColors ?? 64);

                // Drop the stale still so the watcher publishes the animation.
                if (!string.Equals(gifPath, outPath, StringComparison.OrdinalIgnoreCase))
                    TryDelete(outPath);

                long kb = new FileInfo(gifPath).Length / 1024;
                _logger.LogInformation(
                    "Animated board: {Frames} кадров × {Delay} мс, {Size} КБ → {Out}",
                    frames, delayMs, kb, gifPath);

                // Большой GIF долго заливается на карту — всё это время табло держит заставку.
                if (kb > 800)
                {
                    _logger.LogWarning(
                        "GIF весит {Size} КБ — заливка на карту займёт заметное время, и табло будет " +
                        "показывать заставку загрузки. Уменьшите gridLayout.animFrames ({Frames}), " +
                        "поднимите ticker.speed или задайте gridLayout.animColors (сейчас {Colors}).",
                        kb, frames, gl.AnimColors ?? 64);
                }

                return gifPath;
            }
            finally { anim?.Dispose(); }
        }
        finally { strip?.Dispose(); }
    }

    /// <summary>
    /// One tile of the ticker text (transparent background, band height). Tiling it
    /// horizontally and shifting by its own width gives a seamless endless scroll.
    /// </summary>
    private Image<Rgba32> BuildTickerStrip(TickerCfg tk, RatesConfig ratesCfg, int tickerH)
    {
        const int ss = 3;   // supersample for legible small text
        var text = BuildTickerText(tk, ratesCfg);

        int fsz = Math.Clamp(tk.FontSize ?? (int)Math.Round(tickerH * 0.72), 4, tickerH * 2);
        var (font, fallbacks) = ResolveTickerFont(fsz * ss);
        var color = ParseColor(tk.TextColor) ?? Color.White;

        var measureOpts = new RichTextOptions(font)
        {
            Origin = new PointF(0, 0),
            FallbackFontFamilies = fallbacks,
        };
        var size = TextMeasurer.MeasureSize(text, measureOpts);

        int gap = Math.Max(8, tickerH) * ss;                 // space between repeats
        int w = (int)MathF.Ceiling(size.Width) + gap;
        int h = tickerH * ss;

        using var big = new Image<Rgba32>(Math.Max(2, w), Math.Max(2, h));
        big.Mutate(c => c.DrawText(
            new RichTextOptions(font)
            {
                Origin = new PointF(0, h / 2f),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left,
                FallbackFontFamilies = fallbacks,
            },
            text, color));

        return big.Clone(c => c.Resize(new ResizeOptions
        {
            Size = new Size(Math.Max(2, w / ss), tickerH),
            Mode = ResizeMode.Stretch,
            Sampler = KnownResamplers.Lanczos3
        }));
    }

    /// <summary>Ticker captions per language, in the order requested by the config.</summary>
    private static readonly Dictionary<string, string> TickerLabels =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["ru"] = "Обмен валют",
            ["kz"] = "Валюта айырбастау",
            ["en"] = "Currency exchange",
            ["tr"] = "Döviz bozdurma",
            ["zh"] = "货币兑换",
            ["ar"] = "صرف العملات",
        };

    private static string BuildTickerText(TickerCfg tk, RatesConfig ratesCfg)
    {
        if (!string.IsNullOrWhiteSpace(tk.Text)) return tk.Text!;

        // Rates are off by default: they already fill the board below, and every extra
        // character makes the GIF loop longer (or the scroll faster) for the same budget.
        var rates = "";
        if (tk.Rates == true)
        {
            var codes = tk.Codes is { Count: > 0 }
                ? tk.Codes
                : ratesCfg.Currencies.Keys.Take(6).ToList();

            rates = string.Join("   ", codes
                .Where(c => ratesCfg.Currencies.ContainsKey(c))
                .Select(c =>
                {
                    var r = ratesCfg.Currencies[c];
                    return $"{c.ToUpperInvariant()} {FmtRate(r.Buy, c)}/{FmtRate(r.Sell, c)}";
                }));
        }

        var langs = tk.Langs is { Count: > 0 } ? tk.Langs : ["en", "kz", "ru", "tr", "zh", "ar"];
        var sep = string.IsNullOrEmpty(tk.Separator) ? "   ★   " : tk.Separator;

        var parts = langs
            .Where(TickerLabels.ContainsKey)
            .Select(l => string.IsNullOrEmpty(rates)
                ? TickerLabels[l]
                : $"{TickerLabels[l]}:  {rates}");

        return string.Join(sep, parts) + sep;
    }

    /// <summary>
    /// Latin/Cyrillic base font plus fallbacks that carry Chinese and Arabic glyphs —
    /// without them those segments render as empty boxes.
    /// </summary>
    private static (Font Font, List<FontFamily> Fallbacks) ResolveTickerFont(int size)
    {
        var font = ResolveFont(size, FontStyle.Bold);
        string[] candidates =
        [
            "Arial Unicode MS", "Segoe UI", "Tahoma",
            "Microsoft YaHei", "SimSun", "SimHei", "PingFang SC",
            "Noto Sans CJK SC", "Noto Sans SC", "Noto Sans Arabic",
            "Segoe UI Historic", "Geeza Pro", "Arial",
        ];

        var fallbacks = new List<FontFamily>();
        foreach (var name in candidates)
        {
            if (!SystemFonts.TryGet(name, out var fam) || fallbacks.Contains(fam)) continue;

            // Some installed families fail to parse (bitmap-only / broken tables) and
            // only blow up when a glyph is requested — probe each one before trusting it.
            try
            {
                var probe = fam.CreateFont(size);
                TextMeasurer.MeasureSize("A1汉ع", new RichTextOptions(probe));
                fallbacks.Add(fam);
            }
            catch { /* unusable family, skip */ }
        }

        return (font, fallbacks);
    }

    /// <summary>
    /// Paints a soft diagonal highlight sweeping across one flag at progress t (0..1),
    /// clipped to the flag's rounded rectangle and blended with Screen so it reads as
    /// a glint rather than a white box.
    /// </summary>
    private static void DrawShine(
        Image<Rgba32> frame, RectangleF rect, float t, float radius,
        float strength, float widthFactor)
    {
        int w = (int)MathF.Round(rect.Width);
        int h = (int)MathF.Round(rect.Height);
        if (w < 2 || h < 2) return;

        float band = MathF.Max(2f, w * widthFactor);
        float sigma = band / 2f;
        float travel = w + 2f * band + h;          // account for the diagonal slant
        float center = -band + t * travel;

        using var overlay = new Image<Rgba32>(w, h);
        overlay.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < h; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (int x = 0; x < w; x++)
                {
                    // Diagonal band: shift the sample point by the row so it leans over.
                    float d = x + (h - y) - center;
                    float a = MathF.Exp(-(d * d) / (2f * sigma * sigma)) * strength;
                    a *= RoundedCoverage(x + 0.5f, y + 0.5f, w, h, radius);
                    if (a <= 0.004f) continue;
                    row[x] = new Rgba32(255, 255, 255, (byte)Math.Clamp(a * 255f, 0f, 255f));
                }
            }
        });

        frame.Mutate(c => c.DrawImage(
            overlay,
            new Point((int)MathF.Round(rect.X), (int)MathF.Round(rect.Y)),
            PixelColorBlendingMode.Screen,
            PixelAlphaCompositionMode.SrcOver,
            1f));
    }

    // ─── flags: uniform size, rounded corners, optional top layer ─────────────

    private void ResetFlagState()
    {
        foreach (var (img, _) in _pendingFlags) img.Dispose();
        _pendingFlags.Clear();
        _flagRects.Clear();
    }

    /// <summary>
    /// Draws one flag at render-space (x, y) with the exact size (w × h) so all flags
    /// share a width. When flagOnTop is set the flag is queued instead and painted by
    /// <see cref="FlushFlags"/> after the rest of the board, so nothing can cover it.
    /// </summary>
    private async Task DrawFlagAsync(
        Image<Rgba32> canvas, string flagsDir, string flagFile,
        int x, int y, int w, int h, CancellationToken ct)
    {
        Image<Rgba32>? flag = null;
        try
        {
            flag = await LoadFlagAsync(flagsDir, flagFile, w, h, ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Flag not loaded ({File}): {Err}", flagFile, ex.Message);
            return;
        }

        // Remember the slot in 1× output pixels for the shine overlay.
        int os = Math.Max(1, _flagOs);
        _flagRects.Add(new Rectangle(x / os, y / os, Math.Max(1, w / os), Math.Max(1, h / os)));

        if (_flagOnTop)
        {
            _pendingFlags.Add((flag, new Point(x, y)));
            return;
        }

        canvas.Mutate(c => c.DrawImage(flag, new Point(x, y), 1f));
        flag.Dispose();
    }

    /// <summary>Paints queued top-layer flags. Called right before the final downscale.</summary>
    private void FlushFlags(Image<Rgba32> canvas)
    {
        if (_pendingFlags.Count == 0) return;
        canvas.Mutate(c =>
        {
            foreach (var (img, at) in _pendingFlags) c.DrawImage(img, at, 1f);
        });
        foreach (var (img, _) in _pendingFlags) img.Dispose();
        _pendingFlags.Clear();
    }

    private async Task<Image<Rgba32>> LoadFlagAsync(
        string dir, string file, int w, int h, CancellationToken ct)
    {
        var path = ResolvePath(dir, file);
        await using var fs = File.OpenRead(path);
        using var raw = await Image.LoadAsync<Rgba32>(fs, ct);

        // Crop (default) fills the whole slot, so every flag ends up exactly w × h —
        // "pad" keeps the old letterboxed look, "stretch" distorts to fit.
        var mode = _flagFit switch
        {
            "pad" => ResizeMode.Pad,
            "stretch" => ResizeMode.Stretch,
            _ => ResizeMode.Crop,
        };

        var img = raw.Clone(ctx => ctx.Resize(new ResizeOptions
        {
            Size = new Size(Math.Max(1, w), Math.Max(1, h)),
            Mode = mode,
            Position = AnchorPositionMode.Center,
            PadColor = Color.Transparent
        }));

        if (_flagRadiusPx >= 0.5f) ApplyRoundedCorners(img, _flagRadiusPx);
        return img;
    }

    /// <summary>
    /// Alpha coverage of a rounded rectangle at pixel (px, py): 1 inside, 0 outside,
    /// smoothly interpolated across one pixel on the corner arcs (antialiasing).
    /// </summary>
    private static float RoundedCoverage(float px, float py, int w, int h, float r)
    {
        r = Math.Min(r, Math.Min(w, h) / 2f);
        if (r < 0.5f) return 1f;

        // Distance outside the corner circle, per corner.
        float cx = px < r ? r : (px > w - r ? w - r : px);
        float cy = py < r ? r : (py > h - r ? h - r : py);
        float dx = px - cx, dy = py - cy;
        if (dx == 0 || dy == 0) return 1f;

        float d = MathF.Sqrt(dx * dx + dy * dy);
        if (d <= r - 0.5f) return 1f;
        if (d >= r + 0.5f) return 0f;
        return r + 0.5f - d;
    }

    private static void ApplyRoundedCorners(Image<Rgba32> img, float radius)
    {
        int w = img.Width, h = img.Height;
        img.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < h; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (int x = 0; x < w; x++)
                {
                    float cov = RoundedCoverage(x + 0.5f, y + 0.5f, w, h, radius);
                    if (cov >= 1f) continue;
                    row[x].A = (byte)(row[x].A * cov);
                }
            }
        });
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

        // ── Table look (all optional; unset = the classic flat black board) ──
        // Colors are "#RRGGBB" (an "#AARRGGBB" alpha prefix is accepted too).

        /// <summary>Canvas background. Default: black.</summary>
        public string? BgColor { get; init; }
        /// <summary>Fill behind the header band (from y=0 down to rowsStartY).</summary>
        public string? HeaderBg { get; init; }
        /// <summary>Stripe color for rows 1, 3, 5 … Set both stripes for a zebra table.</summary>
        public string? RowBgOdd { get; init; }
        /// <summary>Stripe color for rows 2, 4, 6 …</summary>
        public string? RowBgEven { get; init; }
        /// <summary>Vertical gap left unpainted at the bottom of each stripe, in 1× pixels.</summary>
        public int? RowGap { get; init; }
        /// <summary>Separator line under the header and between rows.</summary>
        public string? GridLineColor { get; init; }
        /// <summary>Separator thickness in 1× pixels. Default: 1.</summary>
        public int? GridLineWidth { get; init; }
        /// <summary>Header label text color. Default: grey (160,160,160).</summary>
        public string? HdrColor { get; init; }
        /// <summary>Currency code text color. Default: white.</summary>
        public string? CodeColor { get; init; }
        /// <summary>Buy/sell value text color. Default: white.</summary>
        public string? ValueColor { get; init; }
        /// <summary>Caption over the currency column, e.g. "Валюта". Empty = no caption.</summary>
        public string? CodeHeader { get; init; }
        /// <summary>Caption center X (1× px, per column). Default: midpoint of flag and code columns.</summary>
        public int? HeaderCodeX { get; init; }
        /// <summary>Caption center Y (1× px). Default: middle of the header band.</summary>
        public int? HeaderCodeY { get; init; }

        // ── Flag look ─────────────────────────────────────────────────────────

        /// <summary>How a flag fills its slot: "crop" (default, uniform width), "pad", "stretch".</summary>
        public string? FlagFit { get; init; }
        /// <summary>Corner radius of the flag in 1× pixels. 0 = square corners. Default: 2.</summary>
        public float? FlagRadius { get; init; }
        /// <summary>Draw flags above everything else so nothing can overlap them. Default: false.</summary>
        public bool? FlagOnTop { get; init; }

        // ── Animation (writes final.gif instead of final.jpg) ────────────────

        /// <summary>Max frames in one GIF loop. Default: 48 (24 when only the shine runs).</summary>
        public int? AnimFrames { get; init; }
        /// <summary>Delay per frame in ms. Default: 70.</summary>
        public int? AnimDelayMs { get; init; }
        /// <summary>
        /// Colours in the GIF palette (2–256). Fewer colours = smaller file = faster upload,
        /// so the card spends less time on its "Loading…" splash. Default: 64.
        /// </summary>
        public int? AnimColors { get; init; }
        /// <summary>Scrolling multi-language exchange-rate band across the top.</summary>
        public TickerCfg? Ticker { get; init; }
        /// <summary>Sweeping highlight over randomly chosen flags.</summary>
        public ShineCfg? Shine { get; init; }
    }

    /// <summary>Top marquee band: orange strip with the rates in several languages.</summary>
    private sealed class TickerCfg
    {
        public bool Enabled { get; init; }
        /// <summary>Band height in 1× px. Default: 14 % of the canvas height.</summary>
        public int? H { get; init; }
        /// <summary>Band fill. Default: the eCash logo orange.</summary>
        public string? BgColor { get; init; }
        public string? TextColor { get; init; }
        /// <summary>Text size in 1× px. Default: 72 % of the band height.</summary>
        public int? FontSize { get; init; }
        /// <summary>Scroll speed in output pixels per frame. Default: 3.</summary>
        public float? Speed { get; init; }
        /// <summary>Fixed text. When set, languages and rates are ignored.</summary>
        public string? Text { get; init; }
        /// <summary>Languages, in order. Supported: en, kz, ru, tr, zh, ar.</summary>
        public List<string>? Langs { get; init; }
        /// <summary>Append the rate list after every caption. Default: false.</summary>
        public bool? Rates { get; init; }
        /// <summary>Currency codes listed in the band when rates = true. Default: first 6 in rates.json.</summary>
        public List<string>? Codes { get; init; }
        public string? Separator { get; init; }
    }

    /// <summary>Glint sweeping across a few flags to catch the eye.</summary>
    private sealed class ShineCfg
    {
        public bool Enabled { get; init; }
        /// <summary>How many flags glow per render, picked at random. Default: 3.</summary>
        public int? Count { get; init; }
        /// <summary>Peak brightness 0..1. Default: 0.75.</summary>
        public float? Strength { get; init; }
        /// <summary>Band width as a fraction of the flag width. Default: 0.35.</summary>
        public float? Width { get; init; }
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
