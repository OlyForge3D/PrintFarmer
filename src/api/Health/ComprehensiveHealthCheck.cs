// Suppress hardcoded URI warning for this file
#pragma warning disable CA1303 // Do not use hardcoded absolute paths or URIs
#pragma warning disable S1075 // Do not use hardcoded absolute paths or URIs (Sonar)
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Farm.Web.Api.Health;

/// <summary>
/// Comprehensive health check that validates server dependencies and system resources
/// while reporting registered printer connectivity as diagnostic data.
/// </summary>
public class ComprehensiveHealthCheck(AppDbContext dbContext, IEnumerable<IPrinterConnectionHealthProvider> connectionHealthProviders, ILogger<ComprehensiveHealthCheck> logger, IHostEnvironment hostEnvironment) : IHealthCheck
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
                else
                {
                    // Catalog and filament readiness are verified directly against the
                    // database rather than via anonymous internal HTTP calls to the
                    // application's own controller routes. CatalogController and
                    // FilamentTypeController are [Authorize]-protected, so an anonymous
                    // loopback GET to them permanently returns 401 and would keep this
                    // health check - and therefore the deployment - unhealthy forever.
                    // Seeded-record counts are equivalent evidence of readiness and don't
                    // require weakening those routes' authentication policy.
                    checks["CatalogApi"] = new { Status = manufacturerCount > 0 ? "Healthy" : "Unhealthy", Count = manufacturerCount, Source = "Database" };
                    if (manufacturerCount == 0)
                    {
                        overallHealthy = false;
                        issues.Add("Catalog readiness check found no manufacturers in database");
                    }

                    try
                    {
                        int filamentTypeCount = await dbContext.FilamentTypes.CountAsync(cancellationToken);
                        checks["FilamentTypesDb"] = new { Status = filamentTypeCount > 0 ? "Healthy" : "Unhealthy", Count = filamentTypeCount };
                        checks["FilamentTypesApi"] = new { Status = filamentTypeCount > 0 ? "Healthy" : "Unhealthy", Count = filamentTypeCount, Source = "Database" };
                        if (filamentTypeCount == 0)
                        {
                            overallHealthy = false;
                            issues.Add("No filament types found in database");
                        }
                    }
                    catch (Exception ex)
                    {
                        checks["FilamentTypesApi"] = new { Status = "Unhealthy", Error = ex.Message };
                        overallHealthy = false;
                        issues.Add($"FilamentType readiness check failed: {ex.Message}");
                    }
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

            // Warning threshold
            if (memoryMB > 1000)
            {
                issues.Add($"High memory usage: {memoryMB}MB");
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Memory health check failed");
            checks["Memory"] = new { Status = "Error", Error = ex.Message };
        }

        // External service connectivity, derived from the live per-backend connection
        // health tracked by each registered IPrinterConnectionHealthProvider (currently
        // Moonraker and SDCP). This reflects real, continuously-updated connection state
        // for every printer on those backends, instead of issuing ad hoc probe requests
        // that were previously gated behind a disabled-by-default setting and hard-filtered
        // to Moonraker only - both of which caused an offline printer to be silently
        // reported as healthy (see issue #1870).
        try
        {
            List<PrinterConnectionHealth> printerHealth = new();
            List<object> providerErrors = new();
            foreach (IPrinterConnectionHealthProvider provider in connectionHealthProviders)
            {
                try
                {
                    printerHealth.AddRange(provider.GetConnectionHealth().Values);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to read connection health from provider {ProviderType}", provider.GetType().Name);
                    providerErrors.Add(new { Provider = provider.GetType().Name, Error = ex.Message });
                }
            }

            int externalServiceCount = printerHealth.Count;
            int failedServices = 0;
            List<object> failedDetails = new();

            foreach (PrinterConnectionHealth printer in printerHealth)
            {
                if (printer.ConnectionState == PrinterConnectionState.Connected)
                {
                    continue;
                }

                failedServices++;
                failedDetails.Add(new
                {
                    Id = printer.PrinterId,
                    Name = printer.PrinterName,
                    printer.Backend,
                    ConnectionState = printer.ConnectionState.ToString(),
                    printer.LastConnectedUtc,
                    printer.LastDisconnectedUtc,
                    ErrorMessage = $"Printer is {printer.ConnectionState}",
                });
            }

            // A provider error must remain visible in the printer-connectivity diagnostics,
            // without changing whether the PrintFarmer server itself is healthy.
            bool hasProviderErrors = providerErrors.Count > 0;
            string serviceStatus = failedServices == 0 && !hasProviderErrors ? "Healthy"
                                  : failedServices > 0 && failedServices >= externalServiceCount ? "Unhealthy"
                                  : "Degraded";

            Dictionary<string, object> externalServicesObj = new()
            {
                ["Status"] = serviceStatus,
                ["CheckedCount"] = externalServiceCount,
                ["FailedCount"] = failedServices
            };

            if (failedDetails.Count > 0)
            {
                externalServicesObj["FailedServicesDetails"] = failedDetails;
            }

            if (hasProviderErrors)
            {
                externalServicesObj["ProviderErrors"] = providerErrors;
            }

            checks["ExternalServices"] = externalServicesObj;
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
            Environment = hostEnvironment.EnvironmentName,
            Version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "Unknown"
        };

        HealthStatus finalStatus = overallHealthy ? HealthStatus.Healthy : HealthStatus.Unhealthy;
        string description = overallHealthy ? "Server systems operational" : string.Join("; ", issues);

        HealthCheckResult result = new(
            finalStatus,
            description: description,
            data: checks);

        return result;
    }
}

// Re-enable warning at end of file
#pragma warning restore CA1303
#pragma warning restore S1075
