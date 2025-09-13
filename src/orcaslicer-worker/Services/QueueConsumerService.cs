using Farm.Slicer.Worker.Core;
using Farm.Web.Shared;
using StackExchange.Redis;

namespace Farm.OrcaSlicer.Worker.Services;

public class QueueConsumerService : BaseQueueConsumerService
{
    private readonly ISlicingPipelineService _pipeline;
    public QueueConsumerService(
        IConnectionMultiplexer redis,
        IProgressReporter progress,
        IServiceProvider services,
    ILogger<QueueConsumerService> logger,
        IWorkerStateService state,
        IConfiguration config,
        ISlicingPipelineService pipeline)
    : base(redis, progress, services, logger, state, "slicer:queue:orcaslicer", "slicer:processing")
    {
        _pipeline = pipeline;
    }

    protected override Task<SlicingResult> ExecutePipelineAsync(DistributedSlicingJob job, IServiceProvider scopeServices, CancellationToken ct)
        => _pipeline.ProcessJobAsync(job, ct);
}
