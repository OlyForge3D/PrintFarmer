using System.Security.Cryptography;
using System.Text;

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
