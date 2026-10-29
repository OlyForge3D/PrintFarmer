using System.Buffers.Binary;

namespace Farm.Infrastructure.Services.Printers;

/// <summary>Checks inert raster containers and dimensions without allocating decoded pixels.</summary>
public static class PrinterFileImageValidator
{
    /// <summary>Rejects truncated containers, unsupported formats and image-dimension bombs.</summary>
    public static bool TryValidate(byte[] image, out string? contentType, out int width, out int height)
    {
        contentType = null;
        width = height = 0;
        if (image.Length is 0 or > PrinterFileEnrichmentLimits.MaximumImageBytes)
        {
            return false;
        }

        string? type = PrinterFilePreviewTransport.DetectImageContentType(image);
        bool valid = type switch
        {
            "image/png" => TryPng(image, out width, out height),
            "image/jpeg" => TryJpeg(image, out width, out height),
            "image/webp" => TryWebP(image, out width, out height),
            _ => false,
        };
        if (!valid || width is <= 0 or > PrinterFileEnrichmentLimits.MaximumImageEdge ||
            height is <= 0 or > PrinterFileEnrichmentLimits.MaximumImageEdge ||
            (long)width * height > PrinterFileEnrichmentLimits.MaximumImagePixels)
        {
            width = height = 0;
            return false;
        }

        contentType = type;
        return true;
    }

    private static bool TryPng(ReadOnlySpan<byte> image, out int width, out int height)
    {
        width = height = 0;
        if (image.Length < 45 || !image.Slice(12, 4).SequenceEqual("IHDR"u8) ||
            BinaryPrimitives.ReadUInt32BigEndian(image.Slice(8, 4)) != 13)
        {
            return false;
        }

        width = BinaryPrimitives.ReadInt32BigEndian(image.Slice(16, 4));
        height = BinaryPrimitives.ReadInt32BigEndian(image.Slice(20, 4));
        bool hasPixels = false;
        for (int offset = 8; offset <= image.Length - 12;)
        {
            uint size = BinaryPrimitives.ReadUInt32BigEndian(image.Slice(offset, 4));
            if (size > image.Length - offset - 12)
            {
                return false;
            }

            ReadOnlySpan<byte> type = image.Slice(offset + 4, 4);
            if (type.SequenceEqual("acTL"u8) || (offset != 8 && type.SequenceEqual("IHDR"u8)))
            {
                return false;
            }

            hasPixels |= type.SequenceEqual("IDAT"u8) && size > 0;
            offset += (int)size + 12;
            if (type.SequenceEqual("IEND"u8))
            {
                return hasPixels && size == 0 && offset == image.Length;
            }
        }

        return false;
    }

    private static bool TryJpeg(ReadOnlySpan<byte> image, out int width, out int height)
    {
        width = height = 0;
        if (image.Length < 4 || image[^2] != 0xff || image[^1] != 0xd9)
        {
            return false;
        }

        int offset = 2;
        while (offset < image.Length - 2)
        {
            if (image[offset++] != 0xff)
            {
                return false;
            }

            while (offset < image.Length && image[offset] == 0xff)
            {
                offset++;
            }

            if (offset >= image.Length)
            {
                return false;
            }

            byte marker = image[offset++];
            if (marker == 0xda)
            {
                return width > 0 && height > 0 && offset + 2 < image.Length;
            }

            if (marker is 0 or 0xd8 or 0xd9 || marker is >= 0xd0 and <= 0xd7 ||
                offset + 2 > image.Length)
            {
                return false;
            }

            int size = BinaryPrimitives.ReadUInt16BigEndian(image.Slice(offset, 2));
            if (size < 2 || size > image.Length - offset)
            {
                return false;
            }

            if (marker is >= 0xc0 and <= 0xc3 or >= 0xc5 and <= 0xc7 or >= 0xc9 and <= 0xcb or >= 0xcd and <= 0xcf)
            {
                if (size < 8 || width != 0)
                {
                    return false;
                }

                height = BinaryPrimitives.ReadUInt16BigEndian(image.Slice(offset + 3, 2));
                width = BinaryPrimitives.ReadUInt16BigEndian(image.Slice(offset + 5, 2));
            }

            offset += size;
        }

        return false;
    }

    private static bool TryWebP(ReadOnlySpan<byte> image, out int width, out int height)
    {
        width = height = 0;
        if (image.Length < 30 || BinaryPrimitives.ReadUInt32LittleEndian(image.Slice(4, 4)) != image.Length - 8)
        {
            return false;
        }

        int canvasWidth = 0;
        int canvasHeight = 0;
        bool hasPixels = false;
        for (int offset = 12; offset <= image.Length - 8;)
        {
            ReadOnlySpan<byte> type = image.Slice(offset, 4);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(image.Slice(offset + 4, 4));
            if (size > image.Length - offset - 8 || size + (size & 1) > image.Length - offset - 8)
            {
                return false;
            }

            ReadOnlySpan<byte> data = image.Slice(offset + 8, (int)size);
            if (type.SequenceEqual("ANIM"u8) || type.SequenceEqual("ANMF"u8))
            {
                return false;
            }

            if (type.SequenceEqual("VP8X"u8))
            {
                if (offset != 12 || size != 10 || (data[0] & 0x02) != 0)
                {
                    return false;
                }

                canvasWidth = 1 + ReadUInt24(data.Slice(4, 3));
                canvasHeight = 1 + ReadUInt24(data.Slice(7, 3));
            }
            else if (type.SequenceEqual("VP8 "u8))
            {
                if (hasPixels || size < 10 || (data[0] & 1) != 0 || !data.Slice(3, 3).SequenceEqual(new byte[] { 0x9d, 0x01, 0x2a }))
                {
                    return false;
                }

                width = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(6, 2)) & 0x3fff;
                height = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(8, 2)) & 0x3fff;
                hasPixels = true;
            }
            else if (type.SequenceEqual("VP8L"u8))
            {
                if (hasPixels || size < 5 || data[0] != 0x2f || (data[4] & 0xe0) != 0)
                {
                    return false;
                }

                uint bits = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(1, 4));
                width = (int)(bits & 0x3fff) + 1;
                height = (int)((bits >> 14) & 0x3fff) + 1;
                hasPixels = true;
            }

            offset += 8 + (int)(size + (size & 1));
            if (offset == image.Length)
            {
                return hasPixels && (canvasWidth == 0 || (canvasWidth == width && canvasHeight == height));
            }
        }

        return false;
    }

    private static int ReadUInt24(ReadOnlySpan<byte> bytes) => bytes[0] | (bytes[1] << 8) | (bytes[2] << 16);
}
