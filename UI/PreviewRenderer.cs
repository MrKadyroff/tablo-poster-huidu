using LedImageUpdaterService.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace LedImageUpdaterService.UI;

/// <summary>
/// Renders a preview JPEG of the LED board using the same DotnetComposer the
/// background service uses, then loads it as a System.Drawing.Image for display.
/// </summary>
internal static class PreviewRenderer
{
    private static string AppDir => AppContext.BaseDirectory;

    public static async Task<(Image? image, string? error)> RenderAsync(
        string composePath, string ratesPath)
    {
        if (!File.Exists(composePath))
            return (null, $"Не найден файл компоновки:\n{composePath}");
        if (!File.Exists(ratesPath))
            return (null, $"Не найден файл курсов:\n{ratesPath}\n\nНажмите «Загрузить курсы из API» или запустите сервис.");

        // Composer resolves relative paths from the current directory.
        var prevCwd = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(AppDir);
            var composer = new DotnetComposer(NullLogger<DotnetComposer>.Instance);
            var outPath = await composer.ComposeAsync(composePath, ratesPath, CancellationToken.None);

            if (!File.Exists(outPath))
                return (null, "Изображение не было создано.");

            // Load into memory so the file isn't locked. Return an independent
            // Bitmap copy so the backing stream can be safely disposed (otherwise
            // GDI+ throws when the image is later drawn).
            byte[] bytes = await File.ReadAllBytesAsync(outPath);
            using var ms = new MemoryStream(bytes);
            using var loaded = Image.FromStream(ms);
            return (new Bitmap(loaded), null);
        }
        catch (Exception ex)
        {
            return (null, $"Ошибка рендера:\n{ex.Message}");
        }
        finally
        {
            Directory.SetCurrentDirectory(prevCwd);
        }
    }
}
