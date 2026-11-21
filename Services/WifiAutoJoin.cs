using System.Diagnostics;
using System.Text.RegularExpressions;

namespace LedImageUpdaterService.Services;

/// <summary>
/// Re-joins the PC to the board's Wi-Fi access point when the board became unreachable.
/// Windows only (uses <c>netsh wlan</c>); the Wi-Fi profile for the SSID must already be saved
/// on the PC (i.e. the PC has been connected to it once). Intended for points with permanent
/// (wired) internet, where the Wi-Fi adapter is used only to talk to the board — joining the
/// board AP therefore never cuts the internet. Rate-limited by a cooldown.
/// </summary>
public static class WifiAutoJoin
{
    private static readonly object Sync = new();
    private static DateTime _lastAttemptUtc = DateTime.MinValue;
    private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Tries to (re)connect to <paramref name="ssid"/>. Returns true if a connect command was
    /// issued successfully (the link may still need a few seconds to come up).
    /// </summary>
    public static bool TryJoin(string ssid, Action<string> log)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(ssid)) return false;
        ssid = ssid.Trim();

        lock (Sync)
        {
            if (DateTime.UtcNow - _lastAttemptUtc < Cooldown) return false;
            _lastAttemptUtc = DateTime.UtcNow;
        }

        try
        {
            // Already on the board SSID but the card still doesn't answer → reconnecting
            // won't help, and would only interrupt the link.
            var current = Run("wlan show interfaces", 8000, out _);
            foreach (Match m in Regex.Matches(current ?? "", @"^\s*SSID\s*:\s*(.+?)\s*$", RegexOptions.Multiline))
            {
                if (string.Equals(m.Groups[1].Value, ssid, StringComparison.OrdinalIgnoreCase))
                {
                    log($"[WifiAutoJoin] ПК уже в сети '{ssid}', но табло не отвечает — переподключение Wi-Fi не требуется.");
                    return false;
                }
            }

            log($"[WifiAutoJoin] Табло недоступно, подключаюсь к Wi-Fi '{ssid}'…");
            var output = Run($"wlan connect name=\"{ssid}\" ssid=\"{ssid}\"", 15000, out var exit);
            var ok = exit == 0 && output is not null &&
                     !output.Contains("not found", StringComparison.OrdinalIgnoreCase);
            log(ok
                ? $"[WifiAutoJoin] Команда подключения к '{ssid}' выполнена. {output?.Trim()}"
                : $"[WifiAutoJoin] Не удалось подключиться к '{ssid}' (exit={exit}): {output?.Trim()} " +
                  "Проверьте, что профиль этой сети сохранён на ПК.");
            return ok;
        }
        catch (Exception ex)
        {
            log($"[WifiAutoJoin] Ошибка: {ex.Message}");
            return false;
        }
    }

    private static string? Run(string args, int timeoutMs, out int exitCode)
    {
        exitCode = -1;
        using var p = Process.Start(new ProcessStartInfo("netsh", args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        if (p is null) return null;
        var stdout = p.StandardOutput.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs))
        {
            try { p.Kill(true); } catch { }
            return null;
        }
        exitCode = p.ExitCode;
        return stdout.GetAwaiter().GetResult();
    }
}
