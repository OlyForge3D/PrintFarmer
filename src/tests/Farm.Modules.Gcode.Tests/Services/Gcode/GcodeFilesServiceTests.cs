using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Gcode;
using Farm.Infrastructure.Repositories.PartsInventory;
using Farm.Infrastructure.Repositories.Queue;
using Farm.Infrastructure.Repositories.UnitOfWork;
using Farm.Infrastructure.Services.FileManagement;
using Farm.Infrastructure.Services.FolderManagement;
using Farm.Infrastructure.Services.Gcode;
using Farm.Infrastructure.Services.StorageManagement;
using Farm.Infrastructure.Services.Tags;
using Farm.Infrastructure.Telemetry;
using Farm.Modules.Gcode.DTOs;
using Farm.Modules.Gcode.Services.Gcode;
using Farm.Slicer.Module.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Farm.Modules.Gcode.Tests.Services.Gcode;

public class GcodeFilesServiceTests
{
    private static Mock<IFolderManagementService> CreateFolderServiceMock(string expectedPath)
    {
        var folder = new FolderNode
        {
            Id = Guid.NewGuid(),
            Path = expectedPath,
            FolderType = "gcode"
        };

        var mock = new Mock<IFolderManagementService>(MockBehavior.Strict);
        mock.Setup(f => f.GetOrCreateFolderAsync(expectedPath, "gcode", It.IsAny<CancellationToken>())).ReturnsAsync(folder);
        return mock;
    }

    private static Mock<IStoredFileOperationsService> CreateStoredFileOperationsServiceMock()
    {
        var mock = new Mock<IStoredFileOperationsService>(MockBehavior.Loose);
        mock.Setup(s => s.BuildGcodeThumbnailUrl(It.IsAny<Guid>()))
            .Returns<Guid>(fileId => $"/api/gcode-files/thumbnail/{fileId}");
        mock.Setup(s => s.GetFullFilePath(It.IsAny<StoredFile>()))
            .Returns<StoredFile>(f => Path.Join(f.FilePath, f.FileName));
        mock.Setup(s => s.GetFullThumbnailPath(It.IsAny<StoredFile>()))
            .Returns<StoredFile>(f => f.ThumbnailFileName != null ? Path.Join(f.FilePath, f.ThumbnailFileName) : null);
        mock.Setup(s => s.GenerateThumbnailFileName(It.IsAny<Guid>(), It.IsAny<string>()))
            .Returns<Guid, string>((id, ext) => $"{id}_thumb{ext}");
        return mock;
    }

    [Fact]
    public async Task FinalizeChunkedUploadAsync_ReturnsNullWhenFileMissingAndLogsWarning()
    {
        // Arrange
        var repo = new Mock<IGcodeRepository>(MockBehavior.Strict);
        var logger = new Mock<ILogger<GcodeFilesService>>(MockBehavior.Loose);
        var storagePath = new Mock<IStoragePathService>(MockBehavior.Strict);
        var metadataExtractor = new Mock<IGcodeMetadataExtractorService>(MockBehavior.Strict);
        var thumbnailExtractor = new Mock<IGcodeThumbnailExtractorService>(MockBehavior.Strict);
        var chunkedUpload = new Mock<IChunkedUploadService>(MockBehavior.Strict);

        string storageDir = Path.Join(Path.GetTempPath(), "pfarm-gcode-tests", Guid.NewGuid().ToString());
        storagePath.Setup(x => x.GetGcodeStorageDirectory()).Returns(storageDir);

        var mockFolderService = new Mock<IFolderManagementService>(MockBehavior.Loose);
        var mockUnitOfWork = new Mock<IUnitOfWork>(MockBehavior.Loose);
        var service = new GcodeFilesService(repo.Object, mockUnitOfWork.Object, logger.Object, storagePath.Object, metadataExtractor.Object, thumbnailExtractor.Object, mockFolderService.Object, CreateStoredFileOperationsServiceMock().Object, new Mock<IPrintFarmerTelemetryService>(MockBehavior.Loose).Object);

        // Act
        GcodeFile? result = await service.FinalizeChunkedUploadAsync(
            filePath: Path.Join(storageDir, "missing.gcode"),
            originalFileName: "missing.gcode",
            thumbnailPath: null,
            virtualDirectory: "/jobs",
            chunkedUploadService: chunkedUpload.Object,
            ct: CancellationToken.None);

        // Assert
        result.Should().BeNull();
        repo.Verify(r => r.AddAsync(It.IsAny<GcodeFile>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DownloadAsync_WithExistingFile_ReturnsPhysicalPathWithoutReadingContent()
    {
        // Arrange
        string storageDir = Path.Join(Path.GetTempPath(), "pfarm-gcode-tests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(storageDir);
        string fullPath = Path.Join(storageDir, "large.gcode");
        byte[] content = new byte[1024];
        await File.WriteAllBytesAsync(fullPath, content);

        try
        {
            var repo = new Mock<IGcodeRepository>(MockBehavior.Strict);
            var logger = new Mock<ILogger<GcodeFilesService>>(MockBehavior.Loose);
            var storagePath = new Mock<IStoragePathService>(MockBehavior.Strict);
            storagePath.Setup(x => x.GetGcodeStorageDirectory()).Returns(storageDir);
            var metadataExtractor = new Mock<IGcodeMetadataExtractorService>(MockBehavior.Strict);
            var thumbnailExtractor = new Mock<IGcodeThumbnailExtractorService>(MockBehavior.Strict);
            var mockFolderService = new Mock<IFolderManagementService>(MockBehavior.Loose);
            var mockUnitOfWork = new Mock<IUnitOfWork>(MockBehavior.Loose);
            var service = new GcodeFilesService(repo.Object, mockUnitOfWork.Object, logger.Object, storagePath.Object, metadataExtractor.Object,
                thumbnailExtractor.Object, mockFolderService.Object, CreateStoredFileOperationsServiceMock().Object, new Mock<IPrintFarmerTelemetryService>(MockBehavior.Loose).Object);

            // Act
            (string FullPath, string FileName)? result = await service.DownloadAsync("/large.gcode", CancellationToken.None);

            // Assert - the service resolves the physical path/filename, it must never buffer file bytes.
            result.Should().NotBeNull();
            result!.Value.FullPath.Should().Be(fullPath);
            result.Value.FileName.Should().Be("large.gcode");
        }
        finally
        {
            Directory.Delete(storageDir, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadAsync_WithMissingFile_ReturnsNull()
    {
        // Arrange
        string storageDir = Path.Join(Path.GetTempPath(), "pfarm-gcode-tests", Guid.NewGuid().ToString());
        var repo = new Mock<IGcodeRepository>(MockBehavior.Strict);
        var logger = new Mock<ILogger<GcodeFilesService>>(MockBehavior.Loose);
        var storagePath = new Mock<IStoragePathService>(MockBehavior.Strict);
        storagePath.Setup(x => x.GetGcodeStorageDirectory()).Returns(storageDir);
        var metadataExtractor = new Mock<IGcodeMetadataExtractorService>(MockBehavior.Strict);
        var thumbnailExtractor = new Mock<IGcodeThumbnailExtractorService>(MockBehavior.Strict);
        var mockFolderService = new Mock<IFolderManagementService>(MockBehavior.Loose);
        var mockUnitOfWork = new Mock<IUnitOfWork>(MockBehavior.Loose);
        var service = new GcodeFilesService(repo.Object, mockUnitOfWork.Object, logger.Object, storagePath.Object, metadataExtractor.Object,
            thumbnailExtractor.Object, mockFolderService.Object, CreateStoredFileOperationsServiceMock().Object, new Mock<IPrintFarmerTelemetryService>(MockBehavior.Loose).Object);

        // Act
        (string FullPath, string FileName)? result = await service.DownloadAsync("/missing.gcode", CancellationToken.None);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task DownloadFileAsync_ById_WithExistingFile_ReturnsPhysicalPathWithoutReadingContent()
    {
        // Arrange
        string storageDir = Path.Join(Path.GetTempPath(), "pfarm-gcode-tests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(storageDir);
        string fileName = "byid.gcode";
        string fullPath = Path.Join(storageDir, fileName);
        await File.WriteAllBytesAsync(fullPath, new byte[2048]);

        try
        {
            var fileId = Guid.NewGuid();
            var gcodeFile = new GcodeFile
            {
                Id = fileId,
                Name = fileName,
                FileName = fileName,
                FilePath = storageDir,
                FileHash = "testhash",
                FileSizeBytes = 2048,
                UploadedAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                Source = GcodeSource.Upload
            };

            var repo = new Mock<IGcodeRepository>(MockBehavior.Strict);
            repo.Setup(x => x.GetByIdWithIncludesAsync(fileId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(gcodeFile);
            var logger = new Mock<ILogger<GcodeFilesService>>(MockBehavior.Loose);
            var storagePath = new Mock<IStoragePathService>(MockBehavior.Strict);
            storagePath.Setup(x => x.GetGcodeStorageDirectory()).Returns(storageDir);
            var metadataExtractor = new Mock<IGcodeMetadataExtractorService>(MockBehavior.Strict);
            var thumbnailExtractor = new Mock<IGcodeThumbnailExtractorService>(MockBehavior.Strict);
            var mockFolderService = new Mock<IFolderManagementService>(MockBehavior.Loose);
            var mockUnitOfWork = new Mock<IUnitOfWork>(MockBehavior.Loose);
            var service = new GcodeFilesService(repo.Object, mockUnitOfWork.Object, logger.Object, storagePath.Object, metadataExtractor.Object,
                thumbnailExtractor.Object, mockFolderService.Object, CreateStoredFileOperationsServiceMock().Object, new Mock<IPrintFarmerTelemetryService>(MockBehavior.Loose).Object);

            // Act
            string? result = await service.DownloadFileAsync(fileId, "/app/wwwroot", CancellationToken.None);

            // Assert
            result.Should().Be(fullPath);
        }
        finally
        {
            Directory.Delete(storageDir, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadFileAsync_ById_WhenPhysicalFileMissing_ReturnsNull()
    {
        // Arrange
        var fileId = Guid.NewGuid();
        string storageDir = Path.Join(Path.GetTempPath(), "pfarm-gcode-tests", Guid.NewGuid().ToString());
        var gcodeFile = new GcodeFile
        {
            Id = fileId,
            Name = "missing.gcode",
            FileName = "missing.gcode",
            FilePath = storageDir,
            FileHash = "testhash",
            FileSizeBytes = 0,
            UploadedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            Source = GcodeSource.Upload
        };

        var repo = new Mock<IGcodeRepository>(MockBehavior.Strict);
        repo.Setup(x => x.GetByIdWithIncludesAsync(fileId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(gcodeFile);
        var logger = new Mock<ILogger<GcodeFilesService>>(MockBehavior.Loose);
        var storagePath = new Mock<IStoragePathService>(MockBehavior.Strict);
        storagePath.Setup(x => x.GetGcodeStorageDirectory()).Returns(storageDir);
        var metadataExtractor = new Mock<IGcodeMetadataExtractorService>(MockBehavior.Strict);
        var thumbnailExtractor = new Mock<IGcodeThumbnailExtractorService>(MockBehavior.Strict);
        var mockFolderService = new Mock<IFolderManagementService>(MockBehavior.Loose);
        var mockUnitOfWork = new Mock<IUnitOfWork>(MockBehavior.Loose);
        var service = new GcodeFilesService(repo.Object, mockUnitOfWork.Object, logger.Object, storagePath.Object, metadataExtractor.Object,
            thumbnailExtractor.Object, mockFolderService.Object, CreateStoredFileOperationsServiceMock().Object, new Mock<IPrintFarmerTelemetryService>(MockBehavior.Loose).Object);

        // Act
        string? result = await service.DownloadFileAsync(fileId, "/app/wwwroot", CancellationToken.None);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task DownloadFileAsync_ById_WithEscapingVirtualPath_ReturnsNull()
    {
        string parent = Path.Join(Path.GetTempPath(), "pfarm-gcode-tests", Guid.NewGuid().ToString());
        string storageDir = Path.Join(parent, "root");
        string outsideDir = Path.Join(parent, "outside");
        Directory.CreateDirectory(storageDir);
        Directory.CreateDirectory(outsideDir);
        string fileName = "escaped.gcode";
        await File.WriteAllTextAsync(Path.Join(outsideDir, fileName), "G28");

        try
        {
            var fileId = Guid.NewGuid();
            var repo = new Mock<IGcodeRepository>(MockBehavior.Strict);
            repo.Setup(x => x.GetByIdWithIncludesAsync(fileId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GcodeFile
                {
                    Id = fileId,
                    Name = fileName,
                    FileName = fileName,
                    FilePath = "../outside",
                    FileHash = "testhash",
                    FileSizeBytes = 3,
                    UploadedAt = DateTime.UtcNow,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                    Source = GcodeSource.Upload,
                });
            var storagePath = new Mock<IStoragePathService>(MockBehavior.Strict);
            storagePath.Setup(x => x.GetGcodeStorageDirectory()).Returns(storageDir);
            var service = new GcodeFilesService(
                repo.Object,
                new Mock<IUnitOfWork>(MockBehavior.Loose).Object,
                new Mock<ILogger<GcodeFilesService>>(MockBehavior.Loose).Object,
                storagePath.Object,
                new Mock<IGcodeMetadataExtractorService>(MockBehavior.Strict).Object,
                new Mock<IGcodeThumbnailExtractorService>(MockBehavior.Strict).Object,
                new Mock<IFolderManagementService>(MockBehavior.Loose).Object,
                CreateStoredFileOperationsServiceMock().Object,
                new Mock<IPrintFarmerTelemetryService>(MockBehavior.Loose).Object);

            string? result = await service.DownloadFileAsync(
                fileId,
                string.Empty,
                CancellationToken.None);

            result.Should().BeNull();
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    [Theory]
    [InlineData("/", "")]
    [InlineData("/nested/thumbnails", "nested/thumbnails")]
    public async Task GetThumbnailPathAsync_WithVirtualDirectory_ResolvesContainedPhysicalPath(
        string virtualDirectory,
        string relativeDirectory)
    {
        string storageRoot = Path.Join(
            Path.GetTempPath(),
            "pfarm-thumbnail-service-tests",
            Guid.NewGuid().ToString());
        string directory = Path.Join(
            storageRoot,
            relativeDirectory.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(directory);
        string thumbnailFileName = "preview.png";
        string expectedPath = Path.Join(directory, thumbnailFileName);
        await File.WriteAllBytesAsync(expectedPath, [1, 2, 3]);

        try
        {
            Guid fileId = Guid.NewGuid();
            var repository = new Mock<IGcodeRepository>(MockBehavior.Strict);
            repository.Setup(candidate => candidate.GetByIdWithIncludesAsync(
                    fileId,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GcodeFile
                {
                    Id = fileId,
                    Name = "model.gcode",
                    FileName = "model.gcode",
                    FilePath = virtualDirectory,
                    ThumbnailFileName = thumbnailFileName,
                    FileHash = "hash",
                    UploadedAt = DateTime.UtcNow,
                });
            var storagePaths = new Mock<IStoragePathService>(MockBehavior.Strict);
            storagePaths.Setup(service => service.GetGcodeStorageDirectory()).Returns(storageRoot);
            var service = new GcodeFilesService(
                repository.Object,
                new Mock<IUnitOfWork>(MockBehavior.Loose).Object,
                new Mock<ILogger<GcodeFilesService>>(MockBehavior.Loose).Object,
                storagePaths.Object,
                new Mock<IGcodeMetadataExtractorService>(MockBehavior.Loose).Object,
                new Mock<IGcodeThumbnailExtractorService>(MockBehavior.Loose).Object,
                new Mock<IFolderManagementService>(MockBehavior.Loose).Object,
                CreateStoredFileOperationsServiceMock().Object,
                new Mock<IPrintFarmerTelemetryService>(MockBehavior.Loose).Object);

            string? result = await service.GetThumbnailPathAsync(fileId, CancellationToken.None);

            result.Should().Be(expectedPath);
        }
        finally
        {
            Directory.Delete(storageRoot, recursive: true);
        }
    }

    [Fact]
    public async Task GetThumbnailPathAsync_WithUnsafeStoredPath_ReturnsNull()
    {
        Guid fileId = Guid.NewGuid();
        string storageRoot = Path.Join(Path.GetTempPath(), Guid.NewGuid().ToString());
        var repository = new Mock<IGcodeRepository>(MockBehavior.Strict);
        repository.Setup(candidate => candidate.GetByIdWithIncludesAsync(
                fileId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GcodeFile
            {
                Id = fileId,
                Name = "model.gcode",
                FileName = "model.gcode",
                FilePath = "../outside",
                ThumbnailFileName = "preview.png",
                FileHash = "hash",
                UploadedAt = DateTime.UtcNow,
            });
        var storagePaths = new Mock<IStoragePathService>(MockBehavior.Strict);
        storagePaths.Setup(service => service.GetGcodeStorageDirectory()).Returns(storageRoot);
        var service = new GcodeFilesService(
            repository.Object,
            new Mock<IUnitOfWork>(MockBehavior.Loose).Object,
            new Mock<ILogger<GcodeFilesService>>(MockBehavior.Loose).Object,
            storagePaths.Object,
            new Mock<IGcodeMetadataExtractorService>(MockBehavior.Loose).Object,
            new Mock<IGcodeThumbnailExtractorService>(MockBehavior.Loose).Object,
            new Mock<IFolderManagementService>(MockBehavior.Loose).Object,
            CreateStoredFileOperationsServiceMock().Object,
            new Mock<IPrintFarmerTelemetryService>(MockBehavior.Loose).Object);

        string? result = await service.GetThumbnailPathAsync(fileId, CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task FinalizeChunkedUploadAsync_PersistsMetadataAndRenamesFiles()
    {
        // Arrange
        string storageDir = Path.Join(Path.GetTempPath(), "pfarm-gcode-tests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(storageDir);
        string sourcePath = Path.Join(storageDir, "input.gcode");
        await File.WriteAllTextAsync(sourcePath, "G1 X1 Y1\n; generated by TestSlicer 1.0");
        string thumbnailSource = Path.Join(storageDir, "thumb.png");
        await File.WriteAllBytesAsync(thumbnailSource, new byte[] { 1, 2, 3, 4 });

        var metadata = new GcodeMetadataExtracted
        {
            SlicerName = "TestSlicer",
            SlicerVersion = "1.0",
            NozzleDiameter = 0.4,
            Material = "PLA",
            EstimatedPrintTimeMinutes = 42,
            FilamentLengthMm = 1234,
            FilamentWeightGrams = 56,
            FilamentPerExtruderWeightG = [10.3, 3.8],
            FilamentPerExtruderLengthMm = [3432.2, 1208.1],
            FilamentPerExtruderColorHex = ["#F2AB1F", "#FFFFFF"],
            FilamentPerExtruderType = ["PLA", "PETG"],
            ExtruderCount = 2
        };

        GcodeFile? added = null;
        var repo = new Mock<IGcodeRepository>(MockBehavior.Strict);
        repo.Setup(r => r.AddAsync(It.IsAny<GcodeFile>(), It.IsAny<CancellationToken>()))
            .Callback<GcodeFile, CancellationToken>((g, _) => added = g)
            .Returns(Task.CompletedTask);
        repo.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var logger = new Mock<ILogger<GcodeFilesService>>(MockBehavior.Loose);
        var storagePath = new Mock<IStoragePathService>(MockBehavior.Strict);
        storagePath.Setup(x => x.GetGcodeStorageDirectory()).Returns(storageDir);
        var metadataExtractor = new Mock<IGcodeMetadataExtractorService>(MockBehavior.Strict);
        var thumbnailExtractor = new Mock<IGcodeThumbnailExtractorService>(MockBehavior.Strict);
        var chunkedUpload = new Mock<IChunkedUploadService>(MockBehavior.Strict);
        chunkedUpload.Setup(c => c.ExtractMetadataFromFileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(metadata);

        Mock<IFolderManagementService> mockFolderService = CreateFolderServiceMock("prints/models");
        var mockUnitOfWork = new Mock<IUnitOfWork>(MockBehavior.Loose);
        var service = new GcodeFilesService(repo.Object, mockUnitOfWork.Object, logger.Object, storagePath.Object, metadataExtractor.Object, thumbnailExtractor.Object, mockFolderService.Object, CreateStoredFileOperationsServiceMock().Object, new Mock<IPrintFarmerTelemetryService>(MockBehavior.Loose).Object);

        // Act
        GcodeFile? result = await service.FinalizeChunkedUploadAsync(
            filePath: sourcePath,
            originalFileName: "cool-model.gcode",
            thumbnailPath: thumbnailSource,
            virtualDirectory: "/prints/models/",
            chunkedUploadService: chunkedUpload.Object,
            ct: CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        added.Should().NotBeNull();
        added!.Id.Should().NotBeEmpty();
        // FileName should now be GUID-based (matching standardized pattern)
        added.FileName.Should().Be($"{added.Id}.gcode");
        added.SlicerName.Should().Be("TestSlicer");
        added.SlicerVersion.Should().Be("1.0");
        added.RequiredNozzleDiameter.Should().Be(0.4);
        added.RequiredMaterial.Should().Be("PLA");
        added.EstimatedPrintTimeMinutes.Should().Be(42);
        added.EstimatedFilamentLengthMm.Should().Be(1234);
        added.EstimatedFilamentWeightG.Should().Be(56);
        JsonSerializer.Deserialize<double[]>(added.FilamentPerExtruderWeightG!).Should().Equal(10.3, 3.8);
        JsonSerializer.Deserialize<double[]>(added.FilamentPerExtruderLengthMm!).Should().Equal(3432.2, 1208.1);
        JsonSerializer.Deserialize<string[]>(added.FilamentPerExtruderColorHex!).Should().Equal("#F2AB1F", "#FFFFFF");
        JsonSerializer.Deserialize<string[]>(added.FilamentPerExtruderType!).Should().Equal("PLA", "PETG");
        added.ExtruderCount.Should().Be(2);

        // FilePath should be the storage directory, construct full path with FileName
        string fullPath = Path.Join(added.FilePath, added.FileName);
        File.Exists(fullPath).Should().BeTrue();
        File.Exists(sourcePath).Should().BeFalse();

        string expectedThumb = Path.Join(storageDir, added.Id + "_thumb.png");
        added.ThumbnailFileName.Should().Be(Path.GetFileName(expectedThumb));  // Store just filename, matching standardized pattern
        File.Exists(expectedThumb).Should().BeTrue();

        using FileStream fs = File.OpenRead(fullPath);  // Open the full file path, not just FilePath (directory)
        using SHA256 sha = SHA256.Create();
        string expectedHash = Convert.ToHexString(await sha.ComputeHashAsync(fs, CancellationToken.None));
        added.FileHash.Should().Be(expectedHash);

        repo.Verify(r => r.AddAsync(It.IsAny<GcodeFile>(), It.IsAny<CancellationToken>()), Times.Once);
        repo.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task QueryAsync_NormalizesThumbnailUrl()
    {
        // Arrange
        string storageDir = Path.Join(Path.GetTempPath(), "pfarm-gcode-tests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(storageDir);
        string thumbPath = Path.Join(storageDir, "thumbs", "model1.png");
        Directory.CreateDirectory(Path.GetDirectoryName(thumbPath)!);
        await File.WriteAllBytesAsync(thumbPath, new byte[] { 9, 9, 9 });

        Guid printerId = Guid.NewGuid();
        var folderJob = new FolderNode { Id = Guid.NewGuid(), Path = "/jobs", FolderType = "gcode" };

        var dbFiles = new List<GcodeFile>
        {
            new()
            {
                Id = Guid.NewGuid(),
                FileName = "model1.gcode",  // Full filename with extension
                FolderId = folderJob.Id,
                FilePath = "/jobs/model1.gcode",  // Virtual path (NOT physical path)
                FileSizeBytes = 1024,
                UploadedAt = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                SourcePrinterId = printerId,
                ThumbnailFileName = thumbPath
            },
            new()
            {
                Id = Guid.NewGuid(),
                FileName = "other.gcode",  // Full filename with extension
                FolderId = folderJob.Id,
                FilePath = "/jobs/other.gcode",  // Virtual path (NOT physical path)
                FileSizeBytes = 2048,
                UploadedAt = new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc),
                SourcePrinterId = Guid.NewGuid(),
                ThumbnailFileName = Path.Join(storageDir, "thumbs", "other.png")
            }
        };

        var repo = new Mock<IGcodeRepository>(MockBehavior.Strict);
        repo.Setup(r => r.QueryFilesAsync(
            "/jobs",
            null, // search
            null, // tagIds
            null, // printerModelId
            null, // printerId
            "name", // sortBy
            "asc", // sortOrder
            1, // page
            10, // pageSize
            It.IsAny<CancellationToken>()
        )).ReturnsAsync((dbFiles, 2));

        var logger = new Mock<ILogger<GcodeFilesService>>(MockBehavior.Loose);
        var storagePath = new Mock<IStoragePathService>(MockBehavior.Strict);
        storagePath.Setup(x => x.GetGcodeStorageDirectory()).Returns(storageDir);
        var metadataExtractor = new Mock<IGcodeMetadataExtractorService>(MockBehavior.Strict);
        var thumbnailExtractor = new Mock<IGcodeThumbnailExtractorService>(MockBehavior.Strict);
        var folderService = new Mock<IFolderManagementService>(MockBehavior.Loose);
        var mockUnitOfWork = new Mock<IUnitOfWork>(MockBehavior.Loose);

        var service = new GcodeFilesService(repo.Object, mockUnitOfWork.Object, logger.Object, storagePath.Object, metadataExtractor.Object, thumbnailExtractor.Object, folderService.Object, CreateStoredFileOperationsServiceMock().Object, new Mock<IPrintFarmerTelemetryService>(MockBehavior.Loose).Object);

        // Act
        GcodeFileListResponse response = await service.QueryAsync(
            path: "/jobs",
            sortBy: "name",
            sortOrder: "asc",
            search: null,
            page: 1,
            pageSize: 10,
            tagIds: null,
            printerModelId: null,
            printerId: null,
            ct: CancellationToken.None);

        // Assert
        response.Files.Should().HaveCount(2);  // Both files returned
        response.TotalFiles.Should().Be(2);    // Total count from repository
        response.TotalSize.Should().Be(3072);  // 1024 + 2048

        // QueryAsync returns files sorted by name, so check both
        GcodeFileEntryDto firstFile = response.Files[0];
        firstFile.IsDirectory.Should().BeFalse();
        firstFile.FileName.Should().Be("model1.gcode");
        firstFile.Path.Should().Be("/jobs/model1.gcode");  // Virtual path constructed from folder path + filename
        firstFile.ThumbnailUrl.Should().Be($"/api/gcode-files/thumbnail/{dbFiles[0].Id}");

        GcodeFileEntryDto secondFile = response.Files[1];
        secondFile.IsDirectory.Should().BeFalse();
        secondFile.FileName.Should().Be("other.gcode");
        secondFile.Path.Should().Be("/jobs/other.gcode");  // Virtual path constructed from folder path + filename
    }
}
