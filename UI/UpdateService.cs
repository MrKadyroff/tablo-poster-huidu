using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace LedImageUpdaterService.UI;

/// <summary>
/// Self-update from GitHub Releases. Checks the latest release of the project repo,
/// compares its tag (e.g. <c>v1.0.1</c>) to the running assembly version, downloads the
/// release .zip asset and applies it via a small detached <c>update.bat</c> that swaps the
/// program (exe + native DLLs + shared <c>content/common</c>) while preserving the
/// operator's data and layout (<c>appsettings.json</c>, <c>config/</c>, <c>layout/</c>,
/// <c>content/points/</c>, logs).
///
/// The repository is public, so no token is needed. All network calls fail soft (return
/// null) so a missing/blocked internet connection never disrupts the app.
/// </summary>
internal static class UpdateService
{
    private const string Owner = "MrKadyroff";
    // Huidu build's release repo: https://github.com/MrKadyroff/tablo-poster-huidu
    private const string Repo = "tablo-poster-huidu";

    private static string TempRoot => Path.Combine(Path.GetTempPath(), "ecash-update");
    private static string BatPath => Path.Combine(Path.GetTempPath(), "ecash-update-apply.bat");

    public static Version CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);

    public sealed record UpdateInfo(Version Version, string Tag, string Notes, string AssetUrl, string AssetName);

    /// <summary>
    /// Queries GitHub for the latest release. Returns details only when the released
    /// version is newer than the running one; otherwise (or on any error) returns null.
    /// </summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("eCashTablo-Updater");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

            var url = $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";
            using var resp = await http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return null;

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            if (string.IsNullOrWhiteSpace(tag)) return null;
            if (!TryParseVersion(tag, out var version)) return null;
            if (version <= CurrentVersion) return null; // already up to date

            var notes = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";

            // Pick the first .zip asset.
            string? assetUrl = null, assetName = null;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in assets.EnumerateArray())
                {
                    var name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    if (name is null || !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                    assetUrl = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    assetName = name;
                    break;
                }
            }

            if (string.IsNullOrWhiteSpace(assetUrl)) return null; // release has no installable .zip

            return new UpdateInfo(version, tag!, notes, assetUrl!, assetName ?? "update.zip");
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Downloads the release zip into the temp folder, reporting 0–100% progress.</summary>
    public static async Task<string> DownloadAsync(UpdateInfo info, IProgress<int>? progress, CancellationToken ct = default)
    {
        Directory.CreateDirectory(TempRoot);
        var zipPath = Path.Combine(TempRoot, info.AssetName);

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("eCashTablo-Updater");

        using var resp = await http.GetAsync(info.AssetUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        var total = resp.Content.Headers.ContentLength ?? -1L;
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Create(zipPath);

        var buffer = new byte[81920];
        long read = 0;
        int n;
        while ((n = await src.ReadAsync(buffer, ct)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, n), ct);
            read += n;
            if (total > 0) progress?.Report((int)(read * 100 / total));
        }
        progress?.Report(100);
        return zipPath;
    }

    /// <summary>
    /// Extracts the downloaded zip, writes and launches the detached updater script, then
    /// calls <paramref name="exitApp"/> so the program (and its child SDK processes) release
    /// the exe/DLL locks. The updater waits for this process to exit, swaps the files and
    /// restarts the app.
    /// </summary>
    public static void ApplyAndExit(string zipPath, Action exitApp)
    {
        var extracted = Path.Combine(TempRoot, "extracted");
        if (Directory.Exists(extracted)) Directory.Delete(extracted, recursive: true);
        Directory.CreateDirectory(extracted);
        ZipFile.ExtractToDirectory(zipPath, extracted, overwriteFiles: true);

        var installDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var exePath = Environment.ProcessPath ?? Path.Combine(installDir, "eCashTabloHuidu.exe");
        var exeName = Path.GetFileName(exePath);

        // The zip may contain files at the root or inside a single folder — locate the exe.
        var sourceRoot = FindSourceRoot(extracted, exeName) ?? extracted;

        File.WriteAllText(BatPath, BuildUpdaterScript(
            pid: Environment.ProcessId,
            sourceRoot: sourceRoot,
            installDir: installDir,
            exeName: exeName));

        Process.Start(new ProcessStartInfo
        {
            FileName = BatPath,
            UseShellExecute = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        });

        exitApp();
    }

    private static string? FindSourceRoot(string extracted, string exeName)
    {
        if (File.Exists(Path.Combine(extracted, exeName))) return extracted;
        var hit = Directory.EnumerateFiles(extracted, exeName, SearchOption.AllDirectories).FirstOrDefault();
        return hit is null ? null : Path.GetDirectoryName(hit);
    }

    private static bool TryParseVersion(string tag, out Version version)
    {
        var s = tag.Trim().TrimStart('v', 'V');
        return Version.TryParse(s, out version!);
    }

    // Updater batch: wait for the app PID to exit, back up the exe, copy the new files
    // (excluding the operator's data/markup), then restart and clean up.
    private static string BuildUpdaterScript(int pid, string sourceRoot, string installDir, string exeName)
    {
        return $"""
            @echo off
            chcp 65001 >nul
            set "PID={pid}"
            set "SRC={sourceRoot}"
            set "DST={installDir}"
            set "EXE={exeName}"

            :waitloop
            tasklist /FI "PID eq %PID%" 2>nul | find "%PID%" >nul
            if not errorlevel 1 (
              timeout /t 1 /nobreak >nul
              goto waitloop
            )

            if exist "%DST%\%EXE%" copy /Y "%DST%\%EXE%" "%DST%\%EXE%.bak" >nul

            robocopy "%SRC%" "%DST%" /E /XF appsettings.json /XD config layout logs relay-output points /R:3 /W:2 >nul

            start "" "%DST%\%EXE%"

            rmdir /S /Q "{TempRoot}" >nul 2>&1
            del "%~f0" >nul 2>&1
            """;
    }
}
