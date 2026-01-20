namespace Farm.Slicer.Worker.Core;

public class WorkerState
{
    public string WorkerId { get; set; } = Environment.MachineName + "-" + Environment.ProcessId;

    public bool IsInitialized { get; set; } = true;

    public bool IsShuttingDown { get; set; }

    public int ActiveJobs { get; set; }

    public int MaxConcurrentJobs { get; set; } = Environment.ProcessorCount;

    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
}
