using System.Diagnostics.Metrics;

namespace Farm.Infrastructure.Services.FailureDetection;

/// <summary>
/// OTel metrics instruments for failure detection capacity planning and observability.
/// Meter name: <c>PrintFarmer.FailureDetection</c>.
/// </summary>
public sealed class FailureDetectionMetrics
{
    private static readonly Meter s_meter = new("PrintFarmer.FailureDetection", "1.0.0");

    private static readonly Counter<long> s_analysesTotal = s_meter.CreateCounter<long>(
        "printfarmer.failure_detection.analyses_total",
        description: "Total number of snapshot analyses performed");

    private static readonly Counter<long> s_failuresDetectedTotal = s_meter.CreateCounter<long>(
        "printfarmer.failure_detection.failures_detected_total",
        description: "Total number of failure detections");

    private static readonly Counter<long> s_autoPausesTotal = s_meter.CreateCounter<long>(
        "printfarmer.failure_detection.auto_pauses_total",
        description: "Total number of auto-pauses triggered by failure detection");

    private static readonly Counter<long> s_errorsTotal = s_meter.CreateCounter<long>(
        "printfarmer.failure_detection.errors_total",
        description: "Total number of analysis errors (timeouts, HTTP failures, parse errors)");

    private static readonly Histogram<double> s_analysisDurationMs = s_meter.CreateHistogram<double>(
        "printfarmer.failure_detection.analysis_duration_ms",
        description: "Duration of individual Obico ML API calls");

    private static readonly Histogram<double> s_cycleDurationMs = s_meter.CreateHistogram<double>(
        "printfarmer.failure_detection.cycle_duration_ms",
        description: "Duration of a full monitoring cycle across all printers");

    private static readonly Histogram<double> s_confidence = s_meter.CreateHistogram<double>(
        "printfarmer.failure_detection.confidence",
        description: "Distribution of ML confidence values returned by Obico (0.0–1.0)");

#pragma warning disable S1450 // ObservableGauge fields must be retained to prevent GC; callbacks read shared state
    private static readonly ObservableGauge<int> s_activePrinters = s_meter.CreateObservableGauge(
        "printfarmer.failure_detection.active_printers",
        ObserveActivePrinters,
        description: "Number of printers actively monitored in last cycle");

    private static readonly ObservableGauge<int> s_configuredPrinters = s_meter.CreateObservableGauge(
        "printfarmer.failure_detection.configured_printers",
        ObserveConfiguredPrinters,
        description: "Number of printers opted into failure detection");
#pragma warning restore S1450

    private static int s_activePrinterCount;
    private static int s_configuredPrinterCount;

    /// <summary>Gets the active printers gauge (retained to prevent GC).</summary>
    public ObservableGauge<int> ActivePrintersGauge => s_activePrinters;

    /// <summary>Gets the configured printers gauge (retained to prevent GC).</summary>
    public ObservableGauge<int> ConfiguredPrintersGauge => s_configuredPrinters;

    /// <summary>Record a successful analysis.</summary>
    /// <param name="durationMs">Duration of the ML API call in milliseconds.</param>
    /// <param name="confidence">Confidence value returned by the model (0.0–1.0).</param>
    /// <param name="isFailure">Whether the analysis detected a failure.</param>
    public void RecordAnalysis(double durationMs, double confidence, bool isFailure)
    {
        s_analysesTotal.Add(1);
        s_analysisDurationMs.Record(durationMs);
        s_confidence.Record(confidence);
        if (isFailure)
        {
            s_failuresDetectedTotal.Add(1);
        }
    }

    /// <summary>Record an auto-pause event.</summary>
    public void RecordAutoPause()
    {
        s_autoPausesTotal.Add(1);
    }

    /// <summary>Record an analysis error (timeout, HTTP failure, parse error).</summary>
    public void RecordError()
    {
        s_errorsTotal.Add(1);
    }

    /// <summary>Record the duration of a full monitoring cycle.</summary>
    /// <param name="durationMs">Total cycle duration in milliseconds.</param>
    /// <param name="activePrinters">Number of printers actively monitored.</param>
    /// <param name="configuredPrinters">Number of printers configured for detection.</param>
    public void RecordCycle(double durationMs, int activePrinters, int configuredPrinters)
    {
        s_cycleDurationMs.Record(durationMs);
        _ = Interlocked.Exchange(ref s_activePrinterCount, activePrinters);
        _ = Interlocked.Exchange(ref s_configuredPrinterCount, configuredPrinters);
    }

    /// <summary>
    /// Reset internal shared counters. Intended for test usage only.
    /// </summary>
    public static void ResetForTests()
    {
        _ = Interlocked.Exchange(ref s_activePrinterCount, 0);
        _ = Interlocked.Exchange(ref s_configuredPrinterCount, 0);
    }

    private static Measurement<int> ObserveActivePrinters() =>
        new(Interlocked.CompareExchange(ref s_activePrinterCount, 0, 0));

    private static Measurement<int> ObserveConfiguredPrinters() =>
        new(Interlocked.CompareExchange(ref s_configuredPrinterCount, 0, 0));
}
