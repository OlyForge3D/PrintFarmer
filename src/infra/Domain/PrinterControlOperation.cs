using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Farm.Infrastructure.Domain;

/// <summary>A durable, non-replayable physical motion intent and its recovery evidence.</summary>
public sealed class PrinterControlOperation : IRevisionedEntity
{
    public Guid Id { get; set; }

    public Guid PrinterId { get; set; }

    public PrinterControlKind Kind { get; set; }

    public double? X { get; set; }

    public double? Y { get; set; }

    public double? Z { get; set; }

    public double? F { get; set; }

    public PrinterControlState State { get; set; }

    public long Revision { get; set; } = 1;

    [MaxLength(256)] public string ActorSubject { get; set; } = string.Empty;

    [MaxLength(512)] public string NormalizedIntent { get; set; } = string.Empty;

    [MaxLength(64)] public string PrinterConfigurationIdentity { get; set; } = string.Empty;

    public Guid? OwnerToken { get; set; }

    public DateTime? OwnerHeartbeatAtUtc { get; set; }

    public DateTime? SendCommittedAtUtc { get; set; }

    public Guid? CorrelationId { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }

    public DateTime? StartedAtUtc { get; set; }

    public DateTime? CompletedAtUtc { get; set; }

    public PrinterControlEvidence CompletionEvidence { get; set; }

    [MaxLength(128)] public string? FailureCode { get; set; }

    [MaxLength(512)] public string? FailureMessage { get; set; }

    public PrinterSenderIsolation SenderIsolation { get; set; }

    /// <summary>Unresolved legacy emergency sender; newer senders have individual attempt records.</summary>
    public bool EmergencyStopInFlight { get; set; }

    public DateTime? RecoveryRequestedAtUtc { get; set; }

    public DateTime? SenderIsolatedAtUtc { get; set; }

    [MaxLength(256)] public string? RecoveryActorSubject { get; set; }

    [MaxLength(8192)] public string? RecoveryEvidenceJson { get; set; }

    public long? RecoveryFromRevision { get; set; }

    [NotMapped] public bool Settled => State is PrinterControlState.Succeeded or PrinterControlState.Failed or PrinterControlState.Recovered;

    [NotMapped] public bool RequiresRecovery => State is PrinterControlState.Unknown or PrinterControlState.Recovering;
}
