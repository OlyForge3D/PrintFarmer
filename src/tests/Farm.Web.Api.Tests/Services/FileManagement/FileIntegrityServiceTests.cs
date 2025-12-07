using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Farm.Web.Api.Services.FileManagement;
using Farm.Infrastructure.Telemetry;
using FluentAssertions;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services.FileManagement;

public class FileIntegrityServiceTests
{
    private readonly Mock<IFileManagementService> _fileManagementService = new();
    private readonly Mock<IUnifiedLoggingService> _logger = new();

    private FileIntegrityService CreateSut() => new(_fileManagementService.Object, _logger.Object);

    [Fact]
    public async Task FileExistsAsync_WhenFilePresent_ReturnsTrue()
    {
        // Arrange
        string tempFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        await File.WriteAllTextAsync(tempFile, "hello");
        FileIntegrityService sut = CreateSut();

        try
        {
            // Act
            bool exists = await sut.FileExistsAsync(tempFile);

            // Assert
            exists.Should().BeTrue();
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task VerifyHashAsync_WithMatchingHash_ReturnsTrue()
    {
        // Arrange
        string tempFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        await File.WriteAllTextAsync(tempFile, "abc");
        const string expectedHash = "hash123";
        _fileManagementService.Setup(x => x.ComputeFileHashAsync(tempFile, "sha256", It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedHash);
        FileIntegrityService sut = CreateSut();

        try
        {
            // Act
            bool result = await sut.VerifyHashAsync(tempFile, expectedHash, "sha256");

            // Assert
            result.Should().BeTrue();
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task VerifyHashAsync_WhenFileMissing_ReturnsFalse()
    {
        // Arrange
        string missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        FileIntegrityService sut = CreateSut();

        // Act
        bool result = await sut.VerifyHashAsync(missing, "any", "sha256");

        // Assert
        result.Should().BeFalse();
        _fileManagementService.Verify(x => x.ComputeFileHashAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task VerifyIntegrityAsync_WithSizeMismatch_ReturnsSizeFailure()
    {
        // Arrange
        string tempFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        await File.WriteAllTextAsync(tempFile, "abc");
        FileIntegrityService sut = CreateSut();

        try
        {
            // Act
            FileIntegrityCheckResult result = await sut.VerifyIntegrityAsync(tempFile, "ignored", expectedSizeBytes: 99);

            // Assert
            result.IsValid.Should().BeFalse();
            result.FailureReason.Should().Be("SizeMismatch");
            _fileManagementService.Verify(x => x.ComputeFileHashAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task VerifyIntegrityAsync_WithHashMismatch_ReturnsHashFailure()
    {
        // Arrange
        string tempFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        await File.WriteAllTextAsync(tempFile, "abc");
        _fileManagementService.Setup(x => x.ComputeFileHashAsync(tempFile, "sha256", It.IsAny<CancellationToken>()))
            .ReturnsAsync("actual");
        FileIntegrityService sut = CreateSut();

        try
        {
            // Act
            FileIntegrityCheckResult result = await sut.VerifyIntegrityAsync(tempFile, expectedHash: "expected", expectedSizeBytes: 3);

            // Assert
            result.IsValid.Should().BeFalse();
            result.FailureReason.Should().Be("HashMismatch");
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task VerifyIntegrityAsync_WhenHashThrowsUnauthorized_ReturnsPermissionDenied()
    {
        // Arrange
        string tempFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        await File.WriteAllTextAsync(tempFile, "abc");
        _fileManagementService.Setup(x => x.ComputeFileHashAsync(tempFile, "sha256", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new UnauthorizedAccessException("denied"));
        FileIntegrityService sut = CreateSut();

        try
        {
            // Act
            FileIntegrityCheckResult result = await sut.VerifyIntegrityAsync(tempFile, expectedHash: "expected", expectedSizeBytes: 3);

            // Assert
            result.IsValid.Should().BeFalse();
            result.FailureReason.Should().Be("PermissionDenied");
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task RecomputeHashAsync_WhenFileMissing_ReturnsNull()
    {
        // Arrange
        string missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        FileIntegrityService sut = CreateSut();

        // Act
        string? hash = await sut.RecomputeHashAsync(missing);

        // Assert
        hash.Should().BeNull();
        _fileManagementService.Verify(x => x.ComputeFileHashAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RecomputeHashAsync_WhenFileExists_ReturnsHash()
    {
        // Arrange
        string tempFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        await File.WriteAllTextAsync(tempFile, "abc");
        _fileManagementService.Setup(x => x.ComputeFileHashAsync(tempFile, "sha256", It.IsAny<CancellationToken>()))
            .ReturnsAsync("hash-abc");
        FileIntegrityService sut = CreateSut();

        try
        {
            // Act
            string? hash = await sut.RecomputeHashAsync(tempFile);

            // Assert
            hash.Should().Be("hash-abc");
        }
        finally
        {
            File.Delete(tempFile);
        }
    }
}
