// Suppress hardcoded URI warning for this file
#pragma warning disable CA1303 // Do not use hardcoded absolute paths or URIs
#pragma warning disable S1075 // Do not use hardcoded absolute paths or URIs (Sonar)
using System.Diagnostics;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Telemetry;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

namespace Farm.Web.Api.Health;

/// <summary>
/// Comprehensive health check that validates database connectivity,
/// external service availability, and system resources
/// </summary>
public class ComprehensiveHealthCheck(AppDbContext dbContext, IHttpClientFactory httpClientFactory, IUnifiedLoggingService logger, Farm.Infrastructure.Settings.ISettingsService settingsService, IHostEnvironment hostEnvironment) : IHealthCheck
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
                    // Avoid making actual outbound HTTP calls to the same process during tests.
                    // In some test modes (Testing env or when using the shared-sqlite fixture)
                    // the in-process test server may not be reachable via network loopback or
                    // constructing typed HTTP clients can trigger DI resolution ordering that
                    // leads to concurrent DB access on the same connection. In those cases we
                    // fall back to direct DB checks instead of internal HTTP.
                    bool skipInternalHttp = hostEnvironment.IsEnvironment("Testing")
                                            || string.Equals(Environment.GetEnvironmentVariable("TEST_USE_SHARED_SQLITE"), "true", StringComparison.OrdinalIgnoreCase);
                    using HttpClient client = httpClientFactory.CreateClient();
                    client.Timeout = TimeSpan.FromSeconds(3);
                    // Determine API base URL for internal health check
                    const string DefaultApiBaseUrl = "http://localhost:5245";
                    string? baseUrl = Environment.GetEnvironmentVariable("API_URL")
                        ?? Environment.GetEnvironmentVariable("ASPNETCORE_URLS")
                        ?? DefaultApiBaseUrl;
                    if (baseUrl.EndsWith('/'))
                    {
                        baseUrl = baseUrl.TrimEnd('/');
                    }

                    // Normalize hosts like 0.0.0.0, ::, * or + which are "listen on all" and
                    // are not valid targets for outbound HTTP calls. Replace them with
                    // localhost so internal health probes target the local loopback.
                    try
                    {
                        if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var parsed))
                        {
                            string host = parsed.Host ?? string.Empty;
                            if (string.Equals(host, "0.0.0.0", StringComparison.OrdinalIgnoreCase)
                                || string.Equals(host, "::", StringComparison.OrdinalIgnoreCase)
                                || string.Equals(host, "*", StringComparison.OrdinalIgnoreCase)
                                || string.Equals(host, "+", StringComparison.OrdinalIgnoreCase))
                            {
                                int port = parsed.IsDefaultPort ? -1 : parsed.Port;
                                string scheme = string.IsNullOrEmpty(parsed.Scheme) ? "http" : parsed.Scheme;
                                baseUrl = port > 0 ? $"{scheme}://localhost:{port}" : $"{scheme}://localhost";
                            }
                        }
                    }
                    catch { /* best-effort normalization - ignore failures and fall back to original baseUrl */ }

                    // Catalog API endpoint check (internal HTTP call)
                    try
                    {
                        if (skipInternalHttp)
                        {
                            // In test runs we skip HTTP calls and consider the catalog API healthy
                            // if the database shows manufacturers seeded (checked above). This avoids
                            // unreliable network calls to the same in-process test server.
                            checks["CatalogApi"] = new { Status = manufacturerCount > 0 ? "Healthy" : "Unhealthy", Count = manufacturerCount, SkippedHttp = true };
                            if (manufacturerCount == 0)
                            {
                                overallHealthy = false;
                                issues.Add("Catalog API skipped HTTP check and found no manufacturers in DB");
                            }
                        }
                        else
                        {
                            // Catalog API health check via HTTP
                            HttpResponseMessage resp = await client.GetAsync($"{baseUrl}/api/catalog/manufacturers", cancellationToken);
                            if (!resp.IsSuccessStatusCode)
                            {
                                checks["CatalogApi"] = new { Status = "Unhealthy", StatusCode = (int)resp.StatusCode, Reason = "Non-200 response" };
                                overallHealthy = false;
                                issues.Add($"Catalog API returned status {(int)resp.StatusCode}");
                            }
                            else
                            {
                                string json = await resp.Content.ReadAsStringAsync(cancellationToken);
                                // Try to parse as array
                                bool valid = false;
                                int count = 0;
                                try
                                {
                                    System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(json);
                                    if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                                    {
                                        count = doc.RootElement.GetArrayLength();
                                        valid = true;
                                    }
                                }
                                catch { }
                                if (!valid)
                                {
                                    checks["CatalogApi"] = new { Status = "Unhealthy", Reason = "Invalid JSON returned" };
                                    overallHealthy = false;
                                    issues.Add("Catalog API returned invalid JSON");
                                }
                                else if (count == 0)
                                {
                                    checks["CatalogApi"] = new { Status = "Unhealthy", Count = 0, Reason = "No manufacturers returned" };
                                    overallHealthy = false;
                                    issues.Add("Catalog API returned empty list");
                                }
                                else
                                {
                                    checks["CatalogApi"] = new { Status = "Healthy", Count = count };
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        checks["CatalogApi"] = new { Status = "Unhealthy", Error = ex.Message };
                        overallHealthy = false;
                        issues.Add($"Catalog API check failed: {ex.Message}");
                    }

                    // Filament presets health check (database and API)
                    try
                    {
                        int filamentTypeCount = await dbContext.FilamentTypes.CountAsync(cancellationToken);
                        checks["FilamentTypesDb"] = new { Status = filamentTypeCount > 0 ? "Healthy" : "Unhealthy", Count = filamentTypeCount };
                        if (filamentTypeCount == 0)
                        {
                            overallHealthy = false;
                            issues.Add("No filament types found in database");
                        }

                        // If running in the test environment, avoid loopback HTTP calls to the in-process server.
                        if (skipInternalHttp)
                        {
                            checks["FilamentTypesApi"] = new { Status = filamentTypeCount > 0 ? "Healthy" : "Unhealthy", Count = filamentTypeCount, SkippedHttp = true };
                            if (filamentTypeCount == 0)
                            {
                                overallHealthy = false;
                                issues.Add("FilamentType API skipped HTTP check and found no filament types in DB");
                            }
                        }
                        else
                        {
                            // Check /api/filament-types endpoint
                            HttpResponseMessage resp = await client.GetAsync($"{baseUrl}/api/filament-types", cancellationToken);
                            if (!resp.IsSuccessStatusCode)
                            {
                                checks["FilamentTypesApi"] = new { Status = "Unhealthy", StatusCode = (int)resp.StatusCode, Reason = "Non-200 response" };
                                overallHealthy = false;
                                issues.Add($"FilamentType API returned status {(int)resp.StatusCode}");
                            }
                            else
                            {
                                string json = await resp.Content.ReadAsStringAsync(cancellationToken);
                                bool valid = false;
                                int count = 0;
                                try
                                {
                                    System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(json);
                                    if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                                    {
                                        count = doc.RootElement.GetArrayLength();
                                        valid = true;
                                    }
                                }
                                catch { }
                                if (!valid)
                                {
                                    checks["FilamentTypesApi"] = new { Status = "Unhealthy", Reason = "Invalid JSON returned" };
                                    overallHealthy = false;
                                    issues.Add("FilamentType API returned invalid JSON");
                                }
                                else if (count == 0)
                                {
                                    checks["FilamentTypesApi"] = new { Status = "Unhealthy", Count = 0, Reason = "No filament types returned" };
                                    overallHealthy = false;
                                    issues.Add("FilamentType API returned empty list");
                                }
                                else
                                {
                                    checks["FilamentTypesApi"] = new { Status = "Healthy", Count = count };
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        checks["FilamentTypesApi"] = new { Status = "Unhealthy", Error = ex.Message };
                        overallHealthy = false;
                        issues.Add($"FilamentType API check failed: {ex.Message}");
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
            // Select printers to probe for external service health based on settings
            List<Printer> printers;
            int printersToCheck = 3; // default fallback
            try
            {
                Farm.Infrastructure.Settings.ExternalServicesHealthSettings s = settingsService.Get<Farm.Infrastructure.Settings.ExternalServicesHealthSettings>();
                if (s != null)
                {
                    printersToCheck = s.PrintersToCheck;
                }
            }
            catch { }

            if (printersToCheck == 0)
            {
                printers = new List<Printer>();
            }
            else
            {
                printers = printersToCheck < 0
                    ? await dbContext.Printers.ToListAsync(cancellationToken)
                    : await dbContext.Printers.Take(printersToCheck).ToListAsync(cancellationToken);
            }
            int externalServiceCount = 0;
            int failedServices = 0;
            List<object> failedDetails = new();

            foreach (Printer? printer in printers)
            {
                if (printer.Backend == 0) // Moonraker
                {
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
                            string snippet = "";
                            try
                            {
                                string body = await response.Content.ReadAsStringAsync(cancellationToken) ?? string.Empty;
                                snippet = body.Length > 200 ? body[..200] : body;
                            }
                            catch { }

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
            catch { }

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
            Environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Unknown",
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
            data: checks
        );

        return result;
    }
}

// Re-enable warning at end of file
#pragma warning restore CA1303
#pragma warning restore S1075
