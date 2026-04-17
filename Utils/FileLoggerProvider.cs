using System.Collections.Concurrent;

namespace Dmart;

// Minimal file logger for dmart. Delegates every write to `LogSink` so all
// sinks (generic ILogger events + the per-request access log) share the
// same file handle + lock. The sink writes JSON lines unconditionally when
// LOG_FILE is set, matching Python's `.ljson.log` format.
//
// Usage: builder.Logging.AddProvider(new FileLoggerProvider(logSink));
public sealed class FileLoggerProvider(LogSink sink) : ILoggerProvider
{
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new();

    public ILogger CreateLogger(string categoryName)
        => _loggers.GetOrAdd(categoryName, name => new FileLogger(name, sink));

    public void Dispose() => _loggers.Clear();
}

internal sealed class FileLogger(string category, LogSink sink) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => sink.IsActive && logLevel >= LogLevel.Information;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;
        var message = formatter(state, exception);
        if (exception is not null) message += $" | {exception.GetType().Name}: {exception.Message}";
        sink.WriteLog(category, logLevel, message);
    }
}
