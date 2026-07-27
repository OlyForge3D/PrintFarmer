// <copyright file="BackendControlOutcome.cs" company="PlaceholderCompany">
// SPDX-License-Identifier: AGPL-3.0-only
// </copyright>

namespace Farm.Infrastructure.Services.Printers;

/// <summary>Hardware lifecycle operation executed by the durable queue control consumer.</summary>
public enum BackendControlOperation
{
    Pause,
    Resume,
    Cancel,
    Abort,
}

/// <summary>Typed delivery result for a backend lifecycle command.</summary>
public enum BackendControlStatus
{
    Accepted,
    Rejected,
    Unknown,
}

/// <summary>
/// Separates explicit pre-call rejection from a response-lost/transport outcome that must
/// be reconciled and never blindly retried.
/// </summary>
public sealed record BackendControlOutcome(
    BackendControlStatus Status,
    string? ErrorCode = null,
    string? ErrorDetail = null)
{
    public static BackendControlOutcome Accepted() =>
        new(BackendControlStatus.Accepted);

    public static BackendControlOutcome Rejected(string code, string detail) =>
        new(BackendControlStatus.Rejected, code, detail);

    public static BackendControlOutcome Unknown(string detail) =>
        new(BackendControlStatus.Unknown, "backend_control_unknown", detail);
}
