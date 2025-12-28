using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Model;
using Farm.Infrastructure.Services.Models;
using Farm.Infrastructure.Services.Thumbnails;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Services.FileManagement;
using Farm.Web.Api.Services.Model;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services
{
    public class ModelServiceAdditionalTests
    {
        private static IFormFile CreateFormFile(string name, string content, string fileName)
        {
            MemoryStream ms = new MemoryStream(Encoding.UTF8.GetBytes(content));
            return new FormFile(ms, 0, ms.Length, name, fileName);
        }

        [Fact]
        public async Task UploadModelAsync_CompositeHash_Path_CreatesNewHash()
        {
            IConfigurationRoot config = new ConfigurationBuilder().AddInMemoryCollection().Build();
            Mock<IUnifiedLoggingService> mockLogger = new Mock<IUnifiedLoggingService>();

            // Arrange: existing model with same file hash but different base name and same extension
            string content = "abc123";
            byte[] hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
            string contentHash = Convert.ToHexString(hashBytes).ToLowerInvariant();

            Model3D existing = new Model3D
            {
                Id = Guid.NewGuid(),
                OriginalFileName = "othername.stl",
                FileHash = contentHash,
                FileFormat = Farm.Infrastructure.Domain.ModelFileFormat.STL,
                IsValid = true,
                UploadedAt = DateTime.UtcNow
            };

            Mock<IModelRepository> mockRepo = new Mock<IModelRepository>(MockBehavior.Strict);
            _ = mockRepo.Setup(r => r.GetByHashAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(existing);
            _ = mockRepo.Setup(r => r.AddAsync(It.IsAny<Model3D>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
            _ = mockRepo.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

            Mock<IFileManagementService> mockFileManagement = new Mock<IFileManagementService>();
            _ = mockFileManagement.Setup(s => s.IsSafePath(It.IsAny<string>(), It.IsAny<string>())).Returns(true);
            _ = mockFileManagement.Setup(s => s.ToHex(It.IsAny<byte[]>()))
                .Returns<byte[]>(b => Convert.ToHexString(b).ToLowerInvariant());

            Mock<AppDbContext> mockDb = new Mock<AppDbContext>(MockBehavior.Loose);
            ModelService service = new ModelService(mockRepo.Object, mockLogger.Object, config, TestFileSystemFactory.WithFiles(new Dictionary<string, byte[]>()), mockFileManagement.Object, mockDb.Object);

            IFormFile file = CreateFormFile("file", content, "model.stl");

            Model3DUploadResultDto result = await service.UploadModelAsync(file, CancellationToken.None);

            // When base names differ, composite hash should be computed and a new model added
            mockRepo.Verify(r => r.AddAsync(It.IsAny<Model3D>(), It.IsAny<CancellationToken>()), Times.Once);
            Assert.NotNull(result);
            Assert.Equal("model.stl", result.FileName);
        }

        [Fact]
        public void ValidateModel_InvalidFileType_ReturnsIssue()
        {
            IConfigurationRoot config = new ConfigurationBuilder().AddInMemoryCollection().Build();
            Mock<IUnifiedLoggingService> mockLogger = new Mock<IUnifiedLoggingService>();
            Mock<IModelRepository> mockRepo = new Mock<IModelRepository>();
            Mock<IFileManagementService> mockFileManagement = new Mock<IFileManagementService>();
            _ = mockFileManagement.Setup(s => s.IsSafePath(It.IsAny<string>(), It.IsAny<string>())).Returns(true);
            // Setup ValidateModelExtension to throw for invalid extensions
            _ = mockFileManagement.Setup(s => s.ValidateModelExtension(It.IsAny<string>()))
                .Callback<string>(ext =>
                {
                    string[] allowedExtensions = new[] { ".stl", ".3mf", ".obj", ".ply", ".step" };
                    string extension = ext.StartsWith('.') ? ext : "." + ext;
                    if (!allowedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
                    {
                        throw new ArgumentException($"Invalid file type '{extension}'");
                    }
                });

            Mock<AppDbContext> mockDb = new Mock<AppDbContext>(MockBehavior.Loose);
            ModelService service = new ModelService(mockRepo.Object, mockLogger.Object, config, TestFileSystemFactory.WithFiles(new Dictionary<string, byte[]>()), mockFileManagement.Object, mockDb.Object);

            IFormFile badFile = CreateFormFile("file", "x", "model.exe");

            Model3DValidationResultDto result = service.ValidateModel(badFile);

            Assert.False(result.Valid);
            Assert.NotNull(result.Issues);
        }

        [Fact]
        public void ValidateModel_EmptyFile_ThrowsArgument()
        {
            IConfigurationRoot config = new ConfigurationBuilder().AddInMemoryCollection().Build();
            Mock<IUnifiedLoggingService> mockLogger = new Mock<IUnifiedLoggingService>();
            Mock<IModelRepository> mockRepo = new Mock<IModelRepository>();
            Mock<IFileManagementService> mockFileManagement = new Mock<IFileManagementService>();
            _ = mockFileManagement.Setup(s => s.IsSafePath(It.IsAny<string>(), It.IsAny<string>())).Returns(true);

            Mock<AppDbContext> mockDb = new Mock<AppDbContext>(MockBehavior.Loose);
            ModelService service = new ModelService(mockRepo.Object, mockLogger.Object, config, TestFileSystemFactory.WithFiles(new Dictionary<string, byte[]>()), mockFileManagement.Object, mockDb.Object);

            IFormFile empty = new FormFile(new MemoryStream(), 0, 0, "file", "empty.stl");

            _ = Assert.Throws<ArgumentException>(() => service.ValidateModel(empty));
        }

        [Fact]
        public async Task UploadModelAsync_AnalysisServiceThrows_Succeeds()
        {
            IConfigurationRoot config = new ConfigurationBuilder().AddInMemoryCollection().Build();
            Mock<IUnifiedLoggingService> mockLogger = new Mock<IUnifiedLoggingService>();

            Mock<IModelRepository> mockRepo = new Mock<IModelRepository>(MockBehavior.Strict);
            _ = mockRepo.Setup(r => r.GetByHashAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((Model3D?)null);
            _ = mockRepo.Setup(r => r.AddAsync(It.IsAny<Model3D>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
            _ = mockRepo.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

            Mock<IModelAnalysisService> mockAnalysis = new Mock<IModelAnalysisService>(MockBehavior.Strict);
            _ = mockAnalysis.Setup(a => a.AnalyzeModelAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("analysis failed"));

            Mock<IFileManagementService> mockFileManagement = new Mock<IFileManagementService>();
            _ = mockFileManagement.Setup(s => s.IsSafePath(It.IsAny<string>(), It.IsAny<string>())).Returns(true);
            _ = mockFileManagement.Setup(s => s.ToHex(It.IsAny<byte[]>()))
                .Returns<byte[]>(b => Convert.ToHexString(b).ToLowerInvariant());

            Mock<Farm.Infrastructure.Data.AppDbContext> mockDb = new Mock<Farm.Infrastructure.Data.AppDbContext>(MockBehavior.Loose);

            ModelService service = new ModelService(mockRepo.Object, mockLogger.Object, config, TestFileSystemFactory.WithFiles(new Dictionary<string, byte[]>()), mockFileManagement.Object, mockDb.Object, mockAnalysis.Object);

            IFormFile file = CreateFormFile("file", "content", "model.stl");

            Model3DUploadResultDto result = await service.UploadModelAsync(file, CancellationToken.None);

            Assert.NotNull(result);
            mockRepo.Verify(r => r.AddAsync(It.IsAny<Model3D>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task UploadModelAsync_RepositorySaveFails_Propagates()
        {
            IConfigurationRoot config = new ConfigurationBuilder().AddInMemoryCollection().Build();
            Mock<IUnifiedLoggingService> mockLogger = new Mock<IUnifiedLoggingService>();

            Mock<IModelRepository> mockRepo = new Mock<IModelRepository>(MockBehavior.Strict);
            _ = mockRepo.Setup(r => r.GetByHashAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((Model3D?)null);
            _ = mockRepo.Setup(r => r.AddAsync(It.IsAny<Model3D>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
            _ = mockRepo.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("db failure"));

            Mock<IFileManagementService> mockFileManagement = new Mock<IFileManagementService>();
            _ = mockFileManagement.Setup(s => s.IsSafePath(It.IsAny<string>(), It.IsAny<string>())).Returns(true);
            _ = mockFileManagement.Setup(s => s.ToHex(It.IsAny<byte[]>()))
                .Returns<byte[]>(b => Convert.ToHexString(b).ToLowerInvariant());

            Mock<Farm.Infrastructure.Data.AppDbContext> mockDb = new Mock<Farm.Infrastructure.Data.AppDbContext>(MockBehavior.Loose);

            ModelService service = new ModelService(mockRepo.Object, mockLogger.Object, config, TestFileSystemFactory.WithFiles(new Dictionary<string, byte[]>()), mockFileManagement.Object, mockDb.Object);

            IFormFile file = CreateFormFile("file", "content", "model.stl");

            _ = await Assert.ThrowsAsync<InvalidOperationException>(() => service.UploadModelAsync(file, CancellationToken.None));
        }
    }
}
