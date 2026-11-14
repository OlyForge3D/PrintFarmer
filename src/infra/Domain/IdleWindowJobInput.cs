namespace Farm.Infrastructure.Domain;

/// <summary>Read-only queue facts needed to evaluate an idle window and select scorer job IDs.</summary>
public sealed record IdleWindowJobInput(
    Guid Id,
    Guid? AssignedPrinterId,
    PrintJobStatus Status,
    int Priority,
    int QueuePosition,
    DateTime QueuedAt);
