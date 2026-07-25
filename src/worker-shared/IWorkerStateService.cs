namespace Farm.Slicer.Worker.Core;

public interface IWorkerStateService
{
    WorkerState GetWorkerState();

    void SetRegisteredService(Guid serviceId, string serviceApiKey);

    void ClearRegisteredService();

    void SetShuttingDown();

    void IncrementActiveJobs();

    void DecrementActiveJobs();
}
