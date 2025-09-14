using Farm.Web.Api.Services;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Farm.Web.Api.Health;

public class SpoolmanHealthCheck : IHealthCheck
{
    private readonly SpoolmanService _spoolmanService;
    private readonly IHttpClientFactory _httpClientFactory;

    public SpoolmanHealthCheck(SpoolmanService spoolmanService, IHttpClientFactory httpClientFactory)
    {
        _spoolmanService = spoolmanService;
        _httpClientFactory = httpClientFactory;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var cfg = _spoolmanService.GetConfig();
        if (cfg is null || string.IsNullOrWhiteSpace(cfg.BaseUrl))
        {
            return HealthCheckResult.Healthy("Spoolman not configured");
        }
        var baseUrl = cfg.BaseUrl.TrimEnd('/');
        string[] paths = ["/api/v1/health", "/api/v1/info"]; // fallback list
        var client = _httpClientFactory.CreateClient("SpoolmanHealth");
        client.Timeout = TimeSpan.FromSeconds(5);
        foreach (var p in paths)
        {
            try
            {
                var resp = await client.GetAsync(baseUrl + p, cancellationToken);
                if (resp.IsSuccessStatusCode)
                {
                    return HealthCheckResult.Healthy($"OK via {p}");
                }
            }
            catch (Exception ex)
            {
                // last attempt -> degrade
                if (p == paths[^1])
                {
                    return HealthCheckResult.Degraded($"Spoolman unreachable: {ex.Message}");
                }
            }
        }
        return HealthCheckResult.Degraded("Spoolman probe failed");
    }
}
