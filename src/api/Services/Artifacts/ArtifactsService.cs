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
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Farm.Web.Api.Services.Artifacts;

/// <summary>
/// Local filesystem implementation of artifact persistence. Files are stored under a configured root path.
/// </summary>
public class ArtifactsService : IArtifactsService
{
    private readonly IWebHostEnvironment _env;
    private readonly Farm.Infrastructure.Data.AppDbContext _db;
    private readonly ArtifactStorageSettings _settings;
    private static readonly Regex FileNameSafeRegex = new("[^a-zA-Z0-9._-]+", RegexOptions.Compiled);

    private readonly ArtifactsMetrics _metrics;
    public ArtifactsService(IWebHostEnvironment env, Farm.Infrastructure.Data.AppDbContext db, IOptions<ArtifactStorageSettings> opts, ArtifactsMetrics metrics)
    {
        _env = env ?? throw new ArgumentNullException(nameof(env));
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _settings = opts?.Value ?? throw new ArgumentNullException(nameof(opts));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
    }

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
            CreatedAt = now
        };

        _ = _db.Set<Artifact>().Add(artifact);
        _ = await _db.SaveChangesAsync(ct);
        _metrics.RecordUpload(file.Length);

        // Increment worker artifact counters if available
        if (workerId.HasValue)
        {
            Worker? worker = await _db.Set<Worker>().FirstOrDefaultAsync(w => w.Id == workerId.Value, ct);
            if (worker != null)
            {
                worker.ArtifactsProduced++;
                worker.ArtifactBytesProduced += file.Length;
                _ = await _db.SaveChangesAsync(ct);
            }
        }

        return artifact;
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

        _ = _db.Set<Artifact>().Add(artifact);
        _ = await _db.SaveChangesAsync(ct);
        _metrics.RecordUpload(bytes.Length);

        if (workerId.HasValue)
        {
            Worker? worker = await _db.Set<Worker>().FirstOrDefaultAsync(w => w.Id == workerId.Value, ct);
            if (worker != null)
            {
                worker.ArtifactsProduced++;
                worker.ArtifactBytesProduced += bytes.Length;
                _ = await _db.SaveChangesAsync(ct);
            }
        }

        return artifact;
    }

    public async Task<Artifact?> GetAsync(Guid id, CancellationToken ct)
    {
        return await _db.Set<Artifact>().FirstOrDefaultAsync(a => a.Id == id, ct);
    }

    public async Task<IReadOnlyList<Artifact>> ListByJobAsync(Guid jobId, CancellationToken ct)
    {
        return await _db.Set<Artifact>().Where(a => a.JobId == jobId).OrderByDescending(a => a.CreatedAt).ToListAsync(ct);
    }

    public async Task<(Artifact artifact, string fullPath)?> GetWithPathAsync(Guid id, CancellationToken ct)
    {
        Artifact? artifact = await _db.Set<Artifact>().FirstOrDefaultAsync(a => a.Id == id, ct);
        if (artifact == null)
        {
            return null;
        }

        string root = ResolveRootPath();
        string fullPath = Path.Combine(root, artifact.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        return (artifact, fullPath);
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
