using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using Farm.Infrastructure.Services.Printers;

namespace Farm.Infrastructure.Services.Gcode;

/// <summary>
/// A thumbnail embedded in a binary G-code (.bgcode) file.
/// </summary>
public sealed record BinaryGcodeThumbnail(BinaryGcodeThumbnailFormat Format, int Width, int Height, byte[] Data);

public enum BinaryGcodeThumbnailFormat
{
    Png = 0,
    Jpg = 1,
    Qoi = 2,
}

/// <summary>
/// Metadata and thumbnails read from the header blocks of a binary G-code file.
/// </summary>
public sealed record BinaryGcodeContent(
    IReadOnlyList<KeyValuePair<string, string>> Metadata,
    IReadOnlyList<BinaryGcodeThumbnail> Thumbnails);

/// <summary>
/// Thrown when a binary G-code file is malformed, oversized, or uses an unsupported feature.
/// The message never contains file content.
/// </summary>
public sealed class BinaryGcodeFormatException : Exception
{
    public BinaryGcodeFormatException()
        : this("invalid")
    {
    }

    public BinaryGcodeFormatException(string reason)
        : base(reason)
    {
        Reason = reason;
    }

    public BinaryGcodeFormatException(string reason, Exception innerException)
        : base(reason, innerException)
    {
        Reason = reason;
    }

    public string Reason { get; }
}

/// <summary>
/// Reads metadata and thumbnail blocks from a Prusa binary G-code (.bgcode) file, implemented from
/// the public format specification (https://github.com/prusa3d/libbgcode/blob/main/doc/specifications.md).
/// Reading stops at the first G-code block, so instruction data is never decoded. All sizes are bounded.
/// </summary>
public static class BinaryGcodeReader
{
    public const int MaximumMetadataBlockBytes = 1024 * 1024;
    public const int MaximumThumbnailBlockBytes = 2 * 1024 * 1024;
    public const int MaximumBlocks = 64;
    public const int MaximumThumbnails = 16;
    public const int MaximumTotalBytes = 16 * 1024 * 1024;

    private const uint SupportedVersion = 1;
    private const ushort ChecksumNone = 0;
    private const ushort ChecksumCrc32 = 1;
    private const ushort CompressionNone = 0;
    private const ushort CompressionDeflate = 1;
    private const ushort EncodingIni = 0;

    private const ushort BlockFileMetadata = 0;
    private const ushort BlockGcode = 1;
    private const ushort BlockSlicerMetadata = 2;
    private const ushort BlockPrinterMetadata = 3;
    private const ushort BlockPrintMetadata = 4;
    private const ushort BlockThumbnail = 5;

    private static ReadOnlySpan<byte> Magic => "GCDE"u8;

    /// <summary>Returns true when the bytes start with the binary G-code magic.</summary>
    public static bool HasSignature(ReadOnlySpan<byte> prefix) => prefix.Length >= 4 && prefix[..4].SequenceEqual(Magic);

    /// <summary>
    /// Peeks the first bytes of a seekable stream to detect the binary G-code magic, restoring the position.
    /// Non-seekable streams always return false.
    /// </summary>
    public static async Task<bool> IsBinaryGcodeAsync(Stream stream, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek || !stream.CanRead)
        {
            return false;
        }

        long position = stream.Position;
        try
        {
            byte[] prefix = new byte[4];
            int read = await stream.ReadAtLeastAsync(prefix, prefix.Length, throwOnEndOfStream: false, ct);
            return read == prefix.Length && HasSignature(prefix);
        }
        finally
        {
            stream.Position = position;
        }
    }

    /// <summary>
    /// Reads metadata and thumbnail blocks from the current position of <paramref name="stream"/>.
    /// </summary>
    /// <exception cref="BinaryGcodeFormatException">The file is malformed, oversized, or unsupported.</exception>
    public static async Task<BinaryGcodeContent> ReadAsync(Stream stream, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        byte[] fileHeader = await ReadExactAsync(stream, 10, ct);
        if (!HasSignature(fileHeader))
        {
            throw new BinaryGcodeFormatException("missing_signature");
        }

        uint version = BinaryPrimitives.ReadUInt32LittleEndian(fileHeader.AsSpan(4, 4));
        if (version != SupportedVersion)
        {
            throw new BinaryGcodeFormatException("unsupported_version");
        }

        ushort checksumType = BinaryPrimitives.ReadUInt16LittleEndian(fileHeader.AsSpan(8, 2));
        if (checksumType is not (ChecksumNone or ChecksumCrc32))
        {
            throw new BinaryGcodeFormatException("unsupported_checksum");
        }

        List<KeyValuePair<string, string>> metadata = [];
        List<BinaryGcodeThumbnail> thumbnails = [];
        long totalBytes = 0;

        // One extra iteration lets a 65th header be a GCode block/EOF, or reject it as too many blocks.
        for (int blockIndex = 0; blockIndex <= MaximumBlocks; blockIndex++)
        {
            byte[] header = await ReadExactAsync(stream, 8, ct, allowEndOfStream: blockIndex > 0);
            if (header.Length == 0)
            {
                break;
            }

            ushort type = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(0, 2));
            ushort compression = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2, 2));
            uint uncompressedSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4, 4));

            if (type == BlockGcode)
            {
                break;
            }

            if (blockIndex >= MaximumBlocks)
            {
                throw new BinaryGcodeFormatException("too_many_blocks");
            }

            if (type > BlockThumbnail)
            {
                throw new BinaryGcodeFormatException("unknown_block_type");
            }

            byte[] compressedSizeBytes = compression == CompressionNone ? [] : await ReadExactAsync(stream, 4, ct);
            uint storedSize = compression == CompressionNone
                ? uncompressedSize
                : BinaryPrimitives.ReadUInt32LittleEndian(compressedSizeBytes);

            int maximum = type == BlockThumbnail ? MaximumThumbnailBlockBytes : MaximumMetadataBlockBytes;
            if (uncompressedSize > maximum || storedSize > maximum)
            {
                throw new BinaryGcodeFormatException("block_too_large");
            }

            totalBytes += Math.Max(uncompressedSize, storedSize);
            if (totalBytes > MaximumTotalBytes)
            {
                throw new BinaryGcodeFormatException("header_too_large");
            }

            byte[] parameters = await ReadExactAsync(stream, type == BlockThumbnail ? 6 : 2, ct);
            byte[] stored = await ReadExactAsync(stream, (int)storedSize, ct);

            if (checksumType == ChecksumCrc32)
            {
                byte[] checksum = await ReadExactAsync(stream, 4, ct);
                uint crc = Crc32.Append(Crc32.Append(Crc32.Append(Crc32.Append(0, header), compressedSizeBytes), parameters), stored);
                if (crc != BinaryPrimitives.ReadUInt32LittleEndian(checksum))
                {
                    throw new BinaryGcodeFormatException("checksum_mismatch");
                }
            }

            byte[]? data = Decompress(stored, compression, (int)uncompressedSize);
            if (data == null)
            {
                // Unsupported compression (e.g. Heatshrink): skip the block, keep reading.
                continue;
            }

            if (type == BlockThumbnail)
            {
                ushort format = BinaryPrimitives.ReadUInt16LittleEndian(parameters.AsSpan(0, 2));
                int width = BinaryPrimitives.ReadUInt16LittleEndian(parameters.AsSpan(2, 2));
                int height = BinaryPrimitives.ReadUInt16LittleEndian(parameters.AsSpan(4, 2));
                bool validImage = format switch
                {
                    (ushort)BinaryGcodeThumbnailFormat.Png => PrinterFilePreviewTransport.IsValidImage(data, "image/png"),
                    (ushort)BinaryGcodeThumbnailFormat.Jpg => PrinterFilePreviewTransport.IsValidImage(data, "image/jpeg"),
                    (ushort)BinaryGcodeThumbnailFormat.Qoi => data.AsSpan().StartsWith("qoif"u8),
                    _ => false,
                };
                if (validImage && width > 0 && height > 0 && thumbnails.Count < MaximumThumbnails)
                {
                    thumbnails.Add(new BinaryGcodeThumbnail((BinaryGcodeThumbnailFormat)format, width, height, data));
                }

                continue;
            }

            ushort encoding = BinaryPrimitives.ReadUInt16LittleEndian(parameters);
            if (encoding == EncodingIni && type is (BlockFileMetadata or BlockSlicerMetadata or BlockPrinterMetadata or BlockPrintMetadata))
            {
                ParseIni(data, metadata);
            }
        }

        return new BinaryGcodeContent(metadata, thumbnails);
    }

    /// <summary>
    /// Converts binary G-code metadata into equivalent text G-code comments
    /// (<c>; key = value</c> and <c>; thumbnail begin</c> blocks) so the existing text extractor can parse it.
    /// PNG thumbnails are preferred; JPG thumbnails are only emitted when no PNG exists. QOI is not emitted.
    /// </summary>
    public static string ToCommentText(BinaryGcodeContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        StringBuilder builder = new();

        foreach ((string key, string value) in content.Metadata)
        {
            if (key.StartsWith("thumbnail", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (key.Equals("Producer", StringComparison.OrdinalIgnoreCase))
            {
                builder.Append("; generated by ").Append(value).Append('\n');
                continue;
            }

            builder.Append("; ").Append(key).Append(" = ").Append(value).Append('\n');
        }

        List<BinaryGcodeThumbnail> pngs = content.Thumbnails.Where(t => t.Format == BinaryGcodeThumbnailFormat.Png).ToList();
        IEnumerable<BinaryGcodeThumbnail> emitted = pngs.Count > 0
            ? pngs
            : content.Thumbnails.Where(t => t.Format == BinaryGcodeThumbnailFormat.Jpg);

        foreach (BinaryGcodeThumbnail thumbnail in emitted)
        {
            string base64 = Convert.ToBase64String(thumbnail.Data);
            string tag = thumbnail.Format == BinaryGcodeThumbnailFormat.Jpg ? "thumbnail_JPG" : "thumbnail";
            builder.Append(CultureInfo.InvariantCulture, $"; {tag} begin {thumbnail.Width}x{thumbnail.Height} {base64.Length}\n");
            for (int offset = 0; offset < base64.Length; offset += 78)
            {
                builder.Append("; ").Append(base64.AsSpan(offset, Math.Min(78, base64.Length - offset))).Append('\n');
            }

            builder.Append(CultureInfo.InvariantCulture, $"; {tag} end\n");
        }

        return builder.ToString();
    }

    /// <summary>
    /// Reads a stream as G-code text. Binary G-code is converted to equivalent comment text.
    /// </summary>
    /// <exception cref="BinaryGcodeFormatException">Binary G-code is malformed, oversized, or unsupported.</exception>
    public static async Task<string> ReadAsTextAsync(Stream stream, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (await IsBinaryGcodeAsync(stream, ct))
        {
            return ToCommentText(await ReadAsync(stream, ct));
        }

        using StreamReader reader = new(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        return await reader.ReadToEndAsync(ct);
    }

    /// <summary>Reads a file as G-code text; see <see cref="ReadAsTextAsync(Stream, CancellationToken)"/>.</summary>
    public static async Task<string> ReadFileAsTextAsync(string path, CancellationToken ct = default)
    {
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await ReadAsTextAsync(stream, ct);
    }

    /// <summary>Converts in-memory file content to G-code text; see <see cref="ReadAsTextAsync(Stream, CancellationToken)"/>.</summary>
    public static async Task<string> ReadBytesAsTextAsync(byte[] content, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!HasSignature(content))
        {
            return Encoding.UTF8.GetString(content);
        }

        using MemoryStream stream = new(content, writable: false);
        return await ReadAsTextAsync(stream, ct);
    }

    private static void ParseIni(byte[] data, List<KeyValuePair<string, string>> metadata)
    {
        string text;
        try
        {
            text = new UTF8Encoding(false, true).GetString(data);
        }
        catch (DecoderFallbackException)
        {
            throw new BinaryGcodeFormatException("invalid_metadata_encoding");
        }

        foreach (string line in text.Split('\n').Select(rawLine => rawLine.TrimEnd('\r')))
        {
            int separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            string key = line[..separator].Trim();
            string value = line[(separator + 1)..].Trim();
            if (key.Length == 0 || key.Any(char.IsControl) || value.Any(char.IsControl))
            {
                continue;
            }

            metadata.Add(new KeyValuePair<string, string>(key, value));
        }
    }

    private static byte[]? Decompress(byte[] stored, ushort compression, int uncompressedSize)
    {
        if (compression == CompressionNone)
        {
            return stored;
        }

        if (compression != CompressionDeflate)
        {
            return null;
        }

        // libbgcode writes zlib-wrapped deflate; accept raw deflate as a tolerant fallback.
        try
        {
            return Inflate(stored, uncompressedSize, zlibWrapped: true);
        }
        catch (InvalidDataException)
        {
            try
            {
                return Inflate(stored, uncompressedSize, zlibWrapped: false);
            }
            catch (InvalidDataException)
            {
                throw new BinaryGcodeFormatException("invalid_compressed_data");
            }
        }
    }

    private static byte[] Inflate(byte[] stored, int uncompressedSize, bool zlibWrapped)
    {
        using MemoryStream input = new(stored, writable: false);
        using Stream inflater = zlibWrapped
            ? new ZLibStream(input, CompressionMode.Decompress)
            : new DeflateStream(input, CompressionMode.Decompress);
        byte[] output = new byte[uncompressedSize];
        int read = inflater.ReadAtLeast(output, uncompressedSize, throwOnEndOfStream: false);
        if (read != uncompressedSize || inflater.ReadByte() != -1)
        {
            throw new BinaryGcodeFormatException("decompressed_size_mismatch");
        }

        return output;
    }

    private static async Task<byte[]> ReadExactAsync(Stream stream, int count, CancellationToken ct, bool allowEndOfStream = false)
    {
        if (count == 0)
        {
            return [];
        }

        byte[] buffer = new byte[count];
        int read = await stream.ReadAtLeastAsync(buffer, count, throwOnEndOfStream: false, ct);
        if (read == count)
        {
            return buffer;
        }

        if (read == 0 && allowEndOfStream)
        {
            return [];
        }

        throw new BinaryGcodeFormatException("truncated");
    }

    private static class Crc32
    {
        private static readonly uint[] Table = CreateTable();

        public static uint Append(uint crc, ReadOnlySpan<byte> data)
        {
            crc = ~crc;
            foreach (byte value in data)
            {
                crc = Table[(crc ^ value) & 0xFF] ^ (crc >> 8);
            }

            return ~crc;
        }

        private static uint[] CreateTable()
        {
            uint[] table = new uint[256];
            for (uint i = 0; i < table.Length; i++)
            {
                uint entry = i;
                for (int bit = 0; bit < 8; bit++)
                {
                    entry = (entry & 1) != 0 ? 0xEDB88320u ^ (entry >> 1) : entry >> 1;
                }

                table[i] = entry;
            }

            return table;
        }
    }
}
