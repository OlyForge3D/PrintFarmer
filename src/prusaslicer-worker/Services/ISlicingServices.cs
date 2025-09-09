using Farm.Web.Shared;

namespace Farm.PrusaSlicer.Worker.Services;

/// <summary>
/// Service responsible for the STL fetch -> slice -> G-code upload pipeline
/// </summary>
public interface ISlicingPipelineService
{
    Task<SlicingResult> ProcessJobAsync(DistributedSlicingJob job, CancellationToken cancellationToken = default);
}

/// <summary>
/// Service for reporting progress and status back to the API gateway
/// </summary>
public interface IProgressReporter
{
    Task ReportProgressAsync(Guid jobId, int progress, string message, CancellationToken cancellationToken = default);
    Task ReportCompletionAsync(DistributedSlicingJob job, SlicingResult result, CancellationToken cancellationToken = default);
    Task ReportFailureAsync(Guid jobId, string errorMessage, CancellationToken cancellationToken = default);
}

/// <summary>
/// Result of the slicing pipeline
/// </summary>
// Removed SlicingPipelineResult alias; use SlicingResult directly.