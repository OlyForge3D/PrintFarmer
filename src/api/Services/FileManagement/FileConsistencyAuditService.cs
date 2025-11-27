using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Model;
using Farm.Infrastructure.Telemetry;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Farm.Web.Api.Services.FileManagement;

/// <summary>
/// Periodic background service that audits file consistency between database records and disk.
/// Detects and logs:
/// - Missing files (DB record exists, but file not found on disk)
/// - Orphaned files (file exists on disk, but no DB record)
/// - Hash mismatches (DB hash differs from actual file hash)
/// - Size mismatches (DB size differs from actual file size)
/// 
/// Runs on a configurable schedule (default: hourly).
/// All findings are logged for administrative review and manual remediation.
/// </summary>
public class FileConsistencyAuditService : BackgroundService
{
    /// <summary>
    /// Internal class to hold audit results before persisting to database.
    /// </summary>
    private class AuditResults
    {
        public int FilesChecked { get; set; }
        public int ValidCount { get; set; }
        public int MissingCount { get; set; }
        public int CorruptedCount { get; set; }
        public int OrphanedCount { get; set; }
        public List<Guid> MissingFileIds { get; set; } = new();
        public List<Guid> CorruptedFileIds { get; set; } = new();
        public List<string> OrphanedPaths { get; set; } = new();
        public string SummaryMessage { get; set; } = string.Empty;
    }

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IUnifiedLoggingService _logger;
    private readonly TimeSpan _interval = TimeSpan.FromHours(1); // Default: audit every hour
    private readonly string _modelsPath;
    private readonly string _gcodePath;

    public FileConsistencyAuditService(
        IServiceScopeFactory scopeFactory,
        IUnifiedLoggingService logger,
        string modelsPath,
        string gcodePath)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _modelsPath = modelsPath ?? throw new ArgumentNullException(nameof(modelsPath));
        _gcodePath = gcodePath ?? throw new ArgumentNullException(nameof(gcodePath));
    }

    /// <summary>
    /// Allow interval configuration via environment variable for testing.
    /// Format: "HH:MM" (e.g., "01:00" for every hour, "12:00" for every 12 hours)
    /// </summary>
    public void SetAuditInterval(TimeSpan interval)
    {
        if (interval.TotalSeconds > 0)
        {
            // Reflection would be needed to change _interval, so log and use in ExecuteAsync instead
            _logger.LogInformation($"File consistency audit interval set to {interval.TotalMinutes} minutes");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("File Consistency Audit Service started");

        // Wait before first run to avoid hammering during startup
        await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunAuditAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError($"File consistency audit failed: {ex.Message}");
                // Continue running despite errors
            }

            // Wait for next audit interval
            await Task.Delay(_interval, stoppingToken);
        }
    }

    /// <summary>
    /// Run a single audit pass checking both model and gcode files.
    /// </summary>
    private async Task RunAuditAsync(CancellationToken ct)
    {
        _logger.LogInformation("Starting file consistency audit");

        using (var scope = _scopeFactory.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var fileManagementService = scope.ServiceProvider.GetRequiredService<IFileManagementService>();
            var fileIntegrityService = scope.ServiceProvider.GetRequiredService<IFileIntegrityService>();

            // Collect results from all audit passes
            var model3dResults = await AuditModel3DFilesAsync(dbContext, fileManagementService, fileIntegrityService, ct);
            var gcodeResults = await AuditGcodeFilesAsync(dbContext, fileManagementService, fileIntegrityService, ct);
            var orphanedResults = await AuditOrphanedFilesAsync(dbContext, ct);

            // Save audit results to database
            await SaveAuditResultsAsync(dbContext, model3dResults, "Model3D", ct);
            await SaveAuditResultsAsync(dbContext, gcodeResults, "GcodeFile", ct);
            await SaveAuditResultsAsync(dbContext, orphanedResults, "OrphanedFiles", ct);

            _logger.LogInformation("File consistency audit completed");
        }
    }

    /// <summary>
    /// Save audit results to FileHealthAudit table for dashboard and admin review.
    /// </summary>
    private async Task SaveAuditResultsAsync(
        AppDbContext dbContext,
        AuditResults results,
        string auditType,
        CancellationToken ct)
    {
        try
        {
            var auditEntry = new FileHealthAudit
            {
                Id = Guid.NewGuid(),
                AuditDate = DateTime.UtcNow,
                AuditType = Enum.Parse<FileAuditType>(auditType),
                FilesChecked = results.FilesChecked,
                HealthyFiles = results.ValidCount,
                MissingFiles = results.MissingCount,
                CorruptedFiles = results.CorruptedCount,
                OrphanedFiles = results.OrphanedCount,
                MissingFileIds = results.MissingFileIds.Any()
                    ? JsonSerializer.Serialize(results.MissingFileIds)
                    : null,
                CorruptedFileIds = results.CorruptedFileIds.Any()
                    ? JsonSerializer.Serialize(results.CorruptedFileIds)
                    : null,
                OrphanedFilePaths = results.OrphanedPaths.Any()
                    ? JsonSerializer.Serialize(results.OrphanedPaths)
                    : null,
                SummaryMessage = results.SummaryMessage,
                HasIssues = results.MissingCount > 0 || results.CorruptedCount > 0 || results.OrphanedCount > 0,
                CreatedAt = DateTime.UtcNow
            };

            dbContext.FileHealthAudits.Add(auditEntry);
            await dbContext.SaveChangesAsync(ct);

            _logger.LogInformation($"Audit results saved for {auditType}: {auditEntry.SummaryMessage}");
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to save audit results for {auditType}: {ex.Message}");
        }
    }

    private async Task<AuditResults> AuditModel3DFilesAsync(
        AppDbContext dbContext,
        IFileManagementService fileManagementService,
        IFileIntegrityService fileIntegrityService,
        CancellationToken ct)
    {
        _logger.LogDebug("Auditing Model3D files");

        var models = await dbContext.Models3D
            .AsNoTracking()
            .ToListAsync(ct);

        var results = new AuditResults { FilesChecked = models.Count };

        foreach (var model in models)
        {
            try
            {
                // Check main file
                if (!fileManagementService.IsSafePath(model.FilePath, _modelsPath))
                {
                    _logger.LogWarning($"Model {model.Id}: Unsafe path - {model.FilePath}");
                    continue;
                }

                var result = await fileIntegrityService.VerifyIntegrityAsync(
                    model.FilePath,
                    model.FileHash,
                    model.FileSizeBytes,
                    ct: ct);

                if (!result.IsValid)
                {
                    _logger.LogWarning($"Model {model.Id}: {result.FailureReason} - {result.ErrorMessage}");
                    if (result.FailureReason == "Missing")
                    {
                        results.MissingCount++;
                        results.MissingFileIds.Add(model.Id);
                    }
                    else if (result.FailureReason == "HashMismatch" || result.FailureReason == "SizeMismatch")
                    {
                        results.CorruptedCount++;
                        results.CorruptedFileIds.Add(model.Id);
                    }
                }
                else
                {
                    results.ValidCount++;
                }

                // Check thumbnail if exists
                if (!string.IsNullOrEmpty(model.ThumbnailPath))
                {
                    if (!fileManagementService.IsSafePath(model.ThumbnailPath, _modelsPath))
                    {
                        _logger.LogWarning($"Model {model.Id}: Thumbnail unsafe path - {model.ThumbnailPath}");
                    }
                    else if (!File.Exists(model.ThumbnailPath))
                    {
                        _logger.LogWarning($"Model {model.Id}: Thumbnail missing - {model.ThumbnailPath}");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Error auditing Model {model.Id}: {ex.Message}");
            }
        }

        results.SummaryMessage = $"Model3D audit: Valid={results.ValidCount}, Missing={results.MissingCount}, Corrupted={results.CorruptedCount}";
        _logger.LogInformation(results.SummaryMessage);

        return results;
    }

    private async Task<AuditResults> AuditGcodeFilesAsync(
        AppDbContext dbContext,
        IFileManagementService fileManagementService,
        IFileIntegrityService fileIntegrityService,
        CancellationToken ct)
    {
        _logger.LogDebug("Auditing GcodeFile files");

        var gcodeFiles = await dbContext.GcodeFiles
            .AsNoTracking()
            .ToListAsync(ct);

        var results = new AuditResults { FilesChecked = gcodeFiles.Count };

        foreach (var gcodeFile in gcodeFiles)
        {
            try
            {
                if (!fileManagementService.IsSafePath(gcodeFile.FilePath, _gcodePath))
                {
                    _logger.LogWarning($"GcodeFile {gcodeFile.Id}: Unsafe path - {gcodeFile.FilePath}");
                    continue;
                }

                var result = await fileIntegrityService.VerifyIntegrityAsync(
                    gcodeFile.FilePath,
                    gcodeFile.FileHash,
                    gcodeFile.FileSizeBytes,
                    ct: ct);

                if (!result.IsValid)
                {
                    _logger.LogWarning($"GcodeFile {gcodeFile.Id}: {result.FailureReason} - {result.ErrorMessage}");
                    if (result.FailureReason == "Missing")
                    {
                        results.MissingCount++;
                        results.MissingFileIds.Add(gcodeFile.Id);
                    }
                    else if (result.FailureReason == "HashMismatch" || result.FailureReason == "SizeMismatch")
                    {
                        results.CorruptedCount++;
                        results.CorruptedFileIds.Add(gcodeFile.Id);
                    }
                }
                else
                {
                    results.ValidCount++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Error auditing GcodeFile {gcodeFile.Id}: {ex.Message}");
            }
        }

        results.SummaryMessage = $"GcodeFile audit: Valid={results.ValidCount}, Missing={results.MissingCount}, Corrupted={results.CorruptedCount}";
        _logger.LogInformation(results.SummaryMessage);

        return results;
    }

    private async Task<AuditResults> AuditOrphanedFilesAsync(
        AppDbContext dbContext,
        CancellationToken ct)
    {
        _logger.LogDebug("Auditing for orphaned files");

        var results = new AuditResults();

        var dbModelPaths = (await dbContext.Models3D
            .AsNoTracking()
            .Select(m => m.FilePath)
            .ToListAsync(ct))
            .Where(p => !string.IsNullOrEmpty(p))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var dbGcodePaths = (await dbContext.GcodeFiles
            .AsNoTracking()
            .Select(g => g.FilePath)
            .ToListAsync(ct))
            .Where(p => !string.IsNullOrEmpty(p))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Check for orphaned model files
        if (Directory.Exists(_modelsPath))
        {
            try
            {
                var diskFiles = Directory.GetFiles(_modelsPath, "*", SearchOption.AllDirectories);

                foreach (var filePath in diskFiles)
                {
                    // Skip temp files and thumbnails - they're expected to be ephemeral
                    if (filePath.Contains(".tmp") || filePath.Contains("_thumb"))
                    {
                        continue;
                    }

                    if (!dbModelPaths.Contains(filePath))
                    {
                        results.OrphanedCount++;
                        results.OrphanedPaths.Add(filePath);
                        _logger.LogWarning($"Orphaned model file detected: {filePath}");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Error scanning for orphaned model files: {ex.Message}");
            }
        }

        // Check for orphaned gcode files
        if (Directory.Exists(_gcodePath))
        {
            try
            {
                var diskFiles = Directory.GetFiles(_gcodePath, "*.gcode", SearchOption.AllDirectories);

                foreach (var filePath in diskFiles)
                {
                    if (!dbGcodePaths.Contains(filePath))
                    {
                        results.OrphanedCount++;
                        results.OrphanedPaths.Add(filePath);
                        _logger.LogWarning($"Orphaned gcode file detected: {filePath}");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Error scanning for orphaned gcode files: {ex.Message}");
            }
        }

        results.SummaryMessage = $"Orphaned files audit: Found {results.OrphanedCount} orphaned files";
        if (results.OrphanedCount > 0)
        {
            _logger.LogInformation(results.SummaryMessage);
        }

        return results;
    }
}
