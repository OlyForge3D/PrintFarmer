namespace Farm.Slicer.Worker.Core;

public class WorkerStateService : IWorkerStateService
{
    private readonly WorkerState _state = new();
    private readonly Lock _lock = new();

    /// <summary>Leases currently held by this worker, keyed by job.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, WorkerJobLease> _leases = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, string> _jobWorkDirectories = new();

    public WorkerState GetWorkerState()
    {
        lock (_lock)
        {
            return new WorkerState
            {
                WorkerId = _state.WorkerId,
                RegisteredServiceId = _state.RegisteredServiceId,
                RegisteredServiceApiKey = _state.RegisteredServiceApiKey,
                IsInitialized = _state.IsInitialized,
                IsShuttingDown = _state.IsShuttingDown,
                ActiveJobs = _state.ActiveJobs,
                MaxConcurrentJobs = _state.MaxConcurrentJobs,
                StartedAt = _state.StartedAt
            };
        }
    }

    public void SetRegisteredService(Guid serviceId, string serviceApiKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceApiKey);
        lock (_lock)
        {
            _state.RegisteredServiceId = serviceId;
            _state.RegisteredServiceApiKey = serviceApiKey;
        }
    }

    public void ClearRegisteredService()
    {
        lock (_lock)
        {
            _state.RegisteredServiceId = null;
            _state.RegisteredServiceApiKey = null;
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
            {
                _state.ActiveJobs--;
            }
        }
    }

    /// <inheritdoc/>
    public void SetJobLease(Guid jobId, WorkerJobLease lease) => _leases[jobId] = lease;

    /// <inheritdoc/>
    public bool TryGetJobLease(Guid jobId, out WorkerJobLease lease) => _leases.TryGetValue(jobId, out lease);

    public void SetJobWorkDirectory(Guid jobId, string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _jobWorkDirectories[jobId] = directory;
    }

    public bool TryGetJobWorkDirectory(Guid jobId, out string directory) =>
        _jobWorkDirectories.TryGetValue(jobId, out directory!);

    public void ClearJobWorkDirectory(Guid jobId) => _jobWorkDirectories.TryRemove(jobId, out _);

    public IReadOnlyCollection<string> GetActiveJobWorkDirectories() => _jobWorkDirectories.Values.ToArray();

    /// <inheritdoc/>
    public void ClearJobLease(Guid jobId) => _leases.TryRemove(jobId, out _);
}
