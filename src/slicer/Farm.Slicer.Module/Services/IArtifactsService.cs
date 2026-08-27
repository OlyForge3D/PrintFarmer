using Farm.Slicer.Module.Domain;
using Microsoft.AspNetCore.Http;

namespace Farm.Slicer.Module.Services;

/// <summary>
/// An open read stream over stored artifact bytes together with the artifact metadata that describes
/// them. The storage path is deliberately not part of the contract.
/// </summary>
public sealed class ArtifactContentStream : IAsyncDisposable
{
    private readonly Stream _content;

    private ArtifactContentStream(Artifact artifact, Stream content)
    {
        Artifact = artifact;
        _content = content;
    }

    /// <summary>Gets the artifact metadata row.</summary>
    public Artifact Artifact { get; }

    /// <summary>Gets the open read-only stream over the stored bytes.</summary>
    public Stream Content => _content;

    /// <summary>Opens artifact bytes and takes ownership of the resulting stream.</summary>
    /// <param name="artifact">The artifact metadata row.</param>
    /// <param name="openContent">Factory that opens the stored bytes.</param>
    /// <returns>An owned content stream that disposes the bytes with the instance.</returns>
    public static ArtifactContentStream Open(Artifact artifact, Func<Stream> openContent)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(openContent);
        return new ArtifactContentStream(artifact, openContent());
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => _content.DisposeAsync();
}

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

    /// <summary>Uploads a worker artifact only while the worker still owns an unexpired job lease.</summary>
    Task<Artifact?> UploadForActiveLeaseAsync(
        IFormFile file,
        Guid jobId,
        Guid workerId,
        Guid claimToken,
        string kind,

        CancellationToken ct);

    /// <summary>
    /// Uploads an artifact whose kind, MIME type, size and SHA-256 are verified before the row is
    /// persisted. Bytes that fail verification are discarded and no artifact is created.
    /// </summary>
    /// <param name="file">The uploaded file.</param>
    /// <param name="jobId">The job ID this artifact belongs to.</param>
    /// <param name="workerId">The worker that produced this artifact.</param>
    /// <param name="kind">The canonical artifact kind.</param>
    /// <param name="declaredSha256">SHA-256 (hex) declared by the producer.</param>
    /// <param name="declaredSizeBytes">Optional byte count declared by the producer.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The persisted artifact.</returns>
    /// <exception cref="ArtifactValidationException">
    /// Thrown when the kind, MIME type, size or digest does not match what was declared.
    /// </exception>
    /// <example>
    /// <code>
    /// Artifact artifact = await artifacts.UploadVerifiedAsync(
    ///     file, jobId, worker.Id, SlicerArtifactKinds.Gcode, declaredSha256, file.Length, ct);
    /// </code>
    /// </example>
    Task<Artifact> UploadVerifiedAsync(
        IFormFile file,
        Guid jobId,
        Guid workerId,
        string kind,
        string? declaredSha256,
        long? declaredSizeBytes,
        CancellationToken ct);

    /// <summary>
    /// Verifies and uploads a worker artifact only while the worker still owns an active lease.
    /// </summary>
    Task<Artifact?> UploadVerifiedForActiveLeaseAsync(
        IFormFile file,
        Guid jobId,
        Guid workerId,
        Guid claimToken,
        string kind,
        string? declaredSha256,
        long? declaredSizeBytes,
        CancellationToken ct);

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

    /// <summary>
    /// Opens a read-only stream over the stored artifact bytes for server-side consumers.
    /// </summary>
    /// <param name="id">The artifact identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// The artifact and an open read stream, or <see langword="null"/> when the artifact row or its
    /// bytes are missing. Callers own the returned instance and never see the storage path.
    /// </returns>
    /// <example>
    /// <code>
    /// await using ArtifactContentStream? content = await artifacts.OpenReadStreamAsync(artifactId, ct);
    /// </code>
    /// </example>
    Task<ArtifactContentStream?> OpenReadStreamAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Checks whether an artifact's backing file is present on disk, without exposing the storage
    /// path to the caller. Prefer this over resolving <see cref="GetWithPathAsync"/> and calling
    /// file-system APIs directly from a controller.
    /// </summary>
    /// <param name="id">The artifact identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><see langword="true"/> when the artifact row and its file both exist.</returns>
    Task<bool> ArtifactFileExistsAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Reads the full contents of an artifact's backing file, without exposing the storage path to
    /// the caller.
    /// </summary>
    /// <param name="id">The artifact identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The file bytes, or <see langword="null"/> when the artifact row or its file is missing.</returns>
    Task<byte[]?> ReadArtifactBytesAsync(Guid id, CancellationToken ct);

    /// <summary>Persist a text payload as an artifact of the specified kind.</summary>
    /// <param name="content">The text content to persist.</param>
    /// <param name="fileName">The name for the artifact file.</param>
    /// <param name="jobId">The job ID this artifact belongs to.</param>
    /// <param name="workerId">The optional worker ID that produced this artifact.</param>
    /// <param name="kind">The kind/type of artifact.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Artifact> UploadTextAsync(string content, string fileName, Guid jobId, Guid? workerId, string kind, CancellationToken ct);
}
