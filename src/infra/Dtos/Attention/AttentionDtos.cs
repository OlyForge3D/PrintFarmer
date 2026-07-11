using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Dtos.Attention;

/// <summary>
/// Kind of attention item. Wire values are lowercase (issue #707); the enum has its own
/// per-enum converter so we do not mutate the global <c>JsonStringEnumConverter</c>
/// contract that existing PascalCase enums depend on.
/// </summary>
/// <remarks>
/// Values are stable; new kinds are added without renaming existing ones so mobile
/// and web clients keep working across backend rollouts. <c>Runout</c> is reserved
/// for the F4 coverage source (#709) and defined here so clients can pattern-match
/// on it before the backend emits it.
/// </remarks>
[JsonConverter(typeof(AttentionKindConverter))]
public enum AttentionKind
{
    /// <summary>Auto-paused or high-confidence print failure.</summary>
    Failure = 0,

    /// <summary>Predicted filament runout (reserved for F4/#709).</summary>
    Runout = 1,

    /// <summary>Completed plate awaiting harvest.</summary>
    Harvest = 2,

    /// <summary>Active maintenance alert.</summary>
    Maintenance = 3,

    /// <summary>Enabled printer that is currently offline.</summary>
    Offline = 4,
}

/// <summary>
/// Severity of an attention item. Wire values are camelCase (issue #707) with a per-enum
/// converter; ordering: Critical &gt; Warning &gt; Info.
/// </summary>
[JsonConverter(typeof(AttentionSeverityConverter))]
public enum AttentionSeverity
{
    /// <summary>Informational; no operator action strictly required.</summary>
    Info = 0,

    /// <summary>Warning; operator attention recommended.</summary>
    Warning = 1,

    /// <summary>Critical; operator action required.</summary>
    Critical = 2,
}

/// <summary>
/// Typed action kinds. Clients MUST NOT synthesize raw URLs — they POST to
/// <c>POST /api/attention/{itemId}/actions/{actionKind}</c>, which the server
/// dispatches to the appropriate existing endpoint. Wire values are camelCase.
/// </summary>
[JsonConverter(typeof(AttentionActionKindConverter))]
public enum AttentionActionKind
{
    /// <summary>Pause the current print.</summary>
    Pause = 0,

    /// <summary>Resume a paused print.</summary>
    Resume = 1,

    /// <summary>Cancel the current print.</summary>
    Cancel = 2,

    /// <summary>Acknowledge (mark seen) a maintenance alert.</summary>
    Acknowledge = 3,

    /// <summary>Resolve (complete) a maintenance alert.</summary>
    Resolve = 4,

    /// <summary>Dismiss (ignore) a maintenance alert.</summary>
    Dismiss = 5,

    /// <summary>Snooze the item for a duration; use the dedicated snooze endpoint.</summary>
    Snooze = 6,

    /// <summary>Harvest a completed plate (reserved for F9/#714; currently not offered).</summary>
    Harvest = 7,
}

/// <summary>Per-enum converter emitting lowercase names for <see cref="AttentionKind"/>.</summary>
public sealed class AttentionKindConverter : JsonStringEnumConverter<AttentionKind>
{
    /// <inheritdoc />
    public AttentionKindConverter()
        : base(JsonNamingPolicy.CamelCase)
    {
    }
}

/// <summary>Per-enum converter emitting camelCase names for <see cref="AttentionSeverity"/>.</summary>
public sealed class AttentionSeverityConverter : JsonStringEnumConverter<AttentionSeverity>
{
    /// <inheritdoc />
    public AttentionSeverityConverter()
        : base(JsonNamingPolicy.CamelCase)
    {
    }
}

/// <summary>Per-enum converter emitting camelCase names for <see cref="AttentionActionKind"/>.</summary>
public sealed class AttentionActionKindConverter : JsonStringEnumConverter<AttentionActionKind>
{
    /// <inheritdoc />
    public AttentionActionKindConverter()
        : base(JsonNamingPolicy.CamelCase)
    {
    }
}

/// <summary>
/// Kind of change carried by an <c>attentionchanged</c> SignalR event (issue #707 /
/// Dallas realtime contract). Wire values are lowercase (<c>created</c>, <c>updated</c>,
/// <c>resolved</c>) via a feature-local converter so the repository-wide PascalCase enum
/// convention is left untouched.
/// </summary>
[JsonConverter(typeof(AttentionChangeKindConverter))]
public enum AttentionChangeKind
{
    /// <summary>A new attention item became visible.</summary>
    Created,

    /// <summary>An existing attention item changed (including per-user snooze state).</summary>
    Updated,

    /// <summary>An attention item was resolved / cleared and should disappear.</summary>
    Resolved,
}

/// <summary>Per-enum converter emitting lowercase names for <see cref="AttentionChangeKind"/>.</summary>
public sealed class AttentionChangeKindConverter : JsonStringEnumConverter<AttentionChangeKind>
{
    /// <inheritdoc />
    public AttentionChangeKindConverter()
        : base(JsonNamingPolicy.CamelCase)
    {
    }
}

/// <summary>
/// Payload for the lowercase <c>attentionchanged</c> SignalR invalidation event. This is an
/// invalidation hint, not a second source of item truth: clients always refetch
/// <c>GET /api/attention</c>. The small typed payload preserves deterministic timing tests
/// and item-level diagnostics per the Dallas F2 realtime adjudication.
/// </summary>
/// <param name="ItemId">Computed attention item id (for example <c>failure:{incidentId}</c>).</param>
/// <param name="ChangeKind">Transition kind: created, updated, or resolved.</param>
/// <param name="OccurredAt">
/// Authoritative source-transition/commit timestamp used for the ≤1s dispatch and ≤5s
/// visible-refresh targets.
/// </param>
public sealed record AttentionChangedPayload(
    string ItemId,
    [property: JsonConverter(typeof(AttentionChangeKindConverter))] AttentionChangeKind ChangeKind,
    DateTime OccurredAt);

/// <summary>
/// A typed action a client can invoke on an attention item. Clients dispatch by
/// <see cref="Kind"/>, not by any URL the server returns.
/// </summary>
public sealed record AttentionActionDto(
    [property: JsonConverter(typeof(AttentionActionKindConverter))] AttentionActionKind Kind,
    string Label,
    bool RequiresConfirmation);

/// <summary>
/// A single attention feed item. Feed items are computed on read; only per-user
/// snoozes are persisted.
/// </summary>
/// <param name="Id">Stable computed id of the form <c>{kind}:{sourceId}</c>.</param>
/// <param name="Kind">Attention kind (failure, maintenance, offline, harvest, runout).</param>
/// <param name="Severity">Severity level driving ordering and UI treatment.</param>
/// <param name="PrinterId">Owning printer id.</param>
/// <param name="PrinterName">Printer display name for compact rendering.</param>
/// <param name="Title">Short operator-facing title.</param>
/// <param name="Detail">Longer description including the actionable next step.</param>
/// <param name="OccurredAt">
/// UTC timestamp used for tie-breaking within severity. MUST be a stable, source-derived
/// value; sources that lack a stable clock (for example continuously-offline printers)
/// MUST set <paramref name="AllowFreshOccurrenceBypass"/> to <c>false</c> so a moving
/// timestamp cannot silently defeat a user snooze.
/// </param>
/// <param name="Actions">Typed actions available to the caller. Only advertise actions the
/// server can actually execute today; do not advertise stubs that return 501.</param>
/// <param name="ToolheadIndex">Optional toolhead index for per-tool items.</param>
/// <param name="DeadlineAt">Optional deadline used for time-to-impact ordering.</param>
/// <param name="JobId">
/// Optional print-job id for <see cref="AttentionKind.Failure"/> items. Used by the action
/// dispatcher to verify the incident's job is still the printer's active job before
/// mutating printer state.
/// </param>
/// <param name="AllowFreshOccurrenceBypass">
/// When true (default), a snoozed item whose current <paramref name="OccurredAt"/> is
/// strictly greater than the snooze anchor becomes visible again (fresh-occurrence
/// bypass). Sources with non-stable timestamps must set this to false.
/// </param>
public sealed record AttentionItemDto(
    string Id,
    [property: JsonConverter(typeof(AttentionKindConverter))] AttentionKind Kind,
    [property: JsonConverter(typeof(AttentionSeverityConverter))] AttentionSeverity Severity,
    Guid PrinterId,
    string PrinterName,
    string Title,
    string Detail,
    DateTime OccurredAt,
    IReadOnlyList<AttentionActionDto> Actions,
    int? ToolheadIndex = null,
    DateTime? DeadlineAt = null,
    Guid? JobId = null,
    bool AllowFreshOccurrenceBypass = true);

/// <summary>
/// Response payload for <c>GET /api/attention</c>.
/// </summary>
/// <param name="Items">Severity-ordered items for the requested page.</param>
/// <param name="TotalCount">Total items across every page after snooze suppression.</param>
/// <param name="Page">1-based page number the caller received.</param>
/// <param name="PageSize">Item cap applied to <paramref name="Items"/>.</param>
/// <param name="TotalPages">Total pages available for <paramref name="TotalCount"/>.</param>
/// <param name="HealthyPrinterIds">
/// Enabled printers with no visible items. Page-independent; the full set is returned
/// on every page so the client can render the "N printers running normally" row without
/// paging.
/// </param>
public sealed record AttentionFeedDto(
    IReadOnlyList<AttentionItemDto> Items,
    int TotalCount,
    int Page,
    int PageSize,
    int TotalPages,
    IReadOnlyList<Guid> HealthyPrinterIds);

/// <summary>
/// Request payload for <c>POST /api/attention/{itemId}/snooze</c>.
/// </summary>
/// <remarks>
/// The optional <see cref="AttentionItemAnchorAtUtc"/> anchor is the item's
/// <see cref="AttentionItemDto.OccurredAt"/> at snooze time. When set, a future
/// occurrence of the same id whose <c>OccurredAt</c> is strictly greater will
/// bypass the snooze (guarantees a fresh incident cannot be suppressed by a
/// stale snooze). Callers may omit the anchor for legacy snoozes.
/// </remarks>
public sealed class SnoozeAttentionRequest
{
    /// <summary>UTC instant until which the item is snoozed. Must be in the future.</summary>
    [Required]
    public DateTime SnoozedUntilUtc { get; set; }

    /// <summary>
    /// Optional timing anchor — the <see cref="AttentionItemDto.OccurredAt"/> of the
    /// item at snooze time. Enables fresh-occurrence bypass; see the remarks on this
    /// request type.
    /// </summary>
    public DateTime? AttentionItemAnchorAtUtc { get; set; }
}
