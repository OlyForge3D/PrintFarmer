using System.Security.Cryptography;
using Farm.Infrastructure.Services.StorageManagement;
using Farm.Slicer.Module.Data.Repositories;
using Farm.Slicer.Module.Domain;
using Microsoft.Extensions.Logging;

namespace Farm.Slicer.Module.Services;

/// <summary>
/// Why a resolved model stream could not be produced.
/// </summary>
public enum ModelResolutionFailure
{
    /// <summary>The resolution succeeded.</summary>
    None = 0,

    /// <summary>No stored model exists for the supplied identity.</summary>
    NotFound = 1,

    /// <summary>The requester does not own the stored model.</summary>
    Forbidden = 2,

    /// <summary>The stored metadata exists but the bytes are missing or outside the storage root.</summary>
    BytesUnavailable = 3,

    /// <summary>The stored bytes no longer match the recorded content hash.</summary>
    HashMismatch = 4,
}

/// <summary>
/// A successfully resolved model stream plus the provenance the worker needs.
/// </summary>
/// <param name="Content">Readable stream over the stored bytes. The caller owns disposal.</param>
/// <param name="FileName">Safe download filename.</param>
/// <param name="ContentType">MIME type for the stored bytes.</param>
/// <param name="Sha256">Recorded SHA-256 (hex) of the stored bytes.</param>
/// <param name="SizeBytes">Length of the stored bytes.</param>
public sealed record ResolvedModelContent(
    Stream Content,
    string FileName,
    string ContentType,
    string? Sha256,
    long SizeBytes);

/// <summary>
/// The outcome of a model resolution attempt.
/// </summary>
/// <param name="Failure">The failure reason, or <see cref="ModelResolutionFailure.None"/>.</param>
/// <param name="Content">The resolved content when <paramref name="Failure"/> is none.</param>
public sealed record ModelResolutionResult(
    ModelResolutionFailure Failure,
    ResolvedModelContent? Content)
{
    /// <summary>Whether the resolution produced a stream.</summary>
    public bool Succeeded => Failure == ModelResolutionFailure.None && Content is not null;

    /// <summary>Creates a failed result.</summary>
    /// <param name="failure">The failure reason.</param>
    /// <returns>A failed resolution result.</returns>
    public static ModelResolutionResult Failed(ModelResolutionFailure failure) => new(failure, null);
}

/// <summary>
/// Streams stored model bytes to an authorized consumer by stored identity.
/// </summary>
/// <remarks>
/// This is the only supported way for a worker to obtain model bytes. Caller-supplied URLs and
/// API-local absolute paths are never dereferenced, which removes the SSRF, traversal, local-file
/// read and internal-service probing surface that a free-form model location would create.
/// </remarks>
public interface IModelStorageResolver
{
    /// <summary>
    /// Opens the stored bytes for a model owned by <paramref name="ownerUserId"/>.
    /// </summary>
    /// <param name="model3DId">Stored model identity.</param>
    /// <param name="ownerUserId">The user whose ownership must cover the model.</param>
    /// <param name="expectedSha256">Optional recorded hash that the stored bytes must still match.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The resolution outcome; the caller disposes any returned stream.</returns>
    Task<ModelResolutionResult> OpenAsync(
        Guid model3DId,
        Guid ownerUserId,
        string? expectedSha256,
        CancellationToken ct);

    /// <summary>
    /// Reads the stored provenance for a model without opening its bytes.
    /// </summary>
    /// <param name="model3DId">Stored model identity.</param>
    /// <param name="ownerUserId">The user whose ownership must cover the model.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The stored model when it exists and is owned by the caller; otherwise <see langword="null"/>.</returns>
    Task<Model3D?> FindOwnedAsync(Guid model3DId, Guid ownerUserId, CancellationToken ct);
}

/// <summary>
/// Filesystem-backed <see cref="IModelStorageResolver"/> constrained to the configured model root.
/// </summary>
/// <param name="models">Repository used to resolve stored model metadata by identity.</param>
/// <param name="storagePaths">Supplies the single directory model bytes may be served from.</param>
/// <param name="logger">Diagnostics sink; never logs the resolved absolute path.</param>
public sealed class Model3DStorageResolver(
    IModel3DFileRepository models,
    IStoragePathService storagePaths,
    ILogger<Model3DStorageResolver> logger) : IModelStorageResolver
{
    private readonly IModel3DFileRepository _models = models ?? throw new ArgumentNullException(nameof(models));
    private readonly IStoragePathService _storagePaths = storagePaths ?? throw new ArgumentNullException(nameof(storagePaths));
    private readonly ILogger<Model3DStorageResolver> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc/>
    public async Task<Model3D?> FindOwnedAsync(Guid model3DId, Guid ownerUserId, CancellationToken ct)
    {
        Model3D? model = await _models.GetByIdAsync(model3DId, ct);
        return model is not null && IsOwnedBy(model, ownerUserId) ? model : null;
    }

    /// <inheritdoc/>
    public async Task<ModelResolutionResult> OpenAsync(
        Guid model3DId,
        Guid ownerUserId,
        string? expectedSha256,
        CancellationToken ct)
    {
        Model3D? model = await _models.GetByIdAsync(model3DId, ct);
        if (model is null)
        {
            return ModelResolutionResult.Failed(ModelResolutionFailure.NotFound);
        }

        if (!IsOwnedBy(model, ownerUserId))
        {
            return ModelResolutionResult.Failed(ModelResolutionFailure.Forbidden);
        }

        if (!TryResolveStoredPath(model, out string storedPath))
        {
            return ModelResolutionResult.Failed(ModelResolutionFailure.BytesUnavailable);
        }

        // Integrity gate: the recorded hash is authoritative, so bytes that drifted on disk are
        // refused rather than handed to a slicer worker.
        string? recordedHash = Normalize(expectedSha256) ?? Normalize(model.FileHash);
        if (recordedHash is not null)
        {
            string actualHash = await ComputeSha256Async(storedPath, ct);
            if (!string.Equals(actualHash, recordedHash, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "Stored model {Model3DId} failed its content hash check and was not served",
                    model3DId);
                return ModelResolutionResult.Failed(ModelResolutionFailure.HashMismatch);
            }
        }

        FileStream content = new(storedPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81920, useAsync: true);
        return new ModelResolutionResult(
            ModelResolutionFailure.None,
            new ResolvedModelContent(
                content,
                SafeFileName(model),
                ResolveContentType(model.Name, model.FileName),
                recordedHash,
                content.Length));
    }

    /// <summary>
    /// Computes the SHA-256 (hex) of a stored file.
    /// </summary>
    /// <param name="path">Absolute path already validated to sit inside the model root.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The uppercase hexadecimal digest.</returns>
    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81920, useAsync: true);
        byte[] hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash);
    }

    // Fail closed: a model with no recorded uploader is not "owned by everyone". Only an exact
    // match on the recorded uploader grants access, matching Model3DFileService's ownership check.
    private static bool IsOwnedBy(Model3D model, Guid ownerUserId) =>
        model.UploadedByUserId == ownerUserId;

    private static string? Normalize(string? hash) =>
        string.IsNullOrWhiteSpace(hash) ? null : hash.Trim().Replace("-", string.Empty, StringComparison.Ordinal);

    private bool TryResolveStoredPath(Model3D model, out string storedPath)
    {
        storedPath = string.Empty;
        if (string.IsNullOrWhiteSpace(model.FileName))
        {
            return false;
        }

        string root = Path.GetFullPath(_storagePaths.GetModelUploadDirectory());

        // Model3D.FilePath records the virtual library location ("/" for every uploaded model), not
        // a filesystem directory: the storage owner writes and reads the bytes as
        // Path.Combine(modelUploadRoot, FileName). Joining FilePath here rooted the candidate at the
        // filesystem root, so every model uploaded through the production route resolved outside the
        // storage root and was reported as having no readable bytes.
        // Only the file component of the stored name is honoured, and the final path must remain
        // inside the configured model root even if stored metadata was tampered with.
        string candidate = Path.GetFullPath(Path.Combine(root, Path.GetFileName(model.FileName)));
        string rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!candidate.StartsWith(rootWithSeparator, comparison) || !File.Exists(candidate))
        {
            return false;
        }

        storedPath = candidate;
        return true;
    }

    private static string SafeFileName(Model3D model)
    {
        string source = string.IsNullOrWhiteSpace(model.Name) ? model.FileName : model.Name;
        string baseName = Path.GetFileName(source.Replace('\\', '/'));
        string sanitized = string.Concat(baseName.Select(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_' ? character : '_'));
        return string.IsNullOrWhiteSpace(sanitized) ? "model.stl" : sanitized;
    }

    private static string ResolveContentType(string? displayName, string storedName)
    {
        string extension = Path.GetExtension(
            string.IsNullOrWhiteSpace(displayName) ? storedName : displayName);
        return extension.ToLowerInvariant() switch
        {
            ".stl" => "model/stl",
            ".obj" => "model/obj",
            ".3mf" => "model/3mf",
            _ => "application/octet-stream",
        };
    }
}
