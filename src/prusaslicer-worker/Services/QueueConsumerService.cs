using Farm.Infrastructure.Telemetry;
using Farm.Slicer.Worker.Core; // base consumer + abstractions
using Farm.Infrastructure;
using Microsoft.Extensions.Configuration;

namespace Farm.PrusaSlicer.Worker.Services;

/// <summary>
/// HTTP-based job poller for PrusaSlicer worker.
/// Polls API via POST /api/slice/claim, executes PrusaSlicer pipeline, uploads artifacts, and completes job.
/// Replaces the old Redis-based queue consumer to integrate with the SQL database queue.
/// </summary>
public class QueueConsumerService : HttpJobPollerService
{
    private readonly ISlicingPipelineService _pipeline;

    public QueueConsumerService(
        IHttpClientFactory httpClientFactory,
        IServiceProvider services,
        IUnifiedLoggingService logger,
        IWorkerStateService state,
        IConfiguration config,
        ISlicingPipelineService pipeline)
        : base(httpClientFactory, services, logger, state, config)
    {
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
    }

    protected override Task<SlicingResult> ExecutePipelineAsync(DistributedSlicingJob job, IServiceProvider scopeServices, CancellationToken ct)
        => _pipeline.ProcessJobAsync(job, ct);

    protected override string[] GetWorkerCapabilities()
        => ["prusaslicer", "stl-processing", "gcode-generation"];
}
