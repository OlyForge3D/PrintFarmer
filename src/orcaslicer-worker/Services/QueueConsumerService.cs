using Farm.Infrastructure.Telemetry;
using Farm.Slicer.Worker.Core;
using Farm.Web.Shared;
using Microsoft.Extensions.Configuration;

namespace Farm.OrcaSlicer.Worker.Services;

/// <summary>
/// HTTP-based job poller for OrcaSlicer worker.
/// Polls API via POST /api/slice/claim, executes OrcaSlicer pipeline, uploads artifacts, and completes job.
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
        => ["orcaslicer", "stl-processing", "gcode-generation"];
}
