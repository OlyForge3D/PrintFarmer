using Farm.Web.Shared;

namespace Farm.OrcaSlicer.Worker.Services;

public interface ISlicingPipelineService
{
    Task<SlicingPipelineResult> ProcessJobAsync(DistributedSlicingJob job, CancellationToken cancellationToken = default);
}

public interface IProgressReporter
{
    Task ReportProgressAsync(Guid jobId, int progress, string message, CancellationToken cancellationToken = default);
    Task ReportCompletionAsync(DistributedSlicingJob job, SlicingPipelineResult result, CancellationToken cancellationToken = default);
    Task ReportFailureAsync(Guid jobId, string errorMessage, CancellationToken cancellationToken = default);
}

public class SlicingPipelineResult
{
    public string GcodeFileUrl { get; set; } = string.Empty;
    public double EstimatedPrintTimeSeconds { get; set; }
    public double EstimatedFilamentUsageGrams { get; set; }
    public long FileSizeBytes { get; set; }
    public int LayerCount { get; set; }
    public Dictionary<string, object> Metadata { get; set; } = new();
}
