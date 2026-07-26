namespace Farm.Slicer.Worker.Core;

public class WorkerStateService : IWorkerStateService
{
    private readonly WorkerState _state = new();
    private readonly Lock _lock = new();

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
}
