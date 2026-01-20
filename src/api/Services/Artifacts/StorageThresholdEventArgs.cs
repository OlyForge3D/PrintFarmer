namespace Farm.Web.Api.Services.Artifacts;

/// <summary>
/// Event arguments for storage threshold exceeded events.
/// </summary>
public sealed class StorageThresholdEventArgs(StorageThresholdLevel level, long currentBytes, long warningThreshold, long criticalThreshold) : EventArgs
{
    public StorageThresholdLevel Level { get; } = level;

    public long CurrentBytes { get; } = currentBytes;

    public long WarningThreshold { get; } = warningThreshold;

    public long CriticalThreshold { get; } = criticalThreshold;
}
