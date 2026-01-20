namespace Farm.Slicer.Worker.Core;

public interface IWorkerStateService
{
    WorkerState GetWorkerState();

    void SetShuttingDown();

    void IncrementActiveJobs();

    void DecrementActiveJobs();
}
