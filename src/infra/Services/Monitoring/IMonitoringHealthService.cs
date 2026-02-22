namespace Farm.Infrastructure.Services.Monitoring;

public interface IMonitoringHealthService
{
    Task<MonitoringStatusDto> GetStatusAsync(CancellationToken cancellationToken = default);

    Task<MonitoringMetricsSummaryDto> GetMetricsSummaryAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams individual metric results as they resolve from Prometheus,
    /// yielding each via <c>Task.WhenAny</c> for progressive client updates.
    /// </summary>
    IAsyncEnumerable<MetricStreamEvent> StreamMetricsAsync(CancellationToken cancellationToken = default);
}

public record MonitoringStatusDto
{
    public ServiceStatusDto Grafana { get; init; } = new();

    public ServiceStatusDto Jaeger { get; init; } = new();

    public ServiceStatusDto Prometheus { get; init; } = new();
}

public record ServiceStatusDto
{
    public bool Available { get; init; }

    public string? Url { get; init; }

    public string? Error { get; init; }
}

public record MonitoringMetricsSummaryDto
{
    public double RequestsPerSecond { get; init; }

    public int ApiCallsLast24h { get; init; }

    public string TopEndpointName { get; init; } = "n/a";

    public double TopEndpointRequestsPerSecond { get; init; }

    public double ErrorRatePercent { get; init; }

    public double ClientErrorRatePercent { get; init; }

    public double P95LatencyMs { get; init; }

    public double P99LatencyMs { get; init; }

    public double MemoryUsageMb { get; init; }

    public int ActivePrinters { get; init; }

    public double PrinterSuccessRatePercent { get; init; }

    public int FileOperationsLast24h { get; init; }

    public double AverageFileSizeMbLast24h { get; init; }

    public int DatabaseOperationsLast24h { get; init; }

    public int SlicerJobsLast24h { get; init; }

    public double SlicerSuccessRatePercent { get; init; }

    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

public record MetricStreamEvent
{
    public required string Key { get; init; }

    public object? Value { get; init; }
}
