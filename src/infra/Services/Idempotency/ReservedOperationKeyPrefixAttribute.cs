using System.ComponentModel.DataAnnotations;

namespace Farm.Infrastructure.Services.Idempotency;

/// <summary>
/// Validation attribute that rejects a client-supplied string in any reserved operation-key
/// namespace (<see cref="IdempotencyKeyUtilities.ReservedOperationKeyPrefixes"/> — currently the
/// <c>idem:</c> and <c>harvest:</c> prefixes) (issue #715, Hicks r3 blocker 2 and r7 blocker H2).
///
/// <para>
/// <c>idem:</c> is reserved for the operationKey the idempotency filter synthesizes on the client's
/// behalf; <c>harvest:</c> is reserved for the keys the server generates when harvesting printed
/// parts. Allowing a client to supply a value in either namespace would let a crafted key collide
/// with a future server-generated key and either incorrectly dedup a genuinely distinct mutation or
/// permanently break a later harvest. Applying this attribute to a request DTO field makes
/// <c>[ApiController]</c> reject such a value at the model-binding boundary with a <c>400</c>
/// <c>ValidationProblemDetails</c> (a <c>ProblemDetails</c>) before the action runs; the service
/// layer enforces the same rule as defense-in-depth.
/// </para>
///
/// <para>
/// Null/empty/whitespace values are treated as valid so the attribute composes with optional,
/// nullable fields. The comparison is case-insensitive and ignores surrounding whitespace to
/// match the service-side normalization (<c>Trim()</c>), so <c>" Idem:x"</c> is rejected too.
/// </para>
/// </summary>
[AttributeUsage(
    AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter,
    AllowMultiple = false)]
public sealed class ReservedOperationKeyPrefixAttribute : ValidationAttribute
{
    /// <inheritdoc />
    public override bool IsValid(object? value)
    {
        if (value is not string text || string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        // Delegate to the shared width-aware guard so a fullwidth ｉｄｅｍ: / ｈａｒｖｅｓｔ: (or
        // fullwidth-colon variant) is rejected just like ASCII idem: / harvest: — an ordinal
        // StartsWith alone would let those compatibility variants through (issue #715, Hicks r4
        // blocker 2 and r7 blocker H2). Because it delegates to the shared reserved-prefix set,
        // this attribute picks up any future reserved namespace with no change here.
        return !IdempotencyKeyUtilities.IsReservedOperationKey(text);
    }

    /// <inheritdoc />
    public override string FormatErrorMessage(string name)
        => $"The {name} field must not begin with a reserved operation-key prefix "
            + $"('{IdempotencyKeyUtilities.SynthesizedOperationKeyPrefix}' or "
            + $"'{IdempotencyKeyUtilities.HarvestOperationKeyPrefix}'), which are reserved for "
            + "server-side idempotency backstopping and harvest bookkeeping.";
}
