using System.Diagnostics.Metrics;

namespace Farm.Slicer.Module.Services.Metrics;

/// <summary>
/// Metrics instruments for artifact lifecycle. Recorded on upload.
/// </summary>
public sealed class ArtifactsMetrics : IDisposable
{
    private static readonly Meter s_meter = new("PrintFarmer.Artifacts", "1.0.0");

    private static readonly Counter<long> s_uploadedCount = s_meter.CreateCounter<long>(
        "printfarmer.artifacts.uploaded_total",
        description: "Total number of artifacts uploaded");

    private static readonly Histogram<long> s_uploadBytes = s_meter.CreateHistogram<long>(
        "printfarmer.artifacts.upload_bytes",
        unit: "bytes",
        description: "Size of individual artifact uploads");

    private static readonly ObservableGauge<long> s_storageTotalBytes = s_meter.CreateObservableGauge(
        "printfarmer.artifacts.storage_total_bytes",
        ObserveStorageBytes,
        unit: "bytes",
        description: "Approximate total size of stored artifacts (global)");

    private static readonly ObservableGauge<int> s_storageThresholdState = s_meter.CreateObservableGauge(
        "printfarmer.artifacts.storage_threshold_state",
        ObserveThresholdState,
        description: "Storage threshold state: 0=normal, 1=warning, 2=critical");

    private static long s_storageBytes;
    private static long s_warningThreshold;
    private static long s_criticalThreshold;
    private static int s_currentState;

    /// <summary>Gets the uploaded count counter.</summary>
    public Counter<long> UploadedCount => s_uploadedCount;

    /// <summary>Gets the upload bytes histogram.</summary>
    public Histogram<long> UploadBytes => s_uploadBytes;

    /// <summary>Gets the storage total bytes gauge.</summary>
    public ObservableGauge<long> StorageTotalBytes => s_storageTotalBytes;

    /// <summary>Gets the storage threshold state gauge.</summary>
    public ObservableGauge<int> StorageThresholdState => s_storageThresholdState;

    /// <summary>Event raised when storage thresholds are crossed.</summary>
    public event EventHandler<SlicerStorageThresholdEventArgs>? ThresholdExceeded;

    /// <summary>
    /// Initializes a new instance of the <see cref="ArtifactsMetrics"/> class.
    /// Resets shared state for test isolation.
    /// </summary>
    public ArtifactsMetrics()
    {
        ResetForTests();
    }

    /// <summary>
    /// Configure storage alert thresholds.
    /// </summary>
    /// <param name="warningBytes">The threshold in bytes at which a warning is triggered.</param>
    /// <param name="criticalBytes">The threshold in bytes at which a critical alert is triggered.</param>
    public void SetThresholds(long warningBytes, long criticalBytes)
    {
        _ = Interlocked.Exchange(ref s_warningThreshold, warningBytes);
        _ = Interlocked.Exchange(ref s_criticalThreshold, criticalBytes);
    }

    /// <summary>Record artifact upload metrics.</summary>
    /// <param name="sizeBytes">The size of the uploaded artifact in bytes.</param>
    public void RecordUpload(long sizeBytes)
    {
        s_uploadedCount.Add(1);
        s_uploadBytes.Record(sizeBytes);
        long newTotal = Interlocked.Add(ref s_storageBytes, sizeBytes);
        CheckThresholds(newTotal);
    }

    /// <summary>
    /// Reset internal shared storage counters. Intended for test usage only.
    /// </summary>
    public static void ResetForTests()
    {
        _ = Interlocked.Exchange(ref s_storageBytes, 0);
        _ = Interlocked.Exchange(ref s_warningThreshold, 0);
        _ = Interlocked.Exchange(ref s_criticalThreshold, 0);
        _ = Interlocked.Exchange(ref s_currentState, 0);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        // No-op: shared static meter is process-wide and may be used by other consumers.
    }

    private static Measurement<long> ObserveStorageBytes() => new(Interlocked.Read(ref s_storageBytes));

    private static Measurement<int> ObserveThresholdState() => new(Interlocked.CompareExchange(ref s_currentState, 0, 0));

    private void CheckThresholds(long currentBytes)
    {
        long warning = Interlocked.Read(ref s_warningThreshold);
        long critical = Interlocked.Read(ref s_criticalThreshold);

        if (warning <= 0 && critical <= 0)
        {
            return;
        }

        int newState = 0;
        SlicerStorageThresholdLevel level = SlicerStorageThresholdLevel.Normal;

        if (critical > 0 && currentBytes >= critical)
        {
            newState = 2;
            level = SlicerStorageThresholdLevel.Critical;
        }
        else if (warning > 0 && currentBytes >= warning)
        {
            newState = 1;
            level = SlicerStorageThresholdLevel.Warning;
        }

        int oldState = Interlocked.Exchange(ref s_currentState, newState);
        if (newState > oldState && newState > 0)
        {
            ThresholdExceeded?.Invoke(this, new SlicerStorageThresholdEventArgs(level, currentBytes, warning, critical));
        }
    }
}
