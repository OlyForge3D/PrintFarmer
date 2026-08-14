// Suppress hardcoded URI warning for this file
#pragma warning disable CA1303 // Do not use hardcoded absolute paths or URIs
#pragma warning disable S1075 // Do not use hardcoded absolute paths or URIs (Sonar)
using System.Diagnostics;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Farm.Web.Api.Health;

/// <summary>
/// Comprehensive health check that validates database connectivity,
/// external service availability, and system resources
/// </summary>
public class ComprehensiveHealthCheck(AppDbContext dbContext, IHttpClientFactory httpClientFactory, ILogger<ComprehensiveHealthCheck> logger, Farm.Infrastructure.Settings.ISettingsService settingsService, IHostEnvironment hostEnvironment) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        Dictionary<string, object> checks = new();
        bool overallHealthy = true;
        bool degraded = false; // Track non-critical degradations (eg. external printers offline)
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
                    checks["CatalogApi"] = new { Status = "Healthy", Count = manufacturerCount, Source = "Database" };

                    // Verify filament presets directly in the database. The corresponding API
                    // endpoint requires user authentication and is not a valid readiness probe.
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
                        checks["FilamentTypesDb"] = new { Status = "Unhealthy", Error = ex.Message };
                        checks["FilamentTypesApi"] = new { Status = "Unhealthy", Error = ex.Message };
                        overallHealthy = false;
                        issues.Add($"Filament type database check failed: {ex.Message}");
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

        // External service connectivity (sample Moonraker check)
        try
        {
            // Select printers to probe for external service health based on settings
            List<ExternalServicePrinter> printers;
            int printersToCheck = 0; // default fallback: don't check external printers by default
            try
            {
                Farm.Infrastructure.Settings.ExternalServicesHealthSettings s = settingsService.Get<Farm.Infrastructure.Settings.ExternalServicesHealthSettings>();
                if (s != null)
                {
                    printersToCheck = s.PrintersToCheck;
                }
            }
            catch
            {
            }

            if (printersToCheck == 0)
            {
                printers = [];
            }
            else
            {
                IQueryable<ExternalServicePrinter> printerQuery = dbContext.Printers
                    .AsNoTracking()
                    .Select(p => new ExternalServicePrinter(p.Id, p.Name, p.ServerUrl, p.Backend));

                printers = printersToCheck < 0
                    ? await printerQuery.ToListAsync(cancellationToken)
                    : await printerQuery.Take(printersToCheck).ToListAsync(cancellationToken);
            }

            int externalServiceCount = 0;
            int failedServices = 0;
            List<object> failedDetails = new();

            foreach (ExternalServicePrinter printer in printers.Where(printer =>
                         printer.Backend == (int)PrinterBackend.Moonraker))
            {
                // Check Moonraker printers
                externalServiceCount++;
                try
                {
                    using HttpClient client = httpClientFactory.CreateClient();
                    client.Timeout = TimeSpan.FromSeconds(2);
                    string attemptedUrl = $"{printer.ServerUrl.TrimEnd('/')}/server/info";
                    Stopwatch sw = Stopwatch.StartNew();
                    HttpResponseMessage response = await client.GetAsync(attemptedUrl, cancellationToken);
                    sw.Stop();
                    if (!response.IsSuccessStatusCode)
                    {
                        failedServices++;
                        string snippet = string.Empty;
                        try
                        {
                            string body = await response.Content.ReadAsStringAsync(cancellationToken) ?? string.Empty;
                            snippet = body.Length > 200 ? body[..200] : body;
                        }
                        catch
                        {
                        }

                        failedDetails.Add(new
                        {
                            printer.Id,
                            printer.Name,
                            printer.ServerUrl,
                            printer.Backend,
                            AttemptedUrl = attemptedUrl,
                            CheckedAtUtc = DateTime.UtcNow,
                            ElapsedMs = sw.ElapsedMilliseconds,
                            StatusCode = (int)response.StatusCode,
                            ResponseSnippet = snippet,
                            ErrorMessage = "Non-200 response"
                        });
                    }
                }
                catch (Exception ex)
                {
                    failedServices++;
                    failedDetails.Add(new
                    {
                        printer.Id,
                        printer.Name,
                        printer.ServerUrl,
                        printer.Backend,
                        AttemptedUrl = $"{printer.ServerUrl.TrimEnd('/')}/server/info",
                        CheckedAtUtc = DateTime.UtcNow,
                        ElapsedMs = (long?)null,
                        ErrorMessage = ex.Message
                    });
                }
            }

            string serviceStatus = failedServices == 0 ? "Healthy"
                                  : failedServices < externalServiceCount ? "Degraded" : "Unhealthy";

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

            checks["ExternalServices"] = externalServicesObj;

            // External printers being offline is considered a degradation by default.
            // Use application settings to determine when the failure threshold should escalate
            // to an Unhealthy status.
            int percentFailed = externalServiceCount == 0 ? 0 : (int)Math.Round((double)failedServices / externalServiceCount * 100);
            int threshold = 100; // default: only unhealthy when 100% fail
            try
            {
                Farm.Infrastructure.Settings.ExternalServicesHealthSettings s = settingsService.Get<Farm.Infrastructure.Settings.ExternalServicesHealthSettings>();
                if (s != null)
                {
                    threshold = Math.Clamp(s.PercentFailedThreshold, 0, 100);
                }
            }
            catch
            {
            }

            if (failedServices > 0)
            {
                if (percentFailed >= threshold)
                {
                    // Treat as Unhealthy when percentFailed meets/exceeds configured threshold
                    overallHealthy = false;
                    issues.Add($"External services unreachable ({failedServices}/{externalServiceCount}) - threshold {threshold}% reached");
                }
                else
                {
                    degraded = true;
                    issues.Add($"External services unreachable ({failedServices}/{externalServiceCount}) - percentFailed={percentFailed}% (<{threshold}%)");
                }
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
            Environment = hostEnvironment.EnvironmentName,
            Version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "Unknown"
        };

        // Determine overall health status: prefer Unhealthy for critical failures,
        // Degraded when only non-critical external services are affected, otherwise Healthy.
        HealthStatus finalStatus;
        string description;
        if (!overallHealthy)
        {
            finalStatus = HealthStatus.Unhealthy;
            description = string.Join("; ", issues);
        }
        else if (degraded)
        {
            finalStatus = HealthStatus.Degraded;
            description = string.Join("; ", issues);
        }
        else
        {
            finalStatus = HealthStatus.Healthy;
            description = "All systems operational";
        }

        HealthCheckResult result = new(
            finalStatus,
            description: description,
            data: checks);

        return result;
    }

    private sealed record ExternalServicePrinter(Guid Id, string Name, string ServerUrl, int Backend);
}

// Re-enable warning at end of file
#pragma warning restore CA1303
#pragma warning restore S1075
