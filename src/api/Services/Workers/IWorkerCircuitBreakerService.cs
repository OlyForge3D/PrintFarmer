using Farm.Slicer.Module.Data.Repositories;

namespace Farm.Web.Api.Services.Workers;

/// <summary>
/// Service for managing circuit breaker state for slicer workers.
/// Prevents repeated job submissions to failing workers.
/// </summary>
public interface IWorkerCircuitBreakerService
{
    /// <summary>Records a job failure for a worker, potentially opening the circuit.</summary>
    Task RecordJobFailureAsync(Guid workerId, IWorkerRepository workerRepo, CancellationToken ct = default);

    /// <summary>Records a job success for a worker, potentially closing the circuit.</summary>
    Task RecordJobSuccessAsync(Guid workerId, IWorkerRepository workerRepo, CancellationToken ct = default);

    /// <summary>Checks and updates circuit states based on time-based recovery rules.</summary>
    void CheckCircuits();

    /// <summary>Gets the current circuit state for a worker.</summary>
    CircuitState GetCircuitState(Guid workerId);

    /// <summary>Manually resets a worker's circuit to closed state.</summary>
    void ResetCircuit(Guid workerId);
}
