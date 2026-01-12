using Farm.Web.Api.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;

namespace Farm.Web.Api.Tests.Services;

public class AssetServiceTests
{
    private readonly Mock<ILogger<AssetService>> _mockLogger;
    private readonly AssetService _service;

    public AssetServiceTests()
    {
        _mockLogger = new Mock<ILogger<AssetService>>();
        _service = new AssetService(_mockLogger.Object);
    }

    [Fact]
    public async Task GetManifestAsync_Returns_Null()
    {
        var result = await _service.GetManifestAsync();

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetManifestAsync_Logs_Information()
    {
        await _service.GetManifestAsync();

        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Asset manifest available")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()
            ),
            Times.Once
        );
    }

    [Fact]
    public async Task GetManifestAsync_With_CancellationToken()
    {
        using var cts = new CancellationTokenSource();

        var result = await _service.GetManifestAsync(cts.Token);

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetManufacturerAsync_WithNullManufacturerId_Returns_Null()
    {
        var result = await _service.GetManufacturerAsync(null!);

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetManufacturerAsync_WithEmptyManufacturerId_Returns_Null()
    {
        var result = await _service.GetManufacturerAsync("");

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetManufacturerAsync_WithWhitespaceManufacturerId_Returns_Null()
    {
        var result = await _service.GetManufacturerAsync("   ");

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetManufacturerAsync_WithValidManufacturerId_Returns_Null()
    {
        var result = await _service.GetManufacturerAsync("Prusa");

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetManufacturerAsync_Logs_Information()
    {
        await _service.GetManufacturerAsync("Prusa");

        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Getting manufacturer assets")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()
            ),
            Times.Once
        );
    }

    [Fact]
    public async Task GetPrinterAssetAsync_WithNullManufacturerId_Returns_Null()
    {
        var result = await _service.GetPrinterAssetAsync(null!, "model");

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetPrinterAssetAsync_WithNullModelId_Returns_Null()
    {
        var result = await _service.GetPrinterAssetAsync("Prusa", null!);

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetPrinterAssetAsync_WithEmptyManufacturerId_Returns_Null()
    {
        var result = await _service.GetPrinterAssetAsync("", "model");

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetPrinterAssetAsync_WithEmptyModelId_Returns_Null()
    {
        var result = await _service.GetPrinterAssetAsync("Prusa", "");

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetPrinterAssetAsync_WithValidParameters_Returns_Asset()
    {
        var result = await _service.GetPrinterAssetAsync("Prusa", "CORE One");

        result.Should().NotBeNull();
        result!.Name.Should().Be("CORE One");
        result.Id.Should().Be("core_one");
    }

    [Fact]
    public async Task GetPrinterAssetAsync_WithValidParameters_Sets_Cover_Url()
    {
        var result = await _service.GetPrinterAssetAsync("Prusa", "CORE One");

        result!.Cover.Should().Be("/assets/orcaslicer/printers/prusa/core_one/cover.png");
    }

    [Fact]
    public async Task GetPrinterAssetAsync_WithValidParameters_Sets_BedTexture_Url()
    {
        var result = await _service.GetPrinterAssetAsync("Prusa", "CORE One");

        result!.BedTexture.Should().Be("/assets/orcaslicer/printers/prusa/core_one/bed-texture.png");
    }

    [Theory]
    [InlineData("Prusa", "prusa")]
    [InlineData("PRUSA", "prusa")]
    [InlineData("Ultimaker", "ultimaker")]
    public async Task GetPrinterAssetAsync_NormalizesManufacturerIdToLowercase(string input, string expected)
    {
        var result = await _service.GetPrinterAssetAsync(input, "Model");

        result!.Cover.Should().Contain($"/printers/{expected}/");
    }

    [Theory]
    [InlineData("CORE One", "core_one")]
    [InlineData("Core One", "core_one")]
    [InlineData("Core-One", "core-one")]
    [InlineData("Core(One)", "coreone")]
    [InlineData("Core + One", "core_+_one")]
    [InlineData("Core&One", "coreandone")]
    public async Task GetPrinterAssetAsync_NormalizesModelIdCorrectly(string input, string expected)
    {
        var result = await _service.GetPrinterAssetAsync("Prusa", input);

        result!.Id.Should().Be(expected.ToLowerInvariant().Replace(" ", "_").Replace("(", "").Replace(")", "").Replace("+", "plus").Replace("&", "and"));
    }

    [Fact]
    public async Task GetPrinterAssetAsync_Logs_Information()
    {
        await _service.GetPrinterAssetAsync("Prusa", "CORE One");

        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Printer asset")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()
            ),
            Times.Once
        );
    }

    [Fact]
    public async Task GetCoverImageUrlAsync_Returns_Cover_Url()
    {
        var result = await _service.GetCoverImageUrlAsync("Prusa", "CORE One");

        result.Should().Be("/assets/orcaslicer/printers/prusa/core_one/cover.png");
    }

    [Fact]
    public async Task GetCoverImageUrlAsync_WithNullManufacturerId_Returns_Null()
    {
        var result = await _service.GetCoverImageUrlAsync(null!, "model");

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetBedTextureUrlAsync_Returns_BedTexture_Url()
    {
        var result = await _service.GetBedTextureUrlAsync("Prusa", "CORE One");

        result.Should().Be("/assets/orcaslicer/printers/prusa/core_one/bed-texture.png");
    }

    [Fact]
    public async Task GetBedTextureUrlAsync_WithNullManufacturerId_Returns_Null()
    {
        var result = await _service.GetBedTextureUrlAsync(null!, "model");

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetCoverImageUrlAsync_With_CancellationToken()
    {
        using var cts = new CancellationTokenSource();

        var result = await _service.GetCoverImageUrlAsync("Prusa", "CORE One", cts.Token);

        result.Should().NotBeNull();
    }

    [Fact]
    public async Task GetBedTextureUrlAsync_With_CancellationToken()
    {
        using var cts = new CancellationTokenSource();

        var result = await _service.GetBedTextureUrlAsync("Prusa", "CORE One", cts.Token);

        result.Should().NotBeNull();
    }

    [Fact]
    public async Task GetPrinterAssetAsync_With_CancellationToken()
    {
        using var cts = new CancellationTokenSource();

        var result = await _service.GetPrinterAssetAsync("Prusa", "CORE One", cts.Token);

        result.Should().NotBeNull();
    }

    [Fact]
    public async Task GetManufacturerAsync_With_CancellationToken()
    {
        using var cts = new CancellationTokenSource();

        var result = await _service.GetManufacturerAsync("Prusa", cts.Token);

        result.Should().BeNull();
    }

    [Fact]
    public void Constructor_WithNullLogger_Throws()
    {
        Action act = () => new AssetService(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task PrinterAssetDto_Properties_Are_Set_Correctly()
    {
        var result = await _service.GetPrinterAssetAsync("Prusa", "CORE One");

        result.Should().NotBeNull();
        result!.Id.Should().NotBeNullOrEmpty();
        result.Name.Should().NotBeNullOrEmpty();
        result.Cover.Should().NotBeNullOrEmpty();
        result.BedTexture.Should().NotBeNullOrEmpty();
    }

    [Theory]
    [InlineData("Prusa", "MK4S")]
    [InlineData("Ultimaker", "S5 Pro")]
    [InlineData("Creality", "K1 Max")]
    public async Task GetPrinterAssetAsync_Multiple_Manufacturers_And_Models(string mfg, string model)
    {
        var result = await _service.GetPrinterAssetAsync(mfg, model);

        result.Should().NotBeNull();
        result!.Cover.Should().Contain("/assets/orcaslicer/printers/");
        result.BedTexture.Should().Contain("/assets/orcaslicer/printers/");
    }
}
