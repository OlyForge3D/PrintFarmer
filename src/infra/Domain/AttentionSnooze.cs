using System.ComponentModel.DataAnnotations;

namespace Farm.Infrastructure.Domain;

/// <summary>
/// Per-user snooze of an item in the unified attention feed.
/// Snoozes are scoped to (<see cref="UserId"/>, <see cref="AttentionItemId"/>) so one
/// operator hiding an item never affects another operator.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="AttentionItemId"/> is the computed, source-derived identifier used by the
/// attention feed (for example <c>failure:{incidentId}</c>, <c>maintenance:{alertId}</c>,
/// <c>offline:{printerId}</c>). It is a stable string, not a foreign key, because attention
/// items are computed from multiple heterogeneous sources.
/// </para>
/// <para>
/// A row with <see cref="SnoozedUntilUtc"/> in the past has expired and MUST be treated
/// as absent by the feed. Expired rows are safe to leave in place for later cleanup.
/// </para>
/// </remarks>
public class AttentionSnooze
{
    /// <summary>Primary key.</summary>
    public Guid Id { get; set; }

    /// <summary>Owning user; snoozes are strictly per-user.</summary>
    public Guid UserId { get; set; }

    /// <summary>Computed attention item id (source-derived, stable). Max length 128.</summary>
    [MaxLength(128)]
    public string AttentionItemId { get; set; } = string.Empty;

    /// <summary>UTC instant after which this snooze is no longer effective.</summary>
    public DateTime SnoozedUntilUtc { get; set; }

    /// <summary>UTC instant this snooze row was created (audit).</summary>
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>
    /// Optional timing anchor — the <see cref="AttentionItemId"/>'s <c>OccurredAt</c>
    /// value at the moment of snooze. If a fresh occurrence of the same computed id
    /// has an <c>OccurredAt</c> strictly greater than this anchor, the feed treats
    /// the snooze as bypassed. Null for legacy rows (no anchor recorded).
    /// </summary>
    public DateTime? AttentionItemAnchorAtUtc { get; set; }
}
