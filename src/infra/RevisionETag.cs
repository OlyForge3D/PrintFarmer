using System.Buffers.Binary;

namespace Farm.Infrastructure;

/// <summary>
/// Encodes portable numeric revisions as opaque strong entity tags.
/// </summary>
public static class RevisionETag
{
    private const byte TokenVersion = 1;
    private const int EncodedByteCount = sizeof(byte) + sizeof(long);

    /// <summary>Encodes a positive revision as an unquoted base-64 token.</summary>
    public static string Encode(long revision) => Convert.ToBase64String(EncodeBytes(revision));

    /// <summary>Encodes a positive revision as a quoted strong ETag header value.</summary>
    public static string EncodeQuoted(long revision) => $"\"{Encode(revision)}\"";

    /// <summary>Encodes a positive revision as a versioned big-endian byte representation.</summary>
    public static byte[] EncodeBytes(long revision)
    {
        if (revision < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(revision),
                revision,
                "A persisted revision must be positive.");
        }

        byte[] bytes = new byte[EncodedByteCount];
        bytes[0] = TokenVersion;
        BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(sizeof(byte)), revision);
        return bytes;
    }

    /// <summary>
    /// Decodes an opaque revision token. Legacy tokens with a different length map to zero
    /// so they are rejected as stale rather than accepted as a current revision.
    /// </summary>
    public static long Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != EncodedByteCount || bytes[0] != TokenVersion)
        {
            return 0;
        }

        long revision = BinaryPrimitives.ReadInt64BigEndian(bytes[sizeof(byte)..]);
        return revision > 0 ? revision : 0;
    }
}
