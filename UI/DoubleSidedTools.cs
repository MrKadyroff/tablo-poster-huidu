using System.Text.Json.Nodes;
using LedImageUpdaterService.Services.DoubleSided;

namespace LedImageUpdaterService.UI;

/// <summary>Вспомогательные данные и действия окна настроек для режима «Аэропорт».</summary>
internal static class DoubleSidedTools
{
    public static readonly (string Value, string Title)[] JoinLayouts =
    [
        (DoubleSidedOptions.Horizontal, "Горизонтальная (512×192, с поворотом макета)"),
        (DoubleSidedOptions.Vertical, "Вертикальная (192×512, без поворота)"),
    ];

    public static readonly (int Value, string Title)[] Rotations =
    [
        (270, "270° по часовой"),
        (90, "90° по часовой"),
    ];

    public static readonly (string Value, string Title)[] SecondSides =
    [
        (DoubleSidedOptions.SecondMirror, "Зеркально"),
        (DoubleSidedOptions.SecondSame, "Та же картинка"),
        (DoubleSidedOptions.SecondRotate180, "Вверх ногами"),
        (DoubleSidedOptions.SecondFlipVertical, "Зеркально по вертикали"),
    ];

    /// <summary>Подпись «На контроллер уйдёт: W×H».</summary>
    public static string JoinedSizeText(AppConfig cfg)
    {
        var (w, h) = cfg.DoubleSidedOptions.GetJoinedSize(cfg.CanvasWidth, cfg.CanvasHeight);
        return $"На контроллер уйдёт: {w}×{h}";
    }

    /// <summary>Размер экрана карты (ScreenWidth × ScreenHeight) для текущих настроек.</summary>
    public static (int W, int H) JoinedSize(AppConfig cfg) =>
        cfg.DoubleSidedOptions.GetJoinedSize(cfg.CanvasWidth, cfg.CanvasHeight);

    /// <summary>Окно предпросмотра склеенной тестовой картинки.</summary>
    public static void ShowTest(IWin32Window owner, AppConfig cfg)
    {
        var png = DoubleSidedBoardComposer.BuildTestPng(
            cfg.CanvasWidth, cfg.CanvasHeight, cfg.DoubleSidedOptions);
        using var ms = new MemoryStream(png);
        using var loaded = Image.FromStream(ms);
        var bmp = new Bitmap(loaded);

        var form = new Form
        {
            Text = $"Тест «Аэропорт» — {bmp.Width}×{bmp.Height}",
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(Math.Clamp(bmp.Width * 2, 360, 1100), Math.Clamp(bmp.Height * 2, 200, 900)),
            BackColor = Color.FromArgb(32, 32, 32),
            ShowIcon = false,
            MinimizeBox = false,
        };
        var pic = new PictureBox
        {
            Dock = DockStyle.Fill,
            Image = bmp,
            SizeMode = PictureBoxSizeMode.Zoom,
        };
        form.Controls.Add(pic);
        form.FormClosed += (_, _) => bmp.Dispose();
        form.ShowDialog(owner);
    }

    /// <summary>
    /// Сохраняет тест в папку вывода точки и отправляет на табло. Настоящий курс вернётся
    /// при следующем обновлении, поэтому перед отправкой спрашиваем подтверждение.
    /// </summary>
    public static async Task SendTestAsync(IWin32Window owner, AppConfig cfg)
    {
        var confirm = MessageBox.Show(owner,
            "Тестовая картинка временно заменит настоящий курс на табло.\n" +
            "Курс вернётся при следующем обновлении.\n\nОтправить тест?",
            "Режим «Аэропорт»", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (confirm != DialogResult.Yes) return;

        try
        {
            var outFile = ResolveOutputFile(cfg.ActivePointId);
            var dir = Path.GetDirectoryName(outFile)!;
            Directory.CreateDirectory(dir);
            // Устаревшая анимация перебила бы тест — убираем.
            var gif = Path.ChangeExtension(outFile, ".gif");
            if (File.Exists(gif)) File.Delete(gif);

            await DoubleSidedBoardComposer.SaveTestAsync(
                outFile, cfg.CanvasWidth, cfg.CanvasHeight, cfg.DoubleSidedOptions, CancellationToken.None);

            var (ok, msg) = await LedControlClient.SendToBoardAsync(cfg.Urls);
            MessageBox.Show(owner, ok ? "Тест отправлен на табло." : $"Не удалось отправить тест:\n{msg}",
                "Режим «Аэропорт»", MessageBoxButtons.OK, ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner, $"Ошибка: {ex.Message}", "Режим «Аэропорт»",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static string ResolveOutputFile(string pointId)
    {
        var root = AppContext.BaseDirectory;
        string rel = $"content/points/{pointId}/output/final.jpg";
        try
        {
            var path = Path.Combine(root, "layout", "points", $"{pointId}.compose.json");
            if (File.Exists(path))
            {
                var node = JsonNode.Parse(File.ReadAllText(path), null,
                    new System.Text.Json.JsonDocumentOptions { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true });
                var v = node?["outputFile"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(v)) rel = v;
            }
        }
        catch { /* fall back to the default */ }
        var full = Path.IsPathRooted(rel) ? rel : Path.Combine(root, rel);
        return Path.ChangeExtension(full, ".jpg");
    }
}
