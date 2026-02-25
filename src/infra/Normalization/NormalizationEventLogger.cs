using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Normalization;

public interface INormalizationEventLogger
{
    void Log(string entityType, string original, string normalized, string? source = null);
}

/// <summary>
/// In-memory rate-limited normalization logger (singleton). Replace with distributed telemetry for multi-instance scaling.
/// </summary>
public sealed class NormalizationEventLogger(ILogger<NormalizationEventLogger> logger) : INormalizationEventLogger
{
    private sealed class Counter(int count, DateTime windowStartUtc)
    {
        public int Count { get; set; } = count;

        public DateTime WindowStartUtc { get; set; } = windowStartUtc;
    }

    private readonly ILogger<NormalizationEventLogger> _logger = logger;
    private readonly ConcurrentDictionary<string, Counter> _counters = new();
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private const int ThresholdPerWindow = 20;

    public void Log(string entityType, string original, string normalized, string? source = null)
    {
        if (string.IsNullOrEmpty(normalized) || string.Equals(original, normalized, StringComparison.Ordinal))
        {
            return;
        }

        string key = entityType + "|" + normalized;
        DateTime now = DateTime.UtcNow;
        Counter counter = _counters.AddOrUpdate(
            key,
            _ => new Counter(1, now),
            (_, existing) =>
            {
                if (now - existing.WindowStartUtc > Window)
                {
                    existing.WindowStartUtc = now;
                    existing.Count = 1;
                }
                else
                {
                    existing.Count++;
                }

                return existing;
            });

        if (counter.Count <= ThresholdPerWindow)
        {
            _logger.LogInformation("Normalization {EntityType} normalized '{Original}' -> '{Normalized}' source={Source} count={CounterCount}", entityType, original, normalized, source ?? "unknown", counter.Count);
        }
    }
}
