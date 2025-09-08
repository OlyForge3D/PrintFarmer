namespace Farm.OrcaSlicer.Worker.Health;

public interface IWorkerStateService
{
    DateTime StartTime { get; }
}

public class WorkerStateService : IWorkerStateService
{
    public DateTime StartTime { get; } = DateTime.UtcNow;
}
