using Farm.Web.Shared;

namespace Farm.Slicer.Worker.Services;

/// <summary>
/// Service responsible for the STL fetch -> slice -> G-code upload pipeline
/// </summary>
public interface ISlicingPipelineService
{
    /// <summary>
    /// Process a slicing job through the complete pipeline
    /// </summary>
    /// <param name="job">Job to process</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Slicing result with G-code URL and metadata</returns>
    Task<SlicingPipelineResult> ProcessJobAsync(DistributedSlicingJob job, CancellationToken cancellationToken = default);
}

/// <summary>
/// Service for reporting progress and status back to the API gateway
/// </summary>
public interface IProgressReporter
{
    /// <summary>
    /// Report job progress
    /// </summary>
    /// <param name="jobId">Job ID</param>
    /// <param name="progress">Progress percentage (0-100)</param>
    /// <param name="message">Progress message</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task ReportProgressAsync(Guid jobId, int progress, string message, CancellationToken cancellationToken = default);

    /// <summary>
    /// Report job completion
    /// </summary>
    /// <param name="job">Completed job</param>
    /// <param name="result">Slicing result</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task ReportCompletionAsync(DistributedSlicingJob job, SlicingPipelineResult result, CancellationToken cancellationToken = default);

    /// <summary>
    /// Report job failure
    /// </summary>
    /// <param name="jobId">Job ID</param>
    /// <param name="errorMessage">Error message</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task ReportFailureAsync(Guid jobId, string errorMessage, CancellationToken cancellationToken = default);
}

/// <summary>
/// Result of the slicing pipeline
/// </summary>
public class SlicingPipelineResult
{
    public string GcodeFileUrl { get; set; } = string.Empty;
    public double EstimatedPrintTimeSeconds { get; set; }
    public double EstimatedFilamentUsageGrams { get; set; }
    public long FileSizeBytes { get; set; }
    public int LayerCount { get; set; }
    public Dictionary<string, object> Metadata { get; set; } = new();
}