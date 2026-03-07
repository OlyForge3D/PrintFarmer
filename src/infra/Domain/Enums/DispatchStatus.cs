namespace Farm.Infrastructure.Domain.Enums;

/// <summary>
/// Outcome status of a dispatch operation.
/// </summary>
public enum DispatchStatus
{
    /// <summary>Dispatch requested but not yet completed.</summary>
    Pending = 0,

    /// <summary>Dispatch completed successfully — job started on printer.</summary>
    Success = 1,

    /// <summary>Dispatch failed — see ErrorMessage for details.</summary>
    Failed = 2
}
