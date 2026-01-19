using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Diagnostics.CodeAnalysis;
using Farm.Infrastructure;
using Farm.Infrastructure.Annotations;

namespace Farm.Infrastructure.Domain;

/// <summary>
/// Tracks state transitions for a print job (Phase 3C)
/// </summary>
public class JobStateHistory
{
    public Guid Id { get; set; }

    public Guid JobId { get; set; }

    public PrintJob PrintJob { get; set; } = null!;

    public string FromState { get; set; } = string.Empty; // Previous state

    public string ToState { get; set; } = string.Empty; // New state

    public DateTime TransitionedAtUtc { get; set; }

    public TimeSpan? DurationInState { get; set; } // How long job stayed in FromState

    public string? Notes { get; set; } // Optional notes about the transition

    public DateTime CreatedAt { get; set; }
}
