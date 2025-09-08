namespace Farm.Slicer.Worker.Health;

/// <summary>
/// Worker state information for health checks
/// </summary>
public class WorkerState
{
    public string WorkerId { get; set; } = Environment.MachineName + "-" + Environment.ProcessId;
    public bool IsInitialized { get; set; } = true;
    public bool IsShuttingDown { get; set; }
    public int ActiveJobs { get; set; }
    public int MaxConcurrentJobs { get; set; } = Environment.ProcessorCount;
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Service to track worker state for health checks
/// </summary>
public interface IWorkerStateService
{
    WorkerState GetWorkerState();
    void SetShuttingDown();
    void IncrementActiveJobs();
    void DecrementActiveJobs();
}

/// <summary>
/// Implementation of worker state service
/// </summary>
public class WorkerStateService : IWorkerStateService
{
    private readonly WorkerState _state = new();
    private readonly object _lock = new();

    public WorkerState GetWorkerState()
    {
        lock (_lock)
        {
            return new WorkerState
            {
                WorkerId = _state.WorkerId,
                IsInitialized = _state.IsInitialized,
                IsShuttingDown = _state.IsShuttingDown,
                ActiveJobs = _state.ActiveJobs,
                MaxConcurrentJobs = _state.MaxConcurrentJobs,
                StartedAt = _state.StartedAt
            };
        }
    }

    public void SetShuttingDown()
    {
        lock (_lock)
        {
            _state.IsShuttingDown = true;
        }
    }

    public void IncrementActiveJobs()
    {
        lock (_lock)
        {
            _state.ActiveJobs++;
        }
    }

    public void DecrementActiveJobs()
    {
        lock (_lock)
        {
            if (_state.ActiveJobs > 0)
                _state.ActiveJobs--;
        }
    }
}