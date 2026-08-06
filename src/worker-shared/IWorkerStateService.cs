namespace Farm.Slicer.Worker.Core;

/// <summary>
/// A lease this worker currently holds over a claimed job.
/// </summary>
/// <param name="Token">Lease token issued by the claim.</param>
/// <param name="Fence">Fencing counter issued by the claim.</param>
/// <param name="ClaimToken">Durable claim-incarnation token issued by the claim.</param>
public readonly record struct WorkerJobLease(Guid Token, long Fence, Guid ClaimToken);

public interface IWorkerStateService
{
    WorkerState GetWorkerState();

    void SetRegisteredService(Guid serviceId, string serviceApiKey);

    void ClearRegisteredService();

    void SetShuttingDown();

    void IncrementActiveJobs();

    void DecrementActiveJobs();

    /// <summary>
    /// Records the lease a claim issued so every subsequent mutation for that job can present it.
    /// </summary>
    /// <param name="jobId">The claimed job.</param>
    /// <param name="lease">The lease token and fencing counter.</param>
    void SetJobLease(Guid jobId, WorkerJobLease lease);

    /// <summary>
    /// Reads the lease held for a job.
    /// </summary>
    /// <param name="jobId">The claimed job.</param>
    /// <param name="lease">The lease when the method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when this worker holds a lease for the job.</returns>
    bool TryGetJobLease(Guid jobId, out WorkerJobLease lease);

    /// <summary>Records the local work directory currently owned by a job attempt.</summary>
    void SetJobWorkDirectory(Guid jobId, string directory);

    /// <summary>Releases the local work directory recorded for a job.</summary>
    void ClearJobWorkDirectory(Guid jobId);

    /// <summary>Returns local work directories owned by currently running job attempts.</summary>
    IReadOnlyCollection<string> GetActiveJobWorkDirectories();

    /// <summary>Releases the lease held for a job.</summary>
    /// <param name="jobId">The job whose lease is finished.</param>
    void ClearJobLease(Guid jobId);
}
