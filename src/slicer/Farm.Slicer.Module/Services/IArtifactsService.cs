using Farm.Slicer.Module.Domain;
using Microsoft.AspNetCore.Http;

namespace Farm.Slicer.Module.Services;

/// <summary>
/// Service for managing build artifacts (sliced G-code, logs, reports) associated with jobs.
/// </summary>
public interface IArtifactsService
{
    /// <summary>Uploads a file as an artifact associated with a job.</summary>
    /// <param name="file">The uploaded file.</param>
    /// <param name="jobId">The job ID this artifact belongs to.</param>
    /// <param name="workerId">The optional worker ID that produced this artifact.</param>
    /// <param name="kind">The kind/type of artifact (e.g., "gcode", "log", "thumbnail").</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Artifact> UploadAsync(IFormFile file, Guid jobId, Guid? workerId, string kind, CancellationToken ct);

    /// <summary>Gets an artifact by its ID.</summary>
    /// <param name="id">The artifact ID.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Artifact?> GetAsync(Guid id, CancellationToken ct);

    /// <summary>Lists all artifacts for a specific job.</summary>
    /// <param name="jobId">The job ID to list artifacts for.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<Artifact>> ListByJobAsync(Guid jobId, CancellationToken ct);

    /// <summary>Resolve full filesystem path for an artifact (returns null if not found).</summary>
    /// <param name="id">The unique identifier of the artifact.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<(Artifact Artifact, string FullPath)?> GetWithPathAsync(Guid id, CancellationToken ct);

    /// <summary>Persist a text payload as an artifact of the specified kind.</summary>
    /// <param name="content">The text content to persist.</param>
    /// <param name="fileName">The name for the artifact file.</param>
    /// <param name="jobId">The job ID this artifact belongs to.</param>
    /// <param name="workerId">The optional worker ID that produced this artifact.</param>
    /// <param name="kind">The kind/type of artifact.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Artifact> UploadTextAsync(string content, string fileName, Guid jobId, Guid? workerId, string kind, CancellationToken ct);
}
