using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace Farm.Web.Api.Services.Artifacts;

public interface IArtifactsService
{
    Task<Farm.Infrastructure.Domain.Artifact> UploadAsync(IFormFile file, Guid jobId, Guid? workerId, string kind, CancellationToken ct);

    Task<Farm.Infrastructure.Domain.Artifact?> GetAsync(Guid id, CancellationToken ct);

    Task<IReadOnlyList<Farm.Infrastructure.Domain.Artifact>> ListByJobAsync(Guid jobId, CancellationToken ct);

    /// <summary>
    /// Resolve full filesystem path for an artifact (returns null if not found).
    /// </summary>
    /// <param name="id">The unique identifier of the artifact.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    Task<(Farm.Infrastructure.Domain.Artifact Artifact, string FullPath)?> GetWithPathAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Persist a text payload as an artifact of the specified kind. Useful for inline log completion data.
    /// </summary>
    /// <param name="content">The text content to persist.</param>
    /// <param name="fileName">The name for the artifact file.</param>
    /// <param name="jobId">The job ID this artifact belongs to.</param>
    /// <param name="workerId">The optional worker ID that produced this artifact.</param>
    /// <param name="kind">The kind/type of artifact.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    Task<Farm.Infrastructure.Domain.Artifact> UploadTextAsync(string content, string fileName, Guid jobId, Guid? workerId, string kind, CancellationToken ct);
}
