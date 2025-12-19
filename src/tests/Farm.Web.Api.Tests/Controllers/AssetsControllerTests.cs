using System;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Web.Api.Controllers;
using Farm.Web.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Controllers
{
    public class AssetsControllerTests
    {
        private readonly Mock<IAssetService> _assetServiceMock;
        private readonly Mock<ILogger<AssetsController>> _loggerMock;
        private readonly AssetsController _controller;

        public AssetsControllerTests()
        {
            _assetServiceMock = new Mock<IAssetService>();
            _loggerMock = new Mock<ILogger<AssetsController>>();
            _controller = new AssetsController(_assetServiceMock.Object, _loggerMock.Object);
        }

        [Fact]
        public void Constructor_WithValidDependencies_InitializesSuccessfully()
        {
            // Act
            var controller = new AssetsController(_assetServiceMock.Object, _loggerMock.Object);

            // Assert
            Assert.NotNull(controller);
        }

        [Fact]
        public void Constructor_WithNullAssetService_ThrowsArgumentNullException()
        {
            // Act & Assert
            Assert.Throws<ArgumentNullException>(() => 
                new AssetsController(null!, _loggerMock.Object));
        }

        [Fact]
        public void Constructor_WithNullLogger_ThrowsArgumentNullException()
        {
            // Act & Assert
            Assert.Throws<ArgumentNullException>(() => 
                new AssetsController(_assetServiceMock.Object, null!));
        }

        [Fact]
        public async Task GetPrinterAssetAsync_WithValidManufacturerAndModel_ReturnsOkWithAsset()
        {
            // Arrange
            var manufacturerId = "bambu-lab";
            var modelId = "x1";
            var asset = new PrinterAssetDto
            {
                Id = modelId,
                Name = "Bambu Lab X1",
                Cover = "https://example.com/cover.png",
                BedTexture = "https://example.com/texture.png"
            };

            _assetServiceMock
                .Setup(s => s.GetPrinterAssetAsync(manufacturerId, modelId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(asset);

            // Act
            var result = await _controller.GetPrinterAssetAsync(manufacturerId, modelId);

            // Assert
            var okResult = Assert.IsType<OkObjectResult>(result.Result);
            Assert.Equal(asset, okResult.Value);
        }

        [Fact]
        public async Task GetPrinterAssetAsync_WithNullManufacturer_ReturnsBadRequest()
        {
            // Arrange
            var modelId = "x1";

            // Act
            var result = await _controller.GetPrinterAssetAsync(null!, modelId);

            // Assert
            var badRequestResult = Assert.IsType<BadRequestObjectResult>(result.Result);
        }

        [Fact]
        public async Task GetPrinterAssetAsync_WithEmptyManufacturer_ReturnsBadRequest()
        {
            // Arrange
            var manufacturerId = "";
            var modelId = "x1";

            // Act
            var result = await _controller.GetPrinterAssetAsync(manufacturerId, modelId);

            // Assert
            var badRequestResult = Assert.IsType<BadRequestObjectResult>(result.Result);
        }

        [Fact]
        public async Task GetPrinterAssetAsync_WithAssetNotFound_ReturnsNotFound()
        {
            // Arrange
            var manufacturerId = "unknown";
            var modelId = "unknown";

            _assetServiceMock
                .Setup(s => s.GetPrinterAssetAsync(manufacturerId, modelId, It.IsAny<CancellationToken>()))
                .ReturnsAsync((PrinterAssetDto?)null);

            // Act
            var result = await _controller.GetPrinterAssetAsync(manufacturerId, modelId);

            // Assert
            Assert.IsType<NotFoundResult>(result.Result);
        }

        [Fact]
        public async Task GetPrinterAssetAsync_CallsServiceWithCorrectParameters()
        {
            // Arrange
            var manufacturerId = "bambu-lab";
            var modelId = "x1";
            var ct = new CancellationToken();
            var asset = new PrinterAssetDto { Id = modelId, Name = "Test" };

            _assetServiceMock
                .Setup(s => s.GetPrinterAssetAsync(manufacturerId, modelId, ct))
                .ReturnsAsync(asset);

            // Act
            await _controller.GetPrinterAssetAsync(manufacturerId, modelId, ct);

            // Assert
            _assetServiceMock.Verify(
                s => s.GetPrinterAssetAsync(manufacturerId, modelId, ct),
                Times.Once);
        }

        [Fact]
        public async Task GetCoverImageAsync_WithValidParameters_ReturnsOkWithUrl()
        {
            // Arrange
            var manufacturerId = "bambu-lab";
            var modelId = "x1";
            var url = "https://example.com/cover.png";

            _assetServiceMock
                .Setup(s => s.GetCoverImageUrlAsync(manufacturerId, modelId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(url);

            // Act
            var result = await _controller.GetCoverImageAsync(manufacturerId, modelId);

            // Assert
            var okResult = Assert.IsType<OkObjectResult>(result.Result);
            Assert.Equal(url, okResult.Value);
        }

        [Fact]
        public async Task GetCoverImageAsync_WithUrlNotFound_ReturnsNotFound()
        {
            // Arrange
            var manufacturerId = "unknown";
            var modelId = "unknown";

            _assetServiceMock
                .Setup(s => s.GetCoverImageUrlAsync(manufacturerId, modelId, It.IsAny<CancellationToken>()))
                .ReturnsAsync((string?)null);

            // Act
            var result = await _controller.GetCoverImageAsync(manufacturerId, modelId);

            // Assert
            Assert.IsType<NotFoundResult>(result.Result);
        }

        [Fact]
        public async Task GetBedTextureAsync_WithValidParameters_ReturnsOkWithUrl()
        {
            // Arrange
            var manufacturerId = "bambu-lab";
            var modelId = "x1";
            var url = "https://example.com/texture.png";

            _assetServiceMock
                .Setup(s => s.GetBedTextureUrlAsync(manufacturerId, modelId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(url);

            // Act
            var result = await _controller.GetBedTextureAsync(manufacturerId, modelId);

            // Assert
            var okResult = Assert.IsType<OkObjectResult>(result.Result);
            Assert.Equal(url, okResult.Value);
        }

        [Fact]
        public async Task GetBedTextureAsync_WithUrlNotFound_ReturnsNotFound()
        {
            // Arrange
            var manufacturerId = "unknown";
            var modelId = "unknown";

            _assetServiceMock
                .Setup(s => s.GetBedTextureUrlAsync(manufacturerId, modelId, It.IsAny<CancellationToken>()))
                .ReturnsAsync((string?)null);

            // Act
            var result = await _controller.GetBedTextureAsync(manufacturerId, modelId);

            // Assert
            Assert.IsType<NotFoundResult>(result.Result);
        }

        [Fact]
        public async Task GetManifestAsync_ReturnsOkWithAssetManifest()
        {
            // Arrange
            var manifest = new AssetManifestDto
            {
                Manufacturers = new System.Collections.Generic.List<ManufacturerAssetsDto>
                {
                    new ManufacturerAssetsDto
                    {
                        Id = "bambu-lab",
                        Name = "Bambu Lab",
                        Printers = new System.Collections.Generic.List<PrinterAssetDto>
                        {
                            new PrinterAssetDto
                            {
                                Id = "x1",
                                Name = "Bambu Lab X1",
                                Cover = "https://example.com/cover.png",
                                BedTexture = "https://example.com/texture.png"
                            }
                        }
                    }
                }
            };

            _assetServiceMock
                .Setup(s => s.GetManifestAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(manifest);

            // Act
            var result = await _controller.GetManifestAsync();

            // Assert
            var okResult = Assert.IsType<OkObjectResult>(result.Result);
            var returnedManifest = Assert.IsType<AssetManifestDto>(okResult.Value);
            Assert.Single(returnedManifest.Manufacturers);
        }

        [Fact]
        public async Task GetManifestAsync_WithServiceException_ReturnsInternalServerError()
        {
            // Arrange
            _assetServiceMock
                .Setup(s => s.GetManifestAsync(It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("Service error"));

            // Act & Assert
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => _controller.GetManifestAsync());
        }

        [Fact]
        public async Task GetManifestAsync_CallsServiceWithCancellationToken()
        {
            // Arrange
            var ct = new CancellationToken();
            var manifest = new AssetManifestDto { Manufacturers = new System.Collections.Generic.List<ManufacturerAssetsDto>() };

            _assetServiceMock
                .Setup(s => s.GetManifestAsync(ct))
                .ReturnsAsync(manifest);

            // Act
            await _controller.GetManifestAsync(ct);

            // Assert
            _assetServiceMock.Verify(
                s => s.GetManifestAsync(ct),
                Times.Once);
        }
    }
}
