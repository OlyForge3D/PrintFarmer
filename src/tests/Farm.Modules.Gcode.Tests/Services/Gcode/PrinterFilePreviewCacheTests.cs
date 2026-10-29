using System.Text.Json;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;
using Farm.Modules.Gcode.Services.Gcode;

namespace Farm.Modules.Gcode.Tests.Services.Gcode;

public sealed class PrinterFilePreviewCacheTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), $"preview-cache-{Guid.NewGuid():N}");

    [Fact]
    public async Task CacheAsync_BoundsCountAndBytesAndExpiresOldEntries()
    {
        PrinterFilePreviewCache cache = new(_root, maximumEntries: 2, maximumBytes: 1024);
        string first = Key(1);
        await cache.PutAsync(first, new(DateTime.UtcNow.AddDays(-2), new(10, null)), CancellationToken.None);
        Assert.Null(await cache.GetAsync(first, CancellationToken.None));
        for (int i = 1; i <= 5; i++)
        {
            await cache.PutAsync(Key(i), new(DateTime.UtcNow, new(10, null)), CancellationToken.None);
        }

        FileInfo[] entries = new DirectoryInfo(_root).GetFiles("*.json");
        Assert.True(entries.Length <= 2);
        Assert.True(entries.Sum(x => x.Length) <= 1024);
        Assert.Null(await cache.GetAsync(first, CancellationToken.None));
        Assert.NotNull(await cache.GetAsync(Key(5), CancellationToken.None));
    }

    [Fact]
    public async Task PutAsync_RejectsOversizedOrNonImageContent()
    {
        PrinterFilePreviewCache cache = new(_root);
        byte[] oversized = new byte[PrinterFilePreviewTransport.MaximumImageBytes + 1];
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            cache.PutAsync(Key(1), new(DateTime.UtcNow, new(null, null, oversized, "image/png")), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            cache.PutAsync(Key(1), new(DateTime.UtcNow, new(null, null, "<svg/>"u8.ToArray(), "image/svg+xml")),
                CancellationToken.None));
        Assert.False(Directory.Exists(_root));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(3600.0, null)]
    [InlineData(null, 0.2)]
    [InlineData(3600.0, 0.2)]
    [InlineData(double.Epsilon, double.MaxValue)]
    [InlineData(double.MaxValue, double.Epsilon)]
    public async Task CacheAsync_OptionalPositiveMeasurements_RoundTrips(double? printTimeSeconds, double? layerHeightMm)
    {
        PrinterFilePreviewCache cache = new(_root);
        PrinterFilePreviewContent content = new(printTimeSeconds, layerHeightMm);

        await cache.PutAsync(Key(1), new(DateTime.UtcNow, content), CancellationToken.None);
        PrinterFilePreviewCapture? capture = await cache.GetAsync(Key(1), CancellationToken.None);

        Assert.NotNull(capture);
        Assert.Equal(content, capture.Content);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    public async Task PutAsync_InvalidMeasurement_RejectsEitherFieldBeforeWriting(double value)
    {
        PrinterFilePreviewCache cache = new(_root);
        PrinterFilePreviewContent[] contents = [new(value, null), new(null, value)];
        foreach (PrinterFilePreviewContent content in contents)
        {
            InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
                cache.PutAsync(Key(1), new(DateTime.UtcNow, content), CancellationToken.None));
            Assert.Equal("Invalid preview cache content.", exception.Message);
        }

        Assert.False(Directory.Exists(_root));
    }

    [Theory]
    [InlineData("89504E470D0A1A0A", "image/png")]
    [InlineData("FFD8FF", "image/jpeg")]
    [InlineData("524946460000000057454250", "image/webp")]
    public async Task CacheAsync_SupportedImage_RoundTrips(string imageHex, string contentType)
    {
        PrinterFilePreviewCache cache = new(_root);
        byte[] image = Convert.FromHexString(imageHex);

        await cache.PutAsync(Key(1), new(DateTime.UtcNow, new(null, null, image, contentType)), CancellationToken.None);
        PrinterFilePreviewCapture? capture = await cache.GetAsync(Key(1), CancellationToken.None);

        Assert.NotNull(capture);
        Assert.Equal(image, capture.Content.Image);
        Assert.Equal(contentType, capture.Content.ContentType);
    }

    [Theory]
    [InlineData(null, "image/png")]
    [InlineData("", "image/png")]
    [InlineData("89504E470D0A1A0A", null)]
    [InlineData("89504E470D0A1A0A", "image/jpeg")]
    [InlineData("FFD8FF", "image/png")]
    public async Task CacheAsync_InvalidImageContentType_RejectsWriteAndEvictsRead(string? imageHex, string? contentType)
    {
        byte[]? image = imageHex is null ? null : Convert.FromHexString(imageHex);
        await AssertInvalidContentAsync(new(null, null, image, contentType));
    }

    [Fact]
    public async Task CacheAsync_NullContent_RejectsWriteAndEvictsRead() =>
        await AssertInvalidContentAsync(null);

    [Theory]
    [InlineData(0.0, null)]
    [InlineData(-1.0, null)]
    [InlineData(null, 0.0)]
    [InlineData(null, -1.0)]
    public async Task CacheAsync_NonPositiveMeasurement_RejectsWriteAndEvictsRead(
        double? printTimeSeconds, double? layerHeightMm) =>
        await AssertInvalidContentAsync(new(printTimeSeconds, layerHeightMm));

    [Fact]
    public async Task CacheAsync_NativeMetadata_RetainsZeroFalseAndToolOrder()
    {
        PrinterFilePreviewCache cache = new(_root);
        PrinterPrintMetadata metadata = new()
        {
            BrimWidthMm = 0,
            LayerHeightMm = 0,
            EstimatedPrintTimeSeconds = 0,
            QuietLeftPresent = false,
            FilamentUsedPerToolG = [0, 2.5],
            FilamentTypePerTool = ["PLA", "PETG"],
        };
        await cache.PutAsync(Key(1), new(DateTime.UtcNow, new(null, null, SizeBytes: 0, Metadata: metadata)),
            CancellationToken.None);
        PrinterFilePreviewCapture? capture = await cache.GetAsync(Key(1), CancellationToken.None);

        Assert.NotNull(capture);
        Assert.Equal(0, capture.Content.SizeBytes);
        Assert.Equal(JsonSerializer.Serialize(metadata), JsonSerializer.Serialize(capture.Content.Metadata));
    }

    [Fact]
    public async Task CacheAsync_InvalidNativeDetails_RejectsWriteAndEvictsRead()
    {
        await AssertInvalidContentAsync(new(null, null, SizeBytes: -1));
        await AssertInvalidContentAsync(new(null, null, Metadata: new() { FilamentCost = -1 }));
        await AssertInvalidContentAsync(new(null, null, Metadata: new() { Profile = "unsafe\ntext" }));
    }

    [Fact]
    public void Key_ChangesForEveryObservationIdentityComponent()
    {
        PrinterFileLocation location = new()
        {
            SourcePrinterId = Guid.NewGuid(),
            StorageRoot = "gcodes",
            RelativePath = "part.gcode",
            SizeBytes = 1,
            ModifiedValue = "1",
        };
        HashSet<string> keys = [PrinterFilePreviewCache.Key(location, "configuration")];
        location.SourcePrinterId = Guid.NewGuid();
        Assert.True(keys.Add(PrinterFilePreviewCache.Key(location, "configuration")));
        location.RelativePath = "Part.gcode";
        Assert.True(keys.Add(PrinterFilePreviewCache.Key(location, "configuration")));
        location.SizeBytes++;
        Assert.True(keys.Add(PrinterFilePreviewCache.Key(location, "configuration")));
        location.ModifiedValue = "2";
        Assert.True(keys.Add(PrinterFilePreviewCache.Key(location, "configuration")));
        location.ObservationRevision++;
        Assert.True(keys.Add(PrinterFilePreviewCache.Key(location, "configuration")));
        Assert.True(keys.Add(PrinterFilePreviewCache.Key(location, "new-configuration")));
    }

    private async Task AssertInvalidContentAsync(PrinterFilePreviewContent? content)
    {
        PrinterFilePreviewCache cache = new(_root);
        PrinterFilePreviewCapture capture = new(DateTime.UtcNow, content!);
        await Assert.ThrowsAsync<InvalidDataException>(() => cache.PutAsync(Key(1), capture, CancellationToken.None));
        Assert.False(File.Exists(Path.Join(_root, Key(1) + ".json")));

        Directory.CreateDirectory(_root);
        string path = Path.Join(_root, Key(1) + ".json");
        await File.WriteAllBytesAsync(path, JsonSerializer.SerializeToUtf8Bytes(capture));

        Assert.Null(await cache.GetAsync(Key(1), CancellationToken.None));
        Assert.False(File.Exists(path));
    }

    private static string Key(int revision) =>
        PrinterFilePreviewCache.Key(new PrinterFileLocation { ObservationRevision = revision }, "test");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }
}
