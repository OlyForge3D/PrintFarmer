using Farm.Web.Api.Data;
using Farm.Web.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Farm.Web.Api.Health;

/// <summary>
/// Comprehensive health check that validates database connectivity,
/// external service availability, and system resources
/// </summary>
public class ComprehensiveHealthCheck(AppDbContext dbContext, IHttpClientFactory httpClientFactory, ILogger<ComprehensiveHealthCheck> logger) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        Dictionary<string, object> checks = new();
        bool overallHealthy = true;
        List<string> issues = new();

        // Database connectivity and initialization
        try
        {
            bool canConnect = await dbContext.Database.CanConnectAsync(cancellationToken);
            if (!canConnect)
            {
                checks["Database"] = new { Status = "Unhealthy", Provider = dbContext.Database.ProviderName, Error = "Cannot connect" };
                overallHealthy = false;
                issues.Add("Database connection failed");
            }
            else
            {
                // Check if database is initialized by verifying manufacturers exist
                int manufacturerCount = await dbContext.Manufacturers.CountAsync(cancellationToken);
                bool isInitialized = manufacturerCount > 0;

                checks["Database"] = new
                {
                    Status = isInitialized ? "Healthy" : "Unhealthy",
                    Provider = dbContext.Database.ProviderName,
                    ManufacturerCount = manufacturerCount,
                    Initialized = isInitialized
                };

                if (!isInitialized)
                {
                    overallHealthy = false;
                    issues.Add("Database not initialized - no manufacturers found");
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Database health check failed");
            checks["Database"] = new { Status = "Unhealthy", Error = ex.Message };
            overallHealthy = false;
            issues.Add($"Database error: {ex.Message}");
        }

        // Memory usage check
        try
        {
            long memoryUsed = GC.GetTotalMemory(false);
            long memoryMB = memoryUsed / (1024 * 1024);
            checks["Memory"] = new { Status = memoryMB < 500 ? "Healthy" : "Warning", UsageMB = memoryMB };

            if (memoryMB > 1000) // Warning threshold
            {
                issues.Add($"High memory usage: {memoryMB}MB");
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Memory health check failed");
            checks["Memory"] = new { Status = "Error", Error = ex.Message };
        }

        // External service connectivity (sample Moonraker check)
        try
        {
            List<Printer> printers = await dbContext.Printers.Take(1).ToListAsync(cancellationToken);
            int externalServiceCount = 0;
            int failedServices = 0;

            foreach (Printer? printer in printers.Take(3)) // Check max 3 printers for performance
            {
                if (printer.Backend == 0) // Moonraker
                {
                    externalServiceCount++;
                    try
                    {
                        using HttpClient client = httpClientFactory.CreateClient();
                        client.Timeout = TimeSpan.FromSeconds(2);
                        HttpResponseMessage response = await client.GetAsync($"{printer.ServerUrl}/server/info", cancellationToken);
                        if (!response.IsSuccessStatusCode)
                        {
                            failedServices++;
                        }
                    }
                    catch
                    {
                        failedServices++;
                    }
                }
            }

            string serviceStatus = failedServices == 0 ? "Healthy" :
                              failedServices < externalServiceCount ? "Degraded" : "Unhealthy";

            checks["ExternalServices"] = new
            {
                Status = serviceStatus,
                CheckedCount = externalServiceCount,
                FailedCount = failedServices
            };

            if (serviceStatus == "Unhealthy")
            {
                overallHealthy = false;
                issues.Add($"All external services unreachable ({failedServices}/{externalServiceCount})");
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "External service health check failed");
            checks["ExternalServices"] = new { Status = "Error", Error = ex.Message };
        }

        // Application metrics
        checks["Application"] = new
        {
            Status = "Healthy",
            Uptime = DateTime.UtcNow.Subtract(System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime()),
            Environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Unknown",
            Version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "Unknown"
        };

        HealthCheckResult result = new(
            overallHealthy ? HealthStatus.Healthy : HealthStatus.Unhealthy,
            description: overallHealthy ? "All systems operational" : string.Join("; ", issues),
            data: checks
        );

        return result;
    }
}
