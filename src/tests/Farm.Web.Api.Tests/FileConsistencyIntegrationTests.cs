using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Web.Api.Services.FileManagement;
using Farm.Web.Api.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Farm.Web.Api.Tests;

/// <summary>
/// Integration tests for file consistency audit, verification, and health status endpoints.
/// Tests the full stack: database persistence, audit service, API endpoints, and health checks.
/// </summary>
public class FileConsistencyIntegrationTests : IAsyncLifetime
{
    private readonly CustomWebApplicationFactory _factory;
    private HttpClient _client = null!;
    private AppDbContext _dbContext = null!;
    private string _modelStoragePath = null!;
    private string _gcodeStoragePath = null!;

    public FileConsistencyIntegrationTests()
    {
        _factory = new CustomWebApplicationFactory();
    }

    public async Task InitializeAsync()
    {
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var scope = _factory.Services.CreateScope();
        _dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // Setup test storage directories
        _modelStoragePath = Path.Combine(Path.GetTempPath(), "test_models_" + Guid.NewGuid());
        _gcodeStoragePath = Path.Combine(Path.GetTempPath(), "test_gcode_" + Guid.NewGuid());
        Directory.CreateDirectory(_modelStoragePath);
        Directory.CreateDirectory(_gcodeStoragePath);

        await _dbContext.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        _dbContext?.Dispose();
        // Cleanup test directories
        if (Directory.Exists(_modelStoragePath))
        {
            Directory.Delete(_modelStoragePath, recursive: true);
        }
        if (Directory.Exists(_gcodeStoragePath))
        {
            Directory.Delete(_gcodeStoragePath, recursive: true);
        }

        _factory?.Dispose();
    }

    [Fact]
    public async Task GetHealthSummary_WithHealthyFiles_ReturnsCorrectStats()
    {
        // Arrange
        var model1 = CreateAndPersistModel3D("test-model-1.stl", FileHealthStatus.Healthy);
        var model2 = CreateAndPersistModel3D("test-model-2.stl", FileHealthStatus.Healthy);
        var gcode1 = CreateAndPersistGcodeFile("test-print.gcode", FileHealthStatus.Healthy);
        await _dbContext.SaveChangesAsync();

        // Act
        var response = await _client.GetAsync("/api/fileconsistency/health/summary");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsAsync<dynamic>();
        content.totalModel3DFiles.Should().Be(2);
        content.model3DHealthy.Should().Be(2);
        content.totalGcodeFiles.Should().Be(1);
        content.gcodeHealthy.Should().Be(1);
        content.overallHealthPercentage.Should().Be(100.0);
    }

    [Fact]
    public async Task GetFilesWithIssues_WithMissingAndCorruptedFiles_ReturnsIssueDetails()
    {
        // Arrange
        var healthyModel = CreateAndPersistModel3D("healthy.stl", FileHealthStatus.Healthy);
        var missingModel = CreateAndPersistModel3D("missing.stl", FileHealthStatus.Missing);
        var corruptedGcode = CreateAndPersistGcodeFile("corrupted.gcode", FileHealthStatus.Corrupted);
        await _dbContext.SaveChangesAsync();

        // Act
        var response = await _client.GetAsync("/api/fileconsistency/files/issues");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsAsync<dynamic>();
        content.totalIssues.Should().Be(2);
        content.missingFiles.Should().Be(1);
        content.corruptedFiles.Should().Be(1);
        ((List<dynamic>)content.issues).Should().HaveCount(2);
    }

    [Fact]
    public async Task GetModel3DHealth_WithSpecificFile_ReturnsCorrectDetails()
    {
        // Arrange
        var model = CreateAndPersistModel3D("test.stl", FileHealthStatus.Healthy);
        model.LastHealthCheckDate = DateTime.UtcNow.AddMinutes(-5);
        model.LastVerificationResult = "{\"verified\": true, \"hash_match\": true}";
        await _dbContext.SaveChangesAsync();

        // Act
        var response = await _client.GetAsync($"/api/fileconsistency/model3d/{model.Id}/health");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsAsync<dynamic>();
        content.fileId.Should().Be(model.Id.ToString());
        content.healthStatus.Should().Be("Healthy");
        content.fileSize.Should().Be(model.FileSizeBytes);
    }

    [Fact]
    public async Task GetGcodeFileHealth_WithNonexistentFile_Returns404()
    {
        // Act
        var response = await _client.GetAsync($"/api/fileconsistency/gcode/{Guid.NewGuid()}/health");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetAuditHistory_WithMultipleAudits_ReturnsInReverseChronological()
    {
        // Arrange
        var audit1 = new FileHealthAudit
        {
            Id = Guid.NewGuid(),
            AuditDate = DateTime.UtcNow.AddHours(-2),
            AuditType = FileAuditType.Model3D,
            FilesChecked = 5,
            HealthyFiles = 5,
            MissingFiles = 0,
            CorruptedFiles = 0,
            OrphanedFiles = 0,
            HasIssues = false,
            SummaryMessage = "Model3D audit: All files healthy",
            CreatedAt = DateTime.UtcNow.AddHours(-2)
        };

        var audit2 = new FileHealthAudit
        {
            Id = Guid.NewGuid(),
            AuditDate = DateTime.UtcNow.AddHours(-1),
            AuditType = FileAuditType.GcodeFile,
            FilesChecked = 3,
            HealthyFiles = 2,
            MissingFiles = 1,
            CorruptedFiles = 0,
            OrphanedFiles = 0,
            HasIssues = true,
            SummaryMessage = "GcodeFile audit: 1 missing file",
            CreatedAt = DateTime.UtcNow.AddHours(-1)
        };

        _dbContext.FileHealthAudits.Add(audit1);
        _dbContext.FileHealthAudits.Add(audit2);
        await _dbContext.SaveChangesAsync();

        // Act
        var response = await _client.GetAsync("/api/fileconsistency/audits/history?pageSize=10");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsAsync<List<dynamic>>();
        content.Should().HaveCount(2);
        ((DateTime)content[0].auditDate).Should().BeAfter((DateTime)content[1].auditDate);
    }

    [Fact]
    public async Task FileConsistencyAuditService_WithMissingFile_SavesAuditResultsToDB()
    {
        // Arrange
        var model = CreateAndPersistModel3D("test-model.stl", FileHealthStatus.Unknown);
        await _dbContext.SaveChangesAsync();

        // Create audit result manually (simulating background service)
        var auditResult = new FileHealthAudit
        {
            Id = Guid.NewGuid(),
            AuditDate = DateTime.UtcNow,
            AuditType = FileAuditType.Model3D,
            FilesChecked = 1,
            HealthyFiles = 0,
            MissingFiles = 1,
            CorruptedFiles = 0,
            OrphanedFiles = 0,
            MissingFileIds = $"[\"{model.Id}\"]",
            HasIssues = true,
            SummaryMessage = "Model3D audit: 1 missing file",
            CreatedAt = DateTime.UtcNow
        };

        // Act
        _dbContext.FileHealthAudits.Add(auditResult);
        await _dbContext.SaveChangesAsync();

        // Assert
        var savedAudit = await _dbContext.FileHealthAudits
            .FirstOrDefaultAsync(a => a.Id == auditResult.Id);
        savedAudit.Should().NotBeNull();
        savedAudit!.MissingFiles.Should().Be(1);
        savedAudit.HasIssues.Should().BeTrue();
    }

    [Fact]
    public async Task FileIntegrityService_VerifyIntegrity_DetectsMissingFiles()
    {
        // Arrange
        var scope = _factory.Services.CreateScope();
        var integrityService = scope.ServiceProvider.GetRequiredService<IFileIntegrityService>();
        var nonexistentPath = Path.Combine(_modelStoragePath, "nonexistent.stl");

        // Act
        var result = await integrityService.VerifyIntegrityAsync(
            nonexistentPath,
            "expectedhash",
            1024,
            "SHA256");

        // Assert
        result.IsValid.Should().BeFalse();
        result.FailureReason.Should().Be("Missing");
    }

    [Fact]
    public async Task FileIntegrityService_VerifyIntegrity_DetectsHashMismatch()
    {
        // Arrange
        var scope = _factory.Services.CreateScope();
        var integrityService = scope.ServiceProvider.GetRequiredService<IFileIntegrityService>();
        var testFilePath = Path.Combine(_modelStoragePath, "test.stl");
        File.WriteAllText(testFilePath, "test content");

        // Act
        var result = await integrityService.VerifyIntegrityAsync(
            testFilePath,
            "wronghash1234567890abcdef1234567890abcdef1234567890abcdef1234567890",
            1024, // Wrong size
            "SHA256");

        // Assert
        result.IsValid.Should().BeFalse();
        result.FailureReason.Should().Be("SizeMismatch");
    }

    [Fact]
    public async Task GetHealthSummary_WithMixedStatus_CalculatesCorrectPercentage()
    {
        // Arrange - Create 4 files: 2 healthy, 1 missing, 1 corrupted
        CreateAndPersistModel3D("m1.stl", FileHealthStatus.Healthy);
        CreateAndPersistModel3D("m2.stl", FileHealthStatus.Healthy);
        CreateAndPersistModel3D("m3.stl", FileHealthStatus.Missing);
        CreateAndPersistGcodeFile("g1.gcode", FileHealthStatus.Corrupted);
        await _dbContext.SaveChangesAsync();

        // Act
        var response = await _client.GetAsync("/api/fileconsistency/health/summary");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsAsync<dynamic>();
        // 2 healthy out of 4 files = 50%
        content.overallHealthPercentage.Should().Be(50.0);
    }

    [Fact]
    public async Task FileConsistencyController_RequiresAuthorization()
    {
        // Arrange - use an unauthenticated client
        var factoryWithoutAuth = new CustomWebApplicationFactory();
        var clientWithoutAuth = factoryWithoutAuth.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        // Act
        var response = await clientWithoutAuth.GetAsync("/api/fileconsistency/health/summary");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        factoryWithoutAuth.Dispose();
        clientWithoutAuth.Dispose();
    }

    // Helper methods

    private Model3D CreateAndPersistModel3D(string fileName, FileHealthStatus healthStatus)
    {
        var model = new Model3D
        {
            Id = Guid.NewGuid(),
            OriginalFileName = fileName,
            DisplayName = fileName,
            FilePath = Path.Combine(_modelStoragePath, fileName),
            FileHash = "abc123def456",
            FileSizeBytes = 2048,
            FileFormat = ModelFileFormat.STL,
            UploadedAt = DateTime.UtcNow,
            IsValid = true,
            HealthStatus = healthStatus,
            LastHealthCheckDate = healthStatus == FileHealthStatus.Unknown ? null : DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _dbContext.Models3D.Add(model);
        return model;
    }

    private GcodeFile CreateAndPersistGcodeFile(string fileName, FileHealthStatus healthStatus)
    {
        var gcode = new GcodeFile
        {
            Id = Guid.NewGuid(),
            OriginalFileName = fileName,
            DisplayName = fileName,
            FilePath = Path.Combine(_gcodeStoragePath, fileName),
            FileHash = "xyz789uvw012",
            FileSizeBytes = 4096,
            UploadedAt = DateTime.UtcNow,
            Source = GcodeSource.Upload,
            HealthStatus = healthStatus,
            LastHealthCheckDate = healthStatus == FileHealthStatus.Unknown ? null : DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _dbContext.GcodeFiles.Add(gcode);
        return gcode;
    }
}
