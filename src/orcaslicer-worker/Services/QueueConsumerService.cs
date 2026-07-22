using Farm.Infrastructure.Telemetry;
using Farm.Slicer.Module.Models;
using Farm.Slicer.Worker.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Farm.OrcaSlicer.Worker.Services;

/// <summary>
/// HTTP-based job poller for OrcaSlicer worker.
/// Polls API via POST /api/slice/claim, executes OrcaSlicer pipeline, uploads artifacts, and completes job.
/// Replaces the old Redis-based queue consumer to integrate with the SQL database queue.
/// </summary>
public class QueueConsumerService(
    IHttpClientFactory httpClientFactory,
    IServiceProvider services,
    ILogger<QueueConsumerService> logger,
    IWorkerStateService state,
    IConfiguration config) : HttpJobPollerService(httpClientFactory, services, logger, state, config)
{
    protected override Task<SlicingResult> ExecutePipelineAsync(DistributedSlicingJob job, IServiceProvider scopeServices, CancellationToken ct)
    {
        ISlicingPipelineService pipeline = scopeServices.GetRequiredService<ISlicingPipelineService>();
        return pipeline.ProcessJobAsync(job, ct);
    }

    protected override string[] GetWorkerCapabilities()
        => ["orcaslicer", "stl-processing", "gcode-generation"];
}
