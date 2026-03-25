using System.Linq;

namespace Farm.Infrastructure.Services.FailureDetection;

/// <summary>
/// Provides runtime snapshots of spaghetti-detection monitoring state.
/// </summary>
public interface IFailureDetectionMonitorStatus
{
    /// <summary>
    /// Returns the latest in-memory monitoring snapshot.
    /// </summary>
    FailureDetectionMonitorStatusDto GetSnapshot();

    /// <summary>
    /// Replaces the current in-memory monitoring snapshot.
    /// </summary>
    /// <param name="snapshot">The latest monitoring snapshot.</param>
    void UpdateSnapshot(FailureDetectionMonitorStatusDto snapshot);
}

/// <summary>
/// Summary of the current in-memory failure-detection monitoring state.
/// </summary>
public sealed record FailureDetectionMonitorStatusDto
{
    /// <summary>
    /// Gets whether failure detection is globally enabled.
    /// </summary>
    public bool MonitoringEnabled { get; init; }

    /// <summary>
    /// Gets the configured confidence threshold.
    /// </summary>
    public decimal ConfidenceThreshold { get; init; }

    /// <summary>
    /// Gets the configured scan interval, in seconds.
    /// </summary>
    public int ScanIntervalSeconds { get; init; }

    /// <summary>
    /// Gets whether auto-pause is enabled.
    /// </summary>
    public bool AutoPauseOnFailure { get; init; }

    /// <summary>
    /// Gets the number of printers opted into failure detection.
    /// </summary>
    public int ConfiguredPrinterCount { get; init; }

    /// <summary>
    /// Gets the number of printers actively being monitored in the current cycle.
    /// </summary>
    public int ActivelyMonitoredPrinterCount { get; init; }

    /// <summary>
    /// Gets the number of printers that were analyzed in the most recent cycle.
    /// </summary>
    public int LastAnalyzedPrinterCount { get; init; }

    /// <summary>
    /// Gets the number of failures detected in the most recent cycle.
    /// </summary>
    public int LastFailureCount { get; init; }

    /// <summary>
    /// Gets when the most recent monitoring cycle started.
    /// </summary>
    public DateTime? LastScanStartedAt { get; init; }

    /// <summary>
    /// Gets when the most recent monitoring cycle completed.
    /// </summary>
    public DateTime? LastScanCompletedAt { get; init; }

    /// <summary>
    /// Gets the most recent cycle-level error, if any.
    /// </summary>
    public string? LastError { get; init; }

    /// <summary>
    /// Gets per-printer monitoring details.
    /// </summary>
    public FailureDetectionPrinterStatusDto[] Printers { get; init; } = [];
}

/// <summary>
/// Runtime monitoring details for a single printer.
/// </summary>
public sealed record FailureDetectionPrinterStatusDto
{
    /// <summary>
    /// Gets the printer identifier.
    /// </summary>
    public Guid PrinterId { get; init; }

    /// <summary>
    /// Gets the printer display name.
    /// </summary>
    public string PrinterName { get; init; } = string.Empty;

    /// <summary>
    /// Gets the current monitoring state.
    /// </summary>
    public string State { get; init; } = "disabled";

    /// <summary>
    /// Gets the user-facing explanation for the current state.
    /// </summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>
    /// Gets whether the printer is currently printing.
    /// </summary>
    public bool IsPrinting { get; init; }

    /// <summary>
    /// Gets whether the printer is using a pooled or global Obico target.
    /// </summary>
    public string DetectionSource { get; init; } = "none";

    /// <summary>
    /// Gets the friendly pooled-server name or global target URL.
    /// </summary>
    public string? DetectionTarget { get; init; }

    /// <summary>
    /// Gets the snapshot URL used for analysis.
    /// </summary>
    public string? SnapshotUrl { get; init; }

    /// <summary>
    /// Gets when the printer was last analyzed.
    /// </summary>
    public DateTime? LastAnalyzedAt { get; init; }

    /// <summary>
    /// Gets the last analysis outcome.
    /// </summary>
    public string LastOutcome { get; init; } = "none";

    /// <summary>
    /// Gets the most recent confidence score.
    /// </summary>
    public decimal? LastConfidence { get; init; }

    /// <summary>
    /// Gets whether the most recent detected failure auto-paused the print.
    /// </summary>
    public bool? LastAutoPaused { get; init; }

    /// <summary>
    /// Gets when the most recent failure was detected.
    /// </summary>
    public DateTime? LastFailureDetectedAt { get; init; }
}

/// <summary>
/// Thread-safe in-memory store for runtime failure-detection snapshots.
/// </summary>
public sealed class FailureDetectionMonitorStatusStore : IFailureDetectionMonitorStatus
{
    private readonly object _sync = new();
    private FailureDetectionMonitorStatusDto _snapshot = new();

    /// <inheritdoc />
    public FailureDetectionMonitorStatusDto GetSnapshot()
    {
        lock (_sync)
        {
            return Clone(_snapshot);
        }
    }

    /// <inheritdoc />
    public void UpdateSnapshot(FailureDetectionMonitorStatusDto snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        lock (_sync)
        {
            _snapshot = Clone(snapshot);
        }
    }

    private static FailureDetectionMonitorStatusDto Clone(FailureDetectionMonitorStatusDto snapshot) =>
        snapshot with
        {
            Printers = snapshot.Printers.Select(status => status with { }).ToArray()
        };
}
