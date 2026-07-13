using System.Security.Cryptography;
using System.Text;
using Farm.Infrastructure.Normalization;

namespace Farm.Infrastructure.Services.Idempotency;

/// <summary>
/// Utilities for computing the SHA-256 request hash and validating client-supplied
/// <c>Idempotency-Key</c> values. Extracted to a static helper so tests can pin
/// exact bytes-in / hex-out behavior without spinning up an HTTP pipeline.
/// </summary>
public static class IdempotencyKeyUtilities
{
    /// <summary>
    /// Maximum accepted length of the client-supplied idempotency key. Also
    /// matches the <c>[MaxLength]</c> on the persisted column so an over-long
    /// key rejects up-front rather than at the store.
    /// </summary>
    public const int MaxKeyLength = 200;

    /// <summary>
    /// Header name used to carry the client-supplied idempotency key. Case
    /// insensitive per RFC 7230 §3.2 — ASP.NET Core's
    /// <c>IHeaderDictionary</c> lookup handles that automatically.
    /// </summary>
    public const string HeaderName = "Idempotency-Key";

    /// <summary>
    /// Prefix marking an <c>operationKey</c> that the idempotency filter synthesized
    /// on the client's behalf (issue #715, Hicks r2 blocker 2). When a gated
    /// parts-adjust request carries an <c>Idempotency-Key</c> header but omits the
    /// body <c>operationKey</c>, the filter derives a deterministic key from the
    /// caller's identity so the domain's natural <c>(PartInventoryId, OperationKey)</c>
    /// uniqueness still backstops the filter — even if a post-mutation flush failure
    /// leaves the Processing row to be reclaimed and the same header key is retried.
    /// </summary>
    public const string SynthesizedOperationKeyPrefix = "idem:";

    /// <summary>
    /// Returns whether <paramref name="operationKey"/> lies in the reserved
    /// <see cref="SynthesizedOperationKeyPrefix"/> (<c>idem:</c>) namespace, comparing in a
    /// <b>width-aware</b> way (issue #715, Hicks r4 blocker 2).
    ///
    /// <para>
    /// A plain ordinal <c>StartsWith("idem:")</c> is byte-exact, so a fullwidth <c>ｉｄｅｍ:</c>
    /// (U+FF49 U+FF44 U+FF45 U+FF4D — optionally with a fullwidth colon U+FF1A) slips past it, yet
    /// SQL Server's width-insensitive collation would later treat the persisted value as equivalent
    /// to an ASCII <c>idem:</c> key and could dedup it against a server-synthesized key. We
    /// therefore fold the value to its Unicode NFKC form
    /// (<see cref="Farm.Infrastructure.Normalization.UnicodeCompatibilityNormalizer"/>) before the
    /// ordinal, case-insensitive prefix test, so every width/compatibility variant of the reserved
    /// prefix is caught. The comparison also trims surrounding whitespace to match the service-side
    /// normalization.
    /// </para>
    ///
    /// <para>
    /// This is a pure comparison helper — it never mutates the key. Callers that persist the key
    /// (the service ledger) must store the client's <b>original</b> value, using this method only to
    /// decide acceptance, so legitimate non-<c>idem:</c> clients that use accented or composed
    /// characters keep their exact key at rest. Null/empty/whitespace is treated as not reserved so
    /// the check composes with optional, nullable operation-key fields.
    /// </para>
    /// </summary>
    /// <param name="operationKey">The client-supplied operation key to inspect. May be <see langword="null"/>.</param>
    /// <returns><see langword="true"/> if the key begins with the reserved prefix in any width variant; otherwise <see langword="false"/>.</returns>
    public static bool IsReservedOperationKey(string? operationKey)
    {
        if (string.IsNullOrWhiteSpace(operationKey))
        {
            return false;
        }

        return UnicodeCompatibilityNormalizer
            .ToCompatibilityForm(operationKey.Trim())
            .StartsWith(SynthesizedOperationKeyPrefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Derives a deterministic domain <c>operationKey</c> from the idempotency identity
    /// so retries of the same client key always collapse onto the same natural-idempotency
    /// slot. The value is the <see cref="SynthesizedOperationKeyPrefix"/> followed by the
    /// lowercase hex SHA-256 of the NUL-delimited <c>(userId, effectiveRouteKey,
    /// idempotencyKey)</c> triple — the exact same identity the store keys its unique index
    /// on. Because it is a pure function of that triple, an initial request and any
    /// post-reclaim re-execution of the same client key produce an identical operation key
    /// and therefore conflict on the domain's <c>(PartInventoryId, OperationKey)</c> unique
    /// index, guaranteeing the stock delta is applied at most once. Two different users (or
    /// two different client keys) derive different operation keys, so genuinely distinct
    /// operations are never collapsed. The result is 69 characters ("idem:" + 64 hex),
    /// comfortably within the 128-char persisted column limit.
    /// </summary>
    public static string ComputeSynthesizedOperationKey(string userId, string effectiveRouteKey, string idempotencyKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(effectiveRouteKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);

        byte[] userIdBytes = Encoding.UTF8.GetBytes(userId);
        byte[] routeBytes = Encoding.UTF8.GetBytes(effectiveRouteKey);
        byte[] keyBytes = Encoding.UTF8.GetBytes(idempotencyKey);

        // NUL-delimited triple: userId + NUL + effectiveRouteKey + NUL + idempotencyKey.
        int total = userIdBytes.Length + 1 + routeBytes.Length + 1 + keyBytes.Length;
        byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(total);
        try
        {
            int offset = 0;
            userIdBytes.CopyTo(buffer, offset);
            offset += userIdBytes.Length;
            buffer[offset++] = 0;
            routeBytes.CopyTo(buffer, offset);
            offset += routeBytes.Length;
            buffer[offset++] = 0;
            keyBytes.CopyTo(buffer, offset);

            Span<byte> hash = stackalloc byte[32];
            _ = SHA256.HashData(buffer.AsSpan(0, total), hash);
            return SynthesizedOperationKeyPrefix + Convert.ToHexStringLower(hash);
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Returns whether <paramref name="key"/> is a syntactically valid
    /// Idempotency-Key. We deliberately accept a wide character set (any printable
    /// ASCII) so clients can use ULIDs, UUIDs, or opaque server-issued tokens
    /// without special encoding. Whitespace and control characters are rejected
    /// to prevent header-smuggling ambiguity.
    /// </summary>
    public static bool IsValidKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        if (key.Length > MaxKeyLength)
        {
            return false;
        }

        foreach (char c in key)
        {
            // Printable ASCII only. Rejects control characters, non-ASCII,
            // and internal whitespace (which would otherwise complicate the
            // canonicalization and log escaping of the header value).
            if (c < 0x21 || c > 0x7E)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// SHA-256 hash (hex, lowercase) of the canonical request payload. The
    /// canonical form is the concatenation of the route key, a NUL separator,
    /// and the raw request body bytes. Including the route key in the hash is a
    /// defense-in-depth measure so that even if a caller re-uses the same key
    /// across two different endpoints, the hash conflict path fires rather than
    /// silently replaying a response from an unrelated endpoint.
    /// </summary>
    public static string ComputeRequestHash(string routeKey, ReadOnlySpan<byte> body)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(routeKey);

        // Two-block hash: route key UTF-8 + NUL + body bytes.
        byte[] routeKeyBytes = Encoding.UTF8.GetBytes(routeKey);
        int total = routeKeyBytes.Length + 1 + body.Length;
        byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(total);
        try
        {
            routeKeyBytes.AsSpan().CopyTo(buffer);
            buffer[routeKeyBytes.Length] = 0;
            body.CopyTo(buffer.AsSpan(routeKeyBytes.Length + 1, body.Length));

            Span<byte> hash = stackalloc byte[32];
            _ = SHA256.HashData(buffer.AsSpan(0, total), hash);
            return Convert.ToHexStringLower(hash);
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
