using System;
using System.Diagnostics.Metrics;
using System.Threading;

namespace Farm.Web.Api.Services.Artifacts;

/// <summary>
/// Metrics instruments for artifact lifecycle. Recorded on upload.
/// </summary>
public sealed class ArtifactsMetrics : IDisposable
{
    private readonly Meter _meter;
    private long _storageBytes;
    private bool _disposed;

    public Counter<long> UploadedCount { get; }
    public Histogram<long> UploadBytes { get; }
    public ObservableGauge<long> StorageTotalBytes { get; }
    public ObservableGauge<int> StorageThresholdState { get; }

    /// <summary>
    /// Event raised when storage thresholds are crossed.
    /// </summary>
    public event EventHandler<StorageThresholdEventArgs>? ThresholdExceeded;

    private long _warningThreshold;
    private long _criticalThreshold;
    private int _currentState; // 0=normal, 1=warning, 2=critical

    public ArtifactsMetrics()
    {
        _meter = new Meter("PrintFarmer.Artifacts", "1.0.0");
        UploadedCount = _meter.CreateCounter<long>(
            "printfarmer.artifacts.uploaded_total",
            description: "Total number of artifacts uploaded");
        UploadBytes = _meter.CreateHistogram<long>(
            "printfarmer.artifacts.upload_bytes",
            unit: "bytes",
            description: "Size of individual artifact uploads");
        StorageTotalBytes = _meter.CreateObservableGauge<long>(
            "printfarmer.artifacts.storage_total_bytes",
            ObserveStorageBytes,
            unit: "bytes",
            description: "Approximate total size of stored artifacts (local session)");
        StorageThresholdState = _meter.CreateObservableGauge<int>(
            "printfarmer.artifacts.storage_threshold_state",
            ObserveThresholdState,
            description: "Storage threshold state: 0=normal, 1=warning, 2=critical");
    }

    private Measurement<long> ObserveStorageBytes() => new(Interlocked.Read(ref _storageBytes));
    private Measurement<int> ObserveThresholdState() => new(Interlocked.CompareExchange(ref _currentState, 0, 0));

    /// <summary>
    /// Configure storage alert thresholds.
    /// </summary>
    public void SetThresholds(long warningBytes, long criticalBytes)
    {
        Interlocked.Exchange(ref _warningThreshold, warningBytes);
        Interlocked.Exchange(ref _criticalThreshold, criticalBytes);
    }

    /// <summary>Record artifact upload metrics (increment counters and update gauge baseline).</summary>
    public void RecordUpload(long sizeBytes)
    {
        UploadedCount.Add(1);
        UploadBytes.Record(sizeBytes);
        var newTotal = Interlocked.Add(ref _storageBytes, sizeBytes);
        CheckThresholds(newTotal);
    }

    private void CheckThresholds(long currentBytes)
    {
        var warning = Interlocked.Read(ref _warningThreshold);
        var critical = Interlocked.Read(ref _criticalThreshold);

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

        var oldState = Interlocked.Exchange(ref _currentState, newState);
        if (newState > oldState && newState > 0)
        {
            // Threshold crossed upward - raise event
            ThresholdExceeded?.Invoke(this, new StorageThresholdEventArgs(level, currentBytes, warning, critical));
        }
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
