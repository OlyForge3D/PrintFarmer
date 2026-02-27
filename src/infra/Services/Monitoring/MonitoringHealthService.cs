using System.Net.Http.Json;
using System.Runtime.CompilerServices;
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
        var apiCalls24h = QueryPrometheusAsync(client, prometheusUrl,
            "sum(increase(printfarmer_api_calls_total[24h]))", cancellationToken);
        var topEndpoint = QueryPrometheusTopEndpointAsync(client, prometheusUrl, cancellationToken);
        var errorRate = QueryPrometheusAsync(client, prometheusUrl,
            "(sum(rate(printfarmer_api_calls_total{status_class=\"5xx\"}[5m])) or vector(0)) / (sum(rate(printfarmer_api_calls_total[5m])) or vector(1)) * 100", cancellationToken);
        var clientErrorRate = QueryPrometheusAsync(client, prometheusUrl,
            "(sum(rate(printfarmer_api_calls_total{status_class=\"4xx\"}[5m])) or vector(0)) / (sum(rate(printfarmer_api_calls_total[5m])) or vector(1)) * 100", cancellationToken);
        var p95Latency = QueryPrometheusAsync(client, prometheusUrl,
            "histogram_quantile(0.95, sum(rate(printfarmer_api_call_duration_seconds_bucket[5m])) by (le))", cancellationToken);
        var p99Latency = QueryPrometheusAsync(client, prometheusUrl,
            "histogram_quantile(0.99, sum(rate(printfarmer_api_call_duration_seconds_bucket[5m])) by (le))", cancellationToken);
        var memoryUsage = QueryPrometheusAsync(client, prometheusUrl,
            "dotnet_process_memory_working_set_bytes / 1024 / 1024", cancellationToken);
        var printerOps = QueryPrometheusAsync(client, prometheusUrl,
            "count(count by (printer_id) (printfarmer_printer_operations_total))", cancellationToken);
        var printerSuccessRate = QueryPrometheusAsync(client, prometheusUrl,
            "(sum(rate(printfarmer_printer_operations_total{success=\"true\"}[24h])) or vector(0)) / (sum(rate(printfarmer_printer_operations_total[24h])) or vector(1)) * 100", cancellationToken);
        var fileOps24h = QueryPrometheusAsync(client, prometheusUrl,
            "sum(increase(printfarmer_file_operations_total[24h]))", cancellationToken);
        var avgFileSize24h = QueryPrometheusAsync(client, prometheusUrl,
            "((sum(increase(printfarmer_file_size_bytes_sum[24h])) or vector(0)) / clamp_min((sum(increase(printfarmer_file_size_bytes_count[24h])) or vector(0)), 1)) / 1024 / 1024", cancellationToken);
        var databaseOps24h = QueryPrometheusAsync(client, prometheusUrl,
            "sum(increase(printfarmer_database_operations_total[24h]))", cancellationToken);
        var slicerJobs = QueryPrometheusAsync(client, prometheusUrl,
            "sum(increase(printfarmer_slicer_operations_total[24h]))", cancellationToken);
        var slicerSuccess = QueryPrometheusAsync(client, prometheusUrl,
            "(sum(rate(printfarmer_slicer_operations_total{success=\"true\"}[24h])) or vector(0)) / (sum(rate(printfarmer_slicer_operations_total[24h])) or vector(1)) * 100", cancellationToken);

        await Task.WhenAll(requestRate, apiCalls24h, topEndpoint, errorRate, clientErrorRate, p95Latency, p99Latency, memoryUsage, printerOps, printerSuccessRate, fileOps24h, avgFileSize24h, databaseOps24h, slicerJobs, slicerSuccess);

        var topEndpointResult = await topEndpoint;

        return new MonitoringMetricsSummaryDto
        {
            RequestsPerSecond = await requestRate,
            ApiCallsLast24h = (int)(await apiCalls24h),
            TopEndpointName = topEndpointResult.Endpoint,
            TopEndpointRequestsPerSecond = topEndpointResult.RequestsPerSecond,
            ErrorRatePercent = await errorRate,
            ClientErrorRatePercent = await clientErrorRate,
            P95LatencyMs = await p95Latency,
            P99LatencyMs = await p99Latency,
            MemoryUsageMb = await memoryUsage,
            ActivePrinters = (int)(await printerOps),
            PrinterSuccessRatePercent = await printerSuccessRate,
            FileOperationsLast24h = (int)(await fileOps24h),
            AverageFileSizeMbLast24h = await avgFileSize24h,
            DatabaseOperationsLast24h = (int)(await databaseOps24h),
            SlicerJobsLast24h = (int)(await slicerJobs),
            SlicerSuccessRatePercent = await slicerSuccess,
        };
    }

    public async IAsyncEnumerable<MetricStreamEvent> StreamMetricsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var prometheusUrl = GetServiceUrl("Monitoring:PrometheusUrl", "http://prometheus:9090");
        var client = httpClientFactory.CreateClient("MonitoringHealth");

        Task<MetricStreamEvent[]> QAsync(string key, string query, bool asInt = false) =>
            WrapMetricAsync(key, () => QueryPrometheusAsync(client, prometheusUrl, query, cancellationToken), asInt);

        var pending = new List<Task<MetricStreamEvent[]>>
        {
            QAsync("requestsPerSecond", "sum(rate(printfarmer_api_calls_total[5m]))"),
            QAsync("apiCallsLast24h", "sum(increase(printfarmer_api_calls_total[24h]))", asInt: true),
            WrapTopEndpointAsync(() => QueryPrometheusTopEndpointAsync(client, prometheusUrl, cancellationToken)),
            QAsync("errorRatePercent", "(sum(rate(printfarmer_api_calls_total{status_class=\"5xx\"}[5m])) or vector(0)) / (sum(rate(printfarmer_api_calls_total[5m])) or vector(1)) * 100"),
            QAsync("clientErrorRatePercent", "(sum(rate(printfarmer_api_calls_total{status_class=\"4xx\"}[5m])) or vector(0)) / (sum(rate(printfarmer_api_calls_total[5m])) or vector(1)) * 100"),
            QAsync("p95LatencyMs", "histogram_quantile(0.95, sum(rate(printfarmer_api_call_duration_seconds_bucket[5m])) by (le))"),
            QAsync("p99LatencyMs", "histogram_quantile(0.99, sum(rate(printfarmer_api_call_duration_seconds_bucket[5m])) by (le))"),
            QAsync("memoryUsageMb", "dotnet_process_memory_working_set_bytes / 1024 / 1024"),
            QAsync("activePrinters", "count(count by (printer_id) (printfarmer_printer_operations_total))", asInt: true),
            QAsync("printerSuccessRatePercent", "(sum(rate(printfarmer_printer_operations_total{success=\"true\"}[24h])) or vector(0)) / (sum(rate(printfarmer_printer_operations_total[24h])) or vector(1)) * 100"),
            QAsync("fileOperationsLast24h", "sum(increase(printfarmer_file_operations_total[24h]))", asInt: true),
            QAsync("averageFileSizeMbLast24h", "((sum(increase(printfarmer_file_size_bytes_sum[24h])) or vector(0)) / clamp_min((sum(increase(printfarmer_file_size_bytes_count[24h])) or vector(0)), 1)) / 1024 / 1024"),
            QAsync("databaseOperationsLast24h", "sum(increase(printfarmer_database_operations_total[24h]))", asInt: true),
            QAsync("slicerJobsLast24h", "sum(increase(printfarmer_slicer_operations_total[24h]))", asInt: true),
            QAsync("slicerSuccessRatePercent", "(sum(rate(printfarmer_slicer_operations_total{success=\"true\"}[24h])) or vector(0)) / (sum(rate(printfarmer_slicer_operations_total[24h])) or vector(1)) * 100"),
        };

        while (pending.Count > 0)
        {
            var completed = await Task.WhenAny(pending);
            pending.Remove(completed);

            foreach (var evt in await completed)
            {
                yield return evt;
            }
        }
    }

    private static async Task<MetricStreamEvent[]> WrapMetricAsync(
        string key, Func<Task<double>> queryFactory, bool asInt = false)
    {
        var value = await queryFactory();
        object result = asInt ? (int)value : value;
        return [new MetricStreamEvent { Key = key, Value = result }];
    }

    private static async Task<MetricStreamEvent[]> WrapTopEndpointAsync(
        Func<Task<(string Endpoint, double RequestsPerSecond)>> queryFactory)
    {
        var (endpoint, rps) = await queryFactory();
        return
        [
            new MetricStreamEvent { Key = "topEndpointName", Value = endpoint },
            new MetricStreamEvent { Key = "topEndpointRequestsPerSecond", Value = rps },
        ];
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

    private async Task<(string Endpoint, double RequestsPerSecond)> QueryPrometheusTopEndpointAsync(
        HttpClient client, string prometheusUrl, CancellationToken cancellationToken)
    {
        const string query = "topk(1, sum by (endpoint) (rate(printfarmer_api_calls_total[5m])))";

        try
        {
            var url = $"{prometheusUrl}/api/v1/query?query={Uri.EscapeDataString(query)}";
            var response = await client.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return ("n/a", 0);
            }

            var json = await response.Content.ReadFromJsonAsync<PrometheusQueryResponse>(cancellationToken);
            if (json?.Data?.Result is { Count: > 0 })
            {
                var first = json.Data.Result[0];
                var endpoint = first.Metric is not null && first.Metric.TryGetValue("endpoint", out var endpointName)
                    ? endpointName
                    : "n/a";

                var value = first.Value;
                if (value is { Count: >= 2 } && double.TryParse(value[1]?.ToString(), out var rps))
                {
                    var safeRps = double.IsNaN(rps) || double.IsInfinity(rps) ? 0 : Math.Round(rps, 2);
                    return (string.IsNullOrWhiteSpace(endpoint) ? "n/a" : endpoint, safeRps);
                }
            }

            return ("n/a", 0);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Prometheus top endpoint query failed");
            return ("n/a", 0);
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
        [JsonPropertyName("metric")]
        public Dictionary<string, string>? Metric { get; init; }

        [JsonPropertyName("value")]
        public List<object?>? Value { get; init; }
    }
}
