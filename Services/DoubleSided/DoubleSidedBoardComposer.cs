using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Processing.Processors.Quantization;
using Color = SixLabors.ImageSharp.Color;
using Image = SixLabors.ImageSharp.Image;
using Point = SixLabors.ImageSharp.Point;
using PointF = SixLabors.ImageSharp.PointF;
using Size = SixLabors.ImageSharp.Size;
using Pens = SixLabors.ImageSharp.Drawing.Processing.Pens;
using Brushes = SixLabors.ImageSharp.Drawing.Processing.Brushes;
using Font = SixLabors.Fonts.Font;
using FontStyle = SixLabors.Fonts.FontStyle;
using SystemFonts = SixLabors.Fonts.SystemFonts;
using RectangleF = SixLabors.ImageSharp.RectangleF;
using FontFamily = SixLabors.Fonts.FontFamily;
using HorizontalAlignment = SixLabors.Fonts.HorizontalAlignment;
using VerticalAlignment = SixLabors.Fonts.VerticalAlignment;

namespace LedImageUpdaterService.Services.DoubleSided;

/// <summary>
/// Turns a one-side picture (JPEG or GIF) into the picture for a double-sided board:
/// transforms side B, rotates the sides if needed and glues them into one image.
/// GIFs are processed frame by frame; frame delays and loop count are kept.
/// </summary>
public static class DoubleSidedBoardComposer
{
    /// <summary>Glues one still frame. The result has exactly <see cref="DoubleSidedOptions.GetJoinedSize"/> size.</summary>
    public static Image<Rgba32> Join(Image<Rgba32> side, DoubleSidedOptions options)
    {
        var o = options.Normalized();
        using var a = PrepareSide(side, o, secondSide: false);
        using var b = PrepareSide(side, o, secondSide: true);

        var result = o.IsHorizontal
            ? new Image<Rgba32>(a.Width + b.Width, Math.Max(a.Height, b.Height), Color.Black)
            : new Image<Rgba32>(Math.Max(a.Width, b.Width), a.Height + b.Height, Color.Black);

        var posB = o.IsHorizontal ? new Point(a.Width, 0) : new Point(0, a.Height);
        result.Mutate(c => c.DrawImage(a, new Point(0, 0), 1f).DrawImage(b, posB, 1f));
        return result;
    }

    private static Image<Rgba32> PrepareSide(Image<Rgba32> src, DoubleSidedOptions o, bool secondSide)
    {
        var img = src.Clone();
        img.Mutate(c =>
        {
            if (secondSide)
            {
                switch (o.SecondSide)
                {
                    case DoubleSidedOptions.SecondMirror: c.Flip(FlipMode.Horizontal); break;
                    case DoubleSidedOptions.SecondRotate180: c.Rotate(RotateMode.Rotate180); break;
                    case DoubleSidedOptions.SecondFlipVertical: c.Flip(FlipMode.Vertical); break;
                }
            }
            if (o.IsHorizontal)
                c.Rotate(o.SideRotation == 90 ? RotateMode.Rotate90 : RotateMode.Rotate270);
        });
        return img;
    }

    /// <summary>
    /// Reads <paramref name="path"/> (JPEG or GIF), glues it per options and writes the result back
    /// to the same path. Files that already have the joined size are left untouched, so a repeated
    /// call never glues twice.
    /// </summary>
    public static async Task<string> ComposeFileAsync(
        string path, int sideW, int sideH, DoubleSidedOptions options, CancellationToken ct)
    {
        var o = options.Normalized();
        var (joinW, joinH) = o.GetJoinedSize(sideW, sideH);

        using var src = await Image.LoadAsync<Rgba32>(path, ct);
        if (src.Width == joinW && src.Height == joinH && (src.Width != sideW || src.Height != sideH))
            return path;
        if (src.Width != sideW || src.Height != sideH)
            throw new InvalidOperationException(
                $"Режим «Аэропорт»: размер макета {src.Width}×{src.Height}, ожидался {sideW}×{sideH}.");

        bool animated = src.Frames.Count > 1;
        string ext = animated ? ".gif" : ".jpg";
        string outPath = Path.ChangeExtension(path, ext);

        Image<Rgba32>? result = null;
        try
        {
            for (int i = 0; i < src.Frames.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                using var frameImg = src.Frames.CloneFrame(i);
                using var joined = Join(frameImg, o);
                if (result is null)
                {
                    result = joined.Clone();
                }
                else
                {
                    result.Frames.AddFrame(joined.Frames.RootFrame);
                }
                if (animated)
                {
                    var srcMeta = src.Frames[i].Metadata.GetGifMetadata();
                    var dstMeta = result.Frames[i].Metadata.GetGifMetadata();
                    dstMeta.FrameDelay = srcMeta.FrameDelay;
                    dstMeta.DisposalMethod = GifDisposalMethod.RestoreToBackground;
                }
            }

            if (result is null) throw new InvalidOperationException("Пустое изображение.");
            if (animated)
                result.Metadata.GetGifMetadata().RepeatCount = src.Metadata.GetGifMetadata().RepeatCount;

            await SaveAtomicAsync(result, outPath, animated, ct);
        }
        finally { result?.Dispose(); }

        return outPath;
    }

    private static async Task SaveAtomicAsync(Image<Rgba32> image, string outPath, bool animated, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(outPath) ?? Directory.GetCurrentDirectory();
        Directory.CreateDirectory(dir);
        var temp = Path.Combine(dir, $".{Path.GetFileName(outPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            if (animated)
                await image.SaveAsGifAsync(temp, new GifEncoder
                {
                    ColorTableMode = GifColorTableMode.Global,
                    Quantizer = new OctreeQuantizer(new QuantizerOptions { MaxColors = 64, Dither = null }),
                }, ct);
            else
                await image.SaveAsJpegAsync(temp, new JpegEncoder { Quality = 95 }, ct);

            for (int attempt = 1; ; attempt++)
            {
                try { File.Copy(temp, outPath, overwrite: true); return; }
                catch (IOException) when (attempt < 10) { await Task.Delay(150, ct); }
            }
        }
        finally { try { File.Delete(temp); } catch { } }
    }

    /// <summary>
    /// Test side: border at the very edge (shows any cropping), a big «R» and a green arrow
    /// pointing right below it. The «R» shows at a glance what to change: rotation, mirror or flip.
    /// </summary>
    public static Image<Rgba32> BuildTestSide(int w, int h)
    {
        var img = new Image<Rgba32>(w, h, Color.Black);
        int fs = Math.Max(12, (int)(Math.Min(w, h) * 0.55f));
        var family = ResolveFamily();
        var font = family.CreateFont(fs, FontStyle.Bold);

        var opts = new RichTextOptions(font)
        {
            Origin = new PointF(w / 2f, h * 0.42f),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        float arrowY = h * 0.78f;
        float arrowHalf = w * 0.30f;
        float head = Math.Max(6, w * 0.08f);
        float thick = Math.Max(3, w * 0.025f);
        var green = Color.FromRgb(50, 220, 70);

        img.Mutate(c =>
        {
            c.Draw(Pens.Solid(Color.White, 2), new RectangleF(1, 1, w - 2, h - 2));
            c.DrawText(opts, "R", Brushes.Solid(Color.White));
            c.DrawLine(Pens.Solid(green, thick),
                new PointF(w / 2f - arrowHalf, arrowY), new PointF(w / 2f + arrowHalf, arrowY));
            var tip = new PointF(w / 2f + arrowHalf + thick, arrowY);
            c.FillPolygon(Brushes.Solid(green), tip,
                new PointF(tip.X - head * 1.4f, arrowY - head), new PointF(tip.X - head * 1.4f, arrowY + head));
        });
        return img;
    }

    private static FontFamily ResolveFamily()
    {
        foreach (var name in new[] { "Arial", "Segoe UI", "DejaVu Sans", "Helvetica" })
            if (SystemFonts.TryGet(name, out var fam)) return fam;
        return SystemFonts.Collection.Families.First();
    }

    /// <summary>Saves the glued test picture as JPEG, as the board service would produce it.</summary>
    public static async Task SaveTestAsync(
        string outPath, int sideW, int sideH, DoubleSidedOptions options, CancellationToken ct)
    {
        using var side = BuildTestSide(sideW, sideH);
        using var joined = Join(side, options);
        await SaveAtomicAsync(joined, outPath, animated: false, ct);
    }

    /// <summary>Glued test picture encoded as PNG (for the preview window).</summary>
    public static byte[] BuildTestPng(int sideW, int sideH, DoubleSidedOptions options)
    {
        using var side = BuildTestSide(sideW, sideH);
        using var joined = Join(side, options);
        using var ms = new MemoryStream();
        joined.SaveAsPng(ms);
        return ms.ToArray();
    }
}
