using System.ComponentModel.DataAnnotations;

namespace Farm.Infrastructure.Domain;

/// <summary>
/// Protected, append-only evidence journal for the indeterminate pre-start claim escape hatch
/// (issue #2859). One row per recovery decision (accepted or denied). Rows are never updated
/// or deleted: the <c>AppDbContext</c> save guard rejects any mutation, no foreign key or
/// cascade ties a row to prunable queue rows, and no retention job prunes this table.
/// The row also stores the exact response so an idempotent replay returns it verbatim.
/// </summary>
public sealed class DispatchRecoveryJournalEntry
{
    /// <summary>Audit identity returned to clients as <c>recoveryAuditId</c>.</summary>
    public Guid Id { get; set; }

    public Guid PrinterId { get; set; }

    public Guid? PrintJobId { get; set; }

    public Guid DispatchAttemptId { get; set; }

    /// <summary>Attempt revision the operator asserted against.</summary>
    public long ClaimRevision { get; set; }

    /// <summary>Attempt outcome observed by the server before the decision.</summary>
    [MaxLength(32)]
    public string PriorOutcome { get; set; } = string.Empty;

    /// <summary>Decision code (for example <c>accepted</c> or <c>rejected_stale</c>).</summary>
    [MaxLength(64)]
    public string Transition { get; set; } = string.Empty;

    /// <summary>Server-trusted authenticated actor identity.</summary>
    [MaxLength(256)]
    public string ActorSubject { get; set; } = string.Empty;

    /// <summary>Server clock time at which the actor's request was evaluated.</summary>
    public DateTime ActorRecordedAtUtc { get; set; }

    /// <summary>Server clock time at which this journal row was written.</summary>
    public DateTime ServerRecordedAtUtc { get; set; }

    /// <summary>Client-reported assertion time. Never trusted for ordering or policy.</summary>
    public DateTime? ClientReportedAtUtc { get; set; }

    public int AssertionVersion { get; set; }

    public bool PhysicalCheckConfirmed { get; set; }

    public bool SenderIsolationConfirmed { get; set; }

    /// <summary>Sender-settlement evidence observed at decision time, when present.</summary>
    public DateTime? SenderSettledAtUtc { get; set; }

    [MaxLength(1000)]
    public string? Note { get; set; }

    [MaxLength(128)]
    public string? CorrelationId { get; set; }

    /// <summary>Redacted structured evidence (identifiers and typed codes only).</summary>
    [MaxLength(4000)]
    public string? EvidenceJson { get; set; }

    /// <summary>SHA-256 of (actor, route, printer, idempotency key); unique replay scope.</summary>
    [MaxLength(64)]
    public string ReplayScopeHash { get; set; } = string.Empty;

    /// <summary>SHA-256 of the canonical request body.</summary>
    [MaxLength(64)]
    public string RequestFingerprint { get; set; } = string.Empty;

    public int ResponseStatusCode { get; set; }

    [MaxLength(64)]
    public string? ResponseETag { get; set; }

    [MaxLength(8000)]
    public string ResponseBodyJson { get; set; } = string.Empty;
}

/// <summary>
/// Durable once-only marker for an escalation notification raised for an unresolved
/// indeterminate claim (issue #2859, refinement D). Unique per
/// (<see cref="DispatchAttemptId"/>, <see cref="PolicyRevision"/>, <see cref="Threshold"/>) so
/// restarts and retention pruning never re-raise the same level. Markers never release claims.
/// </summary>
public sealed class DispatchEscalationMarker
{
    public Guid Id { get; set; }

    public Guid DispatchAttemptId { get; set; }

    public Guid PrinterId { get; set; }

    public Guid? PrintJobId { get; set; }

    /// <summary>Version of the threshold policy that produced this marker.</summary>
    public int PolicyRevision { get; set; }

    /// <summary>Escalation level name (for example <c>Operational</c>).</summary>
    [MaxLength(32)]
    public string Threshold { get; set; } = string.Empty;

    /// <summary>Claim age, in seconds, when the level was raised.</summary>
    public long ClaimAgeSeconds { get; set; }

    public DateTime RaisedAtUtc { get; set; }
}
