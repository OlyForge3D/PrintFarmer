using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Domain;

/// <summary>
/// Types of tasks that can be assigned to users.
/// </summary>
public enum UserTaskType
{
    /// <summary>
    /// Unspecified or unknown task type.
    /// </summary>
    None = 0,

    /// <summary>
    /// Import slicer profiles for a printer model.
    /// </summary>
    ProfileImport = 1,

    /// <summary>
    /// Scheduled or overdue maintenance for a printer.
    /// </summary>
    MaintenanceDue = 2,

    /// <summary>
    /// Firmware update available for a printer.
    /// </summary>
    FirmwareUpdate = 3,

    /// <summary>
    /// Calibration needed for a printer.
    /// </summary>
    CalibrationNeeded = 4,

    /// <summary>
    /// Generic user-created task.
    /// </summary>
    Custom = 99
}

/// <summary>
/// Status of a user task.
/// </summary>
public enum UserTaskStatus
{
    /// <summary>
    /// Task is pending action.
    /// </summary>
    Pending = 0,

    /// <summary>
    /// Task is currently being worked on.
    /// </summary>
    InProgress = 1,

    /// <summary>
    /// Task has been completed successfully.
    /// </summary>
    Completed = 2,

    /// <summary>
    /// Task was dismissed by user without completion.
    /// </summary>
    Dismissed = 3,

    /// <summary>
    /// Task was skipped and should not be shown again.
    /// </summary>
    Skipped = 4
}

/// <summary>
/// Priority level for user tasks.
/// </summary>
public enum UserTaskPriority
{
    /// <summary>
    /// Low priority - can be addressed when convenient.
    /// </summary>
    Low = 0,

    /// <summary>
    /// Normal priority - should be addressed soon.
    /// </summary>
    Normal = 1,

    /// <summary>
    /// High priority - should be addressed immediately.
    /// </summary>
    High = 2
}

/// <summary>
/// Represents a task or action item for users to complete.
/// Used for profile imports, maintenance reminders, and other actionable items.
/// Tasks can be aggregated - e.g., one ProfileImport task for a model covers all printers of that model.
/// </summary>
public class UserTask
{
    /// <summary>
    /// Unique identifier for the task.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Type of task (ProfileImport, MaintenanceDue, etc.).
    /// </summary>
    public UserTaskType TaskType { get; set; }

    /// <summary>
    /// Type of entity this task relates to (e.g., "PrinterModel", "Printer").
    /// </summary>
    [MaxLength(50)]
    public string EntityType { get; set; } = string.Empty;

    /// <summary>
    /// ID of the primary entity this task relates to.
    /// For ProfileImport tasks, this is the PrinterModelId.
    /// </summary>
    public Guid EntityId { get; set; }

    /// <summary>
    /// Human-readable title for the task.
    /// </summary>
    [MaxLength(200)]
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Optional detailed description of the task.
    /// </summary>
    [MaxLength(1000)]
    public string? Description { get; set; }

    /// <summary>
    /// Current status of the task.
    /// </summary>
    public UserTaskStatus Status { get; set; } = UserTaskStatus.Pending;

    /// <summary>
    /// Priority level of the task.
    /// </summary>
    public UserTaskPriority Priority { get; set; } = UserTaskPriority.Normal;

    /// <summary>
    /// When the task was created.
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Optional due date for the task.
    /// </summary>
    public DateTime? DueAt { get; set; }

    /// <summary>
    /// When the task was completed (if completed).
    /// </summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>
    /// When the task was dismissed (if dismissed).
    /// </summary>
    public DateTime? DismissedAt { get; set; }

    /// <summary>
    /// User who dismissed the task (if dismissed).
    /// </summary>
    public Guid? DismissedByUserId { get; set; }

    /// <summary>
    /// Navigation property to user who dismissed the task.
    /// </summary>
    public User? DismissedByUser { get; set; }

    /// <summary>
    /// JSON metadata specific to the task type.
    /// For ProfileImport: { "manufacturerName": "Prusa", "availableVariants": [...] }
    /// </summary>
    public string? MetadataJson { get; set; }

    /// <summary>
    /// JSON array of related entity IDs.
    /// For ProfileImport: IDs of printers waiting for profiles.
    /// Stored as JSON array string: ["guid1", "guid2"]
    /// </summary>
    public string? RelatedEntityIdsJson { get; set; }

    /// <summary>
    /// When the task was last updated.
    /// </summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
