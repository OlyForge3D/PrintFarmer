using Farm.Infrastructure;
using Farm.Infrastructure.Contracts.Printers;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Services;
using Farm.Web.Api.Services.Printers;
using Xunit;
using FluentAssertions;

namespace Farm.Web.Api.Tests.Services.Printers
{
    public class PrinterStatusDtoBuilderTests
    {
        private PrinterStatusDtoBuilder CreateBuilder()
        {
            // Create a simple mock logger for testing
            var mockLogger = new Moq.Mock<IUnifiedLoggingService>();
            return new PrinterStatusDtoBuilder(mockLogger.Object);
        }

        #region Moonraker DTO Building Tests

        [Fact]
        public async Task BuildMoonrakerDtoAsync_WithValidInputs_ReturnsCorrectDto()
        {
            // Arrange
            var builder = CreateBuilder();
            var printer = CreateTestPrinter(PrinterBackend.Moonraker);
            var status = new PrinterCompositeStatus(
                IsOnline: true,
                State: "Printing",
                Progress: null,
                JobName: null,
                ThumbnailUrl: null,
                CameraStreamUrl: null,
                CameraSnapshotUrl: null
            );
            var cameraStreamUrl = "http://camera:8080/stream";
            var cameraSnapshotUrl = "http://camera:8080/snapshot";
            var spoolInfo = CreateTestSpoolInfo();

            // Act
            var result = await builder.BuildMoonrakerDtoAsync(
                printer, status, cameraStreamUrl, cameraSnapshotUrl, spoolInfo);

            // Assert
            result.Should().NotBeNull();
            result.Backend.Should().Be(PrinterBackend.Moonraker);
            result.IsOnline.Should().BeTrue();
            result.State.Should().Be("Printing");
            result.CameraStreamUrl.Should().Be(cameraStreamUrl);
            result.CameraSnapshotUrl.Should().Be(cameraSnapshotUrl);
            result.SpoolInfo.Should().Be(spoolInfo);
        }

        [Fact]
        public async Task BuildMoonrakerDtoAsync_WithNullPrinter_ThrowsArgumentNullException()
        {
            // Arrange
            var builder = CreateBuilder();
            var status = new PrinterCompositeStatus(true, null, null, null, null, null, null);

            // Act & Assert
            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                builder.BuildMoonrakerDtoAsync(null!, status, null, null, null));
        }

        [Fact]
        public async Task BuildMoonrakerDtoAsync_WithNullStatus_ThrowsArgumentNullException()
        {
            // Arrange
            var builder = CreateBuilder();
            var printer = CreateTestPrinter();

            // Act & Assert
            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                builder.BuildMoonrakerDtoAsync(printer, null!, null, null, null));
        }

        [Fact]
        public async Task BuildMoonrakerDtoAsync_WithTemperatureData_PopulatesTemperatures()
        {
            // Arrange
            var builder = CreateBuilder();
            var printer = CreateTestPrinter();
            var status = new PrinterCompositeStatus(
                IsOnline: true,
                State: null,
                Progress: null,
                JobName: null,
                ThumbnailUrl: null,
                CameraStreamUrl: null,
                CameraSnapshotUrl: null,
                HotendTemp: 200.5,
                BedTemp: 60.0,
                HotendTarget: 210.0,
                BedTarget: 65.0
            );

            // Act
            var result = await builder.BuildMoonrakerDtoAsync(
                printer, status, null, null, null);

            // Assert
            result.HotendTemp.Should().Be(200.5);
            result.BedTemp.Should().Be(60.0);
            result.HotendTarget.Should().Be(210.0);
            result.BedTarget.Should().Be(65.0);
        }

        [Fact]
        public async Task BuildMoonrakerDtoAsync_WithPositionData_PopulatesCoordinates()
        {
            // Arrange
            var builder = CreateBuilder();
            var printer = CreateTestPrinter();
            var status = new PrinterCompositeStatus(
                IsOnline: true,
                State: null,
                Progress: null,
                JobName: null,
                ThumbnailUrl: null,
                CameraStreamUrl: null,
                CameraSnapshotUrl: null,
                X: 100.5,
                Y: 50.0,
                Z: 10.25
            );

            // Act
            var result = await builder.BuildMoonrakerDtoAsync(
                printer, status, null, null, null);

            // Assert
            result.X.Should().Be(100.5);
            result.Y.Should().Be(50.0);
            result.Z.Should().Be(10.25);
        }

        [Fact]
        public async Task BuildMoonrakerDtoAsync_WithJobData_PopulatesJobInfo()
        {
            // Arrange
            var builder = CreateBuilder();
            var printer = CreateTestPrinter();
            var status = new PrinterCompositeStatus(
                IsOnline: true,
                State: null,
                Progress: 0.45,
                JobName: "TestModel.gcode",
                ThumbnailUrl: "http://example.com/thumb.png",
                CameraStreamUrl: null,
                CameraSnapshotUrl: null
            );

            // Act
            var result = await builder.BuildMoonrakerDtoAsync(
                printer, status, null, null, null);

            // Assert
            result.JobName.Should().Be("TestModel.gcode");
            result.Progress.Should().Be(0.45);
            result.ThumbnailUrl.Should().Be("http://example.com/thumb.png");
        }

        [Fact]
        public async Task BuildMoonrakerDtoAsync_WithNullCameraUrls_ReturnsSafeValues()
        {
            // Arrange
            var builder = CreateBuilder();
            var printer = CreateTestPrinter();
            var status = new PrinterCompositeStatus(true, null, null, null, null, null, null);

            // Act
            var result = await builder.BuildMoonrakerDtoAsync(
                printer, status, null, null, null);

            // Assert
            result.CameraStreamUrl.Should().BeNull();
            result.CameraSnapshotUrl.Should().BeNull();
        }

        #endregion

        #region PrusaLink DTO Building Tests

        [Fact]
        public async Task BuildPrusaLinkDtoAsync_WithValidInputs_ReturnsCorrectDto()
        {
            // Arrange
            var builder = CreateBuilder();
            var printer = CreateTestPrinter(PrinterBackend.PrusaLink);
            var status = new PrusaCompositeStatus(
                IsOnline: true,
                State: null,
                Progress: null,
                JobName: null,
                ThumbnailUrl: null,
                CameraStreamUrl: null,
                CameraSnapshotUrl: null
            );

            // Act
            var result = await builder.BuildPrusaLinkDtoAsync(printer, status);

            // Assert
            result.Should().NotBeNull();
            result.Backend.Should().Be(PrinterBackend.PrusaLink);
            result.IsOnline.Should().BeTrue();
        }

        [Fact]
        public async Task BuildPrusaLinkDtoAsync_WithNullPrinter_ThrowsArgumentNullException()
        {
            // Arrange
            var builder = CreateBuilder();
            var status = new PrusaCompositeStatus(true, null, null, null, null, null, null);

            // Act & Assert
            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                builder.BuildPrusaLinkDtoAsync(null!, status));
        }

        [Fact]
        public async Task BuildPrusaLinkDtoAsync_WithNullStatus_ThrowsArgumentNullException()
        {
            // Arrange
            var builder = CreateBuilder();
            var printer = CreateTestPrinter();

            // Act & Assert
            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                builder.BuildPrusaLinkDtoAsync(printer, null!));
        }

        [Fact]
        public async Task BuildPrusaLinkDtoAsync_DoesNotIncludeCameraUrls()
        {
            // Arrange
            var builder = CreateBuilder();
            var printer = CreateTestPrinter();
            var status = new PrusaCompositeStatus(true, null, null, null, null, null, null);

            // Act
            var result = await builder.BuildPrusaLinkDtoAsync(printer, status);

            // Assert
            result.CameraStreamUrl.Should().BeNull();
            result.CameraSnapshotUrl.Should().BeNull();
        }

        [Fact]
        public async Task BuildPrusaLinkDtoAsync_DoesNotIncludeSpoolInfo()
        {
            // Arrange
            var builder = CreateBuilder();
            var printer = CreateTestPrinter();
            var status = new PrusaCompositeStatus(true, null, null, null, null, null, null);

            // Act
            var result = await builder.BuildPrusaLinkDtoAsync(printer, status);

            // Assert
            result.SpoolInfo.Should().BeNull();
        }

        #endregion

        #region SDCP DTO Building Tests

        [Fact]
        public async Task BuildSdcpDtoAsync_WithValidInputs_ReturnsCorrectDto()
        {
            // Arrange
            var builder = CreateBuilder();
            var printer = CreateTestPrinter(PrinterBackend.SDCP);
            var status = new PrinterCompositeStatus(true, null, null, null, null, null, null);
            var cameraStreamUrl = "http://camera:8080/stream";
            var cameraSnapshotUrl = "http://camera:8080/snapshot";

            // Act
            var result = await builder.BuildSdcpDtoAsync(
                printer, status, cameraStreamUrl, cameraSnapshotUrl);

            // Assert
            result.Should().NotBeNull();
            result.Backend.Should().Be(PrinterBackend.SDCP);
            result.CameraStreamUrl.Should().Be(cameraStreamUrl);
            result.CameraSnapshotUrl.Should().Be(cameraSnapshotUrl);
        }

        [Fact]
        public async Task BuildSdcpDtoAsync_WithNullPrinter_ThrowsArgumentNullException()
        {
            // Arrange
            var builder = CreateBuilder();
            var status = new PrinterCompositeStatus(true, null, null, null, null, null, null);

            // Act & Assert
            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                builder.BuildSdcpDtoAsync(null!, status, null, null));
        }

        [Fact]
        public async Task BuildSdcpDtoAsync_WithNullStatus_ThrowsArgumentNullException()
        {
            // Arrange
            var builder = CreateBuilder();
            var printer = CreateTestPrinter();

            // Act & Assert
            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                builder.BuildSdcpDtoAsync(printer, null!, null, null));
        }

        #endregion

        #region OctoPrint DTO Building Tests

        [Fact]
        public async Task BuildOctoPrintDtoAsync_WithValidInputs_ReturnsOfflineDto()
        {
            // Arrange
            var builder = CreateBuilder();
            var printer = CreateTestPrinter(PrinterBackend.OctoPrint);
            var printerJson = "{\"status\":{\"flags\":{\"operational\":true}}}";
            var jobJson = "{\"job\":null}";
            var apiKey = "test_api_key";

            // Act
            var result = await builder.BuildOctoPrintDtoAsync(
                printer, printerJson, jobJson, apiKey);

            // Assert
            result.Should().NotBeNull();
            result.Backend.Should().Be(PrinterBackend.OctoPrint);
            result.IsOnline.Should().BeFalse();
            result.State.Should().Be("Offline");
        }

        [Fact]
        public async Task BuildOctoPrintDtoAsync_WithNullPrinter_ThrowsArgumentNullException()
        {
            // Arrange
            var builder = CreateBuilder();

            // Act & Assert
            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                builder.BuildOctoPrintDtoAsync(null!, "{}", "{}", "key"));
        }

        [Fact]
        public async Task BuildOctoPrintDtoAsync_WithNullPrinterJson_ThrowsArgumentNullException()
        {
            // Arrange
            var builder = CreateBuilder();
            var printer = CreateTestPrinter();

            // Act & Assert
            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                builder.BuildOctoPrintDtoAsync(printer, null!, "{}", "key"));
        }

        [Fact]
        public async Task BuildOctoPrintDtoAsync_WithEmptyPrinterJson_ThrowsArgumentNullException()
        {
            // Arrange
            var builder = CreateBuilder();
            var printer = CreateTestPrinter();

            // Act & Assert
            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                builder.BuildOctoPrintDtoAsync(printer, "", "{}", "key"));
        }

        [Fact]
        public async Task BuildOctoPrintDtoAsync_WithNullJobJson_ThrowsArgumentNullException()
        {
            // Arrange
            var builder = CreateBuilder();
            var printer = CreateTestPrinter();

            // Act & Assert
            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                builder.BuildOctoPrintDtoAsync(printer, "{}", null!, "key"));
        }

        [Fact]
        public async Task BuildOctoPrintDtoAsync_WithEmptyJobJson_ThrowsArgumentNullException()
        {
            // Arrange
            var builder = CreateBuilder();
            var printer = CreateTestPrinter();

            // Act & Assert
            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                builder.BuildOctoPrintDtoAsync(printer, "{}", "", "key"));
        }

        #endregion

        #region Base DTO Building Tests

        [Fact]
        public void BuildBasePrinterDto_WithValidInputs_ReturnsCorrectDto()
        {
            // Arrange
            var builder = CreateBuilder();
            var printer = CreateTestPrinter();
            var status = new PrinterCompositeStatus(true, null, null, null, null, null, null);
            var backend = PrinterBackend.Moonraker;

            // Act
            var result = builder.BuildBasePrinterDto(
                printer, status, backend, "stream_url", "snapshot_url", null);

            // Assert
            result.Should().NotBeNull();
            result.Backend.Should().Be(backend);
            result.IsOnline.Should().BeTrue();
            result.CameraStreamUrl.Should().Be("stream_url");
            result.CameraSnapshotUrl.Should().Be("snapshot_url");
        }

        [Fact]
        public void BuildBasePrinterDto_WithNullPrinter_ThrowsArgumentNullException()
        {
            // Arrange
            var builder = CreateBuilder();
            var status = new PrinterCompositeStatus(true, null, null, null, null, null, null);

            // Act & Assert
            Assert.Throws<ArgumentNullException>(() =>
                builder.BuildBasePrinterDto(null!, status, PrinterBackend.Moonraker));
        }

        [Fact]
        public void BuildBasePrinterDto_WithNullStatus_ThrowsArgumentNullException()
        {
            // Arrange
            var builder = CreateBuilder();
            var printer = CreateTestPrinter();

            // Act & Assert
            Assert.Throws<ArgumentNullException>(() =>
                builder.BuildBasePrinterDto(printer, null!, PrinterBackend.Moonraker));
        }

        [Fact]
        public void BuildBasePrinterDto_PopulatesAllCommonProperties()
        {
            // Arrange
            var builder = CreateBuilder();
            var printer = CreateTestPrinter();
            printer.Manufacturer = new Manufacturer { Id = Guid.NewGuid(), Name = "Prusa" };
            printer.Model = new PrinterModel { Id = Guid.NewGuid(), Name = "MK3S+" };
            var status = new PrinterCompositeStatus(
                IsOnline: true,
                State: "Printing",
                Progress: 0.5,
                JobName: "Test.gcode",
                ThumbnailUrl: null,
                CameraStreamUrl: null,
                CameraSnapshotUrl: null
            );

            // Act
            var result = builder.BuildBasePrinterDto(
                printer, status, PrinterBackend.Moonraker);

            // Assert
            result.Name.Should().Be(printer.Name);
            result.ManufacturerName.Should().Be("Prusa");
            result.ModelName.Should().Be("MK3S+");
            result.IsOnline.Should().BeTrue();
            result.State.Should().Be("Printing");
            result.JobName.Should().Be("Test.gcode");
            result.Progress.Should().Be(0.5);
        }

        #endregion

        #region Temperature Data Extraction Tests

        [Fact]
        public void ExtractTemperatureData_WithValidData_ReturnsCorrectValues()
        {
            // Arrange
            var builder = CreateBuilder();
            var status = new PrinterCompositeStatus(
                IsOnline: true,
                State: null,
                Progress: null,
                JobName: null,
                ThumbnailUrl: null,
                CameraStreamUrl: null,
                CameraSnapshotUrl: null,
                HotendTemp: 195.5,
                BedTemp: 55.0,
                HotendTarget: 200.0,
                BedTarget: 60.0
            );

            // Act
            var (hotend, bed, hotendTarget, bedTarget) = builder.ExtractTemperatureData(status);

            // Assert
            hotend.Should().Be(195.5);
            bed.Should().Be(55.0);
            hotendTarget.Should().Be(200.0);
            bedTarget.Should().Be(60.0);
        }

        [Fact]
        public void ExtractTemperatureData_WithNullStatus_ThrowsArgumentNullException()
        {
            // Arrange
            var builder = CreateBuilder();

            // Act & Assert
            Assert.Throws<ArgumentNullException>(() =>
                builder.ExtractTemperatureData(null!));
        }

        [Fact]
        public void ExtractTemperatureData_WithNullTemperatures_ReturnsNullValues()
        {
            // Arrange
            var builder = CreateBuilder();
            var status = new PrinterCompositeStatus(true, null, null, null, null, null, null);

            // Act
            var (hotend, bed, hotendTarget, bedTarget) = builder.ExtractTemperatureData(status);

            // Assert
            hotend.Should().BeNull();
            bed.Should().BeNull();
            hotendTarget.Should().BeNull();
            bedTarget.Should().BeNull();
        }

        #endregion

        #region Position Data Extraction Tests

        [Fact]
        public void ExtractPositionData_WithValidData_ReturnsCorrectValues()
        {
            // Arrange
            var builder = CreateBuilder();
            var status = new PrinterCompositeStatus(
                IsOnline: true,
                State: null,
                Progress: null,
                JobName: null,
                ThumbnailUrl: null,
                CameraStreamUrl: null,
                CameraSnapshotUrl: null,
                X: 10.5,
                Y: 20.3,
                Z: 5.2
            );

            // Act
            var (x, y, z) = builder.ExtractPositionData(status);

            // Assert
            x.Should().Be(10.5);
            y.Should().Be(20.3);
            z.Should().Be(5.2);
        }

        [Fact]
        public void ExtractPositionData_WithNullStatus_ThrowsArgumentNullException()
        {
            // Arrange
            var builder = CreateBuilder();

            // Act & Assert
            Assert.Throws<ArgumentNullException>(() =>
                builder.ExtractPositionData(null!));
        }

        [Fact]
        public void ExtractPositionData_WithNullPositions_ReturnsNullValues()
        {
            // Arrange
            var builder = CreateBuilder();
            var status = new PrinterCompositeStatus(true, null, null, null, null, null, null);

            // Act
            var (x, y, z) = builder.ExtractPositionData(status);

            // Assert
            x.Should().BeNull();
            y.Should().BeNull();
            z.Should().BeNull();
        }

        #endregion

        #region Job Data Extraction Tests

        [Fact]
        public void ExtractJobData_WithValidData_ReturnsCorrectValues()
        {
            // Arrange
            var builder = CreateBuilder();
            var status = new PrinterCompositeStatus(
                IsOnline: true,
                State: "Printing",
                Progress: 0.75,
                JobName: "Model.gcode",
                ThumbnailUrl: "http://example.com/thumb.png",
                CameraStreamUrl: null,
                CameraSnapshotUrl: null
            );

            // Act
            var (jobName, progress, state, thumb) = builder.ExtractJobData(status);

            // Assert
            jobName.Should().Be("Model.gcode");
            progress.Should().Be(0.75);
            state.Should().Be("Printing");
            thumb.Should().Be("http://example.com/thumb.png");
        }

        [Fact]
        public void ExtractJobData_WithNullStatus_ThrowsArgumentNullException()
        {
            // Arrange
            var builder = CreateBuilder();

            // Act & Assert
            Assert.Throws<ArgumentNullException>(() =>
                builder.ExtractJobData(null!));
        }

        [Fact]
        public void ExtractJobData_WithNullValues_ReturnsNullValues()
        {
            // Arrange
            var builder = CreateBuilder();
            var status = new PrinterCompositeStatus(true, null, null, null, null, null, null);

            // Act
            var (jobName, progress, state, thumb) = builder.ExtractJobData(status);

            // Assert
            jobName.Should().BeNull();
            progress.Should().BeNull();
            state.Should().BeNull();
            thumb.Should().BeNull();
        }

        #endregion

        #region Helper Methods

        private static Printer CreateTestPrinter(PrinterBackend backend = PrinterBackend.Moonraker)
        {
            return new Printer
            {
                Id = Guid.NewGuid(),
                Name = "Test Printer",
                ServerUrl = "http://printer.local",
                ApiKey = "test_key",
                Backend = (int)backend,
                OriginalServerUrl = "http://printer.local",
                IpAddress = "192.168.1.100",
                BackendPort = 7125,
                FrontendPort = 80,
                Manufacturer = null,
                Model = null
            };
        }

        private static PrinterSpoolInfoDto CreateTestSpoolInfo()
        {
            return new PrinterSpoolInfoDto(
                HasActiveSpool: true,
                ActiveSpoolId: 1,
                SpoolName: "Test Spool",
                Material: "PLA",
                ColorHex: "FF0000",
                FilamentName: "Red PLA",
                Vendor: "TestVendor",
                RemainingWeightG: 500
            );
        }

        #endregion
    }
}
