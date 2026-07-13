using System.Diagnostics.Metrics;

namespace Farm.Infrastructure.Services.Notifications.NativePush;

/// <summary>
/// System.Diagnostics.Metrics facade for the native-push delivery pipeline. Metric names
/// and tag schema are documented in <c>docs/OPERATOR_NATIVE_PUSH.md §7</c>.
/// </summary>
public sealed class NativePushMetrics : IDisposable
{
    /// <summary>Meter name used by <c>OpenTelemetry</c> subscribers.</summary>
    public const string MeterName = "Farm.Infrastructure.Services.Notifications.NativePush";

    private readonly Meter _meter;

    /// <summary>Counter of envelopes attempted (before dedupe / rate limit).</summary>
    public Counter<long> Attempted { get; }

    /// <summary>Counter of successful deliveries. Tag: <c>mode</c>.</summary>
    public Counter<long> Delivered { get; }

    /// <summary>Counter of transient failures. Tag: <c>mode</c>, <c>reason</c>.</summary>
    public Counter<long> TransientFailed { get; }

    /// <summary>Counter of terminal failures. Tag: <c>mode</c>, <c>reason</c>.</summary>
    public Counter<long> TerminalFailed { get; }

    /// <summary>Counter of tokens invalidated by the provider (410).</summary>
    public Counter<long> TokensInvalidated { get; }

    /// <summary>Counter of skips caused by a disabled operator flag mid-flight.</summary>
    public Counter<long> SkippedFeatureDisabled { get; }

    /// <summary>Counter of skips caused by dedupe.</summary>
    public Counter<long> SkippedDedupe { get; }

    /// <summary>Counter of skips caused by rate limit.</summary>
    public Counter<long> SkippedRateLimit { get; }

    /// <summary>Counter of skips caused by per-user category opt-out.</summary>
    public Counter<long> SkippedCategoryOptOut { get; }

    /// <summary>Counter of skips caused by an incomplete sender configuration
    /// (treated as no-op — no failure counter mutation).</summary>
    public Counter<long> SkippedNotConfigured { get; }

    /// <summary>Constructs the meter and counters.</summary>
    public NativePushMetrics()
    {
        _meter = new Meter(MeterName);
        Attempted = _meter.CreateCounter<long>("native_push.attempted");
        Delivered = _meter.CreateCounter<long>("native_push.delivered");
        TransientFailed = _meter.CreateCounter<long>("native_push.transient_failed");
        TerminalFailed = _meter.CreateCounter<long>("native_push.terminal_failed");
        TokensInvalidated = _meter.CreateCounter<long>("native_push.tokens_invalidated");
        SkippedFeatureDisabled = _meter.CreateCounter<long>("native_push.skipped_feature_disabled");
        SkippedDedupe = _meter.CreateCounter<long>("native_push.skipped_dedupe");
        SkippedRateLimit = _meter.CreateCounter<long>("native_push.skipped_rate_limit");
        SkippedCategoryOptOut = _meter.CreateCounter<long>("native_push.skipped_category_opt_out");
        SkippedNotConfigured = _meter.CreateCounter<long>("native_push.skipped_not_configured");
    }

    /// <inheritdoc />
    public void Dispose() => _meter.Dispose();
}
