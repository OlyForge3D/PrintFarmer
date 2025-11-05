using System;
using System.Diagnostics.Metrics;
using System.Threading;

namespace Farm.Web.Api.Services.Artifacts;

/// <summary>
/// Metrics instruments for artifact lifecycle. Recorded on upload.
/// </summary>
public sealed class ArtifactsMetrics : IDisposable
{
    // Use a single shared Meter and instruments to avoid duplicate instrument
    // registration across multiple ArtifactsMetrics instances (tests create
    // local instances which previously caused duplicate observable/counter
    // callbacks and inflated measurements).
    private static readonly Meter s_meter = new("PrintFarmer.Artifacts", "1.0.0");
    private static readonly Counter<long> s_uploadedCount = s_meter.CreateCounter<long>(
        "printfarmer.artifacts.uploaded_total",
        description: "Total number of artifacts uploaded");
    private static readonly Histogram<long> s_uploadBytes = s_meter.CreateHistogram<long>(
        "printfarmer.artifacts.upload_bytes",
        unit: "bytes",
        description: "Size of individual artifact uploads");
    private static readonly ObservableGauge<long> s_storageTotalBytes = s_meter.CreateObservableGauge<long>(
        "printfarmer.artifacts.storage_total_bytes",
        ObserveStorageBytes,
        unit: "bytes",
        description: "Approximate total size of stored artifacts (global)");
    private static readonly ObservableGauge<int> s_storageThresholdState = s_meter.CreateObservableGauge<int>(
        "printfarmer.artifacts.storage_threshold_state",
        ObserveThresholdState,
        description: "Storage threshold state: 0=normal, 1=warning, 2=critical");

    // Shared state for storage bytes and thresholds
    private static long s_storageBytes;
    private static long s_warningThreshold;
    private static long s_criticalThreshold;
    private static int s_currentState;

    public Counter<long> UploadedCount => s_uploadedCount;
    public Histogram<long> UploadBytes => s_uploadBytes;
    public ObservableGauge<long> StorageTotalBytes => s_storageTotalBytes;
    public ObservableGauge<int> StorageThresholdState => s_storageThresholdState;

    /// <summary>
    /// Event raised when storage thresholds are crossed.
    /// </summary>
    public event EventHandler<StorageThresholdEventArgs>? ThresholdExceeded;

    /// <summary>
    /// Instance constructor. Ensure static shared state is reset when a
    /// new instance is created so short-lived test instances have deterministic
    /// isolation. This keeps test changes minimal while avoiding global state
    /// leakage between tests.
    /// </summary>
    public ArtifactsMetrics()
    {
        // Clear shared counters/state for the new instance so tests that
        // create short-lived instances don't observe leftovers from prior
        // test runs in the same process.
        ResetForTests();
    }

    private static Measurement<long> ObserveStorageBytes() => new(Interlocked.Read(ref s_storageBytes));
    private static Measurement<int> ObserveThresholdState() => new(Interlocked.CompareExchange(ref s_currentState, 0, 0));

    /// <summary>
    /// Configure storage alert thresholds.
    /// </summary>
    public void SetThresholds(long warningBytes, long criticalBytes)
    {
        Interlocked.Exchange(ref s_warningThreshold, warningBytes);
        Interlocked.Exchange(ref s_criticalThreshold, criticalBytes);
    }

    /// <summary>Record artifact upload metrics (increment counters and update gauge baseline).</summary>
    public void RecordUpload(long sizeBytes)
    {
        s_uploadedCount.Add(1);
        s_uploadBytes.Record(sizeBytes);
        var newTotal = Interlocked.Add(ref s_storageBytes, sizeBytes);
        CheckThresholds(newTotal);
    }

    private void CheckThresholds(long currentBytes)
    {
        var warning = Interlocked.Read(ref s_warningThreshold);
        var critical = Interlocked.Read(ref s_criticalThreshold);

        if (warning <= 0 && critical <= 0)
        {
            return; // Thresholds not configured
        }

        int newState = 0;
        StorageThresholdLevel level = StorageThresholdLevel.Normal;

        if (critical > 0 && currentBytes >= critical)
        {
            newState = 2;
            level = StorageThresholdLevel.Critical;
        }
        else if (warning > 0 && currentBytes >= warning)
        {
            newState = 1;
            level = StorageThresholdLevel.Warning;
        }

        var oldState = Interlocked.Exchange(ref s_currentState, newState);
        if (newState > oldState && newState > 0)
        {
            // Threshold crossed upward - raise event
            ThresholdExceeded?.Invoke(this, new StorageThresholdEventArgs(level, currentBytes, warning, critical));
        }
    }

    /// <summary>
    /// Reset internal shared storage counters. Intended for test usage only to
    /// ensure deterministic test isolation when tests interact with global
    /// metric state. Not recommended for production use.
    /// </summary>
    public static void ResetForTests()
    {
        Interlocked.Exchange(ref s_storageBytes, 0);
        Interlocked.Exchange(ref s_warningThreshold, 0);
        Interlocked.Exchange(ref s_criticalThreshold, 0);
        Interlocked.Exchange(ref s_currentState, 0);
    }

    public void Dispose()
    {
        // Do not dispose the shared static meter - it is process-wide and may
        // be used by other consumers/tests. Keep Dispose a no-op to make it
        // safe to create short-lived instances in unit tests.
    }
}

/// <summary>
/// Storage threshold severity levels.
/// </summary>
public enum StorageThresholdLevel
{
    Normal = 0,
    Warning = 1,
    Critical = 2
}

/// <summary>
/// Event arguments for storage threshold exceeded events.
/// </summary>
public sealed class StorageThresholdEventArgs : EventArgs
{
    public StorageThresholdLevel Level { get; }
    public long CurrentBytes { get; }
    public long WarningThreshold { get; }
    public long CriticalThreshold { get; }

    public StorageThresholdEventArgs(StorageThresholdLevel level, long currentBytes, long warningThreshold, long criticalThreshold)
    {
        Level = level;
        CurrentBytes = currentBytes;
        WarningThreshold = warningThreshold;
        CriticalThreshold = criticalThreshold;
    }
}
