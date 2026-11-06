using Farm.Web.Api.Health;
using Microsoft.Extensions.Diagnostics.HealthChecks;

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
        services.AddSingleton<ObicoHealthCheck>();
        services.AddSingleton<PrintGuardHealthCheck>();
        services.AddHealthChecks()
            .AddCheck<ComprehensiveHealthCheck>("comprehensive")
            .AddCheck<SignalRHealthCheck>("signalr")
            .AddCheck<SpoolmanHealthCheck>("spoolman")
            .AddCheck<ObicoHealthCheck>(ObicoHealthCheck.Name)
            .AddCheck<PrintGuardHealthCheck>(PrintGuardHealthCheck.Name, failureStatus: HealthStatus.Degraded)
            .AddCheck<QueueConsumersHealthCheck>(QueueConsumersHealthCheck.Name);

        return services;
    }
}
