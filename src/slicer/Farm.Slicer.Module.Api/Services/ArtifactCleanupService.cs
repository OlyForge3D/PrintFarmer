using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Settings;
using Farm.Slicer.Module.Data.Repositories;
using Farm.Slicer.Module.Domain;
using Farm.Slicer.Module.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Farm.Slicer.Module.Api.Services;

/// <summary>
/// Background service for cleaning up old or excess artifacts based on retention policy.
/// </summary>
public class ArtifactCleanupService(
    IArtifactsRepository artifactsRepo,
    IOptions<ArtifactStorageSettings> opts,
    IWebHostEnvironment env,
    ILogger<ArtifactCleanupService> logger) : IArtifactCleanupService
{
    private readonly IArtifactsRepository _artifactsRepo = artifactsRepo ?? throw new ArgumentNullException(nameof(artifactsRepo));
    private readonly ArtifactStorageSettings _settings = opts?.Value ?? throw new ArgumentNullException(nameof(opts));
    private readonly IWebHostEnvironment _env = env ?? throw new ArgumentNullException(nameof(env));
    private readonly ILogger<ArtifactCleanupService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public async Task<int> ScanAndCleanupAsync(CancellationToken ct)
    {
        string rootPath = ArtifactStorageFileSystem.ResolveRootPath(
            _settings.RootPath,
            _env.ContentRootPath);
        _logger.LogInformation(
            "Starting artifact cleanup scan (DryRun={DryRun}, MaxAgeDays={MaxAgeDays}, MaxTotalBytes={MaxTotalBytes})",
            _settings.EnableCleanupDryRun,
            _settings.MaxAgeDays,
            _settings.MaxTotalBytes);

        int deletedCount = await ReconcileOrphanFilesAsync(rootPath, ct);
        List<Artifact> candidatesForDeletion =
            (await _artifactsRepo.GetCleanupInProgressAsync(ct)).ToList();

        // Age-based cleanup: find artifacts older than MaxAgeDays
        if (_settings.MaxAgeDays.HasValue && _settings.MaxAgeDays.Value > 0)
        {
            DateTime cutoffDate = DateTime.UtcNow.AddDays(-_settings.MaxAgeDays.Value);
            List<Artifact> oldArtifacts = (await _artifactsRepo.GetOlderThanAsync(cutoffDate, ct)).ToList();

            _logger.LogInformation("Found {Count} artifacts older than {Days} days", oldArtifacts.Count, _settings.MaxAgeDays.Value);
            candidatesForDeletion.AddRange(oldArtifacts);
        }

        // Size-based cleanup: if total storage exceeds MaxTotalBytes, delete oldest until under threshold
        if (_settings.MaxTotalBytes.HasValue && _settings.MaxTotalBytes.Value > 0)
        {
            long totalSize = await _artifactsRepo.GetTotalSizeAsync(ct);
            if (totalSize > _settings.MaxTotalBytes.Value)
            {
                _logger.LogInformation(
                    "Total storage {TotalSize} exceeds threshold {MaxTotalBytes}, selecting oldest artifacts for cleanup",
                    totalSize, _settings.MaxTotalBytes.Value);

                List<Artifact> allArtifacts = (await _artifactsRepo.GetAllAsync(ct)).ToList();

                long runningTotal = totalSize;
                foreach (Artifact? artifact in allArtifacts)
                {
                    if (runningTotal <= _settings.MaxTotalBytes.Value)
                    {
                        break;
                    }

                    if (!candidatesForDeletion.Contains(artifact))
                    {
                        candidatesForDeletion.Add(artifact);
                        runningTotal -= artifact.SizeBytes;
                    }
                }
            }
        }

        // Deduplicate candidates
        candidatesForDeletion = candidatesForDeletion
            .DistinctBy(artifact => artifact.Id)
            .ToList();

        if (candidatesForDeletion.Count == 0)
        {
            _logger.LogInformation("No artifacts eligible for cleanup");
            return deletedCount;
        }

        _logger.LogInformation(
            "Identified {Count} artifacts for cleanup (dry-run={DryRun})",
            candidatesForDeletion.Count, _settings.EnableCleanupDryRun);

        if (_settings.EnableCleanupDryRun)
        {
            // Dry-run mode: log what would be deleted
            List<Artifact> eligibleCandidates = candidatesForDeletion
                .Where(artifact => artifact.IsCleanupEligible())
                .ToList();
            foreach (Artifact artifact in eligibleCandidates)
            {
                _logger.LogInformation(
                    "[DRY RUN] Would delete artifact {Id} ({Kind}, {SizeBytes} bytes, uploaded {CreatedAt})",
                    artifact.Id,
                    artifact.Kind,
                    artifact.SizeBytes,
                    artifact.CreatedAt);
            }

            return deletedCount + eligibleCandidates.Count;
        }

        // Actual deletion
        foreach (Artifact artifact in candidatesForDeletion)
        {
            if (!ArtifactStorageFileSystem.TryResolveArtifactPath(
                    rootPath,
                    artifact.RelativePath,
                    out string fullPath))
            {
                _logger.LogWarning(
                    "Skipped artifact {Id} because its path is outside the artifact root or traverses a reparse point",
                    artifact.Id);
                continue;
            }

            Guid reservationToken;
            if (artifact.CleanupReservationToken is Guid inProgressToken &&
                artifact.CleanupDeletionStartedAtUtc.HasValue)
            {
                reservationToken = inProgressToken;
            }
            else
            {
                DateTime reservedAtUtc = DateTime.UtcNow;
                DateTime staleBeforeUtc = reservedAtUtc.AddMinutes(
                    -Math.Max(1, _settings.CleanupReservationTimeoutMinutes));
                reservationToken = Guid.NewGuid();
                bool reserved = await _artifactsRepo.TryReserveForCleanupAsync(
                    artifact.Id,
                    artifact.CleanupReservationToken,
                    artifact.CleanupReservedAtUtc,
                    reservationToken,
                    reservedAtUtc,
                    staleBeforeUtc,
                    ct);
                if (!reserved)
                {
                    _logger.LogDebug(
                        "Skipped artifact {Id} because cleanup or promotion already owns it",
                        artifact.Id);
                    continue;
                }

                bool deletionStarted = await _artifactsRepo.TryBeginCleanupDeletionAsync(
                    artifact.Id,
                    reservationToken,
                    DateTime.UtcNow,
                    ct);
                if (!deletionStarted)
                {
                    await _artifactsRepo.ReleaseCleanupReservationAsync(
                        artifact.Id,
                        reservationToken,
                        CancellationToken.None);
                    _logger.LogWarning(
                        "Cleanup reservation for artifact {Id} was lost before byte deletion began",
                        artifact.Id);
                    continue;
                }
            }

            try
            {
                // File.Delete is idempotent for a missing path and throws for access or filesystem
                // failures. File.Exists cannot distinguish those failures from absence.
                try
                {
                    DeleteArtifactFile(rootPath, fullPath);
                    _logger.LogInformation("Deleted artifact file {Path}", fullPath);
                }
                catch (Exception ex) when (
                    ex is FileNotFoundException or DirectoryNotFoundException)
                {
                    _logger.LogDebug(
                        "Artifact file {Path} was already absent during cleanup",
                        fullPath);
                }

                if (!await _artifactsRepo.FinalizeCleanupAsync(
                        artifact.Id,
                        reservationToken,
                        ct))
                {
                    _logger.LogDebug(
                        "Cleanup metadata for artifact {Id} was already finalized by another pass",
                        artifact.Id);
                    continue;
                }

                deletedCount++;
                _logger.LogInformation(
                    "Deleted artifact {Id} ({Kind}, {SizeBytes} bytes, uploaded {CreatedAt})",
                    artifact.Id,
                    artifact.Kind,
                    artifact.SizeBytes,
                    artifact.CreatedAt);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogError(
                    ex,
                    "Failed to delete artifact {Id}; durable cleanup state will be retried",
                    artifact.Id);
            }
        }

        _logger.LogInformation("Successfully deleted {Count} artifacts", deletedCount);
        return deletedCount;
    }

    private async Task<int> ReconcileOrphanFilesAsync(
        string rootPath,
        CancellationToken ct)
    {
        if (!Directory.Exists(rootPath))
        {
            return 0;
        }

        DateTime staleBeforeUtc = DateTime.UtcNow.AddMinutes(
            -Math.Max(1, _settings.CleanupReservationTimeoutMinutes));
        bool usesCaseInsensitivePaths = OperatingSystem.IsWindows();
        StringComparer pathComparer = usesCaseInsensitivePaths
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        StringComparison pathComparison = usesCaseInsensitivePaths
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        int reconciledCount = 0;
        foreach (string fullPath in ArtifactStorageFileSystem.EnumerateRegularFiles(rootPath))
        {
            ct.ThrowIfCancellationRequested();
            string relativePath =
                ArtifactStorageFileSystem.GetRelativePath(rootPath, fullPath);

            string? parentPath = Path.GetDirectoryName(fullPath);
            bool isDirectStagingFile = string.Equals(
                parentPath,
                ArtifactStorageFileSystem.GetStagingDirectory(rootPath),
                pathComparison);
            Guid artifactId = Guid.Empty;
            bool isDirectPublishedLease =
                string.Equals(parentPath, rootPath, pathComparison) &&
                string.Equals(
                    Path.GetExtension(fullPath),
                    ArtifactStorageFileSystem.LeaseFileExtension,
                    StringComparison.Ordinal) &&
                ArtifactStorageFileSystem.TryGetStagingArtifactId(
                    fullPath,
                    out artifactId);
            if (isDirectStagingFile)
            {
                if (!ArtifactStorageFileSystem.TryGetStagingArtifactId(
                        fullPath,
                        out artifactId))
                {
                    _logger.LogWarning(
                        "Preserving unexpected artifact staging entry {Path}",
                        fullPath);
                    continue;
                }
            }
            else if (isDirectPublishedLease)
            {
                // The artifact ID was parsed while classifying the root lease.
            }
            else if (!ArtifactStorageFileSystem.TryGetProtocolArtifactId(
                         fullPath,
                         out artifactId))
            {
                _logger.LogWarning(
                    "Preserving unexpected artifact storage entry {Path}",
                    fullPath);
                continue;
            }

            if (!TryGetStableFileSnapshot(
                    rootPath,
                    fullPath,
                    staleBeforeUtc,
                    out FileSnapshot snapshot))
            {
                continue;
            }

            string leasePath = isDirectStagingFile
                ? ArtifactStorageFileSystem.GetStagingLeasePath(
                    rootPath,
                    artifactId)
                : ArtifactStorageFileSystem.GetPublishedLeasePath(
                    rootPath,
                    artifactId);
            if (!TryAcquireInactiveLease(
                    leasePath,
                    staleBeforeUtc,
                    out FileStream? leaseStream))
            {
                continue;
            }

            try
            {
                if (!TryGetStableFileSnapshot(
                        rootPath,
                        fullPath,
                        staleBeforeUtc,
                        out FileSnapshot currentSnapshot) ||
                    currentSnapshot != snapshot)
                {
                    continue;
                }

                if (!isDirectStagingFile)
                {
                    Artifact? committed =
                        await _artifactsRepo.GetByIdAsync(artifactId, ct);
                    if (committed is not null &&
                        pathComparer.Equals(
                            committed.RelativePath.Replace('\\', '/'),
                            relativePath))
                    {
                        continue;
                    }
                }

                if (_settings.EnableCleanupDryRun)
                {
                    _logger.LogInformation(
                        "[DRY RUN] Would reconcile stale orphan artifact file {Path}",
                        fullPath);
                    reconciledCount++;
                    continue;
                }

                bool deletingLeaseFile = string.Equals(
                    fullPath,
                    leasePath,
                    pathComparison);
                if (deletingLeaseFile)
                {
                    if (leaseStream is not null)
                    {
                        await leaseStream.DisposeAsync();
                    }

                    leaseStream = null;
                }

                try
                {
                    DeleteArtifactFile(rootPath, fullPath);
                    reconciledCount++;
                    _logger.LogInformation(
                        "Reconciled stale orphan artifact file {Path}",
                        fullPath);
                }
                catch (Exception exception) when (
                    exception is FileNotFoundException or DirectoryNotFoundException)
                {
                    _logger.LogDebug(
                        "Orphan artifact file {Path} was already absent during reconciliation",
                        fullPath);
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(
                        exception,
                        "Could not reconcile stale orphan artifact file {Path}",
                        fullPath);
                }
            }
            finally
            {
                if (leaseStream is not null)
                {
                    await leaseStream.DisposeAsync();
                }
            }
        }

        return reconciledCount;
    }

    private static bool TryGetStableFileSnapshot(
        string rootPath,
        string fullPath,
        DateTime staleBeforeUtc,
        out FileSnapshot snapshot)
    {
        snapshot = default;
        string relativePath =
            ArtifactStorageFileSystem.GetRelativePath(rootPath, fullPath);
        if (!ArtifactStorageFileSystem.TryResolveArtifactPath(
                rootPath,
                relativePath,
                out string resolvedPath))
        {
            return false;
        }

        var file = new FileInfo(resolvedPath);
        try
        {
            file.Refresh();
            if (!file.Exists ||
                ArtifactStorageFileSystem.IsReparsePoint(file) ||
                file.LastWriteTimeUtc > staleBeforeUtc)
            {
                return false;
            }

            snapshot = new FileSnapshot(
                file.Length,
                file.LastWriteTimeUtc);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool TryAcquireInactiveLease(
        string leasePath,
        DateTime staleBeforeUtc,
        out FileStream? leaseStream)
    {
        leaseStream = null;
        var leaseFile = new FileInfo(leasePath);
        try
        {
            leaseFile.Refresh();
            if (!leaseFile.Exists)
            {
                return true;
            }

            if (ArtifactStorageFileSystem.IsReparsePoint(leaseFile) ||
                leaseFile.LastWriteTimeUtc > staleBeforeUtc)
            {
                return false;
            }

            leaseStream = new FileStream(
                leasePath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None);
            if (!ArtifactStorageFileSystem.TryAcquireExclusiveLeaseLock(
                    leaseStream))
            {
                leaseStream.Dispose();
                leaseStream = null;
                return false;
            }

            return true;
        }
        catch (FileNotFoundException)
        {
            leaseStream?.Dispose();
            leaseStream = null;
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            leaseStream?.Dispose();
            leaseStream = null;
            return false;
        }
    }

    private readonly record struct FileSnapshot(
        long Length,
        DateTime LastWriteTimeUtc);

    protected virtual bool ArtifactFileExists(string path) => File.Exists(path);

    protected virtual void DeleteArtifactFile(string rootPath, string path) =>
        ArtifactStorageFileSystem.DeleteFileNoFollow(rootPath, path);
}
