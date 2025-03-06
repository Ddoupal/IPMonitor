using Microsoft.Extensions.Logging;

namespace IpMonitor;

public class FileLoggerProvider(string filePath) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new FileLogger(filePath);

    public void Dispose() { }
}

public class FileLogger(string filePath) : ILogger
{
    private static readonly object _lock = new();

    IDisposable ILogger.BeginScope<TState>(TState state) => null!;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception, string> formatter)
    {
        var message = formatter(state, exception!);
        var logEntry = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{logLevel}] {message}{Environment.NewLine}";

        lock (_lock)
        {
            File.AppendAllText(filePath, logEntry);
        }

    }
}