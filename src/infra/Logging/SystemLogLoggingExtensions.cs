using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Logging;

/// <summary>
/// Extension methods for registering the SystemLog logger provider.
/// </summary>
public static class SystemLogLoggingExtensions
{
    /// <summary>
    /// Adds the SystemLog logger provider to the logging builder.
    /// This logger writes all application logs to the SystemLog database table.
    /// Automatically captures X-Correlation-Id header from HTTP context for distributed tracing.
    /// </summary>
    /// <param name="builder">The logging builder.</param>
    /// <param name="minimumLevel">Minimum log level to capture (default: Information).</param>
    /// <returns>The logging builder for chaining.</returns>
    public static ILoggingBuilder AddSystemLogProvider(
        this ILoggingBuilder builder,
        LogLevel minimumLevel = LogLevel.Information)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Ensure IHttpContextAccessor is registered for correlation ID extraction
        builder.Services.AddHttpContextAccessor();

        builder.Services.AddSingleton<ILoggerProvider>(sp =>
            new SystemLogLoggerProvider(sp, minimumLevel));

        return builder;
    }
}
