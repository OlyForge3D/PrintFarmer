using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.DTOs;
using Farm.Web.Api.Repositories.FileConsistency;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Admin endpoints for file consistency management, health status, and orphan remediation.
/// Requires admin role for all operations.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class FileConsistencyController(
    IFileConsistencyRepository fileConsistencyRepo) : ControllerBase
{
    private readonly IFileConsistencyRepository _repo = fileConsistencyRepo;
    /// <summary>
    /// Get current file health status summary for dashboard.
    /// Returns statistics on file health across Model3D and GcodeFile libraries.
    /// </summary>
    [HttpGet("health/summary")]
    public async Task<ActionResult<FileHealthSummaryDto>> GetHealthSummaryAsync(CancellationToken ct)
    {
        try
        {
            var model3DStats = await GetModel3DHealthStatsAsync(ct);
            var gcodeStats = await GetGcodeHealthStatsAsync(ct);
            var recentAudit = await _repo.GetMostRecentHealthyAuditAsync(ct);

            var summary = new FileHealthSummaryDto
            {
                TotalModel3DFiles = model3DStats.Total,
                Model3DHealthy = model3DStats.Healthy,
                Model3DMissing = model3DStats.Missing,
                Model3DCorrupted = model3DStats.Corrupted,
                TotalGcodeFiles = gcodeStats.Total,
                GcodeHealthy = gcodeStats.Healthy,
                GcodeMissing = gcodeStats.Missing,
                GcodeCorrupted = gcodeStats.Corrupted,
                LastHealthyAuditDate = recentAudit?.AuditDate,
                OverallHealthPercentage = CalculateOverallHealth(model3DStats, gcodeStats)
            };

            return Ok(summary);
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError,
                new { error = "Failed to retrieve file health summary", details = ex.Message });
        }
    }

    /// <summary>
    /// Get detailed audit history with recent findings.
    /// </summary>
    [HttpGet("audits/history")]
    public async Task<ActionResult<List<FileHealthAuditDto>>> GetAuditHistoryAsync(
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        try
        {
            var audits = await _repo.GetRecentAuditsAsync(pageSize, ct);
            var auditDtos = audits.Select(a => new FileHealthAuditDto
            {
                Id = a.Id,
                AuditDate = a.AuditDate,
                AuditType = a.AuditType.ToString(),
                FilesChecked = a.FilesChecked,
                HealthyFiles = a.HealthyFiles,
                MissingFiles = a.MissingFiles,
                CorruptedFiles = a.CorruptedFiles,
                OrphanedFiles = a.OrphanedFiles,
                SummaryMessage = a.SummaryMessage,
                HasIssues = a.HasIssues
            }).ToList();

            return Ok(auditDtos);
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError,
                new { error = "Failed to retrieve audit history", details = ex.Message });
        }
    }

    /// <summary>
    /// Get all files with health issues for review and potential remediation.
    /// </summary>
    [HttpGet("issues")]
    public async Task<ActionResult<FileIssuesSummaryDto>> GetFilesWithIssuesAsync(CancellationToken ct = default)
    {
        try
        {
            // Use repository methods to get files with issues
            var missingModel3DFiles = await _repo.GetModel3DFilesWithIssueAsync(FileHealthStatus.Missing, ct);
            var corruptedModel3DFiles = await _repo.GetModel3DFilesWithIssueAsync(FileHealthStatus.Corrupted, ct);

            var missingGcodeFiles = await _repo.GetGcodeFilesWithIssueAsync(FileHealthStatus.Missing, ct);
            var corruptedGcodeFiles = await _repo.GetGcodeFilesWithIssueAsync(FileHealthStatus.Corrupted, ct);

            var allIssues = new List<FileIssueDto>();

            // Add Model3D missing files
            allIssues.AddRange(missingModel3DFiles.Select(m => new FileIssueDto
            {
                FileId = m.Id,
                FileName = m.DisplayName,
                FilePath = m.FilePath,
                FileType = "Model3D",
                IssueType = "Missing",
                LastCheckDate = m.LastHealthCheckDate
            }));

            // Add Model3D corrupted files
            allIssues.AddRange(corruptedModel3DFiles.Select(m => new FileIssueDto
            {
                FileId = m.Id,
                FileName = m.DisplayName,
                FilePath = m.FilePath,
                FileType = "Model3D",
                IssueType = "Corrupted",
                LastCheckDate = m.LastHealthCheckDate
            }));

            // Add GCode missing files
            allIssues.AddRange(missingGcodeFiles.Select(g => new FileIssueDto
            {
                FileId = g.Id,
                FileName = g.DisplayName,
                FilePath = g.FilePath,
                FileType = "GCode",
                IssueType = "Missing",
                LastCheckDate = g.LastHealthCheckDate
            }));

            // Add GCode corrupted files
            allIssues.AddRange(corruptedGcodeFiles.Select(g => new FileIssueDto
            {
                FileId = g.Id,
                FileName = g.DisplayName,
                FilePath = g.FilePath,
                FileType = "GCode",
                IssueType = "Corrupted",
                LastCheckDate = g.LastHealthCheckDate
            }));

            var summary = new FileIssuesSummaryDto
            {
                TotalIssues = allIssues.Count,
                MissingFiles = allIssues.Count(i => i.IssueType == "Missing"),
                CorruptedFiles = allIssues.Count(i => i.IssueType == "Corrupted"),
                InaccessibleFiles = 0, // No inaccessible status in repository currently
                Issues = allIssues
            };

            return Ok(summary);
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError,
                new { error = "Failed to retrieve files with issues", details = ex.Message });
        }
    }

    /// <summary>
    /// Get details for a specific Model3D file's health status.
    /// </summary>
    [HttpGet("model3d/{modelId}/health")]
    public async Task<ActionResult<FileHealthDetailDto>> GetModel3DHealthAsync(Guid modelId, CancellationToken ct)
    {
        var model = await _repo.GetModel3DWithHealthDetailsAsync(modelId, ct);

        if (model is null)
        {
            return NotFound($"Model3D with ID {modelId} not found");
        }

        var dto = new FileHealthDetailDto
        {
            FileId = model.Id,
            FileName = model.DisplayName,
            FilePath = model.FilePath,
            FileType = "Model3D",
            FileSize = model.FileSizeBytes,
            FileHash = model.FileHash,
            HealthStatus = model.HealthStatus.ToString(),
            LastHealthCheckDate = model.LastHealthCheckDate,
            VerificationDetails = model.LastVerificationResult,
            UploadedDate = model.UploadedAt
        };

        return Ok(dto);
    }

    /// <summary>
    /// Get details for a specific GcodeFile's health status.
    /// </summary>
    [HttpGet("gcode/{gcodeId}/health")]
    public async Task<ActionResult<FileHealthDetailDto>> GetGcodeFileHealthAsync(Guid gcodeId, CancellationToken ct)
    {
        var gcode = await _repo.GetGcodeFileWithHealthDetailsAsync(gcodeId, ct);

        if (gcode is null)
        {
            return NotFound($"GcodeFile with ID {gcodeId} not found");
        }

        var dto = new FileHealthDetailDto
        {
            FileId = gcode.Id,
            FileName = gcode.DisplayName,
            FilePath = gcode.FilePath,
            FileType = "GcodeFile",
            FileSize = gcode.FileSizeBytes,
            FileHash = gcode.FileHash,
            HealthStatus = gcode.HealthStatus.ToString(),
            LastHealthCheckDate = gcode.LastHealthCheckDate,
            VerificationDetails = gcode.LastVerificationResult,
            UploadedDate = gcode.UploadedAt
        };

        return Ok(dto);
    }

    // Private helper methods

    private async Task<(int Total, int Healthy, int Missing, int Corrupted)> GetModel3DHealthStatsAsync(CancellationToken ct)
    {
        var total = await _repo.CountModel3DFilesAsync(ct);
        var healthy = await _repo.CountHealthyModel3DFilesAsync(ct);
        var missing = await _repo.CountMissingModel3DFilesAsync(ct);
        var corrupted = await _repo.CountCorruptedModel3DFilesAsync(ct);

        return (total, healthy, missing, corrupted);
    }

    private async Task<(int Total, int Healthy, int Missing, int Corrupted)> GetGcodeHealthStatsAsync(CancellationToken ct)
    {
        var total = await _repo.CountGcodeFilesAsync(ct);
        var healthy = await _repo.CountHealthyGcodeFilesAsync(ct);
        var missing = await _repo.CountMissingGcodeFilesAsync(ct);
        var corrupted = await _repo.CountCorruptedGcodeFilesAsync(ct);

        return (total, healthy, missing, corrupted);
    }

    private static double CalculateOverallHealth(
        (int Total, int Healthy, int Missing, int Corrupted) model3DStats,
        (int Total, int Healthy, int Missing, int Corrupted) gcodeStats)
    {
        var totalFiles = model3DStats.Total + gcodeStats.Total;
        if (totalFiles == 0)
        {
            return 100.0;
        }

        var totalHealthy = model3DStats.Healthy + gcodeStats.Healthy;
        return (totalHealthy / (double)totalFiles) * 100.0;
    }
}
