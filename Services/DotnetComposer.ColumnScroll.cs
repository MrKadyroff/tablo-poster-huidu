using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Point = SixLabors.ImageSharp.Point;
using Rectangle = SixLabors.ImageSharp.Rectangle;

namespace LedImageUpdaterService.Services;

// Per-column scrolling: a column marked "scroll" (columns[i].scroll) keeps its header and slides all of
// its currencies through the rows window when they do not fit.
public sealed partial class DotnetComposer
{
    /// <summary>One scrolling column: all rows stacked in <see cref="Strip"/>, shown through <see cref="Window"/>.</summary>
    private sealed record ColScroll(Image<Rgba32> Strip, Rectangle Window, int CycleRows, int RowH);

    private readonly List<ColScroll> _colScrolls = new();

    /// <summary>
    /// Writes the looping GIF. One loop = one pass over the longest list; every column is stepped so that it
    /// also finishes exactly one pass of its own list at the end of the loop (no jump when the GIF restarts).
    /// </summary>
    private async Task<string> FinalizeColumnScrollAsync(
        Image<Rgba32> board, GridLayout gl, string outPath, CancellationToken ct)
    {
        float speed = Math.Clamp(gl.Scroll?.Speed ?? 3f, 0.5f, 8f);
        int frameMs = Math.Clamp(gl.Scroll?.FrameMs ?? 70, 20, 300);
        int maxCycleH = _colScrolls.Max(c => c.CycleRows * c.RowH);
        int frames = Math.Clamp((int)Math.Round(maxCycleH / speed), 8, 240);

        if (gl.Ticker is { Enabled: true } || gl.Shine is { Enabled: true })
            _logger.LogWarning("Прокрутка колонки включена: бегущая строка и сияние флагов в этом режиме не рисуются.");

        Image<Rgba32>? anim = null;
        try
        {
            for (int f = 0; f < frames; f++)
            {
                ct.ThrowIfCancellationRequested();
                using var frame = board.Clone();
                foreach (var cs in _colScrolls)
                {
                    int cycleH = cs.CycleRows * cs.RowH;
                    int offset = (int)Math.Round((double)f * cycleH / frames) % Math.Max(1, cycleH);
                    using var crop = cs.Strip.Clone(c => c.Crop(new Rectangle(0, offset, cs.Window.Width, cs.Window.Height)));
                    frame.Mutate(c => c.DrawImage(crop, new Point(cs.Window.X, cs.Window.Y), 1f));
                }
                DrawTopOverlays(frame);
                if (anim is null) anim = frame.Clone();
                else anim.Frames.AddFrame(frame.Frames.RootFrame);
            }

            anim!.Metadata.GetGifMetadata().RepeatCount = 0;
            foreach (var fr in anim.Frames)
            {
                var m = fr.Metadata.GetGifMetadata();
                m.FrameDelay = Math.Max(2, frameMs / 10);
                m.DisposalMethod = GifDisposalMethod.NotDispose;
            }

            var gifPath = Path.ChangeExtension(outPath, ".gif");
            await SaveWithRetryAsync(anim, gifPath, animated: true, ct, gl.AnimColors ?? 64);
            if (!string.Equals(gifPath, outPath, StringComparison.OrdinalIgnoreCase)) TryDelete(outPath);

            long kb = new FileInfo(gifPath).Length / 1024;
            _logger.LogInformation("Прокрутка колонок: {Cols} шт., {Frames} кадров × {Delay} мс, {Size} КБ → {Out}",
                _colScrolls.Count, frames, frameMs, kb, gifPath);
            if (kb > 800)
                _logger.LogWarning("GIF с прокруткой весит {Size} КБ — заливка на карту будет долгой. Поднимите скорость прокрутки.", kb);
            return gifPath;
        }
        finally { anim?.Dispose(); }
    }
}
