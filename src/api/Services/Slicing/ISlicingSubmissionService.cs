using System;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Microsoft.AspNetCore.Http;

namespace Farm.Web.Api.Services.Slicing;

/// <summary>
/// Service for handling slicing job submissions
/// </summary>
public interface ISlicingSubmissionService
{
    /// <summary>
    /// Submit a slicing job from an uploaded file
    /// </summary>
    Task<SlicingSubmissionResult> SubmitSlicingJobAsync(
        IFormFile modelFile,
        string slicerEngine,
        Guid printerId,
        SlicerProfileDto profile,
        Guid userId,
        CancellationToken ct);

    /// <summary>
    /// Submit a slicing job from an existing uploaded model
    /// </summary>
    Task<SlicingSubmissionResult> SubmitSlicingJobFromModelAsync(
        Guid modelId,
        string slicerEngine,
        Guid printerId,
        SlicerProfileDto profile,
        Guid userId,
        CancellationToken ct);
}

/// <summary>
/// Result of a slicing job submission
/// </summary>
public record SlicingSubmissionResult(
    bool Success,
    SliceResultDto? Result = null,
    string? Error = null);
