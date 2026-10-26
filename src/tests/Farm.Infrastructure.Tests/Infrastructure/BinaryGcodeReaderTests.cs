using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Farm.Infrastructure.Repositories.UnitOfWork;
using Farm.Infrastructure.Services.FileManagement;
using Farm.Infrastructure.Services.Gcode;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.SignalR;
using Farm.Infrastructure.Services.StorageManagement;
using Farm.Infrastructure.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Farm.Infrastructure.Tests.Infrastructure;

public class BinaryGcodeReaderTests
{
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");
    private static readonly byte[] Jpg = [0xff, 0xd8, 0xff, 0xe0, 0, 2, 0xff, 0xd9];

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task ReadAsTextAsync_MetadataAndThumbnails_PreserveExtractorContract(int checksum)
    {
        byte[] file = CreateFile(
        [
            MetadataBlock(0, "Producer = PrusaSlicer 2.9.4\n"),
            MetadataBlock(2, "layer_height = 0.2\nfilament_type = PLA\n"),
            MetadataBlock(3, "printer_model = MK4S\nnozzle_diameter = 0.4\n"),
            MetadataBlock(4, "estimated printing time (normal mode) = 2h 30m 45s\nfilament used [g] = 25.5\n"),
            Block(5, Jpg, format: 1, width: 640),
            Block(5, "qoif-test"u8.ToArray(), format: 2),
            Block(5, Png),
        ], checksum: (ushort)checksum);
        using var stream = new MemoryStream(file);

        string text = await BinaryGcodeReader.ReadAsTextAsync(stream);
        GcodeMetadataExtracted result = await Extractor().ExtractMetadataAsync(text);

        Assert.Equal("PrusaSlicer", result.SlicerName);
        Assert.Equal("2.9.4", result.SlicerVersion);
        Assert.Equal("PLA", result.Material);
        Assert.Equal(0.2, result.LayerHeight);
        Assert.Equal(0.4, result.NozzleDiameter);
        Assert.Equal(150.75, result.EstimatedPrintTimeMinutes);
        Assert.Equal(25.5, result.FilamentWeightGrams);
        Assert.Equal(Png, result.ThumbnailData);
        Assert.Contains("; printer_model = MK4S\n", text);
        Assert.DoesNotContain("thumbnail_JPG", text);
        Assert.DoesNotContain("thumbnail_QOI", text);
        Assert.True(stream.CanRead);
        Assert.Equal(file.Length, stream.Position);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadAsync_DeflateMetadataAndImage_DecodesWrappedAndRawStreams(bool rawDeflate)
    {
        byte[] file = CreateFile(
        [
            Block(2, "filament_type = PETG\n"u8.ToArray(), compression: 1, rawDeflate: rawDeflate),
            Block(5, Png, compression: 1, rawDeflate: rawDeflate),
        ]);
        using var stream = new MemoryStream(file);

        BinaryGcodeContent result = await BinaryGcodeReader.ReadAsync(stream);

        Assert.Equal(new KeyValuePair<string, string>("filament_type", "PETG"), Assert.Single(result.Metadata));
        Assert.Equal(Png, Assert.Single(result.Thumbnails).Data);
    }

    [Fact]
    public async Task ReadAsTextAsync_JpegOnlyThumbnail_PreservesImageBytes()
    {
        string text = await BinaryGcodeReader.ReadBytesAsTextAsync(CreateFile([Block(5, Jpg, format: 1)]));
        GcodeMetadataExtracted result = await Extractor().ExtractMetadataAsync(text);

        Assert.Contains("; thumbnail_JPG begin 200x200 ", text);
        Assert.Equal(Jpg, result.ThumbnailData);
    }

    [Fact]
    public async Task ReadAsTextAsync_QoiOnlyThumbnail_DoesNotEmitUnsupportedImage()
    {
        string text = await BinaryGcodeReader.ReadBytesAsTextAsync(CreateFile(
            [MetadataBlock(0, "Producer = PrusaSlicer 2.9.4\n"), Block(5, "qoif-test"u8.ToArray(), format: 2)]));
        GcodeMetadataExtracted result = await Extractor().ExtractMetadataAsync(text);

        Assert.Equal("PrusaSlicer", result.SlicerName);
        Assert.Null(result.ThumbnailData);
        Assert.DoesNotContain("thumbnail", text);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(99)]
    public async Task ReadAsync_InvalidThumbnailSignature_DropsImageButKeepsMetadata(int format)
    {
        byte[] file = CreateFile(
            [MetadataBlock(2, "filament_type = PLA\n"), Block(5, "<svg>not a raster</svg>"u8.ToArray(), format: (ushort)format)]);
        using var stream = new MemoryStream(file);

        BinaryGcodeContent result = await BinaryGcodeReader.ReadAsync(stream);

        Assert.Empty(result.Thumbnails);
        Assert.Single(result.Metadata);
        Assert.DoesNotContain("thumbnail", BinaryGcodeReader.ToCommentText(result));
    }

    [Fact]
    public async Task ReadAsync_ZeroDimensionsAndExcessThumbnails_OnlyRetainsBoundedValidImages()
    {
        byte[][] blocks = [Block(5, Png, width: 0), .. Enumerable.Repeat(Block(5, Png), 17)];
        using var stream = new MemoryStream(CreateFile(blocks));

        BinaryGcodeContent result = await BinaryGcodeReader.ReadAsync(stream);

        Assert.Equal(16, result.Thumbnails.Count);
        Assert.All(result.Thumbnails, thumbnail => Assert.Equal(200, thumbnail.Width));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task ReadAsync_UnsupportedCompression_SkipsBlockAndReadsFollowingMetadata(int compression)
    {
        using var stream = new MemoryStream(CreateFile(
            [Block(2, "ignored = content\n"u8.ToArray(), compression: (ushort)compression), MetadataBlock(4, "filament_g = 12\n")]));

        BinaryGcodeContent result = await BinaryGcodeReader.ReadAsync(stream);

        Assert.Equal(new KeyValuePair<string, string>("filament_g", "12"), Assert.Single(result.Metadata));
    }

    [Fact]
    public async Task ReadAsTextAsync_UnknownEncodingAndThumbnailMetadata_CannotInjectThumbnailComments()
    {
        using var stream = new MemoryStream(CreateFile(
        [
            Block(2, "filament_type = ABS\n"u8.ToArray(), encoding: 1),
            MetadataBlock(2, "thumbnail begin 200x200 = ignored\nthumbnail end = ignored\nfilament_type = PLA\n"),
        ]));

        string text = await BinaryGcodeReader.ReadAsTextAsync(stream);

        Assert.Equal("; filament_type = PLA\n", text);
    }

    [Theory]
    [InlineData("version", "unsupported_version")]
    [InlineData("checksum", "unsupported_checksum")]
    [InlineData("type", "unknown_block_type")]
    [InlineData("signature", "missing_signature")]
    [InlineData("utf8", "invalid_metadata_encoding")]
    [InlineData("crc", "checksum_mismatch")]
    [InlineData("inflated-size", "decompressed_size_mismatch")]
    public async Task ReadAsync_InvalidFormat_ThrowsReasonWithoutFileContent(string corruption, string reason)
    {
        byte[] block = MetadataBlock(2, "secret-fixture-value = private\n");
        byte[] file = corruption switch
        {
            "version" => CreateFile([block], version: 2),
            "checksum" => CreateFile([block], checksum: 2),
            "type" => CreateFile([Block(42, [])]),
            "utf8" => CreateFile([Block(2, [0xff, 0xfe, 0xff])]),
            "inflated-size" => CreateFile([Block(2, "layer_height = 0.2\n"u8.ToArray(), compression: 1, declaredSize: 1)]),
            _ => CreateFile([block]),
        };
        if (corruption == "signature")
        {
            file[0] = 0;
        }
        else if (corruption == "crc")
        {
            file[20] ^= 1;
        }
        using var stream = new MemoryStream(file);

        BinaryGcodeFormatException error = await Assert.ThrowsAsync<BinaryGcodeFormatException>(
            () => BinaryGcodeReader.ReadAsync(stream));

        Assert.Equal(reason, error.Reason);
        Assert.Equal(reason, error.Message);
        Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(17)]
    [InlineData(19)]
    [InlineData(25)]
    public async Task ReadAsync_TruncatedHeaderOrBlock_Throws(int length)
    {
        byte[] file = CreateFile([MetadataBlock(0, "Producer = PrusaSlicer\n")]);
        using var stream = new MemoryStream(file[..length]);

        BinaryGcodeFormatException error = await Assert.ThrowsAsync<BinaryGcodeFormatException>(
            () => BinaryGcodeReader.ReadAsync(stream));

        Assert.Equal("truncated", error.Reason);
    }

    [Theory]
    [InlineData(2, 1048577, 0)]
    [InlineData(5, 2097153, 0)]
    [InlineData(2, 1048577, 1)]
    [InlineData(5, 2097153, 1)]
    public async Task ReadAsync_OversizedBlock_RejectsBeforeReadingPayload(int type, int size, int compression)
    {
        byte[] file = CreateFile([Block((ushort)type, [], compression: (ushort)compression)]);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(compression == 0 ? 14 : 18), (uint)size);
        using var stream = new MemoryStream(file);

        BinaryGcodeFormatException error = await Assert.ThrowsAsync<BinaryGcodeFormatException>(
            () => BinaryGcodeReader.ReadAsync(stream));

        Assert.Equal("block_too_large", error.Reason);
        Assert.Equal(compression == 0 ? 18 : 22, stream.Position);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadAsync_Exactly64HeaderBlocks_AllowsEndOfFileOrGcode(bool gcode)
    {
        using var stream = new MemoryStream(CreateFile(
            Enumerable.Repeat(MetadataBlock(2, "layer_height = 0.2\n"), 64).ToArray(), gcode: gcode));

        BinaryGcodeContent result = await BinaryGcodeReader.ReadAsync(stream);

        Assert.Equal(64, result.Metadata.Count);
    }

    [Fact]
    public async Task ReadAsync_65HeaderBlocks_ThrowsInsteadOfReturningPartialMetadata()
    {
        using var stream = new MemoryStream(CreateFile(
            Enumerable.Repeat(MetadataBlock(2, "layer_height = 0.2\n"), 65).ToArray()));

        BinaryGcodeFormatException error = await Assert.ThrowsAsync<BinaryGcodeFormatException>(
            () => BinaryGcodeReader.ReadAsync(stream));

        Assert.Equal("too_many_blocks", error.Reason);
    }

    [Fact]
    public async Task ReadAsync_TotalDecompressedBudget_AcceptsLimitAndRejectsNextBlock()
    {
        byte[] metadata = Enumerable.Repeat((byte)'#', 1024 * 1024).ToArray();
        byte[] block = Block(2, metadata, compression: 1);
        using var allowed = new MemoryStream(CreateFile(Enumerable.Repeat(block, 16).ToArray()));
        using var oversized = new MemoryStream(CreateFile(Enumerable.Repeat(block, 17).ToArray()));

        Assert.Empty((await BinaryGcodeReader.ReadAsync(allowed)).Metadata);
        BinaryGcodeFormatException error = await Assert.ThrowsAsync<BinaryGcodeFormatException>(
            () => BinaryGcodeReader.ReadAsync(oversized));

        Assert.Equal("header_too_large", error.Reason);
    }

    [Fact]
    public async Task ReadAsync_GcodeBlock_StopsBeforeInstructionPayload()
    {
        byte[] file = CreateFile([MetadataBlock(0, "Producer = PrusaSlicer\n")]);
        using var stream = new MemoryStream([.. file, .. "never interpret G1 X100"u8.ToArray()]);

        Assert.Single((await BinaryGcodeReader.ReadAsync(stream)).Metadata);
        Assert.Equal(file.Length, stream.Position);
    }

    [Fact]
    public async Task ReadAsTextAsync_MalformedBinary_PropagatesFormatFailure()
    {
        using var stream = new MemoryStream("GCDE"u8.ToArray());
        BinaryGcodeFormatException error = await Assert.ThrowsAsync<BinaryGcodeFormatException>(
            () => BinaryGcodeReader.ReadAsTextAsync(stream));

        Assert.Equal("truncated", error.Message);
    }

    [Fact]
    public async Task ReadAsTextAsync_PlainTextAndSignatureProbe_PreserveContentAndPosition()
    {
        const string text = "; generated by PrusaSlicer 2.9.4\r\n; filament_type = PLA\nG1 X10\n";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));

        Assert.False(await BinaryGcodeReader.IsBinaryGcodeAsync(stream));
        Assert.Equal(0, stream.Position);
        Assert.Equal(text, await BinaryGcodeReader.ReadAsTextAsync(stream));
        Assert.Equal(text, await BinaryGcodeReader.ReadBytesAsTextAsync(Encoding.UTF8.GetBytes(text)));
        using var binary = new MemoryStream([0, 0, .. CreateFile([MetadataBlock(0, "Producer = PrusaSlicer\n")])]);
        binary.Position = 2;
        Assert.True(await BinaryGcodeReader.IsBinaryGcodeAsync(binary));
        Assert.Equal(2, binary.Position);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task LocalFileServices_BinaryGcode_ExtractMetadataAndPersistExactThumbnail(int format)
    {
        string directory = Path.Join(Path.GetTempPath(), $"bgcode-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string path = Path.Join(directory, "part.bgcode");
        byte[] image = format == 0 ? Png : Jpg;
        byte[] file = CreateFile(
            [MetadataBlock(0, "Producer = PrusaSlicer 2.9.4\n"), MetadataBlock(2, "filament_type = PETG\n"), Block(5, image, format: (ushort)format)]);
        try
        {
            await File.WriteAllBytesAsync(path, file);
            var storage = new Mock<IStoragePathService>();
            storage.Setup(s => s.GetThumbnailDirectory()).Returns(directory);
            var thumbnailService = new GcodeThumbnailExtractorService(
                Extractor(), storage.Object, NullLogger<GcodeThumbnailExtractorService>.Instance);
            var uploadService = new ChunkedUploadService(
                Mock.Of<IFileManagementService>(), thumbnailService, Extractor(), NullLogger<ChunkedUploadService>.Instance);

            GcodeMetadataExtracted? metadata = await uploadService.ExtractMetadataFromFileAsync(path);
            Assert.NotNull(metadata);
            Assert.Equal("PETG", metadata.Material);
            Assert.Equal(image, metadata.ThumbnailData);
            Assert.Equal(await BinaryGcodeReader.ReadBytesAsTextAsync(file), await BinaryGcodeReader.ReadFileAsTextAsync(path));
            using var stream = new MemoryStream(file);
            string? thumbnailPath = await thumbnailService.ExtractAndSaveThumbnailAsync(stream);
            Assert.NotNull(thumbnailPath);
            Assert.Equal(image, await File.ReadAllBytesAsync(thumbnailPath));

            await File.WriteAllBytesAsync(path, "GCDE"u8.ToArray());
            Assert.Null(await uploadService.ExtractMetadataFromFileAsync(path));
            using var invalidStream = new MemoryStream("GCDE"u8.ToArray());
            Assert.Null(await thumbnailService.ExtractAndSaveThumbnailAsync(invalidStream));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task HarvestMetadata_BinaryGcode_UsesMetadataComments()
    {
        var harvest = new GcodeHarvestService(
            Mock.Of<IUnitOfWork>(), NullLogger<GcodeHarvestService>.Instance,
            Mock.Of<IServiceScopeFactory>(), Mock.Of<IStoragePathService>(), Mock.Of<IBackendCapabilityFactory>(),
            Mock.Of<IHarvestEventBroadcaster>(), Mock.Of<IGcodeFileProcessingService>(), Mock.Of<IPrintFarmerTelemetryService>());
        using var stream = new MemoryStream(CreateFile([MetadataBlock(0, "Producer = PrusaSlicer 2.9.4\n")]));

        GcodeMetadataDto result = await harvest.ExtractMetadataAsync(stream);

        Assert.Equal("PrusaSlicer", result.SlicerName);
        Assert.Equal("2.9.4", result.SlicerVersion);
        Assert.True(stream.CanRead);
    }

    private static GcodeMetadataExtractorService Extractor() => new(NullLogger<GcodeMetadataExtractorService>.Instance);

    private static byte[] MetadataBlock(ushort type, string text) => Block(type, Encoding.UTF8.GetBytes(text));

    // Fixtures follow libbgcode's little-endian wire specification, independently of the reader.
    private static byte[] CreateFile(byte[][] blocks, ushort checksum = 1, uint version = 1, bool gcode = true)
    {
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);
        writer.Write("GCDE"u8);
        writer.Write(version);
        writer.Write(checksum);
        foreach (byte[] block in blocks)
        {
            writer.Write(checksum == 0 ? block[..^4] : block);
        }
        if (gcode)
        {
            writer.Write((ushort)1);
            writer.Write((ushort)3);
            writer.Write(uint.MaxValue);
        }
        return output.ToArray();
    }

    private static byte[] Block(ushort type, byte[] payload, ushort compression = 0, ushort encoding = 0,
        ushort format = 0, ushort width = 200, bool rawDeflate = false, uint? declaredSize = null)
    {
        byte[] stored = payload;
        if (compression == 1)
        {
            using var compressed = new MemoryStream();
            using (Stream compressor = rawDeflate
                ? new DeflateStream(compressed, CompressionLevel.Fastest, leaveOpen: true)
                : new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            {
                compressor.Write(payload);
            }
            stored = compressed.ToArray();
        }
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);
        writer.Write(type);
        writer.Write(compression);
        writer.Write(declaredSize ?? (uint)payload.Length);
        if (compression != 0)
        {
            writer.Write((uint)stored.Length);
        }
        writer.Write(type == 5 ? format : encoding);
        if (type == 5)
        {
            writer.Write(width);
            writer.Write((ushort)200);
        }
        writer.Write(stored);
        writer.Write(Checksum(output.ToArray()));
        return output.ToArray();
    }

    private static uint Checksum(byte[] bytes)
    {
        uint crc = uint.MaxValue;
        foreach (byte value in bytes)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) == 0 ? crc >> 1 : (crc >> 1) ^ 0xedb88320;
            }
        }
        return ~crc;
    }
}
