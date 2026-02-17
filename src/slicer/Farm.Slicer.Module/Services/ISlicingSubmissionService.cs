using Farm.Slicer.Module.Dtos;
using Microsoft.AspNetCore.Http;

namespace Farm.Slicer.Module.Services;

/// <summary>
/// Result of a slicing job submission.
/// </summary>
/// <param name="Success">Whether the submission succeeded.</param>
/// <param name="Result">The slice result if successful.</param>
/// <param name="Error">Error message if the submission failed.</param>
public record SlicingSubmissionResult(
    bool Success,
    SliceResultDto? Result = null,
    string? Error = null);

/// <summary>
/// Service for handling slicing job submissions.
/// </summary>
public interface ISlicingSubmissionService
{
    /// <summary>
    /// Submit a slicing job from an uploaded file.
    /// </summary>
    /// <param name="modelFile">The uploaded model file to slice.</param>
    /// <param name="slicerEngine">The slicer engine to use (e.g., OrcaSlicer, PrusaSlicer).</param>
    /// <param name="printerId">The target printer ID.</param>
    /// <param name="profile">The slicer profile configuration.</param>
    /// <param name="userId">The user ID submitting the job.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Result containing success status and job details or error.</returns>
    Task<SlicingSubmissionResult> SubmitSlicingJobAsync(
        IFormFile modelFile,
        string slicerEngine,
        Guid printerId,
        SlicerProfileDto profile,
        Guid userId,
        CancellationToken ct);

    /// <summary>
    /// Submit a slicing job from an existing uploaded model.
    /// </summary>
    /// <param name="modelId">The ID of the existing model to slice.</param>
    /// <param name="slicerEngine">The slicer engine to use (e.g., OrcaSlicer, PrusaSlicer).</param>
    /// <param name="printerId">The target printer ID.</param>
    /// <param name="profile">The slicer profile configuration.</param>
    /// <param name="userId">The user ID submitting the job.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Result containing success status and job details or error.</returns>
    Task<SlicingSubmissionResult> SubmitSlicingJobFromModelAsync(
        Guid modelId,
        string slicerEngine,
        Guid printerId,
        SlicerProfileDto profile,
        Guid userId,
        CancellationToken ct);
}
