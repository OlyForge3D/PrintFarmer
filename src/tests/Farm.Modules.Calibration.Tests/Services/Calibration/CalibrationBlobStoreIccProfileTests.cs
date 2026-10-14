using System.Buffers.Binary;
using System.IO.Compression;
using System.IO.Hashing;
using System.Text;
using Farm.Modules.Calibration.Services.Calibration;
using Farm.Modules.Calibration.Tests.Services;
using FluentAssertions;
using Microsoft.Extensions.Options;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;

namespace Farm.Modules.Calibration.Tests.Services.Calibration;

/// <summary>
/// Regression tests for #3324 / GHSA-gwg2-r3hj-4w44: calibration uploads must never hand
/// attacker-controlled ICC data to ImageSharp's ICC parser, and must never persist it.
/// </summary>
public sealed class CalibrationBlobStoreIccProfileTests
{
    private const int ImageSize = 8;

    public enum IccPayload
    {
        // Header declares a size far larger than the payload, so ImageSharp rejects the
        // profile with InvalidIccProfileException whenever it reads the metadata.
        InvalidHeader,

        // Structurally valid header with an A2B0 lut16 tag whose CLUT dimensions
        // (15 inputs x 255 grid points) describe an unbounded allocation.
        OversizedClut,
    }

    public static TheoryData<string, IccPayload> MaliciousUploads()
    {
        TheoryData<string, IccPayload> data = [];
        foreach (string contentType in new[] { "image/jpeg", "image/png", "image/webp" })
        {
            foreach (IccPayload payload in Enum.GetValues<IccPayload>())
            {
                data.Add(contentType, payload);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(MaliciousUploads))]
    public async Task PutAsync_WithMaliciousEmbeddedIcc_DecodesWithoutParsingAndStripsProfile(
        string contentType,
        IccPayload payload)
    {
        TestFileSystem fileSystem = new();
        CalibrationBlobStore store = CreateStore(fileSystem);
        byte[] icc = payload == IccPayload.InvalidHeader ? CreateInvalidHeaderIcc() : CreateOversizedClutIcc();
        byte[] upload = EmbedIcc(CreateCleanImage(contentType), contentType, icc);
        await using MemoryStream content = new(upload);

        CalibrationBlobMetadata metadata =
            await store.PutAsync(CreateRequest(contentType), content, CancellationToken.None);

        _ = metadata.ContentType.Should().Be(contentType);
        _ = metadata.Width.Should().Be(ImageSize);
        _ = metadata.Height.Should().Be(ImageSize);
        await AssertStoredImageHasNoIccAsync(store, metadata.StorageKey);
    }

    [Theory]
    [InlineData("image/jpeg")]
    [InlineData("image/png")]
    [InlineData("image/webp")]
    public async Task PutAsync_WithCleanImage_StillAcceptsSupportedFormats(string contentType)
    {
        TestFileSystem fileSystem = new();
        CalibrationBlobStore store = CreateStore(fileSystem);
        await using MemoryStream content = new(CreateCleanImage(contentType));

        CalibrationBlobMetadata metadata =
            await store.PutAsync(CreateRequest(contentType), content, CancellationToken.None);

        _ = metadata.ContentType.Should().Be(contentType);
        _ = metadata.Width.Should().Be(ImageSize);
        _ = metadata.Height.Should().Be(ImageSize);
        _ = metadata.SizeBytes.Should().BePositive();
    }

    [Theory]
    [InlineData("image/jpeg", IccPayload.InvalidHeader)]
    [InlineData("image/jpeg", IccPayload.OversizedClut)]
    [InlineData("image/webp", IccPayload.InvalidHeader)]
    [InlineData("image/webp", IccPayload.OversizedClut)]
    public void UntrustedDecoderOptions_DoNotReadEmbeddedIccFromJpegOrWebp(
        string contentType,
        IccPayload payload)
    {
        byte[] icc = payload == IccPayload.InvalidHeader ? CreateInvalidHeaderIcc() : CreateOversizedClutIcc();
        byte[] upload = EmbedIcc(CreateCleanImage(contentType), contentType, icc);

        using Image untrusted = Image.Load(CalibrationBlobStore.UntrustedDecoderOptions, upload);

        _ = untrusted.Metadata.IccProfile.Should().BeNull();
        _ = untrusted.Width.Should().Be(ImageSize);
    }

    [Fact]
    public void UntrustedDecoderOptions_SkipMetadataAndNeverConvertColorProfiles()
    {
        // ImageSharp's PNG loader captures iCCP bytes even with SkipMetadata; Preserve keeps
        // the ICC tag table (where CLUTs live) from ever being parsed for color conversion.
        _ = CalibrationBlobStore.UntrustedDecoderOptions.SkipMetadata.Should().BeTrue();
        _ = CalibrationBlobStore.UntrustedDecoderOptions.ColorProfileHandling
            .Should().Be(ColorProfileHandling.Preserve);
    }

    [Theory]
    [InlineData("image/jpeg")]
    [InlineData("image/png")]
    [InlineData("image/webp")]
    public void Fixture_OversizedClutIcc_IsSurfacedByDefaultDecoderOptions(string contentType)
    {
        // Proves the fixtures really reach ImageSharp's ICC path: with default options the
        // profile is materialized, so the tests above fail if the hardening is removed.
        byte[] upload = EmbedIcc(CreateCleanImage(contentType), contentType, CreateOversizedClutIcc());

        using Image defaults = Image.Load(upload);

        _ = defaults.Metadata.IccProfile.Should().NotBeNull();
    }

    private static async Task AssertStoredImageHasNoIccAsync(CalibrationBlobStore store, string key)
    {
        await using Stream stored = await store.OpenReadAsync(key, CancellationToken.None);
        using Image image = await Image.LoadAsync(stored);
        _ = image.Metadata.IccProfile.Should().BeNull();
        _ = image.Metadata.ExifProfile.Should().BeNull();
        _ = image.Metadata.XmpProfile.Should().BeNull();
    }

    private static CalibrationBlobStore CreateStore(TestFileSystem fileSystem) =>
        new(
            Options.Create(new CalibrationBlobStorageOptions
            {
                RootPath = Path.Join(Path.GetTempPath(), $"calibration-blobs-{Guid.NewGuid():N}"),
                MaxBytes = 64 * 1024,
                MaxWidth = 128,
                MaxHeight = 128,
                MaxPixels = 16_384,
            }),
            fileSystem);

    private static CalibrationBlobWriteRequest CreateRequest(string contentType) =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            contentType switch
            {
                "image/jpeg" => "photo.jpg",
                "image/png" => "photo.png",
                _ => "photo.webp",
            },
            contentType);

    private static byte[] CreateCleanImage(string contentType)
    {
        using Image<Rgb24> image = new(ImageSize, ImageSize, new Rgb24(200, 120, 40));
        using MemoryStream output = new();
        switch (contentType)
        {
            case "image/jpeg":
                image.SaveAsJpeg(output, new JpegEncoder { Quality = 90 });
                break;
            case "image/png":
                image.SaveAsPng(output, new PngEncoder());
                break;
            default:
                image.SaveAsWebp(output, new WebpEncoder { FileFormat = WebpFileFormatType.Lossless });
                break;
        }

        return output.ToArray();
    }

    private static byte[] EmbedIcc(byte[] image, string contentType, byte[] icc) =>
        contentType switch
        {
            "image/jpeg" => EmbedJpegIcc(image, icc),
            "image/png" => EmbedPngIcc(image, icc),
            _ => EmbedWebpIcc(image, icc),
        };

    private static byte[] EmbedJpegIcc(byte[] jpeg, byte[] icc)
    {
        // APP2 "ICC_PROFILE\0" segment (sequence 1 of 1) inserted directly after SOI.
        byte[] identifier = Encoding.ASCII.GetBytes("ICC_PROFILE\0");
        int segmentLength = 2 + identifier.Length + 2 + icc.Length;
        using MemoryStream output = new();
        output.Write(jpeg, 0, 2);
        output.Write([0xFF, 0xE2, (byte)(segmentLength >> 8), (byte)segmentLength]);
        output.Write(identifier);
        output.Write([1, 1]);
        output.Write(icc);
        output.Write(jpeg, 2, jpeg.Length - 2);
        return output.ToArray();
    }

    private static byte[] EmbedPngIcc(byte[] png, byte[] icc)
    {
        // iCCP chunk ("icc\0", deflate method 0, zlib stream) inserted after IHDR.
        const int afterIhdr = 8 + 4 + 4 + 13 + 4;
        using MemoryStream chunkData = new();
        chunkData.Write(Encoding.ASCII.GetBytes("icc\0"));
        chunkData.WriteByte(0);
        using (ZLibStream zlib = new(chunkData, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(icc);
        }

        byte[] type = Encoding.ASCII.GetBytes("iCCP");
        byte[] data = chunkData.ToArray();
        byte[] crcInput = [.. type, .. data];
        using MemoryStream output = new();
        output.Write(png, 0, afterIhdr);
        WriteUInt32BigEndian(output, (uint)data.Length);
        output.Write(type);
        output.Write(data);
        WriteUInt32BigEndian(output, Crc32.HashToUInt32(crcInput));
        output.Write(png, afterIhdr, png.Length - afterIhdr);
        return output.ToArray();
    }

    private static byte[] EmbedWebpIcc(byte[] webp, byte[] icc)
    {
        // Rebuild as an extended (VP8X) container: VP8X with the ICC flag, ICCP, then the
        // original bitstream chunk.
        byte[] bitstream = FindWebpBitstreamChunk(webp);
        byte[] vp8x = new byte[10];
        vp8x[0] = 0x20;
        WriteUInt24LittleEndian(vp8x.AsSpan(4), ImageSize - 1);
        WriteUInt24LittleEndian(vp8x.AsSpan(7), ImageSize - 1);

        using MemoryStream body = new();
        body.Write(Encoding.ASCII.GetBytes("WEBP"));
        WriteRiffChunk(body, "VP8X", vp8x);
        WriteRiffChunk(body, "ICCP", icc);
        body.Write(bitstream);

        using MemoryStream output = new();
        output.Write(Encoding.ASCII.GetBytes("RIFF"));
        WriteUInt32LittleEndian(output, (uint)body.Length);
        body.Position = 0;
        body.CopyTo(output);
        return output.ToArray();
    }

    private static byte[] FindWebpBitstreamChunk(byte[] webp)
    {
        int offset = 12;
        while (offset + 8 <= webp.Length)
        {
            string fourCc = Encoding.ASCII.GetString(webp, offset, 4);
            int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(webp.AsSpan(offset + 4));
            int total = 8 + size + (size & 1);
            if (fourCc is "VP8L" or "VP8 ")
            {
                return webp.AsSpan(offset, Math.Min(total, webp.Length - offset)).ToArray();
            }

            offset += total;
        }

        throw new InvalidOperationException("Encoded WebP has no bitstream chunk.");
    }

    private static byte[] CreateInvalidHeaderIcc()
    {
        byte[] icc = CreateIccHeader(totalSize: 128);
        BinaryPrimitives.WriteUInt32BigEndian(icc, 0x7FFF_FFFF);
        return icc;
    }

    private static byte[] CreateOversizedClutIcc()
    {
        const int inputChannels = 15;
        const int outputChannels = 15;
        const int tableEntries = 2;
        const int tagOffset = 128 + 4 + 12;

        using MemoryStream tag = new();
        tag.Write(Encoding.ASCII.GetBytes("mft2"));
        tag.Write(new byte[4]);
        tag.Write([inputChannels, outputChannels, 255, 0]);
        for (int i = 0; i < 9; i++)
        {
            WriteUInt32BigEndian(tag, i % 4 == 0 ? 0x0001_0000u : 0u);
        }

        tag.Write([0, tableEntries, 0, tableEntries]);
        tag.Write(new byte[inputChannels * tableEntries * 2]);
        tag.Write(new byte[64]);
        byte[] tagBytes = tag.ToArray();

        int totalSize = tagOffset + tagBytes.Length;
        byte[] icc = CreateIccHeader(totalSize);
        Array.Resize(ref icc, totalSize);
        BinaryPrimitives.WriteUInt32BigEndian(icc.AsSpan(128), 1);
        Encoding.ASCII.GetBytes("A2B0").CopyTo(icc, 132);
        BinaryPrimitives.WriteUInt32BigEndian(icc.AsSpan(136), tagOffset);
        BinaryPrimitives.WriteUInt32BigEndian(icc.AsSpan(140), (uint)tagBytes.Length);
        tagBytes.CopyTo(icc, tagOffset);
        return icc;
    }

    private static byte[] CreateIccHeader(int totalSize)
    {
        byte[] header = new byte[128];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)totalSize);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8), 0x0430_0000);
        Encoding.ASCII.GetBytes("mntr").CopyTo(header, 12);
        Encoding.ASCII.GetBytes("RGB ").CopyTo(header, 16);
        Encoding.ASCII.GetBytes("XYZ ").CopyTo(header, 20);
        Encoding.ASCII.GetBytes("acsp").CopyTo(header, 36);
        return header;
    }

    private static void WriteRiffChunk(Stream output, string fourCc, byte[] data)
    {
        output.Write(Encoding.ASCII.GetBytes(fourCc));
        WriteUInt32LittleEndian(output, (uint)data.Length);
        output.Write(data);
        if ((data.Length & 1) == 1)
        {
            output.WriteByte(0);
        }
    }

    private static void WriteUInt32BigEndian(Stream output, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, value);
        output.Write(buffer);
    }

    private static void WriteUInt32LittleEndian(Stream output, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        output.Write(buffer);
    }

    private static void WriteUInt24LittleEndian(Span<byte> destination, int value)
    {
        destination[0] = (byte)value;
        destination[1] = (byte)(value >> 8);
        destination[2] = (byte)(value >> 16);
    }
}
