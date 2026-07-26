using System.ComponentModel.DataAnnotations;

namespace Farm.Infrastructure.Services.Queue;

/// <summary>
/// Shared <c>If-Match</c> enforcement used by every mutating queue operation so the
/// 428 / 412 / 409 mapping can never drift between endpoints (issue #900, defect 11).
/// </summary>
public static class QueueRevisionGuard
{
    /// <summary>
    /// Enforces a caller-supplied <c>If-Match</c> token against a persisted row version.
    /// </summary>
    /// <param name="ifMatch">
    /// Base-64 ETag from the caller. <see langword="null"/> means "trusted internal caller"
    /// and skips the check; an empty/whitespace string means the header was required but
    /// absent and raises <see cref="QueuePreconditionRequiredException"/>.
    /// </param>
    /// <param name="actual">Persisted row version.</param>
    /// <param name="operationDescription">Human-readable operation name for the error message.</param>
    /// <exception cref="QueuePreconditionRequiredException">The header was required but absent.</exception>
    /// <exception cref="QueueRevisionConflictException">The supplied revision is stale.</exception>
    /// <exception cref="ValidationException">The supplied revision is not valid base-64.</exception>
    public static void EnsureIfMatch(string? ifMatch, byte[]? actual, string operationDescription)
    {
        if (ifMatch is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(ifMatch))
        {
            throw new QueuePreconditionRequiredException(
                $"If-Match is required for {operationDescription}. Fetch the job to obtain its current ETag.");
        }

        byte[] expected;
        try
        {
            expected = Convert.FromBase64String(ifMatch);
        }
        catch (FormatException)
        {
            throw new ValidationException("If-Match must be a base-64 encoded ETag.");
        }

        if (!expected.SequenceEqual(actual ?? []))
        {
            throw new QueueRevisionConflictException(
                $"The resource has changed since the request was prepared ({operationDescription}). " +
                "Re-fetch the ETag and retry.");
        }
    }
}
