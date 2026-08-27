namespace Farm.Infrastructure.Services.Queue;

/// <summary>
/// Configures retention windows and batching bounds for
/// <see cref="QueueRetentionPruneService"/>. Bound via plain <c>IOptions&lt;T&gt;</c>
/// (not the <c>IAppSetting</c>/Settings-UI pattern) since these are internal
/// operational tuning knobs, not user-facing settings.
///
/// <para>
/// Each table has its own independently configurable window. In particular,
/// <see cref="OperationAuditRetentionDays"/> deliberately does NOT default to or
/// inherit <see cref="OutboxRetentionDays"/> — operation audits are a
/// compliance/forensic record and must be retained longer by default. A prior
/// review explicitly rejected a single blanket retention window across all three
/// tables.
/// </para>
/// </summary>
public sealed class QueueRetentionSettings
{
    /// <summary>Configuration section name for binding via <c>IConfiguration</c>.</summary>
    public const string SectionName = "QueueRetention";

    /// <summary>
    /// Days to retain terminal (<c>Published</c>/<c>DeadLettered</c>)
    /// <see cref="Domain.QueueDispatchOutbox"/> rows after completion. Conservative
    /// default; SignalR is the fast path and this table is the durable refetch
    /// authority for gap recovery, so this window must comfortably exceed any
    /// realistic client offline duration.
    /// </summary>
    public int OutboxRetentionDays { get; set; } = 14;

    /// <summary>
    /// Days to retain terminal <see cref="Domain.QueueDispatchAttempt"/> rows after
    /// <see cref="Domain.QueueDispatchAttempt.TerminalAtUtc"/>. Attempts still
    /// flagged <see cref="Domain.QueueDispatchAttempt.RequiresReconciliation"/> are
    /// never pruned by age alone (see <see cref="QueueRetentionPruneService"/>).
    /// </summary>
    public int DispatchAttemptRetentionDays { get; set; } = 30;

    /// <summary>
    /// Days to retain <see cref="Domain.QueueOperationAudit"/> rows. Independent of
    /// the outbox/attempt windows by design — operation audits back compliance and
    /// forensic investigation, so this window defaults much longer.
    /// </summary>
    public int OperationAuditRetentionDays { get; set; } = 180;

    /// <summary>
    /// Maximum number of rows deleted per bulk-delete statement. Bounds the lock
    /// hold time of any single DELETE so a first prune pass against an
    /// already-large table cannot hold a long-running lock.
    /// </summary>
    public int DeleteBatchSize { get; set; } = 500;

    /// <summary>
    /// Maximum total rows deleted per table in a single prune tick. Bounds the
    /// total work of one pass; a table with a larger backlog than this is drained
    /// gradually over subsequent ticks instead of in one long-running sweep.
    /// </summary>
    public int MaxDeletesPerTablePerPass { get; set; } = 20000;

    /// <summary>Interval between prune sweeps.</summary>
    public TimeSpan PruneInterval { get; set; } = TimeSpan.FromHours(6);
}
