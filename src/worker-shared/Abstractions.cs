using Farm.Web.Shared;
using Microsoft.Extensions.Configuration;

namespace Farm.Slicer.Worker.Core;

public interface ISlicingPipelineService
{
    Task<SlicingResult> ProcessJobAsync(DistributedSlicingJob job, CancellationToken cancellationToken = default);
}

public interface IProgressReporter
{
    Task ReportProgressAsync(Guid jobId, int progress, string message, CancellationToken cancellationToken = default);
    Task ReportCompletionAsync(DistributedSlicingJob job, SlicingResult result, CancellationToken cancellationToken = default);
    Task ReportFailureAsync(Guid jobId, string errorMessage, CancellationToken cancellationToken = default);
}

public interface IWorkerStateService
{
    WorkerState GetWorkerState();
    void SetShuttingDown();
    void IncrementActiveJobs();
    void DecrementActiveJobs();
}

public record WorkerQueueOptions(string QueueKey, string ProcessingKey)
{
    public static WorkerQueueOptions From(IConfiguration config, string @defaultQueue, string @defaultProcessing)
    {
        ArgumentNullException.ThrowIfNull(config);
        return new(
            config["Worker:Queue:Key"] ?? @defaultQueue,
            config["Worker:Queue:ProcessingKey"] ?? @defaultProcessing
        );
    }
}

public class WorkerState
{
    public string WorkerId { get; set; } = Environment.MachineName + "-" + Environment.ProcessId;
    public bool IsInitialized { get; set; } = true;
    public bool IsShuttingDown { get; set; }
    public int ActiveJobs { get; set; }
    public int MaxConcurrentJobs { get; set; } = Environment.ProcessorCount;
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
}

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
    public void SetShuttingDown() { lock (_lock) { _state.IsShuttingDown = true; } }
    public void IncrementActiveJobs() { lock (_lock) { _state.ActiveJobs++; } }
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

public static class WorkerIdentity
{
    public static string Create() => Environment.MachineName + "-" + Environment.ProcessId;
}

/// <summary>
/// Generic interface for slicer profile discovery services.
/// Each slicer worker implements this interface to expose profiles from its local installation.
/// </summary>
public interface ISlicerProfilesService
{
    /// <summary>
    /// Discover and list all available machine profiles from the slicer's local installation.
    /// </summary>
    Task<IList<MachineProfileDto>> ListAvailableMachineProfilesAsync(CancellationToken ct = default);

    /// <summary>
    /// Discover and list all available filament profiles from the slicer's local installation.
    /// </summary>
    Task<IList<FilamentProfileDto>> ListAvailableFilamentProfilesAsync(CancellationToken ct = default);

    /// <summary>
    /// Discover and list all available process profiles from the slicer's local installation.
    /// </summary>
    Task<IList<ProcessProfileDto>> ListAvailableProcessProfilesAsync(CancellationToken ct = default);
}
