using Farm.Slicer.Module.Models;

namespace Farm.Slicer.Worker.Core;

public interface IProgressReporter
{
    Task ReportProgressAsync(Guid jobId, int progress, string message, CancellationToken cancellationToken = default);

    Task ReportCompletionAsync(DistributedSlicingJob job, SlicingResult result, CancellationToken cancellationToken = default);

    Task ReportFailureAsync(Guid jobId, string errorMessage, CancellationToken cancellationToken = default);
}
