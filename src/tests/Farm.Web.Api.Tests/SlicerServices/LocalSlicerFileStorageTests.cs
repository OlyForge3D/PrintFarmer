using Farm.Web.Api.Services.SlicerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace Farm.Web.Api.Tests.SlicerServices;

/// <summary>
/// Unit tests for LocalSlicerFileStorage - Local file system storage implementation
/// </summary>
public class LocalSlicerFileStorageTests : IDisposable
{
    private readonly Mock<ILogger<LocalSlicerFileStorage>> _mockLogger;
    private readonly LocalSlicerFileStorage _storage;
    private readonly string _tempBasePath;
    private readonly LocalFileStorageOptions _options;

    public LocalSlicerFileStorageTests()
    {
        _mockLogger = new Mock<ILogger<LocalSlicerFileStorage>>();
        _tempBasePath = Path.Combine(TestInfrastructure.TestPaths.GetUniqueTempDirectory(), "slicer-storage-tests");

        _options = new LocalFileStorageOptions
        {
            BasePath = _tempBasePath
        };

        var optionsWrapper = Options.Create(_options);
        _storage = new LocalSlicerFileStorage(optionsWrapper, _mockLogger.Object);
    }

    public void Dispose()
    {
        // Cleanup test directory
        if (Directory.Exists(_tempBasePath))
        {
            Directory.Delete(_tempBasePath, recursive: true);
        }
    }

    [Fact]
    public async Task UploadFileAsync_WithStream_ShouldUploadFileAndReturnUrl()
    {
        // Arrange
        var key = "test-models/cube.stl";
        var content = CreateTestFileContent();
        using var stream = new MemoryStream(content);

        // Act
        var url = await _storage.UploadFileAsync(key, stream, "application/octet-stream");

        // Assert
        url.Should().NotBeNullOrEmpty();
        url.Should().Contain(key);

        // Verify file exists
        var filePath = Path.Combine(_tempBasePath, key);
        File.Exists(filePath).Should().BeTrue();

        // Verify content
        var uploadedContent = await File.ReadAllBytesAsync(filePath);
        uploadedContent.Should().BeEquivalentTo(content);
    }

    [Fact]
    public async Task UploadFileAsync_WithByteArray_ShouldUploadFileAndReturnUrl()
    {
        // Arrange
        var key = "test-models/sphere.obj";
        var content = CreateTestFileContent();

        // Act
        var url = await _storage.UploadFileAsync(key, content, "application/octet-stream");

        // Assert
        url.Should().NotBeNullOrEmpty();
        url.Should().Contain(key);

        // Verify file exists
        var filePath = Path.Combine(_tempBasePath, key);
        File.Exists(filePath).Should().BeTrue();

        // Verify content
        var uploadedContent = await File.ReadAllBytesAsync(filePath);
        uploadedContent.Should().BeEquivalentTo(content);
    }

    [Fact]
    public async Task UploadFileAsync_WithNestedPath_ShouldCreateDirectories()
    {
        // Arrange
        var key = "users/user123/projects/project456/models/complex.3mf";
        var content = CreateTestFileContent();

        // Act
        var url = await _storage.UploadFileAsync(key, content, "application/octet-stream");

        // Assert
        url.Should().NotBeNullOrEmpty();

        // Verify directory structure was created
        var filePath = Path.Combine(_tempBasePath, key);
        File.Exists(filePath).Should().BeTrue();

        var directoryPath = Path.GetDirectoryName(filePath);
        Directory.Exists(directoryPath).Should().BeTrue();
    }

    [Fact]
    public async Task DownloadFileAsync_ExistingFile_ShouldReturnFileStream()
    {
        // Arrange
        var key = "test-download/file.stl";
        var originalContent = CreateTestFileContent();
        await _storage.UploadFileAsync(key, originalContent, "application/octet-stream");

        // Act
        using var stream = await _storage.DownloadFileAsync(key);

        // Assert
        stream.Should().NotBeNull();

        using var memoryStream = new MemoryStream();
        await stream.CopyToAsync(memoryStream);
        var downloadedContent = memoryStream.ToArray();

        downloadedContent.Should().BeEquivalentTo(originalContent);
    }

    [Fact]
    public async Task DownloadFileAsync_NonExistentFile_ShouldThrowFileNotFoundException()
    {
        // Arrange
        var key = "non-existent/file.stl";

        // Act & Assert
        var exception = await Assert.ThrowsAsync<FileNotFoundException>(() => _storage.DownloadFileAsync(key));
        exception.Message.Should().Contain("File not found");
        exception.Message.Should().Contain(key);
    }

    [Fact]
    public async Task DownloadFileAsync_WithUrl_ShouldExtractKeyAndDownload()
    {
        // Arrange
        var key = "test-url/file.obj";
        var originalContent = CreateTestFileContent();
        var url = await _storage.UploadFileAsync(key, originalContent, "application/octet-stream");

        // Act
        using var stream = await _storage.DownloadFileAsync(url);

        // Assert
        stream.Should().NotBeNull();

        using var memoryStream = new MemoryStream();
        await stream.CopyToAsync(memoryStream);
        var downloadedContent = memoryStream.ToArray();

        downloadedContent.Should().BeEquivalentTo(originalContent);
    }

    [Fact]
    public async Task DownloadFileBytesAsync_ExistingFile_ShouldReturnByteArray()
    {
        // Arrange
        var key = "test-bytes/file.amf";
        var originalContent = CreateTestFileContent();
        await _storage.UploadFileAsync(key, originalContent, "application/octet-stream");

        // Act
        var downloadedContent = await _storage.DownloadFileBytesAsync(key);

        // Assert
        downloadedContent.Should().BeEquivalentTo(originalContent);
    }

    [Fact]
    public async Task DownloadFileBytesAsync_NonExistentFile_ShouldThrowFileNotFoundException()
    {
        // Arrange
        var key = "non-existent/bytes.stl";

        // Act & Assert
        await Assert.ThrowsAsync<FileNotFoundException>(() => _storage.DownloadFileBytesAsync(key));
    }

    [Fact]
    public async Task FileExistsAsync_ExistingFile_ShouldReturnTrue()
    {
        // Arrange
        var key = "test-exists/file.ply";
        var content = CreateTestFileContent();
        await _storage.UploadFileAsync(key, content, "application/octet-stream");

        // Act
        var exists = await _storage.FileExistsAsync(key);

        // Assert
        exists.Should().BeTrue();
    }

    [Fact]
    public async Task FileExistsAsync_NonExistentFile_ShouldReturnFalse()
    {
        // Arrange
        var key = "non-existent/file.stl";

        // Act
        var exists = await _storage.FileExistsAsync(key);

        // Assert
        exists.Should().BeFalse();
    }

    [Fact]
    public async Task FileExistsAsync_WithUrl_ShouldExtractKeyAndCheck()
    {
        // Arrange
        var key = "test-url-exists/file.obj";
        var content = CreateTestFileContent();
        var url = await _storage.UploadFileAsync(key, content, "application/octet-stream");

        // Act
        var exists = await _storage.FileExistsAsync(url);

        // Assert
        exists.Should().BeTrue();
    }

    [Fact]
    public async Task DeleteFileAsync_ExistingFile_ShouldDeleteFile()
    {
        // Arrange
        var key = "test-delete/file.3mf";
        var content = CreateTestFileContent();
        await _storage.UploadFileAsync(key, content, "application/octet-stream");

        // Verify file exists first
        var existsBefore = await _storage.FileExistsAsync(key);
        existsBefore.Should().BeTrue();

        // Act
        await _storage.DeleteFileAsync(key);

        // Assert
        var existsAfter = await _storage.FileExistsAsync(key);
        existsAfter.Should().BeFalse();
    }

    [Fact]
    public async Task DeleteFileAsync_NonExistentFile_ShouldNotThrow()
    {
        // Arrange
        var key = "non-existent/delete-me.stl";

        // Act & Assert - Should not throw
        await _storage.DeleteFileAsync(key);
    }

    [Fact]
    public async Task GetFileMetadataAsync_ExistingFile_ShouldReturnMetadata()
    {
        // Arrange
        var key = "test-metadata/file.stl";
        var content = CreateTestFileContent();
        var beforeUpload = DateTime.UtcNow;

        await _storage.UploadFileAsync(key, content, "application/vnd.ms-3mfdocument");

        var afterUpload = DateTime.UtcNow;

        // Act
        var metadata = await _storage.GetFileMetadataAsync(key);

        // Assert
        metadata.Should().NotBeNull();
        metadata!.Key.Should().Be(key);
        metadata.SizeBytes.Should().Be(content.Length);
        metadata.ContentType.Should().Be("application/vnd.ms-3mfdocument");
        metadata.CreatedAt.Should().BeAfter(beforeUpload.AddSeconds(-1));
        metadata.CreatedAt.Should().BeBefore(afterUpload.AddSeconds(1));
        metadata.LastModified.Should().BeAfter(beforeUpload.AddSeconds(-1));
        metadata.LastModified.Should().BeBefore(afterUpload.AddSeconds(1));
        metadata.ETag.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task GetFileMetadataAsync_NonExistentFile_ShouldReturnNull()
    {
        // Arrange
        var key = "non-existent/metadata.stl";

        // Act
        var metadata = await _storage.GetFileMetadataAsync(key);

        // Assert
        metadata.Should().BeNull();
    }

    [Fact]
    public async Task GenerateSignedUrlAsync_ShouldReturnFileUrl()
    {
        // Arrange
        var key = "test-signed/file.obj";
        var content = CreateTestFileContent();
        await _storage.UploadFileAsync(key, content, "application/octet-stream");
        var expiration = TimeSpan.FromHours(1);

        // Act
        var signedUrl = await _storage.GenerateSignedUrlAsync(key, expiration);

        // Assert
        signedUrl.Should().NotBeNullOrEmpty();
        signedUrl.Should().Contain(key);
    }

    [Fact]
    public async Task CleanupTempFilesAsync_OldFiles_ShouldDeleteOldFiles()
    {
        // Arrange
        var oldKey = "temp/old-file.stl";
        var newKey = "temp/new-file.stl";
        var content = CreateTestFileContent();

        // Upload files
        await _storage.UploadFileAsync(oldKey, content, "application/octet-stream");
        await _storage.UploadFileAsync(newKey, content, "application/octet-stream");

        // Make one file appear old by manually setting its creation time
        var oldFilePath = Path.Combine(_tempBasePath, oldKey);
        var oldTime = DateTime.UtcNow.AddDays(-2);
        File.SetCreationTimeUtc(oldFilePath, oldTime);
        File.SetLastWriteTimeUtc(oldFilePath, oldTime);

        // Act
        _storage.CleanupTempFiles(TimeSpan.FromDays(1));

        // Assert
        var oldExists = await _storage.FileExistsAsync(oldKey);
        var newExists = await _storage.FileExistsAsync(newKey);

        oldExists.Should().BeFalse(); // Old file should be deleted
        newExists.Should().BeTrue();  // New file should remain
    }

    [Fact]
    public async Task CleanupTempFilesAsync_NoOldFiles_ShouldNotDeleteAnything()
    {
        // Arrange
        var key1 = "temp/file1.stl";
        var key2 = "temp/file2.obj";
        var content = CreateTestFileContent();

        await _storage.UploadFileAsync(key1, content, "application/octet-stream");
        await _storage.UploadFileAsync(key2, content, "application/octet-stream");

        // Act
        _storage.CleanupTempFiles(TimeSpan.FromMinutes(1));

        // Assert
        var exists1 = await _storage.FileExistsAsync(key1);
        var exists2 = await _storage.FileExistsAsync(key2);

        exists1.Should().BeTrue();
        exists2.Should().BeTrue();
    }

    [Theory]
    [InlineData("simple-file.stl")]
    [InlineData("path/to/nested/file.obj")]
    [InlineData("user@domain.com/models/test.3mf")]
    [InlineData("files with spaces/model.amf")]
    [InlineData("files_with_underscores/model.ply")]
    [InlineData("files-with-dashes/model.stl")]
    [InlineData("UPPERCASE/MODEL.STL")]
    public async Task UploadAndDownload_VariousKeyFormats_ShouldWork(string key)
    {
        // Arrange
        var content = CreateTestFileContent();

        // Act
        var url = await _storage.UploadFileAsync(key, content, "application/octet-stream");
        var exists = await _storage.FileExistsAsync(key);
        var downloadedContent = await _storage.DownloadFileBytesAsync(key);

        // Assert
        url.Should().NotBeNullOrEmpty();
        exists.Should().BeTrue();
        downloadedContent.Should().BeEquivalentTo(content);
    }

    [Fact]
    public async Task ConcurrentOperations_ShouldHandleMultipleOperations()
    {
        // Arrange
        var tasks = new List<Task>();
        var fileCount = 10;

        // Act - Upload multiple files concurrently
        for (int i = 0; i < fileCount; i++)
        {
            var key = $"concurrent/file-{i}.stl";
            var content = CreateTestFileContent($"Content for file {i}");

            tasks.Add(_storage.UploadFileAsync(key, content, "application/octet-stream"));
        }

        await Task.WhenAll(tasks);

        // Assert - All files should exist
        for (int i = 0; i < fileCount; i++)
        {
            var key = $"concurrent/file-{i}.stl";
            var exists = await _storage.FileExistsAsync(key);
            exists.Should().BeTrue($"File {key} should exist");
        }
    }

    [Fact]
    public void Constructor_InvalidOptions_ShouldThrowArgumentNullException()
    {
        // Arrange & Act & Assert
        Assert.Throws<ArgumentNullException>(() => new LocalSlicerFileStorage(null!, _mockLogger.Object));
    }

    [Fact]
    public void Constructor_ValidOptions_ShouldCreateBaseDirectory()
    {
        // Arrange
        var newTempPath = Path.Combine(TestInfrastructure.TestPaths.GetUniqueTempDirectory(), "test-directory-creation");
        var options = new LocalFileStorageOptions { BasePath = newTempPath };
        var optionsWrapper = Options.Create(options);

        try
        {
            // Act
            var storage = new LocalSlicerFileStorage(optionsWrapper, _mockLogger.Object);

            // Assert
            Directory.Exists(newTempPath).Should().BeTrue();
        }
        finally
        {
            // Cleanup
            if (Directory.Exists(newTempPath))
            {
                Directory.Delete(newTempPath, true);
            }
        }
    }

    // Helper methods

    private static byte[] CreateTestFileContent(string? customContent = null)
    {
        if (customContent != null)
        {
            return System.Text.Encoding.UTF8.GetBytes(customContent);
        }

        // Create test STL-like content
        var content = """
            solid test_model
              facet normal 0 0 1
                outer loop
                  vertex 0 0 1
                  vertex 1 0 1
                  vertex 1 1 1
                endloop
              endfacet
            endsolid test_model
            """;
        return System.Text.Encoding.UTF8.GetBytes(content);
    }
}

/// <summary>
/// Configuration options for LocalSlicerFileStorage
/// </summary>
// NOTE: Removed duplicate LocalFileStorageOptions test shim.
// The production options class (Farm.Web.Api.Services.SlicerServices.LocalFileStorageOptions)
// is used directly via the using directive at the top of this file.
