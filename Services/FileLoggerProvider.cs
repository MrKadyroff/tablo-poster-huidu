using System.Collections.Concurrent;
using System.Text;

namespace LedImageUpdaterService.Services;

/// <summary>
/// Minimal, dependency-free <see cref="ILoggerProvider"/> that writes the entire
/// <see cref="ILogger"/> firehose to a day-rolling file under <c>&lt;app&gt;/logs</c>.
///
/// The framework already filters by the configured log levels, so this provider just
/// persists whatever reaches it. Unlike <c>AddConsole</c> (invisible when running as a
/// Windows Service or a tray app), this survives restarts and is the primary on-disk
/// log. The path is anchored to <see cref="AppContext.BaseDirectory"/> so it is written
/// next to the exe regardless of the process working directory.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _logDir;
    private readonly LogLevel _minLevel;
    private readonly object _writeLock = new();
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new();

    private string? _currentDate;
    private StreamWriter? _writer;

    public FileLoggerProvider(string? logDirectory = null, LogLevel minLevel = LogLevel.Information)
    {
        _logDir = logDirectory ?? Path.Combine(AppContext.BaseDirectory, "logs");
        _minLevel = minLevel;
        try { Directory.CreateDirectory(_logDir); } catch { /* best effort */ }
    }

    public ILogger CreateLogger(string categoryName)
        => _loggers.GetOrAdd(categoryName, name => new FileLogger(this, name));

    internal bool IsEnabled(LogLevel level) => level >= _minLevel && level != LogLevel.None;

    internal void Write(string categoryName, LogLevel level, string message, Exception? exception)
    {
        // Trim the namespace from framework categories for readability (keep the leaf).
        var shortCat = categoryName.Contains('.') ? categoryName[(categoryName.LastIndexOf('.') + 1)..] : categoryName;

        var sb = new StringBuilder();
        sb.Append('[').Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append("] ");
        sb.Append('[').Append(LevelTag(level)).Append("] ");
        sb.Append('[').Append(shortCat).Append("] ");
        sb.Append(message);
        if (exception is not null)
            sb.Append(Environment.NewLine).Append(exception);

        lock (_writeLock)
        {
            try
            {
                RollIfNeeded();
                _writer?.WriteLine(sb.ToString());
            }
            catch { /* a logging failure must never crash the app */ }
        }
    }

    private void RollIfNeeded()
    {
        var today = DateTime.Now.ToString("yyyyMMdd");
        if (_currentDate == today && _writer is not null) return;

        try { _writer?.Flush(); _writer?.Dispose(); } catch { }
        var path = Path.Combine(_logDir, $"ecash-{today}.log");
        _writer = new StreamWriter(path, append: true, new UTF8Encoding(false)) { AutoFlush = true };
        _currentDate = today;
    }

    private static string LevelTag(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRACE",
        LogLevel.Debug => "DEBUG",
        LogLevel.Information => "INFO ",
        LogLevel.Warning => "WARN ",
        LogLevel.Error => "ERROR",
        LogLevel.Critical => "CRIT ",
        _ => level.ToString(),
    };

    public void Dispose()
    {
        lock (_writeLock)
        {
            try { _writer?.Flush(); _writer?.Dispose(); } catch { }
            _writer = null;
        }
    }

    private sealed class FileLogger : ILogger
    {
        private readonly FileLoggerProvider _provider;
        private readonly string _category;

        public FileLogger(FileLoggerProvider provider, string category)
        {
            _provider = provider;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => _provider.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var message = formatter(state, exception);
            if (string.IsNullOrEmpty(message) && exception is null) return;
            _provider.Write(_category, logLevel, message, exception);
        }
    }
}

/// <summary>Registration helper: <c>builder.Logging.AddFile()</c>.</summary>
public static class FileLoggerExtensions
{
    public static ILoggingBuilder AddFile(this ILoggingBuilder builder, string? logDirectory = null,
        LogLevel minLevel = LogLevel.Information)
    {
        builder.AddProvider(new FileLoggerProvider(logDirectory, minLevel));
        return builder;
    }
}
