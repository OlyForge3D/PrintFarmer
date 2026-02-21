using Farm.Slicer.Module.Models;

namespace Farm.Slicer.Worker.Core;

public interface ISlicingPipelineService
{
    Task<SlicingResult> ProcessJobAsync(DistributedSlicingJob job, CancellationToken cancellationToken = default);
}
