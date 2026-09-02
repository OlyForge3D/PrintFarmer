using System.ComponentModel.DataAnnotations;

namespace Farm.Infrastructure.Domain;

/// <summary>
/// Represents a print project that groups multiple gcode files together for tracking.
/// Used by print farm operators to manage multi-file print jobs (e.g., all parts for a Voron 2.4).
/// </summary>
public class PrintProject : IRevisionedEntity
{
    /// <summary>
    /// Maximum length (in UTF-16 code units) allowed for <see cref="Name"/>. Shared by the
    /// EF Core column configuration and the request DTO validation in
    /// <c>PrintProjectsController</c> so long names are rejected with a 400 before ever
    /// reaching the database (see issue #2368).
    /// </summary>
    public const int NameMaxLength = 255;

    public Guid Id { get; set; }

    /// <inheritdoc/>
    public long Revision { get; set; } = 1;

    [Required]
    [MaxLength(NameMaxLength)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(2000)]
    public string? Description { get; set; }

    public PrintProjectStatus Status { get; set; } = PrintProjectStatus.Open;

    public int Priority { get; set; }

    public DateTime? DueDate { get; set; }

    [MaxLength(2000)]
    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    // Navigation property to project files
    public ICollection<PrintProjectFile> Files { get; set; } = new List<PrintProjectFile>();
}

public enum PrintProjectStatus
{
    Open = 0,
    InProgress = 1,
    Completed = 2,
    Cancelled = 3,
    OnHold = 4
}
