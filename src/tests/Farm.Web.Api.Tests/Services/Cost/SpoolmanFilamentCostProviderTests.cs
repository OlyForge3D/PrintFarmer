using Farm.Infrastructure;
using Farm.Infrastructure.Services.Cost;
using Farm.Infrastructure.Services.Interfaces;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services.Cost;

/// <summary>
/// Unit tests for <see cref="SpoolmanFilamentCostProvider"/>.
/// Covers the happy path, missing/unconfigured Spoolman, unreachable Spoolman,
/// and 5-minute TTL cache behaviour.
/// </summary>
public class SpoolmanFilamentCostProviderTests
{
    // ── helpers ────────────────────────────────────────────────────────────────

    private static IMemoryCache BuildCache() =>
        new MemoryCache(new MemoryCacheOptions());

    private static SpoolmanFilamentCostProvider BuildProvider(
        ISpoolmanService spoolman,
        IMemoryCache? cache = null) =>
        new(spoolman, cache ?? BuildCache(), NullLogger<SpoolmanFilamentCostProvider>.Instance);

    // ── GetSpoolCostPerGramAsync ────────────────────────────────────────────────

    [Fact]
    public async Task GetSpoolCostPerGramAsync_SpoolHasPriceAndWeight_ReturnsCostPerGram()
    {
        // Arrange – spool has both price and initial weight.
        var spool = new SpoolmanSpoolDto(
            Id: 1, Name: "Red PLA", Material: "PLA",
            RemainingWeightG: 800, ColorHex: null, InUse: false,
            Price: 25.0, InitialWeightG: 1000.0);

        Mock<ISpoolmanService> spoolman = new();
        spoolman.Setup(s => s.GetSpoolByIdAsync(1, It.IsAny<CancellationToken>()))
                .ReturnsAsync(spool);

        var provider = BuildProvider(spoolman.Object);

        // Act
        decimal? result = await provider.GetSpoolCostPerGramAsync(1);

        // Assert – 25 / 1000 = 0.025 per gram
        Assert.NotNull(result);
        Assert.Equal(0.025m, result);
    }

    [Fact]
    public async Task GetSpoolCostPerGramAsync_SpoolNoPrice_FallsBackToFilamentPrice()
    {
        // Arrange – spool has no price, but filament product does.
        var spool = new SpoolmanSpoolDto(
            Id: 2, Name: "Green PETG", Material: "PETG",
            RemainingWeightG: 500, ColorHex: null, InUse: false,
            Price: null, InitialWeightG: null, FilamentId: 10);

        var filament = new SpoolmanFilamentDto(
            Id: 10, Name: "PETG Clear", Material: "PETG",
            ColorHex: null, Vendor: null, Density: null, Diameter: null,
            Weight: 1000.0, SpoolWeight: null, Price: 30.0,
            SettingsExtruderTemp: null, SettingsBedTemp: null,
            ArticleNumber: null, Comment: null, MultiColorHexes: null, ExternalId: null);

        Mock<ISpoolmanService> spoolman = new();
        spoolman.Setup(s => s.GetSpoolByIdAsync(2, It.IsAny<CancellationToken>()))
                .ReturnsAsync(spool);
        spoolman.Setup(s => s.GetFilamentByIdAsync(10, It.IsAny<CancellationToken>()))
                .ReturnsAsync(filament);

        var provider = BuildProvider(spoolman.Object);

        // Act
        decimal? result = await provider.GetSpoolCostPerGramAsync(2);

        // Assert – 30 / 1000 = 0.030 per gram
        Assert.NotNull(result);
        Assert.Equal(0.030m, result);
    }

    [Fact]
    public async Task GetSpoolCostPerGramAsync_SpoolmanReturnsNull_ReturnsNull()
    {
        // Arrange – Spoolman not configured or spool not found.
        Mock<ISpoolmanService> spoolman = new();
        spoolman.Setup(s => s.GetSpoolByIdAsync(99, It.IsAny<CancellationToken>()))
                .ReturnsAsync((SpoolmanSpoolDto?)null);

        var provider = BuildProvider(spoolman.Object);

        // Act
        decimal? result = await provider.GetSpoolCostPerGramAsync(99);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task GetSpoolCostPerGramAsync_SpoolmanThrows_ReturnsNullGracefully()
    {
        // Arrange – Spoolman server unreachable.
        Mock<ISpoolmanService> spoolman = new();
        spoolman.Setup(s => s.GetSpoolByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new HttpRequestException("Connection refused"));

        var provider = BuildProvider(spoolman.Object);

        // Act
        decimal? result = await provider.GetSpoolCostPerGramAsync(1);

        // Assert – must not propagate the exception
        Assert.Null(result);
    }

    [Fact]
    public async Task GetSpoolCostPerGramAsync_CachesResult_SpoolmanCalledOnce()
    {
        // Arrange
        var spool = new SpoolmanSpoolDto(
            Id: 5, Name: "Blue PLA", Material: "PLA",
            RemainingWeightG: 1000, ColorHex: null, InUse: false,
            Price: 20.0, InitialWeightG: 1000.0);

        Mock<ISpoolmanService> spoolman = new();
        spoolman.Setup(s => s.GetSpoolByIdAsync(5, It.IsAny<CancellationToken>()))
                .ReturnsAsync(spool);

        var provider = BuildProvider(spoolman.Object);

        // Act – call twice
        decimal? first = await provider.GetSpoolCostPerGramAsync(5);
        decimal? second = await provider.GetSpoolCostPerGramAsync(5);

        // Assert – same result, Spoolman called exactly once
        Assert.Equal(first, second);
        spoolman.Verify(s => s.GetSpoolByIdAsync(5, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetSpoolCostPerGramAsync_CacheExpires_SpoolmanCalledAgain()
    {
        // Arrange – use a very short TTL by injecting a real cache and verifying calls.
        var spool = new SpoolmanSpoolDto(
            Id: 7, Name: "Yellow ABS", Material: "ABS",
            RemainingWeightG: 1000, ColorHex: null, InUse: false,
            Price: 22.0, InitialWeightG: 1000.0);

        Mock<ISpoolmanService> spoolman = new();
        spoolman.Setup(s => s.GetSpoolByIdAsync(7, It.IsAny<CancellationToken>()))
                .ReturnsAsync(spool);

        // Use an isolated cache and manually expire the entry.
        using var cache = BuildCache();
        var provider = BuildProvider(spoolman.Object, cache);

        // First call populates cache.
        _ = await provider.GetSpoolCostPerGramAsync(7);

        // Manually remove the cache entry to simulate expiry.
        cache.Remove("spoolman_cpg_spool_7");

        // Second call after expiry should hit Spoolman again.
        _ = await provider.GetSpoolCostPerGramAsync(7);

        spoolman.Verify(s => s.GetSpoolByIdAsync(7, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    // ── GetFilamentCostPerGramAsync ────────────────────────────────────────────

    [Fact]
    public async Task GetFilamentCostPerGramAsync_FilamentHasPriceAndWeight_ReturnsCostPerGram()
    {
        // Arrange
        var filament = new SpoolmanFilamentDto(
            Id: 3, Name: "Tough PLA", Material: "PLA",
            ColorHex: null, Vendor: null, Density: null, Diameter: null,
            Weight: 500.0, SpoolWeight: null, Price: 18.0,
            SettingsExtruderTemp: null, SettingsBedTemp: null,
            ArticleNumber: null, Comment: null, MultiColorHexes: null, ExternalId: null);

        Mock<ISpoolmanService> spoolman = new();
        spoolman.Setup(s => s.GetFilamentByIdAsync(3, It.IsAny<CancellationToken>()))
                .ReturnsAsync(filament);

        var provider = BuildProvider(spoolman.Object);

        // Act
        decimal? result = await provider.GetFilamentCostPerGramAsync(3);

        // Assert – 18 / 500 = 0.036 per gram
        Assert.NotNull(result);
        Assert.Equal(0.036m, result);
    }

    [Fact]
    public async Task GetFilamentCostPerGramAsync_SpoolmanUnconfigured_ReturnsNull()
    {
        // Arrange – simulates missing BaseUrl (GetFilamentByIdAsync returns null).
        Mock<ISpoolmanService> spoolman = new();
        spoolman.Setup(s => s.GetFilamentByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((SpoolmanFilamentDto?)null);

        var provider = BuildProvider(spoolman.Object);

        // Act
        decimal? result = await provider.GetFilamentCostPerGramAsync(1);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task GetFilamentCostPerGramAsync_FilamentHasNoPriceSet_ReturnsNull()
    {
        // Arrange – filament product has no price.
        var filament = new SpoolmanFilamentDto(
            Id: 4, Name: "No-Price PLA", Material: "PLA",
            ColorHex: null, Vendor: null, Density: null, Diameter: null,
            Weight: 1000.0, SpoolWeight: null, Price: null,
            SettingsExtruderTemp: null, SettingsBedTemp: null,
            ArticleNumber: null, Comment: null, MultiColorHexes: null, ExternalId: null);

        Mock<ISpoolmanService> spoolman = new();
        spoolman.Setup(s => s.GetFilamentByIdAsync(4, It.IsAny<CancellationToken>()))
                .ReturnsAsync(filament);

        var provider = BuildProvider(spoolman.Object);

        // Act
        decimal? result = await provider.GetFilamentCostPerGramAsync(4);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task GetFilamentCostPerGramAsync_SpoolmanThrows_ReturnsNullGracefully()
    {
        // Arrange – Spoolman unreachable.
        Mock<ISpoolmanService> spoolman = new();
        spoolman.Setup(s => s.GetFilamentByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new HttpRequestException("Timeout"));

        var provider = BuildProvider(spoolman.Object);

        // Act
        decimal? result = await provider.GetFilamentCostPerGramAsync(1);

        // Assert
        Assert.Null(result);
    }
}
