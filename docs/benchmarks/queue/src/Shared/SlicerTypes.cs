namespace QueueBenchmark.Shared;

/// <summary>
/// Slicing job priority levels
/// </summary>
public enum SlicingJobPriority
{
    Low = 0,
    Normal = 1, 
    High = 2,
    Critical = 3
}

/// <summary>
/// Supported slicer engine types
/// </summary>
public enum SlicerEngineType
{
    PrusaSlicer = 0,
    Cura = 1,
    OrcaSlicer = 2
}