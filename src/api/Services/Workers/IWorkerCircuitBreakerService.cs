using Farm.Infrastructure.Repositories.Workers;

namespace Farm.Web.Api.Services.Workers;

public interface IWorkerCircuitBreakerService
{
    Task RecordJobFailureAsync(Guid workerId, IWorkerRepository workerRepo, CancellationToken ct = default);

    Task RecordJobSuccessAsync(Guid workerId, IWorkerRepository workerRepo, CancellationToken ct = default);

    void CheckCircuits();

    CircuitState GetCircuitState(Guid workerId);

    void ResetCircuit(Guid workerId);
}
