using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Farm.Web.Api.Repositories.Model;
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
            var ms = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));
            return new FormFile(ms, 0, ms.Length, name, fileName);
        }

        [Fact]
        public async Task UploadModelAsync_CompositeHash_Path_CreatesNewHash()
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection().Build();
            var mockLogger = new Mock<Farm.Infrastructure.Telemetry.IUnifiedLoggingService>();

            // Arrange: existing model with same file hash but different base name and same extension
            string content = "abc123";
            byte[] hashBytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content));
            string contentHash = Convert.ToHexString(hashBytes).ToLowerInvariant();

            var existing = new Farm.Infrastructure.Domain.Model3D
            {
                Id = Guid.NewGuid(),
                OriginalFileName = "othername.stl",
                FileHash = contentHash,
                FileFormat = Farm.Infrastructure.Domain.ModelFileFormat.STL,
                IsValid = true,
                UploadedAt = DateTime.UtcNow
            };

            var mockRepo = new Mock<IModelRepository>(MockBehavior.Strict);
            mockRepo.Setup(r => r.GetByHashAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(existing);
            mockRepo.Setup(r => r.AddAsync(It.IsAny<Farm.Infrastructure.Domain.Model3D>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
            mockRepo.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

            var mockFileManagement = new Mock<IFileManagementService>();
            mockFileManagement.Setup(s => s.IsSafePath(It.IsAny<string>(), It.IsAny<string>())).Returns(true);
            mockFileManagement.Setup(s => s.ToHex(It.IsAny<byte[]>()))
                .Returns<byte[]>(b => Convert.ToHexString(b).ToLowerInvariant());

            var service = new ModelService(mockRepo.Object, mockLogger.Object, config, TestFileSystemFactory.WithFiles(new System.Collections.Generic.Dictionary<string, byte[]>()), mockFileManagement.Object);

            IFormFile file = CreateFormFile("file", content, "model.stl");

            var result = await service.UploadModelAsync(file, CancellationToken.None);

            // When base names differ, composite hash should be computed and a new model added
            mockRepo.Verify(r => r.AddAsync(It.IsAny<Farm.Infrastructure.Domain.Model3D>(), It.IsAny<CancellationToken>()), Times.Once);
            Assert.NotNull(result);
            Assert.Equal("model.stl", result.FileName);
        }

        [Fact]
        public void ValidateModel_InvalidFileType_ReturnsIssue()
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection().Build();
            var mockLogger = new Mock<Farm.Infrastructure.Telemetry.IUnifiedLoggingService>();
            var mockRepo = new Mock<IModelRepository>();
            var mockFileManagement = new Mock<IFileManagementService>();
            mockFileManagement.Setup(s => s.IsSafePath(It.IsAny<string>(), It.IsAny<string>())).Returns(true);
            // Setup ValidateModelExtension to throw for invalid extensions
            mockFileManagement.Setup(s => s.ValidateModelExtension(It.IsAny<string>()))
                .Callback<string>(ext =>
                {
                    var allowedExtensions = new[] { ".stl", ".3mf", ".obj", ".ply", ".step" };
                    string extension = ext.StartsWith('.') ? ext : "." + ext;
                    if (!allowedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
                    {
                        throw new ArgumentException($"Invalid file type '{extension}'");
                    }
                });

            var service = new ModelService(mockRepo.Object, mockLogger.Object, config, TestFileSystemFactory.WithFiles(new System.Collections.Generic.Dictionary<string, byte[]>()), mockFileManagement.Object);

            IFormFile badFile = CreateFormFile("file", "x", "model.exe");

            var result = service.ValidateModel(badFile);

            Assert.False(result.Valid);
            Assert.NotNull(result.Issues);
        }

        [Fact]
        public void ValidateModel_EmptyFile_ThrowsArgument()
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection().Build();
            var mockLogger = new Mock<Farm.Infrastructure.Telemetry.IUnifiedLoggingService>();
            var mockRepo = new Mock<IModelRepository>();
            var mockFileManagement = new Mock<IFileManagementService>();
            mockFileManagement.Setup(s => s.IsSafePath(It.IsAny<string>(), It.IsAny<string>())).Returns(true);

            var service = new ModelService(mockRepo.Object, mockLogger.Object, config, TestFileSystemFactory.WithFiles(new System.Collections.Generic.Dictionary<string, byte[]>()), mockFileManagement.Object);

            IFormFile empty = new FormFile(new MemoryStream(), 0, 0, "file", "empty.stl");

            Assert.Throws<ArgumentException>(() => service.ValidateModel(empty));
        }

        [Fact]
        public async Task UploadModelAsync_AnalysisServiceThrows_Succeeds()
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection().Build();
            var mockLogger = new Mock<Farm.Infrastructure.Telemetry.IUnifiedLoggingService>();

            var mockRepo = new Mock<IModelRepository>(MockBehavior.Strict);
            mockRepo.Setup(r => r.GetByHashAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((Farm.Infrastructure.Domain.Model3D?)null);
            mockRepo.Setup(r => r.AddAsync(It.IsAny<Farm.Infrastructure.Domain.Model3D>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
            mockRepo.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

            var mockAnalysis = new Mock<Farm.Web.Api.Services.Interfaces.IModelAnalysisService>(MockBehavior.Strict);
            mockAnalysis.Setup(a => a.AnalyzeModelAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("analysis failed"));

            var mockFileManagement = new Mock<IFileManagementService>();
            mockFileManagement.Setup(s => s.IsSafePath(It.IsAny<string>(), It.IsAny<string>())).Returns(true);
            mockFileManagement.Setup(s => s.ToHex(It.IsAny<byte[]>()))
                .Returns<byte[]>(b => Convert.ToHexString(b).ToLowerInvariant());

            var service = new ModelService(mockRepo.Object, mockLogger.Object, config, TestFileSystemFactory.WithFiles(new System.Collections.Generic.Dictionary<string, byte[]>()), mockFileManagement.Object, mockAnalysis.Object);

            IFormFile file = CreateFormFile("file", "content", "model.stl");

            var result = await service.UploadModelAsync(file, CancellationToken.None);

            Assert.NotNull(result);
            mockRepo.Verify(r => r.AddAsync(It.IsAny<Farm.Infrastructure.Domain.Model3D>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task UploadModelAsync_RepositorySaveFails_Propagates()
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection().Build();
            var mockLogger = new Mock<Farm.Infrastructure.Telemetry.IUnifiedLoggingService>();

            var mockRepo = new Mock<IModelRepository>(MockBehavior.Strict);
            mockRepo.Setup(r => r.GetByHashAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((Farm.Infrastructure.Domain.Model3D?)null);
            mockRepo.Setup(r => r.AddAsync(It.IsAny<Farm.Infrastructure.Domain.Model3D>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
            mockRepo.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("db failure"));

            var mockFileManagement = new Mock<IFileManagementService>();
            mockFileManagement.Setup(s => s.IsSafePath(It.IsAny<string>(), It.IsAny<string>())).Returns(true);
            mockFileManagement.Setup(s => s.ToHex(It.IsAny<byte[]>()))
                .Returns<byte[]>(b => Convert.ToHexString(b).ToLowerInvariant());

            var service = new ModelService(mockRepo.Object, mockLogger.Object, config, TestFileSystemFactory.WithFiles(new System.Collections.Generic.Dictionary<string, byte[]>()), mockFileManagement.Object);

            IFormFile file = CreateFormFile("file", "content", "model.stl");

            await Assert.ThrowsAsync<InvalidOperationException>(() => service.UploadModelAsync(file, CancellationToken.None));
        }
    }
}
