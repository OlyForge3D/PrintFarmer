using System;
using Microsoft.Extensions.Logging;
using Farm.Infrastructure.Telemetry;

namespace Farm.Web.Api.Services.SlicerServices.Progress;

/// <summary>
/// Adapter to allow IUnifiedLoggingService to be used where ILogger is required (for legacy APIs).
/// Only supports a minimal subset of ILogger methods used by SlicerProgressMonitor.
/// </summary>
public sealed class UnifiedLoggerAdapter : ILogger
{
    private readonly IUnifiedLoggingService _logger;
    public UnifiedLoggerAdapter(IUnifiedLoggingService logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public IDisposable BeginScope<TState>(TState state) => NullScope.Instance;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        string message = formatter(state, exception);
        switch (logLevel)
        {
            case LogLevel.Debug:
                if (exception != null)
                {
                    _logger.LogDebug(exception, message);
                }
                else
                {
                    _logger.LogDebug(message);
                }
                break;
            case LogLevel.Information:
                if (exception != null)
                {
                    _logger.LogInformation($"{message} Exception: {exception.Message}");
                }
                else
                {
                    _logger.LogInformation(message);
                }
                break;
            case LogLevel.Warning:
                if (exception != null)
                {
                    _logger.LogWarning(exception, message);
                }
                else
                {
                    _logger.LogWarning(message);
                }
                break;
            case LogLevel.Error:
                if (exception != null)
                {
                    _logger.LogError(exception, message);
                }
                else
                {
                    _logger.LogError(message);
                }
                break;
            case LogLevel.Critical:
                if (exception != null)
                {
                    _logger.LogCritical(exception, message);
                }
                else
                {
                    _logger.LogCritical(message);
                }
                break;
            default:
                if (exception != null)
                {
                    _logger.LogDebug(exception, message);
                }
                else
                {
                    _logger.LogDebug(message);
                }
                break;
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new NullScope();
        public void Dispose() { }
    }
}
