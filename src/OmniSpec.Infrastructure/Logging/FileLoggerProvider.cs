using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;

namespace OmniSpec.Infrastructure.Logging;

/// <summary>
/// Minimal local file logger (one file per day, 7 days retained). Logs never leave the machine.
/// Writes are queued and flushed by a background task to keep logging off hot paths.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _directory;
    private readonly LogLevel _minLevel;
    private readonly BlockingCollection<string> _queue = new(new ConcurrentQueue<string>(), 10_000);
    private readonly Task _writer;

    public FileLoggerProvider(string directory, LogLevel minLevel = LogLevel.Information)
    {
        _directory = directory;
        _minLevel = minLevel;
        Directory.CreateDirectory(directory);
        CleanupOldLogs();
        _writer = Task.Factory.StartNew(WriteLoop, TaskCreationOptions.LongRunning);
    }

    public string CurrentFile => Path.Combine(_directory, $"omnispec-{DateTime.Now:yyyyMMdd}.log");

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    private void Enqueue(string line)
    {
        if (!_queue.IsAddingCompleted) _queue.TryAdd(line);
    }

    private void WriteLoop()
    {
        foreach (var line in _queue.GetConsumingEnumerable())
        {
            try { File.AppendAllText(CurrentFile, line + Environment.NewLine, Encoding.UTF8); }
            catch { /* disk full / locked: drop log line */ }
        }
    }

    private void CleanupOldLogs()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_directory, "omnispec-*.log"))
                if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-7)) File.Delete(f);
        }
        catch { /* best effort */ }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        try { _writer.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _queue.Dispose();
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        private readonly string _category = category.Contains('.') ? category[(category.LastIndexOf('.') + 1)..] : category;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= provider._minLevel && logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{logLevel.ToString()[..4].ToUpperInvariant()}] {_category}: {formatter(state, exception)}";
            if (exception is not null) line += Environment.NewLine + exception;
            provider.Enqueue(line);
        }
    }
}
