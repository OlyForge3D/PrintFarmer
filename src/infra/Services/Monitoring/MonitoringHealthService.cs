using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.Monitoring;

public class MonitoringHealthService(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    ILogger<MonitoringHealthService> logger) : IMonitoringHealthService
{
    public async Task<MonitoringStatusDto> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var grafana = ProbeServiceAsync(
            "Grafana",
            GetServiceUrl("Monitoring:GrafanaUrl", "http://grafana:3000"),
            "/api/health", cancellationToken);

        var jaeger = ProbeServiceAsync(
            "Jaeger",
            GetServiceUrl("Monitoring:JaegerUrl", "http://jaeger:16686"),
            "/", cancellationToken);

        var prometheus = ProbeServiceAsync(
            "Prometheus",
            GetServiceUrl("Monitoring:PrometheusUrl", "http://prometheus:9090"),
            "/-/healthy", cancellationToken);

        await Task.WhenAll(grafana, jaeger, prometheus);

        return new MonitoringStatusDto
        {
            Grafana = await grafana,
            Jaeger = await jaeger,
            Prometheus = await prometheus,
        };
    }

    public async Task<MonitoringMetricsSummaryDto> GetMetricsSummaryAsync(CancellationToken cancellationToken = default)
    {
        var prometheusUrl = GetServiceUrl("Monitoring:PrometheusUrl", "http://prometheus:9090");
        var client = httpClientFactory.CreateClient("MonitoringHealth");

        var requestRate = QueryPrometheusAsync(client, prometheusUrl,
            "sum(rate(printfarmer_api_calls_total[5m]))", cancellationToken);
        var errorRate = QueryPrometheusAsync(client, prometheusUrl,
            "sum(rate(printfarmer_api_calls_total{status_class=\"5xx\"}[5m])) / sum(rate(printfarmer_api_calls_total[5m])) * 100", cancellationToken);
        var p95Latency = QueryPrometheusAsync(client, prometheusUrl,
            "histogram_quantile(0.95, sum(rate(printfarmer_api_call_duration_seconds_bucket[5m])) by (le)) * 1000", cancellationToken);
        var memoryUsage = QueryPrometheusAsync(client, prometheusUrl,
            "process_working_set_bytes / 1024 / 1024", cancellationToken);
        var printerOps = QueryPrometheusAsync(client, prometheusUrl,
            "count(count by (printer_id) (printfarmer_printer_operations_total))", cancellationToken);
        var slicerJobs = QueryPrometheusAsync(client, prometheusUrl,
            "sum(increase(printfarmer_slicer_operations_total[24h]))", cancellationToken);
        var slicerSuccess = QueryPrometheusAsync(client, prometheusUrl,
            "sum(rate(printfarmer_slicer_operations_total{success=\"true\"}[24h])) / sum(rate(printfarmer_slicer_operations_total[24h])) * 100", cancellationToken);

        await Task.WhenAll(requestRate, errorRate, p95Latency, memoryUsage, printerOps, slicerJobs, slicerSuccess);

        return new MonitoringMetricsSummaryDto
        {
            RequestsPerSecond = await requestRate,
            ErrorRatePercent = await errorRate,
            P95LatencyMs = await p95Latency,
            MemoryUsageMb = await memoryUsage,
            ActivePrinters = (int)(await printerOps),
            SlicerJobsLast24h = (int)(await slicerJobs),
            SlicerSuccessRatePercent = await slicerSuccess,
        };
    }

    private async Task<ServiceStatusDto> ProbeServiceAsync(
        string name, string baseUrl, string healthPath, CancellationToken cancellationToken)
    {
        try
        {
            var client = httpClientFactory.CreateClient("MonitoringHealth");
            client.Timeout = TimeSpan.FromSeconds(5);
            var response = await client.GetAsync($"{baseUrl}{healthPath}", cancellationToken);
            return new ServiceStatusDto
            {
                Available = response.IsSuccessStatusCode,
                Url = $"/{name.ToLowerInvariant()}/",
            };
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Monitoring probe for {Service} failed", name);
            return new ServiceStatusDto
            {
                Available = false,
                Error = $"{name} is not reachable",
            };
        }
    }

    private async Task<double> QueryPrometheusAsync(
        HttpClient client, string prometheusUrl, string query, CancellationToken cancellationToken)
    {
        try
        {
            var url = $"{prometheusUrl}/api/v1/query?query={Uri.EscapeDataString(query)}";
            var response = await client.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return 0;
            }

            var json = await response.Content.ReadFromJsonAsync<PrometheusQueryResponse>(cancellationToken);
            if (json?.Data?.Result is { Count: > 0 })
            {
                var value = json.Data.Result[0].Value;
                if (value is { Count: >= 2 } && double.TryParse(value[1]?.ToString(), out var result))
                {
                    return double.IsNaN(result) || double.IsInfinity(result) ? 0 : Math.Round(result, 2);
                }
            }

            return 0;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Prometheus query failed: {Query}", query);
            return 0;
        }
    }

    private string GetServiceUrl(string configKey, string defaultUrl) =>
        configuration[configKey] ?? defaultUrl;

    // Prometheus API response models
    private record PrometheusQueryResponse
    {
        [JsonPropertyName("status")]
        public string? Status { get; init; }

        [JsonPropertyName("data")]
        public PrometheusData? Data { get; init; }
    }

    private record PrometheusData
    {
        [JsonPropertyName("result")]
        public List<PrometheusResult>? Result { get; init; }
    }

    private record PrometheusResult
    {
        [JsonPropertyName("value")]
        public List<object?>? Value { get; init; }
    }
}
