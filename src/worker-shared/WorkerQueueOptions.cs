using Microsoft.Extensions.Configuration;

namespace Farm.Slicer.Worker.Core;

public record WorkerQueueOptions(string QueueKey, string ProcessingKey)
{
    public static WorkerQueueOptions From(IConfiguration config, string @defaultQueue, string @defaultProcessing)
    {
        ArgumentNullException.ThrowIfNull(config);
        return new(
            config["Worker:Queue:Key"] ?? @defaultQueue,
            config["Worker:Queue:ProcessingKey"] ?? @defaultProcessing);
    }
}
