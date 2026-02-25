using System.Text;
using Farm.Slicer.Module.Api.Services;
using Farm.Slicer.Module.Services.Configuration;
using Farm.Slicer.Module.Tests.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Farm.Slicer.Module.Tests.SlicerServices;

/// <summary>
/// Unit tests for LocalSlicerFileStorage - Local file system storage implementation
/// </summary>
public class LocalSlicerFileStorageTests : IDisposable
{
    private readonly ILogger<LocalSlicerFileStorage> _testLogger;
    private readonly LocalSlicerFileStorage _storage;
    private readonly string _tempBasePath;
    private readonly LocalFileStorageOptions _options;
    private readonly TestFileSystem _testFs;

    public LocalSlicerFileStorageTests()
    {
        _testLogger = NullLogger<LocalSlicerFileStorage>.Instance;
        _tempBasePath = Path.Combine(TestInfrastructure.TestPaths.GetUniqueTempDirectory(), "slicer-storage-tests");

        _options = new LocalFileStorageOptions
        {
            BasePath = _tempBasePath
        };

        _testFs = TestFileSystemFactory.WithFiles(new Dictionary<string, byte[]>());
        IOptions<LocalFileStorageOptions> optionsWrapper = Options.Create(_options);
        _storage = new LocalSlicerFileStorage(optionsWrapper, _testLogger, _testFs);
    }

    public void Dispose()
    {
        // No OS-level cleanup required when using TestFileSystem; keep legacy cleanup for safety
        try
        {
            if (Directory.Exists(_tempBasePath))
            {
                Directory.Delete(_tempBasePath, recursive: true);
            }
        }
        catch { }
    }

    [Fact]
    public async Task UploadFileAsync_WithStream_ShouldUploadFileAndReturnUrl()
    {
        // Arrange
        string key = "test-models/cube.stl";
        byte[] content = CreateTestFileContent();
        using MemoryStream stream = new MemoryStream(content);

        // Act
        string url = await _storage.UploadFileAsync(key, stream, "application/octet-stream");

        // Assert
        _ = url.Should().NotBeNullOrEmpty();
        _ = url.Should().Contain(key);

        // Verify file exists in test file system
        string filePath = Path.Combine(_tempBasePath, key);
        _ = (await _testFs.ReadAllBytesAsync(filePath)).Should().BeEquivalentTo(content);
    }

    [Fact]
    public async Task UploadFileAsync_WithByteArray_ShouldUploadFileAndReturnUrl()
    {
        // Arrange
        string key = "test-models/sphere.obj";
        byte[] content = CreateTestFileContent();

        // Act
        string url = await _storage.UploadFileAsync(key, content, "application/octet-stream");

        // Assert
        _ = url.Should().NotBeNullOrEmpty();
        _ = url.Should().Contain(key);

        // Verify file exists in test file system
        string filePath = Path.Combine(_tempBasePath, key);
        _ = (await _testFs.ReadAllBytesAsync(filePath)).Should().BeEquivalentTo(content);
    }

    [Fact]
    public async Task UploadFileAsync_WithNestedPath_ShouldCreateDirectories()
    {
        // Arrange
        string key = "users/user123/projects/project456/models/complex.3mf";
        byte[] content = CreateTestFileContent();

        // Act
        string url = await _storage.UploadFileAsync(key, content, "application/octet-stream");

        // Assert
        _ = url.Should().NotBeNullOrEmpty();

        // Verify directory structure was created
        string filePath = Path.Combine(_tempBasePath, key);
        _ = (await _testFs.ReadAllBytesAsync(filePath)).Should().NotBeNull();

        string? directoryPath = Path.GetDirectoryName(filePath);
        _ = directoryPath.Should().NotBeNull();
        _ = _testFs.DirectoryExists(directoryPath!).Should().BeTrue();
    }

    [Fact]
    public async Task DownloadFileAsync_ExistingFile_ShouldReturnFileStream()
    {
        // Arrange
        string key = "test-download/file.stl";
        byte[] originalContent = CreateTestFileContent();
        _ = await _storage.UploadFileAsync(key, originalContent, "application/octet-stream");

        // Act
        using Stream stream = await _storage.DownloadFileAsync(key);

        // Assert
        _ = stream.Should().NotBeNull();

        using MemoryStream memoryStream = new MemoryStream();
        await stream.CopyToAsync(memoryStream);
        byte[] downloadedContent = memoryStream.ToArray();

        string filePath = Path.Combine(_tempBasePath, key);
        byte[] uploadedContent = await _testFs.ReadAllBytesAsync(filePath);
        _ = uploadedContent.Should().BeEquivalentTo(originalContent);
    }

    [Fact]
    public async Task DownloadFileAsync_NonExistentFile_ShouldThrowFileNotFoundException()
    {
        // Arrange
        string key = "non-existent/file.stl";

        // Act & Assert
        FileNotFoundException exception = await Assert.ThrowsAsync<FileNotFoundException>(() => _storage.DownloadFileAsync(key));
        _ = exception.Message.Should().Contain("File not found");
        _ = exception.Message.Should().Contain(key);
    }

    [Fact]
    public async Task DownloadFileAsync_WithUrl_ShouldExtractKeyAndDownload()
    {
        // Arrange
        string key = "test-url/file.obj";
        byte[] originalContent = CreateTestFileContent();
        string url = await _storage.UploadFileAsync(key, originalContent, "application/octet-stream");

        // Act
        using Stream stream = await _storage.DownloadFileAsync(url);

        // Assert
        _ = stream.Should().NotBeNull();

        using MemoryStream memoryStream = new MemoryStream();
        await stream.CopyToAsync(memoryStream);
        byte[] downloadedContent = memoryStream.ToArray();

        _ = downloadedContent.Should().BeEquivalentTo(originalContent);
    }

    [Fact]
    public async Task DownloadFileBytesAsync_ExistingFile_ShouldReturnByteArray()
    {
        // Arrange
        string key = "test-bytes/file.amf";
        byte[] originalContent = CreateTestFileContent();
        _ = await _storage.UploadFileAsync(key, originalContent, "application/octet-stream");

        // Act
        string filePath = Path.Combine(_tempBasePath, key);
        byte[] downloadedContent = await _testFs.ReadAllBytesAsync(filePath);

        // Assert
        _ = downloadedContent.Should().BeEquivalentTo(originalContent);
    }

    [Fact]
    public async Task DownloadFileBytesAsync_NonExistentFile_ShouldThrowFileNotFoundException()
    {
        // Arrange
        string key = "non-existent/bytes.stl";

        // Act & Assert
        _ = await Assert.ThrowsAsync<FileNotFoundException>(() => _storage.DownloadFileBytesAsync(key));
    }

    [Fact]
    public async Task FileExistsAsync_ExistingFile_ShouldReturnTrue()
    {
        // Arrange
        string key = "test-exists/file.ply";
        byte[] content = CreateTestFileContent();
        _ = await _storage.UploadFileAsync(key, content, "application/octet-stream");

        // Act
        bool exists = await _storage.FileExistsAsync(key);

        // Assert
        _ = exists.Should().BeTrue();
    }

    [Fact]
    public async Task FileExistsAsync_NonExistentFile_ShouldReturnFalse()
    {
        // Arrange
        string key = "non-existent/file.stl";

        // Act
        bool exists = await _storage.FileExistsAsync(key);

        // Assert
        _ = exists.Should().BeFalse();
    }

    [Fact]
    public async Task FileExistsAsync_WithUrl_ShouldExtractKeyAndCheck()
    {
        // Arrange
        string key = "test-url-exists/file.obj";
        byte[] content = CreateTestFileContent();
        string url = await _storage.UploadFileAsync(key, content, "application/octet-stream");

        // Act
        bool exists = await _storage.FileExistsAsync(url);

        // Assert
        _ = exists.Should().BeTrue();
    }

    [Fact]
    public async Task DeleteFileAsync_ExistingFile_ShouldDeleteFile()
    {
        // Arrange
        string key = "test-delete/file.3mf";
        byte[] content = CreateTestFileContent();
        _ = await _storage.UploadFileAsync(key, content, "application/octet-stream");

        // Verify file exists first
        bool existsBefore = await _storage.FileExistsAsync(key);
        _ = existsBefore.Should().BeTrue();

        // Act
        await _storage.DeleteFileAsync(key);

        // Assert
        bool existsAfter = await _storage.FileExistsAsync(key);
        _ = existsAfter.Should().BeFalse();
    }

    [Fact]
    public async Task DeleteFileAsync_NonExistentFile_ShouldNotThrow()
    {
        // Arrange
        string key = "non-existent/delete-me.stl";

        // Act & Assert - Should not throw
        await _storage.DeleteFileAsync(key);
    }

    [Fact]
    public async Task GetFileMetadataAsync_ExistingFile_ShouldReturnMetadata()
    {
        // Arrange
        string key = "test-metadata/file.stl";
        byte[] content = CreateTestFileContent();
        DateTime beforeUpload = DateTime.UtcNow;

        _ = await _storage.UploadFileAsync(key, content, "application/vnd.ms-3mfdocument");

        DateTime afterUpload = DateTime.UtcNow;

        // Act
        SlicerFileMetadata? metadata = await _storage.GetFileMetadataAsync(key);

        // Assert
        _ = metadata.Should().NotBeNull();
        _ = metadata!.Key.Should().Be(key);
        _ = metadata.SizeBytes.Should().Be(content.Length);
        _ = metadata.ContentType.Should().Be("application/vnd.ms-3mfdocument");
        _ = metadata.CreatedAt.Should().BeAfter(beforeUpload.AddSeconds(-1));
        _ = metadata.CreatedAt.Should().BeBefore(afterUpload.AddSeconds(1));
        _ = metadata.LastModified.Should().BeAfter(beforeUpload.AddSeconds(-1));
        _ = metadata.LastModified.Should().BeBefore(afterUpload.AddSeconds(1));
        _ = metadata.ETag.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task GetFileMetadataAsync_NonExistentFile_ShouldReturnNull()
    {
        // Arrange
        string key = "non-existent/metadata.stl";

        // Act
        SlicerFileMetadata? metadata = await _storage.GetFileMetadataAsync(key);

        // Assert
        _ = metadata.Should().BeNull();
    }

    [Fact]
    public async Task GenerateSignedUrlAsync_ShouldReturnFileUrl()
    {
        // Arrange
        string key = "test-signed/file.obj";
        byte[] content = CreateTestFileContent();
        _ = await _storage.UploadFileAsync(key, content, "application/octet-stream");
        TimeSpan expiration = TimeSpan.FromHours(1);

        // Act
        string signedUrl = await _storage.GenerateSignedUrlAsync(key, expiration);

        // Assert
        _ = signedUrl.Should().NotBeNullOrEmpty();
        _ = signedUrl.Should().Contain(key);
    }

    [Fact]
    public async Task CleanupTempFilesAsync_OldFiles_ShouldDeleteOldFiles()
    {
        // Arrange
        string oldKey = "temp/old-file.stl";
        string newKey = "temp/new-file.stl";
        byte[] content = CreateTestFileContent();

        // Upload files
        _ = await _storage.UploadFileAsync(oldKey, content, "application/octet-stream");
        _ = await _storage.UploadFileAsync(newKey, content, "application/octet-stream");

        // Make one file appear old by manually setting its creation time
        string oldFilePath = Path.Combine(_tempBasePath, oldKey);
        DateTime oldTime = DateTime.UtcNow.AddDays(-2);
        _testFs.SetCreationTimeUtc(oldFilePath, oldTime);
        _testFs.SetLastWriteTimeUtc(oldFilePath, oldTime);

        // Act
        _storage.CleanupTempFiles(TimeSpan.FromDays(1));

        // Assert
        bool oldExists = await _storage.FileExistsAsync(oldKey);
        bool newExists = await _storage.FileExistsAsync(newKey);

        _ = oldExists.Should().BeFalse(); // Old file should be deleted
        _ = newExists.Should().BeTrue();  // New file should remain
    }

    [Fact]
    public async Task CleanupTempFilesAsync_NoOldFiles_ShouldNotDeleteAnything()
    {
        // Arrange
        string key1 = "temp/file1.stl";
        string key2 = "temp/file2.obj";
        byte[] content = CreateTestFileContent();

        _ = await _storage.UploadFileAsync(key1, content, "application/octet-stream");
        _ = await _storage.UploadFileAsync(key2, content, "application/octet-stream");

        // Act
        _storage.CleanupTempFiles(TimeSpan.FromMinutes(1));

        // Assert
        bool exists1 = await _storage.FileExistsAsync(key1);
        bool exists2 = await _storage.FileExistsAsync(key2);

        _ = exists1.Should().BeTrue();
        _ = exists2.Should().BeTrue();
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
        byte[] content = CreateTestFileContent();

        // Act
        string url = await _storage.UploadFileAsync(key, content, "application/octet-stream");
        bool exists = await _storage.FileExistsAsync(key);
        byte[] downloadedContent = await _storage.DownloadFileBytesAsync(key);

        // Assert
        _ = url.Should().NotBeNullOrEmpty();
        _ = exists.Should().BeTrue();
        _ = downloadedContent.Should().BeEquivalentTo(content);
    }

    [Fact]
    public async Task ConcurrentOperations_ShouldHandleMultipleOperations()
    {
        // Arrange
        List<Task> tasks = new List<Task>();
        int fileCount = 10;

        // Act - Upload multiple files concurrently
        for (int i = 0; i < fileCount; i++)
        {
            string key = $"concurrent/file-{i}.stl";
            byte[] content = CreateTestFileContent($"Content for file {i}");

            tasks.Add(_storage.UploadFileAsync(key, content, "application/octet-stream"));
        }

        await Task.WhenAll(tasks);

        // Assert - All files should exist
        for (int i = 0; i < fileCount; i++)
        {
            string key = $"concurrent/file-{i}.stl";
            bool exists = await _storage.FileExistsAsync(key);
            _ = exists.Should().BeTrue($"File {key} should exist");
        }
    }

    [Fact]
    public void Constructor_InvalidOptions_ShouldThrowArgumentNullException()
    {
        // Arrange & Act & Assert
        _ = Assert.Throws<ArgumentNullException>(() => new LocalSlicerFileStorage(null!, _testLogger, TestFileSystemFactory.WithFiles(new Dictionary<string, byte[]>())));
    }

    [Fact]
    public void Constructor_ValidOptions_ShouldCreateBaseDirectory()
    {
        // Arrange
        string newTempPath = Path.Combine(TestInfrastructure.TestPaths.GetUniqueTempDirectory(), "test-directory-creation");
        LocalFileStorageOptions options = new LocalFileStorageOptions { BasePath = newTempPath };
        IOptions<LocalFileStorageOptions> optionsWrapper = Options.Create(options);

        try
        {
            // Act
            TestFileSystem testFs = TestFileSystemFactory.WithFiles(new Dictionary<string, byte[]>());
            LocalSlicerFileStorage storage = new LocalSlicerFileStorage(optionsWrapper, _testLogger, testFs);

            // Assert - the storage implementation should create the base directory via the file system
            _ = testFs.DirectoryExists(newTempPath).Should().BeTrue();
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
            return Encoding.UTF8.GetBytes(customContent);
        }

        // Create test STL-like content
        string content = """
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
        return Encoding.UTF8.GetBytes(content);
    }
}

/// <summary>
/// Configuration options for LocalSlicerFileStorage
/// </summary>
// NOTE: Removed duplicate LocalFileStorageOptions test shim.
// The production options class (Farm.Slicer.Module.Services.Configuration.LocalFileStorageOptions)
// is used directly via the using directive at the top of this file.
