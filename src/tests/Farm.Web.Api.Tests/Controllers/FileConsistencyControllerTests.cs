using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.FileConsistency;
using Farm.Web.Api.Controllers;
using Farm.Web.Api.DTOs;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Controllers;

public class FileConsistencyControllerTests
{
    private readonly Mock<IFileConsistencyRepository> _repoMock;
    private readonly FileConsistencyController _controller;

    public FileConsistencyControllerTests()
    {
        _repoMock = new Mock<IFileConsistencyRepository>();
        _controller = new FileConsistencyController(_repoMock.Object);
    }

    [Fact]
    public async Task GetHealthSummaryAsync_ReturnsOkWithSummary()
    {
        // Arrange
        _repoMock.Setup(r => r.CountModel3DFilesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(100);
        _repoMock.Setup(r => r.CountHealthyModel3DFilesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(90);
        _repoMock.Setup(r => r.CountMissingModel3DFilesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(5);
        _repoMock.Setup(r => r.CountCorruptedModel3DFilesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(5);
        
        _repoMock.Setup(r => r.CountGcodeFilesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(200);
        _repoMock.Setup(r => r.CountHealthyGcodeFilesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(180);
        _repoMock.Setup(r => r.CountMissingGcodeFilesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(10);
        _repoMock.Setup(r => r.CountCorruptedGcodeFilesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(10);
        
        _repoMock.Setup(r => r.GetMostRecentHealthyAuditAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FileHealthAudit { AuditDate = DateTime.UtcNow });

        // Act
        var result = await _controller.GetHealthSummaryAsync(CancellationToken.None);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var summary = Assert.IsType<FileHealthSummaryDto>(okResult.Value);
        Assert.Equal(100, summary.TotalModel3DFiles);
        Assert.Equal(90, summary.Model3DHealthy);
        Assert.Equal(200, summary.TotalGcodeFiles);
        Assert.Equal(180, summary.GcodeHealthy);
        Assert.Equal(90.0, summary.OverallHealthPercentage);
    }

    [Fact]
    public async Task GetHealthSummaryAsync_WhenExceptionThrown_ReturnsInternalServerError()
    {
        // Arrange
        _repoMock.Setup(r => r.CountModel3DFilesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Database error"));

        // Act
        var result = await _controller.GetHealthSummaryAsync(CancellationToken.None);

        // Assert
        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(500, objectResult.StatusCode);
    }

    [Fact]
    public async Task GetAuditHistoryAsync_ReturnsOkWithAudits()
    {
        // Arrange
        var audits = new List<FileHealthAudit>
        {
            new FileHealthAudit
            {
                Id = Guid.NewGuid(),
                AuditDate = DateTime.UtcNow,
                AuditType = FileAuditType.FullAudit,
                FilesChecked = 300,
                HealthyFiles = 270,
                MissingFiles = 15,
                CorruptedFiles = 15,
                OrphanedFiles = 0,
                HasIssues = true
            }
        };
        _repoMock.Setup(r => r.GetRecentAuditsAsync(20, It.IsAny<CancellationToken>()))
            .ReturnsAsync(audits);

        // Act
        var result = await _controller.GetAuditHistoryAsync(20, CancellationToken.None);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var auditDtos = Assert.IsType<List<FileHealthAuditDto>>(okResult.Value);
        Assert.Single(auditDtos);
        Assert.Equal(300, auditDtos[0].FilesChecked);
    }

    [Fact]
    public async Task GetAuditHistoryAsync_WhenExceptionThrown_ReturnsInternalServerError()
    {
        // Arrange
        _repoMock.Setup(r => r.GetRecentAuditsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Database error"));

        // Act
        var result = await _controller.GetAuditHistoryAsync(20, CancellationToken.None);

        // Assert
        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(500, objectResult.StatusCode);
    }

    [Fact]
    public async Task GetFilesWithIssuesAsync_ReturnsOkWithIssues()
    {
        // Arrange
        var missingModels = new List<Model3D>
        {
            new Model3D
            {
                Id = Guid.NewGuid(),
                DisplayName = "Missing Model",
                FilePath = "/path/to/missing.stl",
                HealthStatus = FileHealthStatus.Missing,
                LastHealthCheckDate = DateTime.UtcNow
            }
        };
        var corruptedGcode = new List<GcodeFile>
        {
            new GcodeFile
            {
                Id = Guid.NewGuid(),
                DisplayName = "Corrupted Gcode",
                FilePath = "/path/to/corrupted.gcode",
                HealthStatus = FileHealthStatus.Corrupted,
                LastHealthCheckDate = DateTime.UtcNow
            }
        };

        _repoMock.Setup(r => r.GetModel3DFilesWithIssueAsync(FileHealthStatus.Missing, It.IsAny<CancellationToken>()))
            .ReturnsAsync(missingModels);
        _repoMock.Setup(r => r.GetModel3DFilesWithIssueAsync(FileHealthStatus.Corrupted, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Model3D>());
        _repoMock.Setup(r => r.GetGcodeFilesWithIssueAsync(FileHealthStatus.Missing, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<GcodeFile>());
        _repoMock.Setup(r => r.GetGcodeFilesWithIssueAsync(FileHealthStatus.Corrupted, It.IsAny<CancellationToken>()))
            .ReturnsAsync(corruptedGcode);

        // Act
        var result = await _controller.GetFilesWithIssuesAsync(CancellationToken.None);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var summary = Assert.IsType<FileIssuesSummaryDto>(okResult.Value);
        Assert.Equal(2, summary.TotalIssues);
        Assert.Equal(1, summary.MissingFiles);
        Assert.Equal(1, summary.CorruptedFiles);
    }

    [Fact]
    public async Task GetFilesWithIssuesAsync_WhenExceptionThrown_ReturnsInternalServerError()
    {
        // Arrange
        _repoMock.Setup(r => r.GetModel3DFilesWithIssueAsync(It.IsAny<FileHealthStatus>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Database error"));

        // Act
        var result = await _controller.GetFilesWithIssuesAsync(CancellationToken.None);

        // Assert
        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(500, objectResult.StatusCode);
    }

    [Fact]
    public async Task GetModel3DHealthAsync_WithValidId_ReturnsOkWithDetails()
    {
        // Arrange
        var modelId = Guid.NewGuid();
        var model = new Model3D
        {
            Id = modelId,
            DisplayName = "Test Model",
            FilePath = "/path/to/model.stl",
            FileSizeBytes = 1024,
            FileHash = "abc123",
            HealthStatus = FileHealthStatus.Healthy,
            LastHealthCheckDate = DateTime.UtcNow,
            LastVerificationResult = "All checks passed",
            UploadedAt = DateTime.UtcNow
        };

        _repoMock.Setup(r => r.GetModel3DWithHealthDetailsAsync(modelId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(model);

        // Act
        var result = await _controller.GetModel3DHealthAsync(modelId, CancellationToken.None);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var detail = Assert.IsType<FileHealthDetailDto>(okResult.Value);
        Assert.Equal(modelId, detail.FileId);
        Assert.Equal("Test Model", detail.FileName);
        Assert.Equal("Healthy", detail.HealthStatus);
    }

    [Fact]
    public async Task GetModel3DHealthAsync_WithInvalidId_ReturnsNotFound()
    {
        // Arrange
        var modelId = Guid.NewGuid();
        _repoMock.Setup(r => r.GetModel3DWithHealthDetailsAsync(modelId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Model3D?)null);

        // Act
        var result = await _controller.GetModel3DHealthAsync(modelId, CancellationToken.None);

        // Assert
        var notFoundResult = Assert.IsType<NotFoundObjectResult>(result.Result);
        Assert.Contains(modelId.ToString(), notFoundResult.Value?.ToString());
    }

    [Fact]
    public async Task GetGcodeFileHealthAsync_WithValidId_ReturnsOkWithDetails()
    {
        // Arrange
        var gcodeId = Guid.NewGuid();
        var gcode = new GcodeFile
        {
            Id = gcodeId,
            DisplayName = "Test Gcode",
            FilePath = "/path/to/test.gcode",
            FileSizeBytes = 2048,
            FileHash = "def456",
            HealthStatus = FileHealthStatus.Healthy,
            LastHealthCheckDate = DateTime.UtcNow,
            LastVerificationResult = "All checks passed",
            UploadedAt = DateTime.UtcNow
        };

        _repoMock.Setup(r => r.GetGcodeFileWithHealthDetailsAsync(gcodeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(gcode);

        // Act
        var result = await _controller.GetGcodeFileHealthAsync(gcodeId, CancellationToken.None);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var detail = Assert.IsType<FileHealthDetailDto>(okResult.Value);
        Assert.Equal(gcodeId, detail.FileId);
        Assert.Equal("Test Gcode", detail.FileName);
        Assert.Equal("Healthy", detail.HealthStatus);
    }

    [Fact]
    public async Task GetGcodeFileHealthAsync_WithInvalidId_ReturnsNotFound()
    {
        // Arrange
        var gcodeId = Guid.NewGuid();
        _repoMock.Setup(r => r.GetGcodeFileWithHealthDetailsAsync(gcodeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((GcodeFile?)null);

        // Act
        var result = await _controller.GetGcodeFileHealthAsync(gcodeId, CancellationToken.None);

        // Assert
        var notFoundResult = Assert.IsType<NotFoundObjectResult>(result.Result);
        Assert.Contains(gcodeId.ToString(), notFoundResult.Value?.ToString());
    }

    [Fact]
    public async Task GetHealthSummaryAsync_WithNoFiles_ReturnsHundredPercentHealth()
    {
        // Arrange - all counts are zero
        _repoMock.Setup(r => r.CountModel3DFilesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(0);
        _repoMock.Setup(r => r.CountHealthyModel3DFilesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(0);
        _repoMock.Setup(r => r.CountMissingModel3DFilesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(0);
        _repoMock.Setup(r => r.CountCorruptedModel3DFilesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(0);
        
        _repoMock.Setup(r => r.CountGcodeFilesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(0);
        _repoMock.Setup(r => r.CountHealthyGcodeFilesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(0);
        _repoMock.Setup(r => r.CountMissingGcodeFilesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(0);
        _repoMock.Setup(r => r.CountCorruptedGcodeFilesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(0);
        
        _repoMock.Setup(r => r.GetMostRecentHealthyAuditAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((FileHealthAudit?)null);

        // Act
        var result = await _controller.GetHealthSummaryAsync(CancellationToken.None);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var summary = Assert.IsType<FileHealthSummaryDto>(okResult.Value);
        Assert.Equal(100.0, summary.OverallHealthPercentage);
    }
}
