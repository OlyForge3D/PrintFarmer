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
