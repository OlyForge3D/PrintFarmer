using System.ComponentModel.DataAnnotations;

namespace Farm.Infrastructure.Domain;

/// <summary>
/// Junction table linking print projects to gcode files with tracking information.
/// Tracks how many times each file needs to be printed and completion status.
/// </summary>
public class PrintProjectFile
{
    public Guid Id { get; set; }

    [Timestamp]
    public byte[]? RowVersion { get; set; }

    // Foreign key to PrintProject
    public Guid PrintProjectId { get; set; }

    public PrintProject? PrintProject { get; set; }

    // Foreign key to GcodeFile
    public Guid GcodeFileId { get; set; }

    public GcodeFile? GcodeFile { get; set; }

    /// <summary>
    /// Optional Spoolman spool ID for filament assignment.
    /// Links this file to a specific spool in the Spoolman inventory.
    /// </summary>
    public int? SpoolmanSpoolId { get; set; }

    /// <summary>
    /// Optional material requirement override (e.g., "PLA", "ABS").
    /// If null, uses the material from the gcode file metadata.
    /// </summary>
    [MaxLength(64)]
    public string? MaterialRequirement { get; set; }

    /// <summary>
    /// Number of copies to print for this file.
    /// </summary>
    public int PrintCount { get; set; } = 1;

    /// <summary>
    /// Number of copies that have been successfully printed.
    /// </summary>
    public int PrintedCount { get; set; }

    /// <summary>
    /// Current status of this file within the project.
    /// </summary>
    public PrintProjectFileStatus Status { get; set; } = PrintProjectFileStatus.Pending;

    /// <summary>
    /// Sort order within the project for display purposes.
    /// </summary>
    public int SortOrder { get; set; }

    /// <summary>
    /// Optional notes for this specific file within the project.
    /// </summary>
    [MaxLength(500)]
    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    /// <summary>
    /// Timestamp of when this file was last marked as printed.
    /// </summary>
    public DateTime? LastPrintedAt { get; set; }

    /// <summary>
    /// Reference to the last PrintJob that completed this file (for tracking).
    /// </summary>
    public Guid? LastPrintJobId { get; set; }

    public PrintJob? LastPrintJob { get; set; }
}

public enum PrintColorRequirement
{
    Base = 0,
    Accent = 1,
    Custom = 2
}

public enum PrintProjectFileStatus
{
    Pending = 0,
    Printing = 1,
    Completed = 2,
    Skipped = 3
}
