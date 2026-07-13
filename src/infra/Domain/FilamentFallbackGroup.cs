namespace Farm.Infrastructure.Domain;

/// <summary>
/// Ordered same-material fallback chain over existing physical toolheads (or MMU gates)
/// on a single printer. When the currently-loaded slot cannot satisfy a print (runout,
/// clog, mismatch), the dispatch/attention layer walks the ordered members and picks the
/// first member whose loaded spool covers the print. Membership is per-printer so a
/// group cannot span multiple printers. Issue #711 (F6).
/// </summary>
public class FilamentFallbackGroup
{
    public Guid Id { get; set; }

    /// <summary>Printer that owns this fallback group. Groups never span printers.</summary>
    public Guid PrinterId { get; set; }

    public Printer? Printer { get; set; }

    /// <summary>Operator-facing name (e.g. "PLA Chain", "PETG Fallback").</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Case-folded (trimmed, lower-invariant) copy of <see cref="Name"/> used to enforce
    /// case-insensitive per-printer name uniqueness at the database level. The service-layer
    /// duplicate check compares names case-insensitively; without a normalized column the raw
    /// unique index on PostgreSQL/SQL Server sees "PLA Chain" and "pla chain" as distinct, so
    /// two concurrent inserts could both pass the service check AND the DB index. The unique
    /// index is defined over (PrinterId, NameNormalized) so the database is the final arbiter.
    /// Populated by the service on every create/update. Issue #711 (F6 remediation, FIX A).
    /// </summary>
    public string NameNormalized { get; set; } = string.Empty;

    /// <summary>
    /// Canonical material family the chain is scoped to (e.g. "PLA"). Members that do not
    /// currently carry a compatible material remain in the chain but are reported as
    /// mismatched by the dispatch/attention layer — the group definition itself is not
    /// invalidated by a hot swap.
    /// </summary>
    public string MaterialType { get; set; } = string.Empty;

    /// <summary>Display ordering when a printer has multiple fallback groups.</summary>
    public int DisplayOrder { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<FilamentFallbackGroupMember> Members { get; set; } = new List<FilamentFallbackGroupMember>();
}

/// <summary>
/// Ordered member of a <see cref="FilamentFallbackGroup"/>. Points at an existing
/// <see cref="Toolhead"/> row (physical dock or MMU gate) — never introduces a duplicate
/// slot hierarchy.
/// </summary>
public class FilamentFallbackGroupMember
{
    public Guid Id { get; set; }

    public Guid FallbackGroupId { get; set; }

    public FilamentFallbackGroup? FallbackGroup { get; set; }

    /// <summary>Toolhead this member points at. Must belong to the same printer as the group.</summary>
    public Guid ToolheadId { get; set; }

    public Toolhead? Toolhead { get; set; }

    /// <summary>Zero-based position within the ordered chain. Unique within a group.</summary>
    public int Position { get; set; }
}
