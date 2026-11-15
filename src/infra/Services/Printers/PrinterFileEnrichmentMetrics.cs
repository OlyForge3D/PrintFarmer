using System.Diagnostics;
using System.Diagnostics.Metrics;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Services.Printers;

/// <summary>Low-cardinality measurements of printer-file acquisition and bounded G-code fallback.</summary>
public sealed class PrinterFileEnrichmentMetrics : IDisposable
{
    /// <summary>The meter registered with the API's existing OpenTelemetry exporters.</summary>
    public const string MeterName = "PrintFarmer.PrinterFileEnrichment";

    /// <summary>The fallback elapsed-time histogram, measured in milliseconds.</summary>
    public const string FallbackDurationName = "printer_file.enrichment.fallback.duration";

    private readonly TimeProvider timeProvider;
    private readonly Counter<long> enrichmentStarted;
    private readonly Counter<long> fallbackStarted;
    private readonly Counter<long> fallbackSkipped;
    private readonly Histogram<double> fallbackDuration;

    internal static PrinterFileEnrichmentMetrics Shared { get; } = new();

    internal Meter Meter { get; }

    /// <summary>Creates measurements using a monotonic clock, independently of the acquisition deadline.</summary>
    public PrinterFileEnrichmentMetrics(TimeProvider? timeProvider = null)
    {
        this.timeProvider = timeProvider ?? TimeProvider.System;
        Meter = new(MeterName);
        enrichmentStarted = Meter.CreateCounter<long>(
            "printer_file.enrichment.started", "{acquisition}", "Shared printer-file acquisitions started.");
        fallbackStarted = Meter.CreateCounter<long>(
            "printer_file.enrichment.fallback.started", "{fallback}", "Bounded G-code fallbacks started.");
        fallbackSkipped = Meter.CreateCounter<long>(
            "printer_file.enrichment.fallback.skipped", "{fallback}", "Needed fallbacks skipped before reading.");
        fallbackDuration = Meter.CreateHistogram<double>(
            FallbackDurationName, "ms", "Elapsed bounded G-code reads and parsing, including unsuccessful attempts.");
    }

    internal void RecordEnrichment(PrinterBackend backend) =>
        enrichmentStarted.Add(1, new KeyValuePair<string, object?>("backend", BackendName(backend)));

    internal long StartFallback(PrinterBackend backend, string trigger)
    {
        long started = timeProvider.GetTimestamp();
        TagList tags = new() { { "backend", BackendName(backend) }, { "trigger", trigger } };
        fallbackStarted.Add(1, tags);
        return started;
    }

    internal void CompleteFallback(PrinterBackend backend, string trigger, string outcome, long started)
    {
        double elapsed = timeProvider.GetElapsedTime(started).TotalMilliseconds;
        TagList tags = new()
        {
            { "backend", BackendName(backend) }, { "trigger", trigger }, { "outcome", outcome },
        };
        fallbackDuration.Record(elapsed, tags);
    }

    internal void SkipFallback(PrinterBackend backend, string trigger, string reason)
    {
        TagList tags = new()
        {
            { "backend", BackendName(backend) }, { "trigger", trigger }, { "reason", reason },
        };
        fallbackSkipped.Add(1, tags);
    }

    private static string BackendName(PrinterBackend backend) => backend switch
    {
        PrinterBackend.Moonraker => "moonraker",
        PrinterBackend.OctoPrint => "octoprint",
        PrinterBackend.PrusaLink => "prusalink",
        _ => "unknown",
    };

    /// <inheritdoc/>
    public void Dispose() => Meter.Dispose();
}
