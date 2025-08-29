using Farm.Web.Server.Data;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace Farm.Web.Server.Health;

/// <summary>
/// Comprehensive health check that validates database connectivity,
/// external service availability, and system resources
/// </summary>
public class ComprehensiveHealthCheck(AppDbContext dbContext, IHttpClientFactory httpClientFactory, ILogger<ComprehensiveHealthCheck> logger) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var checks = new Dictionary<string, object>();
        var overallHealthy = true;
        var issues = new List<string>();

        // Database connectivity
        try
        {
            var canConnect = await dbContext.Database.CanConnectAsync(cancellationToken);
            checks["Database"] = new { Status = canConnect ? "Healthy" : "Unhealthy", Provider = dbContext.Database.ProviderName };
            
            if (!canConnect)
            {
                overallHealthy = false;
                issues.Add("Database connection failed");
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
            var memoryUsed = GC.GetTotalMemory(false);
            var memoryMB = memoryUsed / (1024 * 1024);
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
            var printers = await dbContext.Printers.Take(1).ToListAsync(cancellationToken);
            var externalServiceCount = 0;
            var failedServices = 0;

            foreach (var printer in printers.Take(3)) // Check max 3 printers for performance
            {
                if (printer.Backend == 0) // Moonraker
                {
                    externalServiceCount++;
                    try
                    {
                        using var client = httpClientFactory.CreateClient();
                        client.Timeout = TimeSpan.FromSeconds(2);
                        var response = await client.GetAsync($"{printer.ServerUrl}/server/info", cancellationToken);
                        if (!response.IsSuccessStatusCode)
                            failedServices++;
                    }
                    catch
                    {
                        failedServices++;
                    }
                }
            }

            var serviceStatus = failedServices == 0 ? "Healthy" : 
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

        var result = new HealthCheckResult(
            overallHealthy ? HealthStatus.Healthy : HealthStatus.Unhealthy,
            description: overallHealthy ? "All systems operational" : string.Join("; ", issues),
            data: checks
        );

        return result;
    }
}
