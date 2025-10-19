using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Settings;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Farm.Web.Api.Services.Artifacts;

/// <summary>
/// Background service for cleaning up old or excess artifacts based on retention policy.
/// </summary>
public class ArtifactCleanupService : IArtifactCleanupService
{
    private readonly AppDbContext _db;
    private readonly ArtifactStorageSettings _settings;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<ArtifactCleanupService> _logger;

    public ArtifactCleanupService(
        AppDbContext db,
        IOptions<ArtifactStorageSettings> opts,
        IWebHostEnvironment env,
        ILogger<ArtifactCleanupService> logger)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _settings = opts?.Value ?? throw new ArgumentNullException(nameof(opts));
        _env = env ?? throw new ArgumentNullException(nameof(env));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<int> ScanAndCleanupAsync(CancellationToken ct)
    {
        _logger.LogInformation(
            "Starting artifact cleanup scan (DryRun={DryRun}, MaxAgeDays={MaxAgeDays}, MaxTotalBytes={MaxTotalBytes})",
            _settings.EnableCleanupDryRun,
            _settings.MaxAgeDays,
            _settings.MaxTotalBytes);

        var candidatesForDeletion = new System.Collections.Generic.List<Farm.Infrastructure.Domain.Artifact>();

        // Age-based cleanup: find artifacts older than MaxAgeDays
        if (_settings.MaxAgeDays.HasValue && _settings.MaxAgeDays.Value > 0)
        {
            var cutoffDate = DateTime.UtcNow.AddDays(-_settings.MaxAgeDays.Value);
            var oldArtifacts = await _db.Artifacts
                .Where(a => a.CreatedAt < cutoffDate)
                .OrderBy(a => a.CreatedAt)
                .ToListAsync(ct);

            _logger.LogInformation("Found {Count} artifacts older than {Days} days", oldArtifacts.Count, _settings.MaxAgeDays.Value);
            candidatesForDeletion.AddRange(oldArtifacts);
        }

        // Size-based cleanup: if total storage exceeds MaxTotalBytes, delete oldest until under threshold
        if (_settings.MaxTotalBytes.HasValue && _settings.MaxTotalBytes.Value > 0)
        {
            var totalSize = await _db.Artifacts.SumAsync(a => (long?)a.SizeBytes, ct) ?? 0;
            if (totalSize > _settings.MaxTotalBytes.Value)
            {
                _logger.LogInformation("Total storage {TotalSize} exceeds threshold {MaxTotalBytes}, selecting oldest artifacts for cleanup",
                    totalSize, _settings.MaxTotalBytes.Value);

                var allArtifacts = await _db.Artifacts
                    .OrderBy(a => a.CreatedAt)
                    .ToListAsync(ct);

                long runningTotal = totalSize;
                foreach (var artifact in allArtifacts)
                {
                    if (runningTotal <= _settings.MaxTotalBytes.Value) break;
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
            foreach (var artifact in candidatesForDeletion)
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
        foreach (var artifact in candidatesForDeletion)
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
                _db.Artifacts.Remove(artifact);
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

        if (deletedCount > 0)
        {
            await _db.SaveChangesAsync(ct);
            _logger.LogInformation("Successfully deleted {Count} artifacts", deletedCount);
        }

        return deletedCount;
    }
}
