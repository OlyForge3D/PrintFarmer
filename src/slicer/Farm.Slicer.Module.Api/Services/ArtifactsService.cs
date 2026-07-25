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

    public async Task<Artifact> UploadAsync(IFormFile file, Guid jobId, Guid? workerId, string kind, CancellationToken ct)
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
            throw new InvalidOperationException("Unsupported artifact kind.");
        }

        return await PersistAsync(file, jobId, workerId, kind, declaredSha256: null, ct);
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
            throw new ArtifactValidationException(
                ArtifactValidationException.UnsupportedKind,
                "The artifact kind is not accepted by this deployment.");
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

        if (declaredSizeBytes.HasValue && declaredSizeBytes.Value != file.Length)
        {
            throw new ArtifactValidationException(
                ArtifactValidationException.SizeMismatch,
                "The declared artifact size does not match the uploaded bytes.");
        }

        string? normalizedDeclaredHash = NormalizeHash(declaredSha256);
        return await PersistAsync(file, jobId, workerId, kind, normalizedDeclaredHash, ct);
    }

    private async Task<Artifact> PersistAsync(
        IFormFile file,
        Guid jobId,
        Guid? workerId,
        string kind,
        string? declaredSha256,
        CancellationToken ct)
    {
        string sanitized = SanitizeFileName(file.FileName);
        string root = ResolveRootPath();
        DateTime now = DateTime.UtcNow;
        string folder = Path.Combine(root, now.Year.ToString(), now.Month.ToString("00"), now.Day.ToString("00"), jobId.ToString());
        _ = Directory.CreateDirectory(folder);
        Guid artifactId = Guid.NewGuid();
        string targetFileName = artifactId.ToString() + "-" + sanitized; // ensure uniqueness even if same original name
        string fullPath = Path.Combine(folder, targetFileName);

        string sha256;
        await using (FileStream target = File.Create(fullPath))
        await using (Stream input = file.OpenReadStream())
        using (SHA256 hasher = SHA256.Create())
        {
            sha256 = await CopyAndHashAsync(input, target, hasher, ct);
        }

        // Integrity gate: a mismatch never becomes a persisted artifact, so completion cannot
        // reference bytes that differ from what the worker claims it produced.
        if (declaredSha256 is not null &&
            !string.Equals(declaredSha256, sha256, StringComparison.OrdinalIgnoreCase))
        {
            TryDeleteQuietly(fullPath);
            _metrics.RecordUploadRejected();
            throw new ArtifactValidationException(
                ArtifactValidationException.HashMismatch,
                "The declared artifact digest does not match the uploaded bytes.");
        }

        string relativePath = Path.GetRelativePath(root, fullPath).Replace(Path.DirectorySeparatorChar, '/');
        Artifact artifact = new Artifact
        {
            Id = artifactId,
            JobId = jobId,
            WorkerId = workerId,
            Kind = kind,
            FileName = sanitized,
            RelativePath = relativePath,
            ContentType = file.ContentType ?? "application/octet-stream",
            SizeBytes = file.Length,
            Sha256 = sha256,
            DeclaredSha256 = declaredSha256,
            CreatedAt = now
        };

        _ = await _artifactsRepo.AddAsync(artifact, ct);
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

    private static string? NormalizeHash(string? hash) =>
        string.IsNullOrWhiteSpace(hash)
            ? null
            : hash.Trim().Replace("-", string.Empty, StringComparison.Ordinal);

    private static void TryDeleteQuietly(string fullPath)
    {
        try
        {
            File.Delete(fullPath);
        }
        catch (IOException exception)
        {
            // Orphan bytes are reclaimed by ArtifactCleanupService; a delete failure must not mask
            // the integrity error that the caller needs to see.
            System.Diagnostics.Debug.WriteLine(exception.Message);
        }
        catch (UnauthorizedAccessException exception)
        {
            System.Diagnostics.Debug.WriteLine(exception.Message);
        }
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
        string folder = Path.Combine(root, now.Year.ToString(), now.Month.ToString("00"), now.Day.ToString("00"), jobId.ToString());
        _ = Directory.CreateDirectory(folder);
        Guid artifactId = Guid.NewGuid();
        string targetFileName = artifactId.ToString() + "-" + sanitized;
        string fullPath = Path.Combine(folder, targetFileName);

        string sha256;
        await using (FileStream target = File.Create(fullPath))
        using (SHA256 hasher = SHA256.Create())
        {
            // Write and hash
            _ = hasher.TransformBlock(bytes, 0, bytes.Length, null, 0);
            await target.WriteAsync(bytes.AsMemory(0, bytes.Length), ct);
            _ = hasher.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            sha256 = Convert.ToHexString(hasher.Hash!);
        }

        string relativePath = Path.GetRelativePath(root, fullPath).Replace(Path.DirectorySeparatorChar, '/');
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

        _ = await _artifactsRepo.AddAsync(artifact, ct);
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
        string fullPath = Path.Combine(root, artifact.RelativePath.Replace('/', Path.DirectorySeparatorChar));
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
        string root = _settings.RootPath;
        if (!Path.IsPathFullyQualified(root))
        {
            root = Path.Combine(_env.ContentRootPath, root);
        }

        return root;
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
