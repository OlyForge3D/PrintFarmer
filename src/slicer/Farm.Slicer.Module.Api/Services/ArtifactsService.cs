using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Settings;
using Farm.Slicer.Module.Data.Repositories;
using Farm.Slicer.Module.Domain;
using Farm.Slicer.Module.Services;
using Farm.Slicer.Module.Services.Metrics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Farm.Slicer.Module.Api.Services;

/// <summary>
/// Local filesystem implementation of artifact persistence. Files are stored under a configured root path.
/// </summary>
public class ArtifactsService(IWebHostEnvironment env, IArtifactsRepository artifactsRepo, IOptions<ArtifactStorageSettings> opts, ArtifactsMetrics metrics) : IArtifactsService
{
    private readonly IWebHostEnvironment _env = env ?? throw new ArgumentNullException(nameof(env));
    private readonly IArtifactsRepository _artifactsRepo = artifactsRepo ?? throw new ArgumentNullException(nameof(artifactsRepo));
    private readonly ArtifactStorageSettings _settings = opts?.Value ?? throw new ArgumentNullException(nameof(opts));
    private static readonly Regex FileNameSafeRegex = new("[^a-zA-Z0-9._-]+", RegexOptions.Compiled);

    private readonly ArtifactsMetrics _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));

    public async Task<Artifact> UploadAsync(
        IFormFile file,
        Guid jobId,
        Guid? workerId,
        string kind,
        CancellationToken ct)
    {
        ValidateUpload(file, kind, declaredSizeBytes: null, requireVerification: false);
        return await PersistAsync(
            file,
            jobId,
            workerId,
            claimToken: null,
            kind,
            declaredSha256: null,
            requireActiveLease: false,
            ct)
            ?? throw new InvalidOperationException("The artifact could not be persisted.");
    }

    public async Task<Artifact?> UploadForActiveLeaseAsync(
        IFormFile file,
        Guid jobId,
        Guid workerId,
        Guid claimToken,
        string kind,
        CancellationToken ct)
    {
        ValidateUpload(file, kind, declaredSizeBytes: null, requireVerification: false);
        return await PersistAsync(
            file,
            jobId,
            workerId,
            claimToken,
            kind,
            declaredSha256: null,
            requireActiveLease: true,
            ct);
    }

    /// <inheritdoc/>
    public async Task<Artifact> UploadVerifiedAsync(
        IFormFile file,
        Guid jobId,
        Guid workerId,
        string kind,
        string? declaredSha256,
        long? declaredSizeBytes,
        CancellationToken ct)
    {
        ValidateUpload(file, kind, declaredSizeBytes, requireVerification: true);
        string normalizedSha256 = NormalizeRequiredHash(declaredSha256);
        return await PersistAsync(
            file,
            jobId,
            workerId,
            claimToken: null,
            kind,
            normalizedSha256,
            requireActiveLease: false,
            ct)
            ?? throw new InvalidOperationException("The artifact could not be persisted.");
    }

    /// <inheritdoc/>
    public async Task<Artifact?> UploadVerifiedForActiveLeaseAsync(
        IFormFile file,
        Guid jobId,
        Guid workerId,
        Guid claimToken,
        string kind,
        string? declaredSha256,
        long? declaredSizeBytes,
        CancellationToken ct)
    {
        ValidateUpload(file, kind, declaredSizeBytes, requireVerification: true);
        string normalizedSha256 = NormalizeRequiredHash(declaredSha256);
        return await PersistAsync(
            file,
            jobId,
            workerId,
            claimToken,
            kind,
            normalizedSha256,
            requireActiveLease: true,
            ct);
    }

    private async Task<Artifact?> PersistAsync(
        IFormFile file,
        Guid jobId,
        Guid? workerId,
        Guid? claimToken,
        string kind,
        string? declaredSha256,
        bool requireActiveLease,
        CancellationToken ct)
    {
        string sanitized = SanitizeFileName(file.FileName);
        string root = ResolveRootPath();
        DateTime now = DateTime.UtcNow;
        string folder = ArtifactStorageFileSystem.EnsureArtifactRoot(root);
        Guid artifactId = Guid.NewGuid();
        string targetFileName = artifactId + "-" + sanitized; // ensure uniqueness even if same original name
        string fullPath = Path.Join(folder, targetFileName);

        bool persisted = false;
        using (ArtifactWriteLease writeLease =
               ArtifactWriteLease.Create(root, artifactId))
        {
            string sha256;
            FileStream target = writeLease.OpenStagingStream();
            await using (Stream input = file.OpenReadStream())
            using (SHA256 hasher = SHA256.Create())
            {
                sha256 = await CopyAndHashAsync(input, target, hasher, ct);
                await target.FlushAsync(ct);
            }

            if (declaredSha256 is not null &&
                !string.Equals(declaredSha256, sha256, StringComparison.OrdinalIgnoreCase))
            {
                _metrics.RecordUploadRejected();
                throw new ArtifactValidationException(
                    ArtifactValidationException.HashMismatch,
                    "The declared artifact digest does not match the uploaded bytes.");
            }

            writeLease.Publish(root, fullPath, DateTime.UtcNow);
            string relativePath =
                ArtifactStorageFileSystem.GetRelativePath(root, fullPath);
            Artifact artifact = new Artifact
            {
                Id = artifactId,
                JobId = jobId,
                WorkerId = workerId,
                ClaimToken = claimToken,
                Kind = kind,
                FileName = sanitized,
                RelativePath = relativePath,
                ContentType = file.ContentType ?? "application/octet-stream",
                SizeBytes = file.Length,
                Sha256 = sha256,
                DeclaredSha256 = declaredSha256,
                CreatedAt = now
            };

            persisted = await PersistArtifactMetadataAsync(
                artifact,
                writeLease,
                workerId,
                claimToken,
                requireActiveLease,
                ct);

            if (!persisted)
            {
                return null;
            }

            writeLease.Commit();
            _metrics.RecordUpload(file.Length);

            // Increment worker artifact counters if available
            if (workerId.HasValue)
            {
                Worker? worker = await _artifactsRepo.GetWorkerByIdAsync(workerId.Value, ct);
                if (worker != null)
                {
                    worker.ArtifactsProduced++;
                    worker.ArtifactBytesProduced += file.Length;
                    await _artifactsRepo.UpdateWorkerAsync(worker, ct);
                }
            }

            return artifact;
        }
    }

    private void ValidateUpload(
        IFormFile file,
        string kind,
        long? declaredSizeBytes,
        bool requireVerification)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (file.Length <= 0)
        {
            throw new InvalidOperationException("Empty file not allowed.");
        }

        if (file.Length > _settings.MaxFileSizeBytes)
        {
            throw new InvalidOperationException("File exceeds maximum size.");
        }

        if (!IsAllowedKind(kind))
        {
            if (requireVerification)
            {
                throw new ArtifactValidationException(
                    ArtifactValidationException.UnsupportedKind,
                    "The artifact kind is not accepted by this deployment.");
            }

            throw new InvalidOperationException("Unsupported artifact kind.");
        }

        if (!requireVerification)
        {
            return;
        }

        IReadOnlyList<string> acceptedMimeTypes = SlicerArtifactKinds.AcceptedMimeTypes(kind);
        string contentType = string.IsNullOrWhiteSpace(file.ContentType)
            ? "application/octet-stream"
            : file.ContentType.Split(';')[0].Trim();
        if (acceptedMimeTypes.Count > 0 &&
            !acceptedMimeTypes.Contains(contentType, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArtifactValidationException(
                ArtifactValidationException.UnsupportedMediaType,
                "The artifact media type is not accepted for this artifact kind.");
        }

        if (requireVerification &&
            (!declaredSizeBytes.HasValue || declaredSizeBytes.Value < 0))
        {
            throw new ArtifactValidationException(
                ArtifactValidationException.SizeMismatch,
                "A valid declared artifact size is required for a verified upload.");
        }

        if (declaredSizeBytes.HasValue && declaredSizeBytes.Value != file.Length)
        {
            throw new ArtifactValidationException(
                ArtifactValidationException.SizeMismatch,
                "The declared artifact size does not match the uploaded bytes.");
        }
    }

    private static string NormalizeRequiredHash(string? hash)
    {
        if (string.IsNullOrWhiteSpace(hash))
        {
            throw new ArtifactValidationException(
                ArtifactValidationException.InvalidHash,
                "A SHA-256 digest is required for a verified artifact upload.");
        }

        string normalized = hash.Trim().Replace("-", string.Empty, StringComparison.Ordinal);
        if (normalized.Length != 64 || normalized.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArtifactValidationException(
                ArtifactValidationException.InvalidHash,
                "The declared artifact digest must be a 64-character hexadecimal SHA-256 value.");
        }

        return normalized;
    }

    public async Task<Artifact> UploadTextAsync(string content, string fileName, Guid jobId, Guid? workerId, string kind, CancellationToken ct)
    {
        if (content == null)
        {
            content = string.Empty;
        }

        byte[] bytes = Encoding.UTF8.GetBytes(content);
        if (bytes.Length == 0)
        {
            throw new InvalidOperationException("Empty content not allowed.");
        }

        if (bytes.Length > _settings.MaxFileSizeBytes)
        {
            throw new InvalidOperationException("Content exceeds maximum size.");
        }

        if (!IsAllowedKind(kind))
        {
            throw new InvalidOperationException("Unsupported artifact kind.");
        }

        string sanitized = SanitizeFileName(string.IsNullOrWhiteSpace(fileName) ? "artifact.txt" : fileName);
        string root = ResolveRootPath();
        DateTime now = DateTime.UtcNow;
        string folder = ArtifactStorageFileSystem.EnsureArtifactRoot(root);
        Guid artifactId = Guid.NewGuid();
        string targetFileName = artifactId + "-" + sanitized;
        string fullPath = Path.Join(folder, targetFileName);

        using (ArtifactWriteLease writeLease =
               ArtifactWriteLease.Create(root, artifactId))
        {
            string sha256;
            FileStream target = writeLease.OpenStagingStream();
            using (SHA256 hasher = SHA256.Create())
            {
                // Write and hash
                _ = hasher.TransformBlock(bytes, 0, bytes.Length, null, 0);
                await target.WriteAsync(bytes.AsMemory(0, bytes.Length), ct);
                await target.FlushAsync(ct);
                _ = hasher.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                sha256 = Convert.ToHexString(hasher.Hash!);
            }

            writeLease.Publish(root, fullPath, DateTime.UtcNow);
            string relativePath =
                ArtifactStorageFileSystem.GetRelativePath(root, fullPath);
            Artifact artifact = new Artifact
            {
                Id = artifactId,
                JobId = jobId,
                WorkerId = workerId,
                Kind = kind,
                FileName = sanitized,
                RelativePath = relativePath,
                ContentType = "text/plain",
                SizeBytes = bytes.Length,
                Sha256 = sha256,
                CreatedAt = now
            };

            _ = await PersistArtifactMetadataAsync(
                artifact,
                writeLease,
                workerId,
                claimToken: null,
                requireActiveLease: false,
                ct);
            writeLease.Commit();
            _metrics.RecordUpload(bytes.Length);

            if (workerId.HasValue)
            {
                Worker? worker = await _artifactsRepo.GetWorkerByIdAsync(workerId.Value, ct);
                if (worker != null)
                {
                    worker.ArtifactsProduced++;
                    worker.ArtifactBytesProduced += bytes.Length;
                    await _artifactsRepo.UpdateWorkerAsync(worker, ct);
                }
            }

            return artifact;
        }
    }

    private async Task<bool> PersistArtifactMetadataAsync(
        Artifact artifact,
        ArtifactWriteLease writeLease,
        Guid? workerId,
        Guid? claimToken,
        bool requireActiveLease,
        CancellationToken ct)
    {
        try
        {
            if (requireActiveLease)
            {
                if (workerId is not Guid activeWorkerId)
                {
                    throw new ArgumentException(
                        "An active-lease upload requires a worker identifier.",
                        nameof(workerId));
                }

                return await _artifactsRepo.TryAddForActiveLeaseAsync(
                    artifact,
                    activeWorkerId,
                    claimToken ?? Guid.Empty,
                    ct);
            }

            _ = await _artifactsRepo.AddAsync(artifact, ct);
            return true;
        }
        catch
        {
            try
            {
                Artifact? committedArtifact = await _artifactsRepo.GetByIdAsync(
                    artifact.Id,
                    CancellationToken.None);
                if (committedArtifact is not null)
                {
                    writeLease.PreservePublishedForReconciliation();
                }
            }
            catch
            {
                // A failed commit probe is ambiguous, so retain bytes for startup reconciliation.
                writeLease.PreservePublishedForReconciliation();
            }

            throw;
        }
    }

    public async Task<Artifact?> GetAsync(Guid id, CancellationToken ct)
    {
        return await _artifactsRepo.GetByIdAsync(id, ct);
    }

    public async Task<IReadOnlyList<Artifact>> ListByJobAsync(Guid jobId, CancellationToken ct)
    {
        return await _artifactsRepo.GetByJobIdAsync(jobId, ct);
    }

    public async Task<(Artifact Artifact, string FullPath)?> GetWithPathAsync(Guid id, CancellationToken ct)
    {
        Artifact? artifact = await _artifactsRepo.GetByIdAsync(id, ct);
        if (artifact == null)
        {
            return null;
        }

        string root = ResolveRootPath();
        if (!ArtifactStorageFileSystem.TryResolveArtifactPath(
                root,
                artifact.RelativePath,
                out string fullPath))
        {
            return null;
        }

        return (artifact, fullPath);
    }

    /// <inheritdoc/>
    public async Task<ArtifactContentStream?> OpenReadStreamAsync(Guid id, CancellationToken ct)
    {
        (Artifact Artifact, string FullPath)? resolved = await GetWithPathAsync(id, ct);
        if (resolved is null || !File.Exists(resolved.Value.FullPath))
        {
            return null;
        }

        string fullPath = resolved.Value.FullPath;
        return ArtifactContentStream.Open(
            resolved.Value.Artifact,
            () => new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 81920,
                useAsync: true));
    }

    private string ResolveRootPath()
    {
        return ArtifactStorageFileSystem.ResolveRootPath(
            _settings.RootPath,
            _env.ContentRootPath);
    }

    private static string SanitizeFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return "artifact.bin";
        }

        string cleaned = FileNameSafeRegex.Replace(fileName.Trim(), "-");
        return cleaned.Length > 128 ? cleaned.Substring(0, 128) : cleaned;
    }

    private bool IsAllowedKind(string kind)
    {
        if (string.IsNullOrWhiteSpace(kind))
        {
            return false;
        }

        string[] allowed = _settings.AllowedKinds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return allowed.Contains(kind, StringComparer.OrdinalIgnoreCase);
    }

    private static async Task<string> CopyAndHashAsync(Stream input, Stream target, HashAlgorithm hasher, CancellationToken ct)
    {
        byte[] buffer = new byte[81920];
        int read;
        while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), ct);
            _ = hasher.TransformBlock(buffer, 0, read, null, 0);
        }

        _ = hasher.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(hasher.Hash!);
    }
}
