namespace Farm.Slicer.Module.Services;

/// <summary>
/// Storage threshold severity levels.
/// </summary>
public enum StorageThresholdLevel
{
    /// <summary>Normal storage usage.</summary>
    Normal = 0,

    /// <summary>Storage usage exceeds warning threshold.</summary>
    Warning = 1,

    /// <summary>Storage usage exceeds critical threshold.</summary>
    Critical = 2,
}
