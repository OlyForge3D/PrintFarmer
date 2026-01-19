using System.Collections.Concurrent;
using Farm.Infrastructure.Telemetry;

namespace Farm.Infrastructure;

public record CircuitBreakerMetrics
{
    public string Name { get; init; } = string.Empty;

    public CircuitState State { get; init; }

    public int FailureCount { get; init; }

    public DateTime LastFailureTime { get; init; }

    public int FailureThreshold { get; init; }
}
