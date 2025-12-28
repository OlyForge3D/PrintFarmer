using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Services.Gcode;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Services;
using Farm.Web.Api.Services.FileManagement;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services.FileManagement
{
    public class ChunkedUploadServiceTests : IDisposable
    {
        private readonly string _testDirectory;
        private readonly Mock<IFileManagementService> _mockFileManagement;
        private readonly Mock<IGcodeThumbnailExtractorService> _mockThumbnailExtractor;
        private readonly Mock<IGcodeMetadataExtractorService> _mockMetadataExtractor;
        private readonly Mock<IUnifiedLoggingService> _mockLogger;
        private readonly ChunkedUploadService _service;

        public ChunkedUploadServiceTests()
        {
            // Create temporary test directory
            _testDirectory = Path.Combine(Path.GetTempPath(), "ChunkedUploadTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_testDirectory);

            // Setup mocks
            _mockFileManagement = new Mock<IFileManagementService>();
            _mockThumbnailExtractor = new Mock<IGcodeThumbnailExtractorService>();
            _mockMetadataExtractor = new Mock<IGcodeMetadataExtractorService>();
            _mockLogger = new Mock<IUnifiedLoggingService>();

            // Default mock behaviors
            _mockFileManagement.Setup(f => f.SanitizeFileName(It.IsAny<string>(), It.IsAny<string>()))
                .Returns<string, string>((name, ext) => Path.GetFileNameWithoutExtension(name) + ext);
            _mockFileManagement.Setup(f => f.ResolveUniqueFileName(It.IsAny<string>(), It.IsAny<string>()))
                .Returns<string, string>((dir, name) => name);
            _mockFileManagement.Setup(f => f.ToHex(It.IsAny<byte[]>()))
                .Returns<byte[]>(bytes => Convert.ToHexString(bytes));

            _service = new ChunkedUploadService(
                _mockFileManagement.Object,
                _mockThumbnailExtractor.Object,
                _mockMetadataExtractor.Object,
                _mockLogger.Object);
        }

        public void Dispose()
        {
            // Cleanup test directory
            if (Directory.Exists(_testDirectory))
            {
                try
                {
                    Directory.Delete(_testDirectory, recursive: true);
                }
                catch
                {
                    // Best effort cleanup
                }
            }
        }

        #region InitializeUpload Tests

        [Fact]
        public void InitializeUpload_WithValidParameters_ReturnsInitResult()
        {
            // Arrange
            const string userId = "user123";
            const string fileName = "test.gcode";
            const long fileSize = 1024 * 1024; // 1 MB
            var allowedExtensions = new List<string> { ".gcode", ".stl" };

            // Act
            ChunkedUploadInitResult result = _service.InitializeUpload(
                userId, fileName, fileSize, _testDirectory, allowedExtensions);

            // Assert
            Assert.NotNull(result);
            Assert.NotEmpty(result.UploadId);
            Assert.Equal("test.gcode", result.SafeFileName);
            Assert.True(result.RecommendedChunkSize > 0);
        }

        [Fact]
        public void InitializeUpload_WithInvalidExtension_ThrowsException()
        {
            // Arrange
            const string userId = "user123";
            const string fileName = "test.exe";
            const long fileSize = 1024;
            var allowedExtensions = new List<string> { ".gcode", ".stl" };

            // Act & Assert
            var exception = Assert.Throws<InvalidOperationException>(() =>
                _service.InitializeUpload(userId, fileName, fileSize, _testDirectory, allowedExtensions));
            Assert.Contains("Invalid file type", exception.Message);
        }

        [Fact]
        public void InitializeUpload_WithEmptyUserId_ThrowsArgumentException()
        {
            // Arrange
            const string userId = "";
            const string fileName = "test.gcode";
            const long fileSize = 1024;
            var allowedExtensions = new List<string> { ".gcode" };

            // Act & Assert
            var exception = Assert.Throws<ArgumentException>(() =>
                _service.InitializeUpload(userId, fileName, fileSize, _testDirectory, allowedExtensions));
            Assert.Contains("userId", exception.Message);
        }

        [Fact]
        public void InitializeUpload_WithNegativeFileSize_ThrowsArgumentException()
        {
            // Arrange
            const string userId = "user123";
            const string fileName = "test.gcode";
            const long fileSize = -1;
            var allowedExtensions = new List<string> { ".gcode" };

            // Act & Assert
            var exception = Assert.Throws<ArgumentException>(() =>
                _service.InitializeUpload(userId, fileName, fileSize, _testDirectory, allowedExtensions));
            Assert.Contains("fileSize", exception.Message);
        }

        [Fact]
        public void InitializeUpload_WithNullAllowedExtensions_ThrowsArgumentException()
        {
            // Arrange
            const string userId = "user123";
            const string fileName = "test.gcode";
            const long fileSize = 1024;

            // Act & Assert
            var exception = Assert.Throws<ArgumentException>(() =>
                _service.InitializeUpload(userId, fileName, fileSize, _testDirectory, null!));
            Assert.Contains("allowedExtensions", exception.Message);
        }

        [Fact]
        public void InitializeUpload_WithHashAlgorithm_CreatesUploadWithHash()
        {
            // Arrange
            const string userId = "user123";
            const string fileName = "test.gcode";
            const long fileSize = 1024;
            var allowedExtensions = new List<string> { ".gcode" };
            const string hashAlgorithm = "sha256";
            const string expectedHash = "abc123";

            // Act
            ChunkedUploadInitResult result = _service.InitializeUpload(
                userId, fileName, fileSize, _testDirectory, allowedExtensions, hashAlgorithm, expectedHash);

            // Assert
            Assert.NotNull(result);
            Assert.NotEmpty(result.UploadId);
        }

        [Fact]
        public void InitializeUpload_WithUnsupportedHashAlgorithm_ThrowsArgumentException()
        {
            // Arrange
            const string userId = "user123";
            const string fileName = "test.gcode";
            const long fileSize = 1024;
            var allowedExtensions = new List<string> { ".gcode" };
            const string hashAlgorithm = "md5"; // Unsupported

            // Act & Assert
            var exception = Assert.Throws<ArgumentException>(() =>
                _service.InitializeUpload(userId, fileName, fileSize, _testDirectory, allowedExtensions, hashAlgorithm));
            Assert.Contains("Unsupported hashAlgorithm", exception.Message);
        }

        #endregion

        #region AppendChunk Tests

        [Fact]
        public async Task AppendChunkAsync_WithValidChunk_UpdatesUploadedBytes()
        {
            // Arrange
            const string userId = "user123";
            const long fileSize = 2048;
            var allowedExtensions = new List<string> { ".gcode" };
            var quotaService = CreateMockQuotaService(allow: true);

            ChunkedUploadInitResult initResult = _service.InitializeUpload(
                userId, "test.gcode", fileSize, _testDirectory, allowedExtensions);

            byte[] chunk = new byte[1024];
            Array.Fill(chunk, (byte)0x42);

            // Act
            ChunkedUploadStatus status = await _service.AppendChunkAsync(
                initResult.UploadId, 0, chunk, userId, quotaService.Object);

            // Assert
            Assert.Equal(1024, status.UploadedBytes);
            Assert.Equal(fileSize, status.TotalSize);
            Assert.False(status.IsCompleted);
        }

        [Fact]
        public async Task AppendChunkAsync_WithFinalChunk_CompletesUpload()
        {
            // Arrange
            const string userId = "user123";
            const long fileSize = 1024;
            var allowedExtensions = new List<string> { ".gcode" };
            var quotaService = CreateMockQuotaService(allow: true);

            ChunkedUploadInitResult initResult = _service.InitializeUpload(
                userId, "test.gcode", fileSize, _testDirectory, allowedExtensions);

            byte[] chunk = new byte[1024];

            // Act
            ChunkedUploadStatus status = await _service.AppendChunkAsync(
                initResult.UploadId, 0, chunk, userId, quotaService.Object);

            // Assert
            Assert.True(status.IsCompleted);
            Assert.Equal(fileSize, status.UploadedBytes);
        }

        [Fact]
        public async Task AppendChunkAsync_WithInvalidUploadId_ThrowsInvalidOperationException()
        {
            // Arrange
            const string invalidUploadId = "nonexistent";
            byte[] chunk = new byte[1024];
            var quotaService = CreateMockQuotaService(allow: true);

            // Act & Assert
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await _service.AppendChunkAsync(invalidUploadId, 0, chunk, "user123", quotaService.Object));
            Assert.Contains("not found", exception.Message);
        }

        [Fact]
        public async Task AppendChunkAsync_WithWrongOffset_ThrowsInvalidOperationException()
        {
            // Arrange
            const string userId = "user123";
            var allowedExtensions = new List<string> { ".gcode" };
            var quotaService = CreateMockQuotaService(allow: true);

            ChunkedUploadInitResult initResult = _service.InitializeUpload(
                userId, "test.gcode", 2048, _testDirectory, allowedExtensions);

            byte[] chunk = new byte[1024];

            // Act & Assert - wrong offset (should be 0, passing 1024)
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await _service.AppendChunkAsync(initResult.UploadId, 1024, chunk, userId, quotaService.Object));
            Assert.Contains("Offset mismatch", exception.Message);
        }

        [Fact]
        public async Task AppendChunkAsync_ExceedingFileSize_ThrowsInvalidOperationException()
        {
            // Arrange
            const string userId = "user123";
            const long fileSize = 1024;
            var allowedExtensions = new List<string> { ".gcode" };
            var quotaService = CreateMockQuotaService(allow: true);

            ChunkedUploadInitResult initResult = _service.InitializeUpload(
                userId, "test.gcode", fileSize, _testDirectory, allowedExtensions);

            byte[] chunk = new byte[2048]; // Larger than fileSize

            // Act & Assert
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await _service.AppendChunkAsync(initResult.UploadId, 0, chunk, userId, quotaService.Object));
            Assert.Contains("exceeds remaining file size", exception.Message);
        }

        [Fact]
        public async Task AppendChunkAsync_QuotaExceeded_ThrowsInvalidOperationException()
        {
            // Arrange
            const string userId = "user123";
            var allowedExtensions = new List<string> { ".gcode" };
            var quotaService = CreateMockQuotaService(allow: false); // Quota exceeded

            ChunkedUploadInitResult initResult = _service.InitializeUpload(
                userId, "test.gcode", 2048, _testDirectory, allowedExtensions);

            byte[] chunk = new byte[1024];

            // Act & Assert
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await _service.AppendChunkAsync(initResult.UploadId, 0, chunk, userId, quotaService.Object));
            Assert.Contains("quota exceeded", exception.Message);
        }

        [Fact]
        public async Task AppendChunkAsync_ToPausedUpload_ThrowsInvalidOperationException()
        {
            // Arrange
            const string userId = "user123";
            var allowedExtensions = new List<string> { ".gcode" };
            var quotaService = CreateMockQuotaService(allow: true);

            ChunkedUploadInitResult initResult = _service.InitializeUpload(
                userId, "test.gcode", 2048, _testDirectory, allowedExtensions);

            // Pause the upload
            _service.PauseUpload(initResult.UploadId);

            byte[] chunk = new byte[1024];

            // Act & Assert
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await _service.AppendChunkAsync(initResult.UploadId, 0, chunk, userId, quotaService.Object));
            Assert.Contains("paused", exception.Message);
        }

        [Fact]
        public async Task AppendChunkAsync_WithHashMatch_ComputesFinalHashAndCleansTempFiles()
        {
            // Arrange
            const string userId = "user123";
            const long fileSize = 512;
            var allowedExtensions = new List<string> { ".gcode" };
            var quotaService = CreateMockQuotaService(allow: true);

            byte[] chunk = new byte[512];
            Array.Fill(chunk, (byte)0xAB);

            string expectedHash = Convert.ToHexString(SHA256.HashData(chunk));

            ChunkedUploadInitResult initResult = _service.InitializeUpload(
                userId,
                "test.gcode",
                fileSize,
                _testDirectory,
                allowedExtensions,
                hashAlgorithm: "sha256",
                expectedHash: expectedHash);

            string tempPath = Path.Combine(_testDirectory, $"test.gcode.{initResult.UploadId}.part");
            string metaPath = tempPath + ".meta.json";

            // Act
            ChunkedUploadStatus status = await _service.AppendChunkAsync(
                initResult.UploadId,
                0,
                chunk,
                userId,
                quotaService.Object);

            // Assert
            Assert.True(status.IsCompleted);
            Assert.Equal(expectedHash, status.FinalHash);
            Assert.False(File.Exists(tempPath));
            Assert.False(File.Exists(metaPath));
            Assert.True(File.Exists(Path.Combine(_testDirectory, "test.gcode")));
        }

        [Fact]
        public async Task AppendChunkAsync_WithHashMismatch_DeletesTempFilesAndThrows()
        {
            // Arrange
            const string userId = "user123";
            const long fileSize = 256;
            var allowedExtensions = new List<string> { ".gcode" };
            var quotaService = CreateMockQuotaService(allow: true);

            byte[] chunk = new byte[fileSize];
            Array.Fill(chunk, (byte)0x01);

            ChunkedUploadInitResult initResult = _service.InitializeUpload(
                userId,
                "hashcheck.gcode",
                fileSize,
                _testDirectory,
                allowedExtensions,
                hashAlgorithm: "sha256",
                expectedHash: "deadbeef");

            string tempPath = Path.Combine(_testDirectory, $"hashcheck.gcode.{initResult.UploadId}.part");
            string metaPath = tempPath + ".meta.json";
            string finalPath = Path.Combine(_testDirectory, "hashcheck.gcode");

            // Act
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await _service.AppendChunkAsync(initResult.UploadId, 0, chunk, userId, quotaService.Object));

            // Assert
            Assert.Contains("Hash mismatch", ex.Message);
            Assert.False(File.Exists(tempPath));
            Assert.False(File.Exists(metaPath));
            Assert.False(File.Exists(finalPath));
        }

        [Fact]
        public async Task AppendChunkAsync_ForNonGcodeFile_SkipsThumbnailExtraction()
        {
            // Arrange
            const string userId = "user123";
            var allowedExtensions = new List<string> { ".txt" };
            var quotaService = CreateMockQuotaService(allow: true);

            ChunkedUploadInitResult initResult = _service.InitializeUpload(
                userId,
                "notes.txt",
                64,
                _testDirectory,
                allowedExtensions);

            byte[] chunk = new byte[64];

            // Act
            ChunkedUploadStatus status = await _service.AppendChunkAsync(
                initResult.UploadId,
                0,
                chunk,
                userId,
                quotaService.Object);

            // Assert
            Assert.True(status.IsCompleted);
            Assert.Null(status.ThumbnailPath);
            _mockThumbnailExtractor.Verify(x => x.ExtractAndSaveThumbnailAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        #endregion

        #region Pause/Resume Tests

        [Fact]
        public void PauseUpload_WithValidUploadId_PausesUpload()
        {
            // Arrange
            const string userId = "user123";
            var allowedExtensions = new List<string> { ".gcode" };

            ChunkedUploadInitResult initResult = _service.InitializeUpload(
                userId, "test.gcode", 2048, _testDirectory, allowedExtensions);

            // Act
            ChunkedUploadStatus? status = _service.PauseUpload(initResult.UploadId);

            // Assert
            Assert.NotNull(status);
            Assert.True(status.IsPaused);
        }

        [Fact]
        public void PauseUpload_WithInvalidUploadId_ReturnsNull()
        {
            // Act
            ChunkedUploadStatus? status = _service.PauseUpload("nonexistent");

            // Assert
            Assert.Null(status);
        }

        [Fact]
        public void ResumeUpload_AfterPause_ResumesUpload()
        {
            // Arrange
            const string userId = "user123";
            var allowedExtensions = new List<string> { ".gcode" };

            ChunkedUploadInitResult initResult = _service.InitializeUpload(
                userId, "test.gcode", 2048, _testDirectory, allowedExtensions);

            _service.PauseUpload(initResult.UploadId);

            // Act
            ChunkedUploadStatus? status = _service.ResumeUpload(initResult.UploadId);

            // Assert
            Assert.NotNull(status);
            Assert.False(status.IsPaused);
        }

        [Fact]
        public void ResumeUpload_WithInvalidUploadId_ReturnsNull()
        {
            // Act
            ChunkedUploadStatus? status = _service.ResumeUpload("nonexistent");

            // Assert
            Assert.Null(status);
        }

        #endregion

        #region GetOrResumeUpload Tests

        [Fact]
        public void GetOrResumeUpload_WithValidUploadId_ReturnsStatus()
        {
            // Arrange
            const string userId = "user123";
            var allowedExtensions = new List<string> { ".gcode" };

            ChunkedUploadInitResult initResult = _service.InitializeUpload(
                userId, "test.gcode", 2048, _testDirectory, allowedExtensions);

            // Act
            ChunkedUploadStatus? status = _service.GetOrResumeUpload(initResult.UploadId);

            // Assert
            Assert.NotNull(status);
            Assert.Equal(0, status.UploadedBytes);
            Assert.Equal(2048, status.TotalSize);
        }

        [Fact]
        public void GetOrResumeUpload_WithInvalidUploadId_ReturnsNull()
        {
            // Act
            ChunkedUploadStatus? status = _service.GetOrResumeUpload("nonexistent");

            // Assert
            Assert.Null(status);
        }

        #endregion

        #region CancelUpload Tests

        [Fact]
        public void CancelUpload_WithValidUploadId_RemovesUpload()
        {
            // Arrange
            const string userId = "user123";
            var allowedExtensions = new List<string> { ".gcode" };

            ChunkedUploadInitResult initResult = _service.InitializeUpload(
                userId, "test.gcode", 2048, _testDirectory, allowedExtensions);

            // Act
            _service.CancelUpload(initResult.UploadId);

            // Assert - upload should no longer exist
            ChunkedUploadStatus? status = _service.GetOrResumeUpload(initResult.UploadId);
            Assert.Null(status);
        }

        [Fact]
        public void CancelUpload_WithInvalidUploadId_DoesNotThrow()
        {
            // Act & Assert - should not throw
            _service.CancelUpload("nonexistent");
        }

        #endregion

        #region GetUploadVirtualDirectory Tests

        [Fact]
        public void GetUploadVirtualDirectory_WithValidUploadId_ReturnsDirectory()
        {
            // Arrange
            const string userId = "user123";
            var allowedExtensions = new List<string> { ".gcode" };
            const string virtualDirectory = "/uploads/user123";

            ChunkedUploadInitResult initResult = _service.InitializeUpload(
                userId, "test.gcode", 2048, _testDirectory, allowedExtensions,
                virtualDirectory: virtualDirectory);

            // Act
            string? directory = _service.GetUploadVirtualDirectory(initResult.UploadId);

            // Assert
            Assert.Equal(virtualDirectory, directory);
        }

        [Fact]
        public void GetUploadVirtualDirectory_WithInvalidUploadId_ReturnsNull()
        {
            // Act
            string? directory = _service.GetUploadVirtualDirectory("nonexistent");

            // Assert
            Assert.Null(directory);
        }

        #endregion

        #region ExtractMetadataFromFileAsync Tests

        [Fact]
        public async Task ExtractMetadataFromFileAsync_WhenFileMissing_ReturnsNull()
        {
            // Act
            GcodeMetadataExtracted? result = await _service.ExtractMetadataFromFileAsync(Path.Combine(_testDirectory, "missing.gcode"));

            // Assert
            Assert.Null(result);
            _mockMetadataExtractor.Verify(m => m.ExtractMetadataAsync(It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task ExtractMetadataFromFileAsync_WhenNotGcode_ReturnsNull()
        {
            // Arrange
            string filePath = Path.Combine(_testDirectory, "notes.txt");
            await File.WriteAllTextAsync(filePath, "hello world");

            // Act
            GcodeMetadataExtracted? result = await _service.ExtractMetadataFromFileAsync(filePath);

            // Assert
            Assert.Null(result);
            _mockMetadataExtractor.Verify(m => m.ExtractMetadataAsync(It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task ExtractMetadataFromFileAsync_WithGcodeContent_ReturnsMetadata()
        {
            // Arrange
            string filePath = Path.Combine(_testDirectory, "meta.gcode");
            await File.WriteAllTextAsync(filePath, "; G-code file\nG1 X10");

            var metadata = new GcodeMetadataExtracted { SlicerName = "TestSlicer" };
            _mockMetadataExtractor.Setup(m => m.ExtractMetadataAsync(It.IsAny<string>()))
                .ReturnsAsync(metadata);

            // Act
            GcodeMetadataExtracted? result = await _service.ExtractMetadataFromFileAsync(filePath);

            // Assert
            Assert.NotNull(result);
            Assert.Equal("TestSlicer", result!.SlicerName);
            _mockMetadataExtractor.Verify(m => m.ExtractMetadataAsync(It.Is<string>(c => c.Contains("G-code"))), Times.Once);
        }

        #endregion

        #region Helper Methods

        private static Mock<IGcodeUploadQuotaService> CreateMockQuotaService(bool allow)
        {
            var mock = new Mock<IGcodeUploadQuotaService>();
            mock.Setup(q => q.TryAddUsage(It.IsAny<string>(), It.IsAny<long>(), out It.Ref<long>.IsAny, out It.Ref<long>.IsAny))
                .Returns((string userId, long bytes, out long used, out long limit) =>
                {
                    used = 1000;
                    limit = 10000;
                    return allow;
                });
            return mock;
        }

        #endregion
    }
}
