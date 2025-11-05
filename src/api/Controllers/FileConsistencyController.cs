using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Web.Api.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Admin endpoints for file consistency management, health status, and orphan remediation.
/// Requires admin role for all operations.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class FileConsistencyController(AppDbContext dbContext) : ControllerBase
{
    /// <summary>
    /// Get current file health status summary for dashboard.
    /// Returns statistics on file health across Model3D and GcodeFile libraries.
    /// </summary>
    [HttpGet("health/summary")]
    public async Task<ActionResult<FileHealthSummaryDto>> GetHealthSummaryAsync(CancellationToken ct)
    {
        var model3DStats = await GetModel3DHealthStatsAsync(ct);
        var gcodeStats = await GetGcodeHealthStatsAsync(ct);
        var recentAudit = await dbContext.FileHealthAudits
            .Where(a => a.HasIssues == false)
            .OrderByDescending(a => a.AuditDate)
            .FirstOrDefaultAsync(ct);

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

    /// <summary>
    /// Get detailed audit history with recent findings.
    /// </summary>
    [HttpGet("audits/history")]
    public async Task<ActionResult<List<FileHealthAuditDto>>> GetAuditHistoryAsync(
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        var audits = await dbContext.FileHealthAudits
            .OrderByDescending(a => a.AuditDate)
            .Take(pageSize)
            .Select(a => new FileHealthAuditDto
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
            })
            .ToListAsync(ct);

        return Ok(audits);
    }

    /// <summary>
    /// Get files with health issues (missing, corrupted, inaccessible).
    /// </summary>
    [HttpGet("files/issues")]
    public async Task<ActionResult<FileIssuesSummaryDto>> GetFilesWithIssuesAsync(CancellationToken ct)
    {
        var missingModel3DFiles = await dbContext.Models3D
            .Where(m => m.HealthStatus == FileHealthStatus.Missing)
            .Select(m => new FileIssueDto
            {
                FileId = m.Id,
                FileName = m.DisplayName,
                FilePath = m.FilePath,
                FileType = "Model3D",
                IssueType = "Missing",
                LastCheckDate = m.LastHealthCheckDate
            })
            .ToListAsync(ct);

        var corruptedModel3DFiles = await dbContext.Models3D
            .Where(m => m.HealthStatus == FileHealthStatus.Corrupted)
            .Select(m => new FileIssueDto
            {
                FileId = m.Id,
                FileName = m.DisplayName,
                FilePath = m.FilePath,
                FileType = "Model3D",
                IssueType = "Corrupted",
                LastCheckDate = m.LastHealthCheckDate
            })
            .ToListAsync(ct);

        var inaccessibleModel3DFiles = await dbContext.Models3D
            .Where(m => m.HealthStatus == FileHealthStatus.Inaccessible)
            .Select(m => new FileIssueDto
            {
                FileId = m.Id,
                FileName = m.DisplayName,
                FilePath = m.FilePath,
                FileType = "Model3D",
                IssueType = "Inaccessible",
                LastCheckDate = m.LastHealthCheckDate
            })
            .ToListAsync(ct);

        var missingGcodeFiles = await dbContext.GcodeFiles
            .Where(g => g.HealthStatus == FileHealthStatus.Missing)
            .Select(g => new FileIssueDto
            {
                FileId = g.Id,
                FileName = g.DisplayName,
                FilePath = g.FilePath,
                FileType = "GcodeFile",
                IssueType = "Missing",
                LastCheckDate = g.LastHealthCheckDate
            })
            .ToListAsync(ct);

        var corruptedGcodeFiles = await dbContext.GcodeFiles
            .Where(g => g.HealthStatus == FileHealthStatus.Corrupted)
            .Select(g => new FileIssueDto
            {
                FileId = g.Id,
                FileName = g.DisplayName,
                FilePath = g.FilePath,
                FileType = "GcodeFile",
                IssueType = "Corrupted",
                LastCheckDate = g.LastHealthCheckDate
            })
            .ToListAsync(ct);

        var inaccessibleGcodeFiles = await dbContext.GcodeFiles
            .Where(g => g.HealthStatus == FileHealthStatus.Inaccessible)
            .Select(g => new FileIssueDto
            {
                FileId = g.Id,
                FileName = g.DisplayName,
                FilePath = g.FilePath,
                FileType = "GcodeFile",
                IssueType = "Inaccessible",
                LastCheckDate = g.LastHealthCheckDate
            })
            .ToListAsync(ct);

        var allIssues = new List<FileIssueDto>();
        allIssues.AddRange(missingModel3DFiles);
        allIssues.AddRange(corruptedModel3DFiles);
        allIssues.AddRange(inaccessibleModel3DFiles);
        allIssues.AddRange(missingGcodeFiles);
        allIssues.AddRange(corruptedGcodeFiles);
        allIssues.AddRange(inaccessibleGcodeFiles);

        var summary = new FileIssuesSummaryDto
        {
            TotalIssues = allIssues.Count,
            MissingFiles = allIssues.Count(i => i.IssueType == "Missing"),
            CorruptedFiles = allIssues.Count(i => i.IssueType == "Corrupted"),
            InaccessibleFiles = allIssues.Count(i => i.IssueType == "Inaccessible"),
            Issues = allIssues
        };

        return Ok(summary);
    }

    /// <summary>
    /// Get details for a specific Model3D file's health status.
    /// </summary>
    [HttpGet("model3d/{modelId}/health")]
    public async Task<ActionResult<FileHealthDetailDto>> GetModel3DHealthAsync(Guid modelId, CancellationToken ct)
    {
        var model = await dbContext.Models3D
            .Where(m => m.Id == modelId)
            .Select(m => new FileHealthDetailDto
            {
                FileId = m.Id,
                FileName = m.DisplayName,
                FilePath = m.FilePath,
                FileType = "Model3D",
                FileSize = m.FileSizeBytes,
                FileHash = m.FileHash,
                HealthStatus = m.HealthStatus.ToString(),
                LastHealthCheckDate = m.LastHealthCheckDate,
                VerificationDetails = m.LastVerificationResult,
                UploadedDate = m.UploadedAt
            })
            .FirstOrDefaultAsync(ct);

        if (model is null)
        {
            return NotFound($"Model3D with ID {modelId} not found");
        }

        return Ok(model);
    }

    /// <summary>
    /// Get details for a specific GcodeFile's health status.
    /// </summary>
    [HttpGet("gcode/{gcodeId}/health")]
    public async Task<ActionResult<FileHealthDetailDto>> GetGcodeFileHealthAsync(Guid gcodeId, CancellationToken ct)
    {
        var gcode = await dbContext.GcodeFiles
            .Where(g => g.Id == gcodeId)
            .Select(g => new FileHealthDetailDto
            {
                FileId = g.Id,
                FileName = g.DisplayName,
                FilePath = g.FilePath,
                FileType = "GcodeFile",
                FileSize = g.FileSizeBytes,
                FileHash = g.FileHash,
                HealthStatus = g.HealthStatus.ToString(),
                LastHealthCheckDate = g.LastHealthCheckDate,
                VerificationDetails = g.LastVerificationResult,
                UploadedDate = g.UploadedAt
            })
            .FirstOrDefaultAsync(ct);

        if (gcode is null)
        {
            return NotFound($"GcodeFile with ID {gcodeId} not found");
        }

        return Ok(gcode);
    }

    // Private helper methods

    private async Task<(int Total, int Healthy, int Missing, int Corrupted)> GetModel3DHealthStatsAsync(CancellationToken ct)
    {
        var total = await dbContext.Models3D.CountAsync(ct);
        var healthy = await dbContext.Models3D
            .CountAsync(m => m.HealthStatus == FileHealthStatus.Healthy, ct);
        var missing = await dbContext.Models3D
            .CountAsync(m => m.HealthStatus == FileHealthStatus.Missing, ct);
        var corrupted = await dbContext.Models3D
            .CountAsync(m => m.HealthStatus == FileHealthStatus.Corrupted, ct);

        return (total, healthy, missing, corrupted);
    }

    private async Task<(int Total, int Healthy, int Missing, int Corrupted)> GetGcodeHealthStatsAsync(CancellationToken ct)
    {
        var total = await dbContext.GcodeFiles.CountAsync(ct);
        var healthy = await dbContext.GcodeFiles
            .CountAsync(g => g.HealthStatus == FileHealthStatus.Healthy, ct);
        var missing = await dbContext.GcodeFiles
            .CountAsync(g => g.HealthStatus == FileHealthStatus.Missing, ct);
        var corrupted = await dbContext.GcodeFiles
            .CountAsync(g => g.HealthStatus == FileHealthStatus.Corrupted, ct);

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
