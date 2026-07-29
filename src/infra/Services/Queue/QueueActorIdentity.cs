// <copyright file="QueueActorIdentity.cs" company="PlaceholderCompany">
// SPDX-License-Identifier: AGPL-3.0-only
// </copyright>

using System.Security.Claims;

namespace Farm.Infrastructure.Services.Queue;

/// <summary>Canonical queue actor subjects used by HTTP and durable system callers.</summary>
public static class QueueActorIdentity
{
    public const string AutoDispatch = "system:auto-dispatch";
    public const string Scheduler = "system:scheduler";

    /// <summary>Resolves the authenticated user subject in the same form used by resource authorization.</summary>
    public static string Resolve(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        string? subject = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!Guid.TryParse(subject, out Guid userId) &&
            !Guid.TryParse(principal.FindFirst("sub")?.Value, out userId))
        {
            throw new UnauthorizedAccessException(
                "Unable to resolve the authenticated queue actor.");
        }

        return userId.ToString();
    }

    /// <summary>Returns whether the subject is an explicitly authorized internal queue actor.</summary>
    public static bool IsTrustedSystemActor(string actorSubject) =>
        string.Equals(actorSubject, AutoDispatch, StringComparison.Ordinal) ||
        string.Equals(actorSubject, Scheduler, StringComparison.Ordinal);
}
