namespace Farm.Slicer.Module.Services;

/// <summary>
/// Event arguments for storage threshold exceeded events.
/// </summary>
/// <param name="level">The severity level reached.</param>
/// <param name="currentBytes">Current total storage in bytes.</param>
/// <param name="warningThreshold">Configured warning threshold in bytes.</param>
/// <param name="criticalThreshold">Configured critical threshold in bytes.</param>
public sealed class SlicerStorageThresholdEventArgs(
    SlicerStorageThresholdLevel level,
    long currentBytes,
    long warningThreshold,
    long criticalThreshold) : EventArgs
{
    /// <summary>Gets the threshold severity level.</summary>
    public SlicerStorageThresholdLevel Level { get; } = level;

    /// <summary>Gets the current total storage in bytes.</summary>
    public long CurrentBytes { get; } = currentBytes;

    /// <summary>Gets the configured warning threshold in bytes.</summary>
    public long WarningThreshold { get; } = warningThreshold;

    /// <summary>Gets the configured critical threshold in bytes.</summary>
    public long CriticalThreshold { get; } = criticalThreshold;
}
