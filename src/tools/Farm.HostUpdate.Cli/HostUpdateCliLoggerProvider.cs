using Microsoft.Extensions.Logging;

namespace Farm.HostUpdate.Cli;

/// <summary>Writes engine diagnostics to stderr so stdout stays machine-readable.</summary>
internal sealed class HostUpdateCliLoggerProvider(TextWriter error) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new Logger(error, categoryName);

    public void Dispose()
    {
    }

    private sealed class Logger(TextWriter error, string categoryName) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            error.WriteLine($"{logLevel}: {categoryName}: {formatter(state, exception)}");
        }
    }
}
