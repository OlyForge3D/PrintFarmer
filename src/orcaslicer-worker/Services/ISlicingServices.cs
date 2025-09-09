using Farm.Web.Shared;

namespace Farm.OrcaSlicer.Worker.Services;

public interface ISlicingPipelineService
{
    Task<SlicingResult> ProcessJobAsync(DistributedSlicingJob job, CancellationToken cancellationToken = default);
}

public interface IProgressReporter
{
    Task ReportProgressAsync(Guid jobId, int progress, string message, CancellationToken cancellationToken = default);
    Task ReportCompletionAsync(DistributedSlicingJob job, SlicingResult result, CancellationToken cancellationToken = default);
    Task ReportFailureAsync(Guid jobId, string errorMessage, CancellationToken cancellationToken = default);
}

// Removed SlicingPipelineResult alias; use SlicingResult directly.
