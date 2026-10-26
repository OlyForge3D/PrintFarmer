using System.Security.Cryptography;
using System.Text;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Gcode;
using Farm.Infrastructure.Repositories.UnitOfWork;
using Farm.Infrastructure.Services.FileManagement;
using Farm.Infrastructure.Services.FolderManagement;
using Farm.Infrastructure.Services.Gcode;
using Farm.Infrastructure.Services.Gcode.PrinterFiles;
using Farm.Infrastructure.Services.StorageManagement;
using Farm.Infrastructure.Telemetry;
using Farm.Modules.Gcode.Services.Gcode;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Modules.Gcode.Tests.Services.Gcode;

public sealed class PrinterFileNeutralIngestTests
{
    [Theory]
    [InlineData("; captured output\nG28\nG1 X10 Y10 F3000\nM104 S0\nM140 S0\nM84\n", false)]
    [InlineData("G28\nG1 X10 Y10\n", false)]
    [InlineData("G28\n", true)]
    public async Task PrepareAsync_ParsesLocalBytesWithoutFabricatingPromotionLineageOrSavingAsync(
        string content, bool withBom)
    {
        string directory = Path.Join(Path.GetTempPath(), "neutral-ingest", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string staged = Path.Join(directory, "capture.part");
        byte[] bytes = withBom
            ? [.. Encoding.UTF8.Preamble, .. Encoding.UTF8.GetBytes(content)]
            : Encoding.UTF8.GetBytes(content);
        await File.WriteAllBytesAsync(staged, bytes);
        try
        {
            var repository = new Mock<IGcodeRepository>(MockBehavior.Strict);
            repository.Setup(x => x.ResolvePrinterModelIdAsync(null, It.IsAny<CancellationToken>()))
                .ReturnsAsync((Guid?)null);
            var metadata = new Mock<IGcodeMetadataExtractorService>(MockBehavior.Strict);
            metadata.Setup(x => x.ExtractMetadataAsync(content))
                .ReturnsAsync(new GcodeMetadataExtracted { Material = "PLA", NozzleDiameter = 0.4 });
            var storage = new Mock<IStoragePathService>();
            storage.Setup(x => x.GetGcodeStorageDirectory()).Returns(directory);
            var service = new GcodeFilesService(repository.Object, Mock.Of<IUnitOfWork>(),
                NullLogger<GcodeFilesService>.Instance, storage.Object, metadata.Object,
                Mock.Of<IGcodeThumbnailExtractorService>(), Mock.Of<IFolderManagementService>(),
                Mock.Of<IStoredFileOperationsService>(), Mock.Of<IPrintFarmerTelemetryService>());
            Guid id = Guid.NewGuid();

            GcodeFile prepared = await service.PrepareAsync(id, staged, "part.gcode", "/",
                Convert.ToHexString(SHA256.HashData(bytes)), bytes.Length, CancellationToken.None);

            Assert.Equal(id, prepared.Id);
            Assert.Equal("PLA", prepared.RequiredMaterial);
            Assert.Equal(0.4, prepared.RequiredNozzleDiameter);
            Assert.Equal(Guid.Empty, prepared.FolderId);
            Assert.Null(prepared.SourceArtifactId);
            Assert.Null(prepared.SourceSliceJobId);
            Assert.Null(prepared.CalibrationProjectId);
            Assert.Null(prepared.PromotionOperationId);
            Assert.False(prepared.IsImmutable);
            repository.Verify(x => x.AddAsync(It.IsAny<GcodeFile>(), It.IsAny<CancellationToken>()), Times.Never);
            repository.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(false, "local_content_changed")]
    [InlineData(true, "unsupported_gcode_encoding")]
    public async Task PrepareAsync_RejectsDigestMismatchAndNonUtf8BytesAsync(bool invalidEncoding, string reason)
    {
        string staged = Path.Join(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".part");
        byte[] bytes = invalidEncoding ? [0x47, 0x32, 0x38, 0x0A, 0xFF] : Encoding.UTF8.GetBytes("G28\n");
        await File.WriteAllBytesAsync(staged, bytes);
        try
        {
            var metadata = new Mock<IGcodeMetadataExtractorService>(MockBehavior.Strict);
            var service = new GcodeFilesService(Mock.Of<IGcodeRepository>(), Mock.Of<IUnitOfWork>(),
                NullLogger<GcodeFilesService>.Instance, Mock.Of<IStoragePathService>(),
                metadata.Object, Mock.Of<IGcodeThumbnailExtractorService>(),
                Mock.Of<IFolderManagementService>(), Mock.Of<IStoredFileOperationsService>(),
                Mock.Of<IPrintFarmerTelemetryService>());
            string digest = invalidEncoding ? Convert.ToHexString(SHA256.HashData(bytes)) : new string('0', 64);

            PrinterFileCommandException error = await Assert.ThrowsAsync<PrinterFileCommandException>(() =>
                service.PrepareAsync(Guid.NewGuid(), staged, "part.gcode", "/", digest,
                    bytes.Length, CancellationToken.None));

            Assert.Equal(reason, error.Reason);
            metadata.VerifyNoOtherCalls();
        }
        finally
        {
            File.Delete(staged);
        }
    }

    [Theory]
    [InlineData("RUN_SHELL_COMMAND CMD=bad\n", "unsupported_gcode_format")]
    [InlineData("G28\0\n", "unsupported_gcode_format")]
    [InlineData("", "local_content_changed")]
    public async Task PrepareAsync_UnsupportedFormatNeverBecomesOwnedContentAsync(string content, string reason)
    {
        string staged = Path.Join(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".part");
        byte[] bytes = Encoding.UTF8.GetBytes(content);
        await File.WriteAllBytesAsync(staged, bytes);
        try
        {
            var service = new GcodeFilesService(Mock.Of<IGcodeRepository>(), Mock.Of<IUnitOfWork>(),
                NullLogger<GcodeFilesService>.Instance, Mock.Of<IStoragePathService>(),
                Mock.Of<IGcodeMetadataExtractorService>(), Mock.Of<IGcodeThumbnailExtractorService>(),
                Mock.Of<IFolderManagementService>(), Mock.Of<IStoredFileOperationsService>(),
                Mock.Of<IPrintFarmerTelemetryService>());
            PrinterFileCommandException error = await Assert.ThrowsAsync<PrinterFileCommandException>(() =>
                service.PrepareAsync(Guid.NewGuid(), staged, "part.gcode", "/",
                    Convert.ToHexString(SHA256.HashData(bytes)), bytes.Length, CancellationToken.None));
            Assert.Equal(reason, error.Reason);
        }
        finally
        {
            File.Delete(staged);
        }
    }
}
