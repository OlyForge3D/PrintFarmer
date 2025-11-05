using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Web.Api.Services.FileManagement;
using Farm.Web.Api.Services.Model;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;
using Shared = Farm.Web.Shared;

namespace Farm.Web.Api.Tests.Services
{
    public class ModelServiceTests
    {
        private static IFormFile CreateFormFile(string name, string content, string fileName)
        {
            var ms = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));
            return new FormFile(ms, 0, ms.Length, name, fileName);
        }

        [Fact]
        public async Task UploadModelAsync_HappyPath_CreatesEntity()
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection().Build();
            var mockLogger = new Mock<Farm.Infrastructure.Telemetry.IUnifiedLoggingService>();

            var mockRepo = new Mock<Farm.Web.Api.Repositories.Model.IModelRepository>(MockBehavior.Strict);
            // For happy path: repository returns no existing model for the hash and will accept AddAsync
            mockRepo.Setup(r => r.GetByHashAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Farm.Infrastructure.Domain.Model3D?)null);
            mockRepo.Setup(r => r.AddAsync(It.IsAny<Farm.Infrastructure.Domain.Model3D>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            mockRepo.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

            var mockFileManagement = new Mock<IFileManagementService>();
            mockFileManagement.Setup(s => s.IsSafePath(It.IsAny<string>(), It.IsAny<string>())).Returns(true);

            var service = new ModelService(mockRepo.Object, mockLogger.Object, config, TestFileSystemFactory.WithFiles(new System.Collections.Generic.Dictionary<string, byte[]>()), mockFileManagement.Object);

            IFormFile file = CreateFormFile("file", "dummy-content", "model.stl");

            var result = await service.UploadModelAsync(file, CancellationToken.None);

            Assert.NotNull(result);
            Assert.Equal("model.stl", result.FileName);
            Assert.Equal("stl", result.FileType);
            mockRepo.Verify(r => r.GetByHashAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task UploadModelAsync_Duplicate_TreatedAsDuplicate()
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection().Build();
            var mockLogger = new Mock<Farm.Infrastructure.Telemetry.IUnifiedLoggingService>();

            // Prepare an existing model with a computed hash matching the test content
            string content = "dummy-content";
            byte[] hashBytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content));
            string contentHash = Convert.ToHexString(hashBytes).ToLowerInvariant();

            var existing = new Farm.Infrastructure.Domain.Model3D
            {
                Id = Guid.NewGuid(),
                OriginalFileName = "model.stl",
                DisplayName = "model",
                FilePath = "path",
                FileSizeBytes = 12,
                FileHash = contentHash,
                FileFormat = Farm.Infrastructure.Domain.ModelFileFormat.STL,
                UploadedAt = DateTime.UtcNow,
                IsValid = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            var mockRepo = new Mock<Farm.Web.Api.Repositories.Model.IModelRepository>(MockBehavior.Strict);
            // For duplicate scenario: GetByHashAsync returns existing model for any hash
            mockRepo.Setup(r => r.GetByHashAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(existing);

            var mockFileManagement = new Mock<IFileManagementService>();
            mockFileManagement.Setup(s => s.IsSafePath(It.IsAny<string>(), It.IsAny<string>())).Returns(true);
            // Setup ToHex to match the expected hash
            mockFileManagement.Setup(s => s.ToHex(It.IsAny<byte[]>()))
                .Returns<byte[]>(b => Convert.ToHexString(b).ToLowerInvariant());
            // Setup ToHex to match the expected hash
            mockFileManagement.Setup(s => s.ToHex(It.IsAny<byte[]>()))
                .Returns<byte[]>(b => Convert.ToHexString(b).ToLowerInvariant());

            var service = new ModelService(mockRepo.Object, mockLogger.Object, config, TestFileSystemFactory.WithFiles(new System.Collections.Generic.Dictionary<string, byte[]>()), mockFileManagement.Object);
            IFormFile file = CreateFormFile("file", content, "model.stl");

            var result = await service.UploadModelAsync(file, CancellationToken.None);

            // Should return existing model info (duplicate)
            Assert.Equal(existing.Id, result.Id);
            mockRepo.Verify(r => r.GetByHashAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        }
    }
}
