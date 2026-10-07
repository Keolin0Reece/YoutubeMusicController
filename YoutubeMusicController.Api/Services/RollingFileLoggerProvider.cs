using System.Collections.Concurrent;

namespace YoutubeMusicController.Api.Services;

public sealed class RollingFileLoggerProvider : ILoggerProvider
{
    private const long MaximumFileSize = 5 * 1024 * 1024;
    private const int RetainedFileCount = 5;

    private readonly ConcurrentDictionary<string, RollingFileLogger> _loggers =
        new(StringComparer.Ordinal);
    private readonly string _directory;
    private readonly object _writeGate = new();

    public RollingFileLoggerProvider(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
    }

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(
            categoryName,
            category => new RollingFileLogger(category, Write));

    private void Write(string message)
    {
        lock (_writeGate)
        {
            var path = Path.Combine(_directory, "controller.log");

            if (File.Exists(path) &&
                new FileInfo(path).Length >= MaximumFileSize)
            {
                RollFiles(path);
            }

            File.AppendAllText(path, message + Environment.NewLine);
        }
    }

    private static void RollFiles(string activePath)
    {
        for (var index = RetainedFileCount - 1; index >= 1; index--)
        {
            var source = $"{activePath}.{index}";
            var destination = $"{activePath}.{index + 1}";

            if (File.Exists(source))
            {
                File.Move(source, destination, overwrite: true);
            }
        }

        File.Move(activePath, $"{activePath}.1", overwrite: true);
    }

    public void Dispose() => _loggers.Clear();

    private sealed class RollingFileLogger(
        string category,
        Action<string> write) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) =>
            logLevel >= LogLevel.Information;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var text = formatter(state, exception);
            var exceptionText = exception is null
                ? string.Empty
                : $" | {exception.GetType().Name}: {exception.Message}";
            write(
                $"{DateTimeOffset.Now:O} [{logLevel}] {category}: {text}{exceptionText}");
        }
    }
}
