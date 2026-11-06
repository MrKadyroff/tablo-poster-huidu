using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Processing.Processors.Quantization;
using Color = SixLabors.ImageSharp.Color;
using Image = SixLabors.ImageSharp.Image;
using Point = SixLabors.ImageSharp.Point;
using Size = SixLabors.ImageSharp.Size;

namespace LedImageUpdaterService.Services.Splash;

/// <summary>Секция <c>splash</c> compose.json: периодическая заставка с логотипом.</summary>
public sealed class SplashOptions
{
    public bool Enabled { get; set; }
    /// <summary>Период повтора заставки, секунды (длина одного круга GIF).</summary>
    public int PeriodSec { get; set; } = 10;
    /// <summary>Длительность самой заставки (логотип раздувается и открываются курсы), мс.</summary>
    public int DurationMs { get; set; } = 2600;
}

/// <summary>
/// Превращает готовую картинку с курсами в GIF-петлю: логотип раздувается, затем сверху вниз
/// проявляются курсы и остаются на экране до конца периода. Статичная картинка (JPEG) даёт
/// всего один «долгий» кадр на показ курсов, поэтому файл остаётся маленьким.
/// </summary>
public static class SplashAnimator
{
    private const int StepMs = 80;

    public static async Task<string> ApplyAsync(
        string path, Image<Rgba32> logo, Color background, SplashOptions options, CancellationToken ct)
    {
        int periodMs = Math.Clamp(options.PeriodSec, 3, 120) * 1000;
        int splashMs = Math.Clamp(options.DurationMs, 800, periodMs - 500);
        int n = Math.Max(8, splashMs / StepMs);

        using var src = await Image.LoadAsync<Rgba32>(path, ct);
        int w = src.Width, h = src.Height;
        bool animated = src.Frames.Count > 1;

        using var board = src.Frames.CloneFrame(0);
        Image<Rgba32>? result = null;
        var delays = new List<int>();
        try
        {
            for (int i = 0; i < n; i++)
            {
                ct.ThrowIfCancellationRequested();
                using var frame = BuildSplashFrame(w, h, logo, background, board, (i + 0.5f) / n);
                Add(ref result, frame);
                delays.Add(StepMs / 10);
            }

            if (animated)
            {
                for (int i = 0; i < src.Frames.Count; i++)
                {
                    using var f = src.Frames.CloneFrame(i);
                    Add(ref result, f);
                    delays.Add(Math.Max(2, src.Frames[i].Metadata.GetGifMetadata().FrameDelay));
                }
            }
            else
            {
                // Курсы держатся до конца периода одним кадром.
                Add(ref result, board);
                delays.Add(Math.Max(100, (periodMs - n * StepMs) / 10));
            }

            result!.Metadata.GetGifMetadata().RepeatCount = 0;
            for (int i = 0; i < delays.Count; i++)
            {
                var m = result.Frames[i].Metadata.GetGifMetadata();
                m.FrameDelay = delays[i];
                m.DisposalMethod = GifDisposalMethod.RestoreToBackground;
            }

            var outPath = Path.ChangeExtension(path, ".gif");
            await SaveAsync(result, outPath, ct);
            return outPath;
        }
        finally { result?.Dispose(); }
    }

    private static void Add(ref Image<Rgba32>? result, Image<Rgba32> frame)
    {
        if (result is null) result = frame.Clone();
        else result.Frames.AddFrame(frame.Frames.RootFrame);
    }

    /// <summary>t: 0..1 по заставке. 0–0.45 логотип раздувается, до 0.6 держится, дальше уходит и проявляются курсы.</summary>
    private static Image<Rgba32> BuildSplashFrame(
        int w, int h, Image<Rgba32> logo, Color bg, Image<Rgba32> board, float t)
    {
        var frame = new Image<Rgba32>(w, h, bg);

        float scale, alpha;
        float reveal = 0f;
        if (t < 0.45f)
        {
            scale = 0.12f + 0.88f * EaseOutBack(t / 0.45f);
            alpha = 1f;
        }
        else if (t < 0.6f)
        {
            float k = (t - 0.45f) / 0.15f;
            scale = 1f + 0.04f * MathF.Sin(k * MathF.PI);   // лёгкий «пульс»
            alpha = 1f;
        }
        else
        {
            reveal = (t - 0.6f) / 0.4f;
            scale = 1f - 0.15f * reveal;
            alpha = 1f - reveal;
        }

        int box = (int)(Math.Min(w, h) * 0.62f * scale);
        if (box >= 2 && alpha > 0.01f)
        {
            using var l = logo.Clone(c => c.Resize(new ResizeOptions
            {
                Size = new Size(box, box), Mode = ResizeMode.Max, Sampler = KnownResamplers.Bicubic,
            }));
            frame.Mutate(c => c.DrawImage(l, new Point((w - l.Width) / 2, (h - l.Height) / 2), alpha));
        }

        if (reveal > 0f) Wipe(frame, board, reveal);
        return frame;
    }

    /// <summary>Курсы проявляются сверху вниз с мягкой границей.</summary>
    private static void Wipe(Image<Rgba32> under, Image<Rgba32> board, float p)
    {
        float soft = Math.Max(8, under.Height * 0.25f);
        float edge = p * (under.Height + soft);
        under.ProcessPixelRows(board, (ua, ba) =>
        {
            for (int y = 0; y < ua.Height; y++)
            {
                float a = Math.Clamp((edge - y) / soft, 0f, 1f);
                if (a <= 0f) continue;
                var u = ua.GetRowSpan(y);
                var b = ba.GetRowSpan(y);
                for (int x = 0; x < u.Length; x++)
                {
                    if (a >= 1f) { u[x] = b[x]; continue; }
                    u[x] = new Rgba32(
                        (byte)(u[x].R + (b[x].R - u[x].R) * a),
                        (byte)(u[x].G + (b[x].G - u[x].G) * a),
                        (byte)(u[x].B + (b[x].B - u[x].B) * a),
                        255);
                }
            }
        });
    }

    private static float EaseOutBack(float x)
    {
        const float c1 = 1.70158f, c3 = c1 + 1f;
        float p = x - 1f;
        return 1f + c3 * p * p * p + c1 * p * p;
    }

    private static async Task SaveAsync(Image<Rgba32> image, string outPath, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(outPath) ?? Directory.GetCurrentDirectory();
        Directory.CreateDirectory(dir);
        var temp = Path.Combine(dir, $".{Path.GetFileName(outPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await image.SaveAsGifAsync(temp, new GifEncoder
            {
                ColorTableMode = GifColorTableMode.Global,
                Quantizer = new OctreeQuantizer(new QuantizerOptions { MaxColors = 128, Dither = null }),
            }, ct);
            for (int attempt = 1; ; attempt++)
            {
                try { File.Copy(temp, outPath, overwrite: true); return; }
                catch (IOException) when (attempt < 10) { await Task.Delay(150, ct); }
            }
        }
        finally { try { File.Delete(temp); } catch { } }
    }
}
