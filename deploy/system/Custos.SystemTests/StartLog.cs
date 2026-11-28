using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Custos.SystemTests;

/// <summary>
/// Keeps the log of the AppHost and of its resources in memory while the system starts. A failed
/// start writes it to the test output, so that the output shows the cause.
/// </summary>
internal sealed class StartLog : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _lines = new();

    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, _lines);

    public void Dispose() { }

    public Task WriteToAsync(TextWriter writer) =>
        writer.WriteLineAsync(string.Join(Environment.NewLine, _lines));

    private sealed class Logger(string category, ConcurrentQueue<string> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) => lines.Enqueue($"{logLevel} {category}: {formatter(state, exception)} {exception}");
    }
}
