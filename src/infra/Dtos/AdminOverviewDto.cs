namespace Farm.Infrastructure.Dtos;

/// <summary>
/// Status of a subsystem in the admin overview. Serialized as a string via
/// <c>JsonStringEnumConverter</c>, so clients receive <c>"Healthy"</c>, <c>"Degraded"</c>,
/// <c>"Unhealthy"</c>, or <c>"Unknown"</c>. Ordering matters for roll-up:
/// higher ordinal is worse and wins when combining several sub-checks.
/// </summary>
public enum SubsystemStatus
{
    /// <summary>The subsystem is fully operational.</summary>
    Healthy = 0,

    /// <summary>The subsystem is running but with non-critical issues that warrant attention.</summary>
    Degraded = 1,

    /// <summary>The subsystem is not functioning correctly and needs immediate attention.</summary>
    Unhealthy = 2,

    /// <summary>Status could not be determined (probe timed out or threw).</summary>
    Unknown = 3,
}

/// <summary>
/// Severity of an actionable attention item. Serialized as a string via
/// <c>JsonStringEnumConverter</c>. Higher ordinal sorts earlier in the list.
/// </summary>
public enum AttentionSeverity
{
    /// <summary>Informational only; no action strictly required.</summary>
    Info = 0,

    /// <summary>Worth reviewing but the system is still functional.</summary>
    Warning = 1,

    /// <summary>Requires prompt operator attention.</summary>
    Error = 2,
}

/// <summary>
/// Aggregate snapshot of admin-facing subsystem health plus a ranked list of items
/// that need attention. Returned by <c>GET /api/admin/overview</c> and consumed by
/// the Admin Control Center hub.
/// </summary>
public record AdminOverviewDto
{
    /// <summary>UTC timestamp when the snapshot was generated. Renders as ISO-8601 in JSON.</summary>
    public required DateTime CheckedAt { get; init; }

    /// <summary>
    /// The single worst status across <see cref="Subsystems"/>, per the roll-up rule
    /// documented on <see cref="SubsystemStatus"/> (higher ordinal wins). Callers must
    /// render this — not assume "Healthy" — for any overall status indicator, so a
    /// degraded or unhealthy subsystem is never masked by a contradictory "all clear"
    /// header (see issue #2222).
    /// </summary>
    public required SubsystemStatus OverallStatus { get; init; }

    /// <summary>
    /// Subsystem health tiles in stable display order. Always includes the core subsystems
    /// (api, database, signalr, backends); optional subsystems (e.g. spoolman) appear only
    /// when configured.
    /// </summary>
    public required IReadOnlyList<SubsystemHealthDto> Subsystems { get; init; }

    /// <summary>
    /// Ranked list of items needing operator attention. Sorted with Errors first, then Warnings,
    /// then Info. An empty list means everything is healthy.
    /// </summary>
    public required IReadOnlyList<AttentionItemDto> Attention { get; init; }
}

/// <summary>
/// A single subsystem health tile in the admin overview.
/// </summary>
public record SubsystemHealthDto
{
    /// <summary>Stable machine key (e.g. <c>"database"</c>, <c>"signalr"</c>). Do not localize.</summary>
    public required string Key { get; init; }

    /// <summary>Human-readable name (e.g. <c>"Database"</c>, <c>"SignalR Hub"</c>).</summary>
    public required string Name { get; init; }

    /// <summary>Current status of the subsystem.</summary>
    public required SubsystemStatus Status { get; init; }

    /// <summary>Short one-line detail for the tile (e.g. <c>"PostgreSQL · 4 ms"</c>). May be null.</summary>
    public string? Detail { get; init; }
}

/// <summary>
/// A single actionable item in the "needs attention" list.
/// </summary>
public record AttentionItemDto
{
    /// <summary>Stable identifier for this attention item so the UI can deduplicate and track it.</summary>
    public required string Key { get; init; }

    /// <summary>Severity of the issue. Higher severity items sort earlier in the list.</summary>
    public required AttentionSeverity Severity { get; init; }

    /// <summary>Plain-language title of what is wrong.</summary>
    public required string Title { get; init; }

    /// <summary>Additional detail explaining the issue and its impact.</summary>
    public required string Detail { get; init; }

    /// <summary>
    /// Optional call-to-action label (e.g. <c>"Open Slicer Workers"</c>). Present when
    /// <see cref="ActionDestinationId"/> or <see cref="ActionRoute"/> is present.
    /// </summary>
    public string? ActionLabel { get; init; }

    /// <summary>
    /// Preferred navigation target: the stable <c>id</c> of an entry in the frontend's
    /// <c>ADMIN_DESTINATIONS</c> registry (e.g. <c>"ops-status"</c>). The client resolves
    /// the id to the current canonical path, so route renames stay a frontend concern
    /// and the backend never hardcodes URLs it does not own.
    /// </summary>
    /// <remarks>
    /// When both <see cref="ActionDestinationId"/> and <see cref="ActionRoute"/> are
    /// provided, the client prefers the id and falls back to the route only if the id
    /// does not resolve. Emit <see cref="ActionRoute"/> alone only when there is no
    /// matching registry entry (e.g. non-admin operational pages).
    /// </remarks>
    public string? ActionDestinationId { get; init; }

    /// <summary>
    /// Fallback client-side route the UI navigates to when no <see cref="ActionDestinationId"/>
    /// is available (e.g. <c>"/printers"</c>). Never a raw URL, so the client is safe to
    /// treat it as a router path.
    /// </summary>
    public string? ActionRoute { get; init; }
}
