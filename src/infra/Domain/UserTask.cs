using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Domain;

/// <summary>
/// Strict camelCase JSON converter for <see cref="UserTaskAnchorKind"/>.
/// Rejects integer values; maps unknown strings to <see cref="UserTaskAnchorKind.Unspecified"/>.
/// Wire values are kept in lockstep with the EF value converter in
/// <c>UserTaskConfiguration</c>.
/// </summary>
internal sealed class UserTaskAnchorKindJsonConverter : JsonConverter<UserTaskAnchorKind>
{
    public override UserTaskAnchorKind Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException($"Expected a string value for {nameof(UserTaskAnchorKind)}, got {reader.TokenType}.");
        }

        return FromWire(reader.GetString() ?? string.Empty);
    }

    public override void Write(Utf8JsonWriter writer, UserTaskAnchorKind value, JsonSerializerOptions options)
        => writer.WriteStringValue(ToWire(value));

    internal static string ToWire(UserTaskAnchorKind value) => value switch
    {
        UserTaskAnchorKind.Now => "now",
        UserTaskAnchorKind.At => "at",
        UserTaskAnchorKind.Window => "window",
        UserTaskAnchorKind.AnytimeToday => "anytimeToday",
        UserTaskAnchorKind.Timeline => "timeline",
        _ => "unspecified",
    };

    internal static UserTaskAnchorKind FromWire(string value) => value switch
    {
        "now" => UserTaskAnchorKind.Now,
        "at" => UserTaskAnchorKind.At,
        "window" => UserTaskAnchorKind.Window,
        "anytimeToday" => UserTaskAnchorKind.AnytimeToday,
        "timeline" => UserTaskAnchorKind.Timeline,
        _ => UserTaskAnchorKind.Unspecified,
    };
}

/// <summary>
/// Strict camelCase JSON converter for <see cref="UserTaskSourceKind"/>.
/// Rejects integer values; maps unknown strings to <see cref="UserTaskSourceKind.Unspecified"/>.
/// Wire values are kept in lockstep with the EF value converter in
/// <c>UserTaskConfiguration</c>.
/// </summary>
internal sealed class UserTaskSourceKindJsonConverter : JsonConverter<UserTaskSourceKind>
{
    public override UserTaskSourceKind Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException($"Expected a string value for {nameof(UserTaskSourceKind)}, got {reader.TokenType}.");
        }

        return FromWire(reader.GetString() ?? string.Empty);
    }

    public override void Write(Utf8JsonWriter writer, UserTaskSourceKind value, JsonSerializerOptions options)
        => writer.WriteStringValue(ToWire(value));

    internal static string ToWire(UserTaskSourceKind value) => value switch
    {
        UserTaskSourceKind.Attention => "attention",
        UserTaskSourceKind.FailureIncident => "failureIncident",
        UserTaskSourceKind.Harvest => "harvest",
        UserTaskSourceKind.FilamentCoverage => "filamentCoverage",
        UserTaskSourceKind.Maintenance => "maintenance",
        UserTaskSourceKind.SpoolReorder => "spoolReorder",
        UserTaskSourceKind.PrintedPartStock => "printedPartStock",
        _ => "unspecified",
    };

    internal static UserTaskSourceKind FromWire(string value) => value switch
    {
        "attention" => UserTaskSourceKind.Attention,
        "failureIncident" => UserTaskSourceKind.FailureIncident,
        "harvest" => UserTaskSourceKind.Harvest,
        "filamentCoverage" => UserTaskSourceKind.FilamentCoverage,
        "maintenance" => UserTaskSourceKind.Maintenance,
        "spoolReorder" => UserTaskSourceKind.SpoolReorder,
        "printedPartStock" => UserTaskSourceKind.PrintedPartStock,
        _ => UserTaskSourceKind.Unspecified,
    };
}

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
    Custom = 99,

    // -- Shift-plan compiler task types (issue #713) --
    // Values are stable; new kinds are added without renaming existing ones so
    // web/mobile clients stay compatible across backend rollouts. Ranges are
    // grouped so future additions remain contiguous.

    /// <summary>
    /// Failure that requires operator clearing (paused/failed print).
    /// Materialized by the ShiftPlanCompiler from the F2 attention feed.
    /// </summary>
    FailureClear = 100,

    /// <summary>
    /// Completed plate awaiting harvest.
    /// Materialized by the ShiftPlanCompiler from the harvest attention source.
    /// </summary>
    HarvestReady = 101,

    /// <summary>
    /// Predicted filament runout on an active job. The task's
    /// <see cref="UserTask.AnchorAtUtc"/> is set to
    /// <c>predictedRunoutAt − runoutWarningLeadMinutes</c>.
    /// </summary>
    FilamentRunout = 102,

    /// <summary>
    /// Maintenance surface scheduled into a predicted idle window.
    /// Distinct from <see cref="MaintenanceDue"/> — this task is anchored to a
    /// time window rather than a printer's current maintenance state.
    /// </summary>
    MaintenanceInIdleWindow = 103,

    /// <summary>
    /// Spool restock check triggered when spool burn-rate + on-hand falls below
    /// the configured reorder threshold.
    /// </summary>
    SpoolRestock = 104,

    /// <summary>
    /// Reserved for printed-part restock tasks materialized from stock model
    /// after issue #714 lands. Enum value is pinned so future integration does
    /// not require another migration.
    /// </summary>
    PrintedPartRestock = 105
}

/// <summary>
/// Time-anchor bucket for a shift-plan task. Wire values are lowercase
/// camelCase strings; unknown/future values are tolerated by the API layer.
/// </summary>
/// <remarks>
/// Introduced by issue #713. Legacy tasks materialize with
/// <see cref="Unspecified"/>; anchor sorting groups Unspecified as
/// <see cref="AnytimeToday"/>. Names are stable.
/// </remarks>
[JsonConverter(typeof(UserTaskAnchorKindJsonConverter))]
public enum UserTaskAnchorKind
{
    /// <summary>Legacy or unspecified anchor; treated as anytimeToday.</summary>
    Unspecified = 0,

    /// <summary>Do now — top of the shift-plan list.</summary>
    Now = 1,

    /// <summary>Timed anchor at <see cref="UserTask.AnchorAtUtc"/> (e.g. runout deadline − lead).</summary>
    At = 2,

    /// <summary>Timed window between <c>WindowStartUtc</c> and <c>WindowEndUtc</c>.</summary>
    Window = 3,

    /// <summary>Anytime during the current shift (no fixed time).</summary>
    AnytimeToday = 4,

    /// <summary>
    /// Group label for the merged At/Window timeline segment in the shift-plan
    /// response. Individual tasks within this group retain their specific
    /// <see cref="At"/> or <see cref="Window"/> anchor kind. This value is used
    /// only on <see cref="Farm.Infrastructure.Services.Tasks.ShiftPlanGroupDto"/>,
    /// never persisted on <see cref="UserTask"/>.
    /// </summary>
    Timeline = 5,
}

/// <summary>
/// Canonical source of a materialized shift-plan task. Used together with
/// <see cref="UserTask.SourceId"/> to dedupe and to auto-complete when the
/// source resolves. Wire values are lowercase camelCase.
/// </summary>
/// <remarks>
/// Names are stable. Adding a new source requires adding a value here; unknown
/// values from older code paths are tolerated (materializer treats them as
/// <see cref="Unspecified"/>).
/// </remarks>
[JsonConverter(typeof(UserTaskSourceKindJsonConverter))]
public enum UserTaskSourceKind
{
    /// <summary>Legacy or manual task with no compiler source.</summary>
    Unspecified = 0,

    /// <summary>F2 attention feed (failure incidents, offline, generic).</summary>
    Attention = 1,

    /// <summary>Failure-detection incident (auto-paused/high-confidence failure).</summary>
    FailureIncident = 2,

    /// <summary>Awaiting-harvest attention item.</summary>
    Harvest = 3,

    /// <summary>Filament coverage / F4 runout prediction.</summary>
    FilamentCoverage = 4,

    /// <summary>Maintenance alert or upcoming plan window.</summary>
    Maintenance = 5,

    /// <summary>Spool burn-rate reorder check.</summary>
    SpoolReorder = 6,

    /// <summary>Printed-part stock model (reserved; F9 / #714).</summary>
    PrintedPartStock = 7
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

    // -- Shift-plan compiler fields (issue #713) --

    /// <summary>
    /// Time-anchor bucket for shift-plan grouping. Stored as canonical string in
    /// the database (via <see cref="UserTaskAnchorKind"/>) so unknown/future
    /// values from newer code round-trip safely. Legacy tasks default to
    /// <see cref="UserTaskAnchorKind.Unspecified"/>.
    /// </summary>
    public UserTaskAnchorKind AnchorKind { get; set; } = UserTaskAnchorKind.Unspecified;

    /// <summary>
    /// UTC instant this task is anchored to (deadline / soft target). Set when
    /// <see cref="AnchorKind"/> is <see cref="UserTaskAnchorKind.At"/>; may also
    /// be set for <see cref="UserTaskAnchorKind.Window"/> as the anchor point
    /// inside the window used for tie-breaking.
    /// </summary>
    public DateTime? AnchorAtUtc { get; set; }

    /// <summary>Start of the anchor window (UTC) when <see cref="AnchorKind"/> is Window.</summary>
    public DateTime? WindowStartUtc { get; set; }

    /// <summary>End of the anchor window (UTC) when <see cref="AnchorKind"/> is Window.</summary>
    public DateTime? WindowEndUtc { get; set; }

    /// <summary>
    /// Canonical source that materialized this task. Combined with
    /// <see cref="SourceId"/> forms the dedupe key. Manual/legacy tasks use
    /// <see cref="UserTaskSourceKind.Unspecified"/>.
    /// </summary>
    public UserTaskSourceKind SourceKind { get; set; } = UserTaskSourceKind.Unspecified;

    /// <summary>
    /// Stable identifier from the source (e.g. attention item id
    /// <c>failure:{guid}</c>, coverage <c>runout:{printerId}:toolhead:{n}</c>).
    /// Used to dedupe when re-running the compiler and to auto-complete when the
    /// source no longer emits the item.
    /// </summary>
    [MaxLength(128)]
    public string? SourceId { get; set; }
}
