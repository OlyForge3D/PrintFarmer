using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Artifacts;
using Farm.Infrastructure.Settings;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Farm.Web.Api.Services.Artifacts;

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
        _logger.LogInformation(
            "Starting artifact cleanup scan (DryRun={DryRun}, MaxAgeDays={MaxAgeDays}, MaxTotalBytes={MaxTotalBytes})",
            _settings.EnableCleanupDryRun,
            _settings.MaxAgeDays,
            _settings.MaxTotalBytes);

        List<Artifact> candidatesForDeletion = [];

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
                _logger.LogInformation("Total storage {TotalSize} exceeds threshold {MaxTotalBytes}, selecting oldest artifacts for cleanup",
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
        candidatesForDeletion = candidatesForDeletion.Distinct().ToList();

        if (candidatesForDeletion.Count == 0)
        {
            _logger.LogInformation("No artifacts eligible for cleanup");
            return 0;
        }

        _logger.LogInformation("Identified {Count} artifacts for cleanup (dry-run={DryRun})",
            candidatesForDeletion.Count, _settings.EnableCleanupDryRun);

        if (_settings.EnableCleanupDryRun)
        {
            // Dry-run mode: log what would be deleted
            foreach (Artifact artifact in candidatesForDeletion)
            {
                _logger.LogInformation(
                    "[DRY RUN] Would delete artifact {Id} ({Kind}, {SizeBytes} bytes, uploaded {CreatedAt})",
                    artifact.Id,
                    artifact.Kind,
                    artifact.SizeBytes,
                    artifact.CreatedAt);
            }
            return candidatesForDeletion.Count;
        }

        // Actual deletion
        int deletedCount = 0;
        foreach (Artifact artifact in candidatesForDeletion)
        {
            try
            {
                // Delete file from filesystem
                string rootPath = Path.IsPathRooted(_settings.RootPath)
                    ? _settings.RootPath
                    : Path.Combine(_env.ContentRootPath, _settings.RootPath);
                string fullPath = Path.Combine(rootPath, artifact.RelativePath);

                if (File.Exists(fullPath))
                {
                    File.Delete(fullPath);
                    _logger.LogInformation("Deleted artifact file {Path}", fullPath);
                }

                // Remove from database
                _ = await _artifactsRepo.DeleteByIdAsync(artifact.Id, ct);
                deletedCount++;

                _logger.LogInformation(
                    "Deleted artifact {Id} ({Kind}, {SizeBytes} bytes, uploaded {CreatedAt})",
                    artifact.Id,
                    artifact.Kind,
                    artifact.SizeBytes,
                    artifact.CreatedAt);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete artifact {Id}", artifact.Id);
            }
        }

        _logger.LogInformation("Successfully deleted {Count} artifacts", deletedCount);
        return deletedCount;
    }
}
