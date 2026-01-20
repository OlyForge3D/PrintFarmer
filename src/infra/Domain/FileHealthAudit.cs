using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Diagnostics.CodeAnalysis;
using Farm.Infrastructure;
using Farm.Infrastructure.Annotations;

namespace Farm.Infrastructure.Domain;

// File Consistency Audit System
public class FileHealthAudit
{
    public Guid Id { get; set; }

    public DateTime AuditDate { get; set; }

    public FileAuditType AuditType { get; set; } // Model3D, GcodeFile, or Orphaned

    // Statistics
    public int FilesChecked { get; set; }

    public int HealthyFiles { get; set; }

    public int MissingFiles { get; set; }

    public int CorruptedFiles { get; set; }

    public int OrphanedFiles { get; set; }

    // Details - JSON arrays of file IDs/paths with issues
    public string? MissingFileIds { get; set; } // JSON array of Guids

    public string? CorruptedFileIds { get; set; } // JSON array of Guids

    public string? OrphanedFilePaths { get; set; } // JSON array of file paths

    // Summary & status
    public string? SummaryMessage { get; set; } // Human-readable audit summary

    public bool HasIssues { get; set; } // True if any files missing/corrupted/orphaned

    public DateTime CreatedAt { get; set; }
}
