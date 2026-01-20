namespace Farm.Web.Api.DTOs;

/// <summary>
/// Overall file health summary for dashboard display.
/// Contains statistics across all Model3D and GcodeFile libraries.
/// </summary>
public record FileHealthSummaryDto
{
    public int TotalModel3DFiles { get; init; }

    public int Model3DHealthy { get; init; }

    public int Model3DMissing { get; init; }

    public int Model3DCorrupted { get; init; }

    public int TotalGcodeFiles { get; init; }

    public int GcodeHealthy { get; init; }

    public int GcodeMissing { get; init; }

    public int GcodeCorrupted { get; init; }

    public DateTime? LastHealthyAuditDate { get; init; }

    public double OverallHealthPercentage { get; init; }
}

/// <summary>
/// Individual audit record with findings.
/// </summary>
public record FileHealthAuditDto
{
    public Guid Id { get; init; }

    public DateTime AuditDate { get; init; }

    public string AuditType { get; init; } = string.Empty;

    public int FilesChecked { get; init; }

    public int HealthyFiles { get; init; }

    public int MissingFiles { get; init; }

    public int CorruptedFiles { get; init; }

    public int OrphanedFiles { get; init; }

    public string? SummaryMessage { get; init; }

    public bool HasIssues { get; init; }
}

/// <summary>
/// Single file issue record.
/// </summary>
public record FileIssueDto
{
    public Guid FileId { get; init; }

    public string FileName { get; init; } = string.Empty;

    public string FilePath { get; init; } = string.Empty;

    public string FileType { get; init; } = string.Empty;

    public string IssueType { get; init; } = string.Empty;

    public DateTime? LastCheckDate { get; init; }
}

/// <summary>
/// Summary of all files with health issues.
/// </summary>
public record FileIssuesSummaryDto
{
    public int TotalIssues { get; init; }

    public int MissingFiles { get; init; }

    public int CorruptedFiles { get; init; }

    public int InaccessibleFiles { get; init; }

    public List<FileIssueDto> Issues { get; init; } = new();
}

/// <summary>
/// Detailed health information for a specific file.
/// </summary>
public record FileHealthDetailDto
{
    public Guid FileId { get; init; }

    public string FileName { get; init; } = string.Empty;

    public string FilePath { get; init; } = string.Empty;

    public string FileType { get; init; } = string.Empty;

    public long FileSize { get; init; }

    public string FileHash { get; init; } = string.Empty;

    public string HealthStatus { get; init; } = string.Empty;

    public DateTime? LastHealthCheckDate { get; init; }

    public string? VerificationDetails { get; init; }

    public DateTime UploadedDate { get; init; }
}
