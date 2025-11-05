using System.Diagnostics.Metrics;

namespace Farm.Web.Api.Services.Slicing;

/// <summary>
/// Metrics for slice job lifecycle events. Focused on completion and artifact association.
/// </summary>
public sealed class SliceJobMetrics : IDisposable
{
    private readonly Meter _meter;
    private bool _disposed;

    public Counter<long> JobsCompletedTotal { get; }
    public Counter<long> JobsCompletedWithLogTotal { get; }
    public Histogram<int> ArtifactsPerJobHistogram { get; }
    public Counter<long> JobsTimedOutTotal { get; }
    public Counter<long> JobRetriesTotal { get; }

    public SliceJobMetrics()
    {
        _meter = new Meter("PrintFarmer.Slicing", "1.0.0");
        JobsCompletedTotal = _meter.CreateCounter<long>(
            "printfarmer.slicing.jobs_completed_total",
            description: "Total slice jobs completed successfully");
        JobsCompletedWithLogTotal = _meter.CreateCounter<long>(
            "printfarmer.slicing.jobs_completed_with_log_total",
            description: "Completed jobs that included log artifact (inline or uploaded)");
        ArtifactsPerJobHistogram = _meter.CreateHistogram<int>(
            "printfarmer.slicing.artifacts_per_job",
            unit: "artifacts",
            description: "Number of artifacts associated with completed jobs");
        JobsTimedOutTotal = _meter.CreateCounter<long>(
            "printfarmer.slicing.jobs_timed_out_total",
            description: "Slice jobs that timed out and were handled by the error recovery scanner");
        JobRetriesTotal = _meter.CreateCounter<long>(
            "printfarmer.slicing.job_retries_total",
            description: "Total number of retries performed by the error recovery scanner");
    }

    /// <summary>
    /// Record job completion with artifact metadata.
    /// </summary>
    public void RecordJobCompletion(int artifactCount, bool hasLog)
    {
        JobsCompletedTotal.Add(1);
        ArtifactsPerJobHistogram.Record(artifactCount);
        if (hasLog)
        {
            JobsCompletedWithLogTotal.Add(1);
        }
    }

    public void RecordJobTimedOut()
    {
        JobsTimedOutTotal.Add(1);
    }

    public void RecordJobRetry()
    {
        JobRetriesTotal.Add(1);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _meter.Dispose();
        _disposed = true;
    }
}
