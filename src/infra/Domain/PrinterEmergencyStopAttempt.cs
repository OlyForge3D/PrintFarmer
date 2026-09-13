using System.ComponentModel.DataAnnotations;

namespace Farm.Infrastructure.Domain;

/// <summary>Delivery evidence for one explicit out-of-band emergency request; never replayed.</summary>
public sealed class PrinterEmergencyStopAttempt
{
    public Guid Id { get; set; }

    public Guid OperationId { get; set; }

    public Guid PrinterId { get; set; }

    [MaxLength(256)] public string ActorSubject { get; set; } = string.Empty;

    [MaxLength(64)] public string ConfigurationIdentity { get; set; } = string.Empty;

    public PrinterEmergencyStopDelivery Delivery { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime? SendCommittedAtUtc { get; set; }

    public DateTime? CompletedAtUtc { get; set; }
}

/// <summary>Only explicit delivery evidence or operator isolation settles an emergency sender.</summary>
public enum PrinterEmergencyStopDelivery
{
    Pending,
    Accepted,
    NotSent,
    Unknown,
    ExternallyIsolated,
}
