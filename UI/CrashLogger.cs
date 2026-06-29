namespace LedImageUpdaterService.UI;

/// <summary>
/// Process-wide safety net for the tray app. Catches unhandled exceptions from the WinForms
/// UI thread, background <see cref="Task"/>s and the app domain, logs them to
/// <c>logs/crash.log</c> and (for UI-thread faults) keeps the application alive instead of
/// letting Windows kill it with the default crash dialog.
/// </summary>
internal static class CrashLogger
{
    private static readonly string LogPath =
        Path.Combine(AppContext.BaseDirectory, "logs", "crash.log");

    private static readonly object Gate = new();

    /// <summary>Wires up all unhandled-exception hooks. Call once, before Application.Run.</summary>
    public static void Install()
    {
        // UI-thread exceptions: caught so the message loop survives (app does NOT exit).
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Handle(e.Exception, "UI", showDialog: true);

        // Non-UI-thread exceptions: the runtime will still tear down, but we log first.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Handle(e.ExceptionObject as Exception, "AppDomain", showDialog: true);

        // Faulted Tasks whose exception was never observed: log + mark observed so the
        // finalizer does not crash the process.
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Handle(e.Exception, "Task", showDialog: false);
            e.SetObserved();
        };
    }

    /// <summary>Logs an exception and optionally shows a non-fatal notice to the operator.</summary>
    public static void Handle(Exception? ex, string source, bool showDialog)
    {
        if (ex is null) return;

        Log(source, ex);

        if (!showDialog) return;

        // MessageBox can be shown from any thread; keep it short so it never blocks shutdown.
        try
        {
            MessageBox.Show(
                "Произошла внутренняя ошибка, но программа продолжает работать.\n\n" +
                $"{ex.GetType().Name}: {ex.Message}\n\n" +
                $"Подробности записаны в:\n{LogPath}",
                "eCash Tablo — ошибка",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch { /* never let the error handler throw */ }
    }

    private static void Log(string source, Exception ex)
    {
        try
        {
            lock (Gate)
            {
                var dir = Path.GetDirectoryName(LogPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.AppendAllText(LogPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{source}] {ex}{Environment.NewLine}{Environment.NewLine}");
            }
        }
        catch { /* logging must never throw */ }
    }
}
