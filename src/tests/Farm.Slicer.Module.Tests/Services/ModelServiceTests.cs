using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Repositories.Tags;
using Farm.Infrastructure.Repositories.UnitOfWork;
using Farm.Infrastructure.Services.StorageManagement;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Services.FileManagement;
using Farm.Web.Api.Services.FolderManagement;
using Farm.Web.Api.Services.Model;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;
using IStoredFileOperationsService = Farm.Web.Api.Services.FileManagement.IStoredFileOperationsService;


namespace Farm.Slicer.Module.Tests.Services
{
    public class ModelServiceTests
    {
        private static IFormFile CreateFormFile(string name, string content, string fileName)
        {
            MemoryStream ms = new MemoryStream(Encoding.UTF8.GetBytes(content));
            return new FormFile(ms, 0, ms.Length, name, fileName);
        }

        private static Mock<IFolderManagementService> CreateFolderServiceMock()
        {
            var folder = new FolderNode
            {
                Id = Guid.NewGuid(),
                Path = "/",
                FolderType = "models"
            };

            var mock = new Mock<IFolderManagementService>(MockBehavior.Strict);
            _ = mock.Setup(f => f.GetOrCreateFolderAsync("/", "models", It.IsAny<CancellationToken>()))
                .ReturnsAsync(folder);
            return mock;
        }

        private static Mock<IStoredFileOperationsService> CreateStoredFileOperationsServiceMock()
        {
            var mock = new Mock<IStoredFileOperationsService>(MockBehavior.Loose);
            mock.Setup(s => s.BuildModel3DThumbnailUrl(It.IsAny<Guid>()))
                .Returns<Guid>(modelId => $"/api/3d-models/thumbnail/{modelId}");
            mock.Setup(s => s.GetFullFilePath(It.IsAny<StoredFile>()))
                .Returns<StoredFile>(f => Path.Combine(f.FilePath, f.FileName));
            mock.Setup(s => s.GetFullThumbnailPath(It.IsAny<StoredFile>()))
                .Returns<StoredFile>(f => f.ThumbnailFileName != null ? Path.Combine(f.FilePath, f.ThumbnailFileName) : null);
            mock.Setup(s => s.GenerateThumbnailFileName(It.IsAny<Guid>(), It.IsAny<string>()))
                .Returns<Guid, string>((id, ext) => $"{id}_thumb{ext}");
            return mock;
        }

        [Fact]
        public async Task UploadModelAsync_HappyPath_CreatesEntity()
        {
            IConfigurationRoot config = new ConfigurationBuilder().AddInMemoryCollection().Build();
            Mock<IUnifiedLoggingService> mockLogger = new Mock<IUnifiedLoggingService>();

            Mock<IModel3DFileRepository> mockRepo = new Mock<IModel3DFileRepository>(MockBehavior.Strict);
            // For happy path: repository returns no existing model for the hash and will accept AddAsync
            _ = mockRepo.Setup(r => r.GetByHashAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Model3D?)null);
            _ = mockRepo.Setup(r => r.AddAsync(It.IsAny<Model3D>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            _ = mockRepo.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

            Mock<IFileManagementService> mockFileManagement = new Mock<IFileManagementService>();
            _ = mockFileManagement.Setup(s => s.IsSafePath(It.IsAny<string>(), It.IsAny<string>())).Returns(true);

            Mock<IFolderManagementService> mockFolderService = CreateFolderServiceMock();

            // Wrap the repository in a UnitOfWork mock
            Mock<IUnitOfWork> mockUoW = new Mock<IUnitOfWork>(MockBehavior.Loose);
            mockUoW.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

            // Mock IStoragePathService (like GcodeFilesService does)
            var mockStoragePath = new Mock<IStoragePathService>(MockBehavior.Strict);
            string tempDir = Path.Combine(Path.GetTempPath(), "pfarm-model-tests", Guid.NewGuid().ToString());
            mockStoragePath.Setup(x => x.GetModelUploadDirectory()).Returns(tempDir);

            Model3DFileService service = new Model3DFileService(mockUoW.Object, mockRepo.Object, new Mock<ITagRepository>().Object, mockLogger.Object, config, TestFileSystemFactory.WithFiles(new Dictionary<string, byte[]>()), mockFileManagement.Object, mockFolderService.Object, mockStoragePath.Object, CreateStoredFileOperationsServiceMock().Object);

            IFormFile file = CreateFormFile("file", "dummy-content", "model.stl");

            Model3DUploadResultDto result = await service.UploadModelAsync(file, CancellationToken.None);

            Assert.NotNull(result);
            // FileName should now be GUID-based (matching GcodeFile pattern)
            Assert.EndsWith(".stl", result.FileName);
            Assert.Equal("stl", result.FileType);
            mockRepo.Verify(r => r.GetByHashAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task UploadModelAsync_Duplicate_TreatedAsDuplicate()
        {
            IConfigurationRoot config = new ConfigurationBuilder().AddInMemoryCollection().Build();
            Mock<IUnifiedLoggingService> mockLogger = new Mock<IUnifiedLoggingService>();

            // Prepare an existing model with a computed hash matching the test content
            string content = "dummy-content";
            byte[] hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
            string contentHash = Convert.ToHexString(hashBytes).ToLowerInvariant();

            Model3D existing = new Model3D
            {
                Id = Guid.NewGuid(),
                FileName = "model.stl",
                FilePath = "path",
                FileSizeBytes = 12,
                FileHash = contentHash,
                FileFormat = ModelFileFormat.STL,
                UploadedAt = DateTime.UtcNow,
                IsValid = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            Mock<IModel3DFileRepository> mockRepo = new Mock<IModel3DFileRepository>(MockBehavior.Strict);
            // For duplicate scenario: GetByHashAsync returns existing model for any hash
            _ = mockRepo.Setup(r => r.GetByHashAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(existing);

            Mock<IFileManagementService> mockFileManagement = new Mock<IFileManagementService>();
            _ = mockFileManagement.Setup(s => s.IsSafePath(It.IsAny<string>(), It.IsAny<string>())).Returns(true);
            // Setup ToHex to match the expected hash
            _ = mockFileManagement.Setup(s => s.ToHex(It.IsAny<byte[]>()))
                .Returns<byte[]>(b => Convert.ToHexString(b).ToLowerInvariant());
            // Setup ToHex to match the expected hash
            _ = mockFileManagement.Setup(s => s.ToHex(It.IsAny<byte[]>()))
                .Returns<byte[]>(b => Convert.ToHexString(b).ToLowerInvariant());

            Mock<IFolderManagementService> mockFolderService = CreateFolderServiceMock();

            // Wrap the repository in a UnitOfWork mock
            Mock<IUnitOfWork> mockUoW = new Mock<IUnitOfWork>(MockBehavior.Loose);
            mockUoW.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

            // Mock IStoragePathService (like GcodeFilesService does)
            var mockStoragePath = new Mock<IStoragePathService>(MockBehavior.Strict);
            string tempDir = Path.Combine(Path.GetTempPath(), "pfarm-model-tests", Guid.NewGuid().ToString());
            mockStoragePath.Setup(x => x.GetModelUploadDirectory()).Returns(tempDir);

            Model3DFileService service = new Model3DFileService(mockUoW.Object, mockRepo.Object, new Mock<ITagRepository>().Object, mockLogger.Object, config, TestFileSystemFactory.WithFiles(new Dictionary<string, byte[]>()), mockFileManagement.Object, mockFolderService.Object, mockStoragePath.Object, CreateStoredFileOperationsServiceMock().Object);
            IFormFile file = CreateFormFile("file", content, "model.stl");

            Model3DUploadResultDto result = await service.UploadModelAsync(file, CancellationToken.None);

            // Should return existing model info (duplicate)
            Assert.Equal(existing.Id, result.Id);
            mockRepo.Verify(r => r.GetByHashAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        }
    }
}
