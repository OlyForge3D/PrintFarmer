using Farm.Infrastructure;
using Farm.Infrastructure.Telemetry;
using Farm.Slicer.Worker.Core;
using Microsoft.Extensions.Configuration;

namespace Farm.OrcaSlicer.Worker.Services;

/// <summary>
/// HTTP-based job poller for OrcaSlicer worker.
/// Polls API via POST /api/slice/claim, executes OrcaSlicer pipeline, uploads artifacts, and completes job.
/// Replaces the old Redis-based queue consumer to integrate with the SQL database queue.
/// </summary>
public class QueueConsumerService(
    IHttpClientFactory httpClientFactory,
    IServiceProvider services,
    IUnifiedLoggingService logger,
    IWorkerStateService state,
    IConfiguration config,
    ISlicingPipelineService pipeline) : HttpJobPollerService(httpClientFactory, services, logger, state, config)
{
    private readonly ISlicingPipelineService _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));

    protected override Task<SlicingResult> ExecutePipelineAsync(DistributedSlicingJob job, IServiceProvider scopeServices, CancellationToken ct)
        => _pipeline.ProcessJobAsync(job, ct);

    protected override string[] GetWorkerCapabilities()
        => ["orcaslicer", "stl-processing", "gcode-generation"];
}
