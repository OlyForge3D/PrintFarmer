// <copyright file="BedClearAcknowledgementExpiryMetrics.cs" company="PlaceholderCompany">
// SPDX-License-Identifier: AGPL-3.0-only
// </copyright>

using System.Diagnostics.Metrics;

namespace Farm.Infrastructure.Services.Queue;

/// <summary>
/// System.Diagnostics.Metrics facade for <see cref="BedClearAcknowledgementExpiryService"/>'s
/// scan loop. Records, per pass, how many acknowledged printers were scanned and how long the
/// pass took, so the acknowledged-printer distribution and per-pass DB/processing cost can be
/// measured in production instead of modeled — see issue #1732.
/// </summary>
public sealed class BedClearAcknowledgementExpiryMetrics : IDisposable
{
    /// <summary>Meter name used by OpenTelemetry subscribers.</summary>
    public const string MeterName = "Farm.Infrastructure.Services.Queue.BedClearAcknowledgementExpiry";

    private readonly Meter _meter;

    /// <summary>Histogram of the number of acknowledged printers scanned per pass.</summary>
    public Histogram<int> ScannedCount { get; }

    /// <summary>Histogram of the wall-clock duration of a scan pass, in milliseconds.</summary>
    public Histogram<double> ScanDurationMs { get; }

    /// <summary>Constructs the meter and instruments.</summary>
    public BedClearAcknowledgementExpiryMetrics()
    {
        _meter = new Meter(MeterName);
        ScannedCount = _meter.CreateHistogram<int>(
            "bed_clear_acknowledgement_expiry.scanned_count",
            description: "Number of acknowledged printers scanned in a single pass");
        ScanDurationMs = _meter.CreateHistogram<double>(
            "bed_clear_acknowledgement_expiry.scan_duration_ms",
            unit: "ms",
            description: "Wall-clock duration of a single scan pass, including the outer scan query and the per-printer invalidation loop");
    }

    /// <summary>Record the outcome of one scan pass.</summary>
    /// <param name="scannedCount">Number of acknowledged printers scanned in this pass.</param>
    /// <param name="durationMs">Wall-clock duration of the pass in milliseconds.</param>
    public void RecordScan(int scannedCount, double durationMs)
    {
        ScannedCount.Record(scannedCount);
        ScanDurationMs.Record(durationMs);
    }

    /// <inheritdoc />
    public void Dispose() => _meter.Dispose();
}
