using Farm.Web.Api.Health;

namespace Farm.Web.Api.Startup;

/// <summary>
/// Configures health checks for system monitoring.
/// </summary>
public static class HealthCheckStartup
{
    /// <summary>
    /// Adds PrintFarmer health checks.
    /// </summary>
    public static IServiceCollection AddPrintFarmerHealthChecks(this IServiceCollection services)
    {
        // Health checks
        services.AddHealthChecks()
            .AddCheck<ComprehensiveHealthCheck>("comprehensive")
            .AddCheck<SignalRHealthCheck>("signalr")
            .AddCheck<SpoolmanHealthCheck>("spoolman");

        return services;
    }
}
