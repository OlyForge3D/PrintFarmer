using Farm.Infrastructure.Domain;
using Farm.Slicer.Module.Domain;

namespace Farm.Web.Api.Services.JobDispatch;

/// <summary>
/// Service for dispatching jobs to available workers based on capabilities and load balancing
/// </summary>
public interface IJobDispatcherService
{
    /// <summary>
    /// Attempt to dispatch the next queued job to an available worker
    /// </summary>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>True if a job was dispatched, false if no suitable worker was found</returns>
    Task<bool> DispatchNextJobAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Dispatch a specific job to the best available worker
    /// </summary>
    /// <param name="jobId">The unique identifier of the job to dispatch.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    Task<bool> DispatchJobAsync(Guid jobId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Find the best worker for a job based on capabilities and load
    /// </summary>
    /// <param name="job">The slice job to find a worker for.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    Task<Worker?> FindBestWorkerForJobAsync(SliceJob job, CancellationToken cancellationToken = default);
}
