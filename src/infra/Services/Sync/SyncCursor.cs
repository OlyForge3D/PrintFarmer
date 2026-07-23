using System.Globalization;
using System.Text;
using Farm.Infrastructure.Exceptions;

namespace Farm.Infrastructure.Services.Sync;

/// <summary>
/// Encodes and decodes the opaque continuation cursor used by the #845 pull endpoint. A cursor
/// is the base64url encoding of <c>v1:{revision}</c>. Keeping it opaque means clients treat it
/// as a black box (its internal format can evolve), and any value the server did not issue —
/// wrong version tag, non-numeric revision, negative revision, or malformed base64 — is
/// rejected with <see cref="InvalidSyncCursorException"/> rather than being trusted as a
/// position. Because the pull query always re-applies visibility filtering regardless of the
/// decoded revision, a forged cursor can at most shift the caller's own position; it can never
/// surface another user's changes.
/// </summary>
public static class SyncCursor
{
    private const string Prefix = "v1:";

    /// <summary>Encodes a revision into an opaque base64url cursor token.</summary>
    /// <param name="revision">The (non-negative) revision the cursor points at.</param>
    public static string Encode(long revision)
    {
        if (revision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(revision), "Cursor revision must be non-negative");
        }

        byte[] bytes = Encoding.UTF8.GetBytes(Prefix + revision.ToString(CultureInfo.InvariantCulture));
        return ToBase64Url(bytes);
    }

    /// <summary>
    /// Decodes an opaque cursor into its exclusive lower-bound revision. A null or empty cursor
    /// means "from the beginning" and returns 0. Any non-empty value that is not a well-formed,
    /// server-issued cursor throws <see cref="InvalidSyncCursorException"/>.
    /// </summary>
    /// <param name="cursor">The opaque cursor token, or null/empty to start from the beginning.</param>
    public static long Decode(string? cursor)
    {
        if (string.IsNullOrEmpty(cursor))
        {
            return 0;
        }

        byte[] bytes;
        try
        {
            bytes = FromBase64Url(cursor);
        }
        catch (FormatException ex)
        {
            throw new InvalidSyncCursorException("The supplied sync cursor is not valid base64url", ex);
        }

        string decoded = Encoding.UTF8.GetString(bytes);
        if (!decoded.StartsWith(Prefix, StringComparison.Ordinal))
        {
            throw new InvalidSyncCursorException("The supplied sync cursor has an unrecognized format");
        }

        string revisionPart = decoded[Prefix.Length..];
        if (!long.TryParse(revisionPart, NumberStyles.None, CultureInfo.InvariantCulture, out long revision))
        {
            throw new InvalidSyncCursorException("The supplied sync cursor does not encode a valid revision");
        }

        return revision;
    }

    private static string ToBase64Url(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static byte[] FromBase64Url(string value)
    {
        string padded = value
            .Replace('-', '+')
            .Replace('_', '/');

        switch (padded.Length % 4)
        {
            case 2:
                padded += "==";
                break;
            case 3:
                padded += "=";
                break;
            case 1:
                throw new FormatException("Invalid base64url length");
            default:
                break;
        }

        return Convert.FromBase64String(padded);
    }
}
