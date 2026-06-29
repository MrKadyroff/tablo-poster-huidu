using System.Collections.Concurrent;

namespace LedImageUpdaterService.Services;

/// <summary>
/// Thread-safe in-memory circular log buffer used by the /api/led/logs endpoint.
/// Also writes logs to a persistent file: logs/led-all.log
/// </summary>
public sealed class InMemoryLogStore
{
    private const int MaxEntries = 500;

    // Anchored to the app directory (not the process CWD, which is System32 when running
    // as a Windows Service) so the file is always written next to the exe.
    private static readonly string LogFilePath =
        Path.Combine(AppContext.BaseDirectory, "logs", "led-all.log");

    private readonly ConcurrentQueue<LogEntry> _entries = new();
    private readonly object _fileLock = new();

    public InMemoryLogStore()
    {
        // Ensure logs directory exists
        var logsDir = Path.GetDirectoryName(LogFilePath);
        if (!string.IsNullOrEmpty(logsDir) && !Directory.Exists(logsDir))
        {
            Directory.CreateDirectory(logsDir);
        }
    }

    /// <summary>Adds a log record to the store and writes to file. Oldest records are evicted when the buffer is full.</summary>
    public void Add(LogLevel level, string category, string message)
    {
        var entry = new LogEntry(DateTimeOffset.UtcNow, level, category, message);
        _entries.Enqueue(entry);

        // Write to file asynchronously to avoid blocking
        _ = Task.Run(() => WriteToFileAsync(entry));

        // Trim excess entries (allow small overshoot for performance)
        while (_entries.Count > MaxEntries)
            _entries.TryDequeue(out _);
    }

    private Task WriteToFileAsync(LogEntry entry)
    {
        try
        {
            lock (_fileLock)
            {
                var logLine = $"[{entry.Timestamp:yyyy-MM-dd HH:mm:ss.fff}] [{entry.Level}] [{entry.Category}] {entry.Message}";
                File.AppendAllText(LogFilePath, logLine + Environment.NewLine);
            }
        }
        catch
        {
            // Silently ignore file write errors to avoid infinite log loops
        }
        return Task.CompletedTask;
    }

    /// <summary>Returns up to <paramref name="count"/> most recent entries.</summary>
    public IReadOnlyList<LogEntry> GetRecent(int count = 100)
    {
        var clamped = Math.Clamp(count, 1, MaxEntries);
        return _entries.TakeLast(clamped).ToList();
    }

    /// <summary>Clears all stored entries.</summary>
    public void Clear() => _entries.Clear();

    public record LogEntry(
        DateTimeOffset Timestamp,
        LogLevel Level,
        string Category,
        string Message);
}
