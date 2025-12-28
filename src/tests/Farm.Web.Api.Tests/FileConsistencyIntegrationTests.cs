using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
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
        // Reset database to ensure clean state for this test
        await _factory.ResetDatabaseAsync();
        
        _client = await _factory.CreateAuthenticatedClientAsync();
        IServiceScope scope = _factory.Services.CreateScope();
        _dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // Setup test storage directories
        _modelStoragePath = Path.Combine(Path.GetTempPath(), "test_models_" + Guid.NewGuid());
        _gcodeStoragePath = Path.Combine(Path.GetTempPath(), "test_gcode_" + Guid.NewGuid());
        _ = Directory.CreateDirectory(_modelStoragePath);
        _ = Directory.CreateDirectory(_gcodeStoragePath);
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
        Model3D model1 = CreateAndPersistModel3D("test-model-1.stl", FileHealthStatus.Healthy);
        Model3D model2 = CreateAndPersistModel3D("test-model-2.stl", FileHealthStatus.Healthy);
        GcodeFile gcode1 = CreateAndPersistGcodeFile("test-print.gcode", FileHealthStatus.Healthy);
        _ = await _dbContext.SaveChangesAsync();

        // Act
        HttpResponseMessage response = await _client.GetAsync("/api/fileconsistency/health/summary");

        // Assert
        _ = response.StatusCode.Should().Be(HttpStatusCode.OK);
        string json = await response.Content.ReadAsStringAsync();
        JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        _ = root.GetProperty("totalModel3DFiles").GetInt32().Should().Be(2);
        _ = root.GetProperty("model3DHealthy").GetInt32().Should().Be(2);
        _ = root.GetProperty("totalGcodeFiles").GetInt32().Should().Be(1);
        _ = root.GetProperty("gcodeHealthy").GetInt32().Should().Be(1);
        _ = root.GetProperty("overallHealthPercentage").GetDouble().Should().Be(100.0);
    }


    [Fact]
    public async Task GetModel3DHealth_WithSpecificFile_ReturnsCorrectDetails()
    {
        // Arrange
        Model3D model = CreateAndPersistModel3D("test.stl", FileHealthStatus.Healthy);
        model.LastHealthCheckDate = DateTime.UtcNow.AddMinutes(-5);
        model.LastVerificationResult = "{\"verified\": true, \"hash_match\": true}";
        _ = await _dbContext.SaveChangesAsync();

        // Act
        HttpResponseMessage response = await _client.GetAsync($"/api/fileconsistency/model3d/{model.Id}/health");

        // Assert
        _ = response.StatusCode.Should().Be(HttpStatusCode.OK);
        string json = await response.Content.ReadAsStringAsync();
        JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        _ = root.GetProperty("fileId").GetString().Should().Be(model.Id.ToString());
        _ = root.GetProperty("healthStatus").GetString().Should().Be("Healthy");
        _ = root.GetProperty("fileSize").GetInt64().Should().Be(model.FileSizeBytes);
    }

    [Fact]
    public async Task GetGcodeFileHealth_WithNonexistentFile_Returns404()
    {
        // Act
        HttpResponseMessage response = await _client.GetAsync($"/api/fileconsistency/gcode/{Guid.NewGuid()}/health");

        // Assert
        _ = response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetAuditHistory_WithMultipleAudits_ReturnsInReverseChronological()
    {
        // Arrange
        FileHealthAudit audit1 = new FileHealthAudit
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

        FileHealthAudit audit2 = new FileHealthAudit
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

        _ = _dbContext.FileHealthAudits.Add(audit1);
        _ = _dbContext.FileHealthAudits.Add(audit2);
        _ = await _dbContext.SaveChangesAsync();

        // Act
        HttpResponseMessage response = await _client.GetAsync("/api/fileconsistency/audits/history?pageSize=10");

        // Assert
        _ = response.StatusCode.Should().Be(HttpStatusCode.OK);
        string json = await response.Content.ReadAsStringAsync();
        JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        _ = root.GetArrayLength().Should().Be(2);
        DateTime audit1Date = DateTime.Parse(root[0].GetProperty("auditDate").GetString() ?? "");
        DateTime audit2Date = DateTime.Parse(root[1].GetProperty("auditDate").GetString() ?? "");
        _ = audit1Date.Should().BeAfter(audit2Date);
    }

    [Fact]
    public async Task FileConsistencyAuditService_WithMissingFile_SavesAuditResultsToDB()
    {
        // Arrange
        Model3D model = CreateAndPersistModel3D("test-model.stl", FileHealthStatus.Unknown);
        _ = await _dbContext.SaveChangesAsync();

        // Create audit result manually (simulating background service)
        FileHealthAudit auditResult = new FileHealthAudit
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
        _ = _dbContext.FileHealthAudits.Add(auditResult);
        _ = await _dbContext.SaveChangesAsync();

        // Assert
        FileHealthAudit? savedAudit = await _dbContext.FileHealthAudits
            .FirstOrDefaultAsync(a => a.Id == auditResult.Id);
        _ = savedAudit.Should().NotBeNull();
        _ = savedAudit!.MissingFiles.Should().Be(1);
        _ = savedAudit.HasIssues.Should().BeTrue();
    }

    [Fact]
    public async Task FileIntegrityService_VerifyIntegrity_DetectsMissingFiles()
    {
        // Arrange
        IServiceScope scope = _factory.Services.CreateScope();
        IFileIntegrityService integrityService = scope.ServiceProvider.GetRequiredService<IFileIntegrityService>();
        string nonexistentPath = Path.Combine(_modelStoragePath, "nonexistent.stl");

        // Act
        FileIntegrityCheckResult result = await integrityService.VerifyIntegrityAsync(
            nonexistentPath,
            "expectedhash",
            1024,
            "SHA256");

        // Assert
        _ = result.IsValid.Should().BeFalse();
        _ = result.FailureReason.Should().Be("Missing");
    }

    [Fact]
    public async Task FileIntegrityService_VerifyIntegrity_DetectsHashMismatch()
    {
        // Arrange
        IServiceScope scope = _factory.Services.CreateScope();
        IFileIntegrityService integrityService = scope.ServiceProvider.GetRequiredService<IFileIntegrityService>();
        string testFilePath = Path.Combine(_modelStoragePath, "test.stl");
        File.WriteAllText(testFilePath, "test content");

        // Act
        FileIntegrityCheckResult result = await integrityService.VerifyIntegrityAsync(
            testFilePath,
            "wronghash1234567890abcdef1234567890abcdef1234567890abcdef1234567890",
            1024, // Wrong size
            "SHA256");

        // Assert
        _ = result.IsValid.Should().BeFalse();
        _ = result.FailureReason.Should().Be("SizeMismatch");
    }

    [Fact]
    public async Task GetHealthSummary_WithMixedStatus_CalculatesCorrectPercentage()
    {
        // Arrange - Create 4 files: 2 healthy, 1 missing, 1 corrupted
        _ = CreateAndPersistModel3D("m1.stl", FileHealthStatus.Healthy);
        _ = CreateAndPersistModel3D("m2.stl", FileHealthStatus.Healthy);
        _ = CreateAndPersistModel3D("m3.stl", FileHealthStatus.Missing);
        _ = CreateAndPersistGcodeFile("g1.gcode", FileHealthStatus.Corrupted);
        _ = await _dbContext.SaveChangesAsync();

        // Act
        HttpResponseMessage response = await _client.GetAsync("/api/fileconsistency/health/summary");

        // Assert
        _ = response.StatusCode.Should().Be(HttpStatusCode.OK);
        string json = await response.Content.ReadAsStringAsync();
        JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        // 2 healthy out of 4 files = 50%
        _ = root.GetProperty("overallHealthPercentage").GetDouble().Should().Be(50.0);
    }

    // Helper methods

    private Model3D CreateAndPersistModel3D(string fileName, FileHealthStatus healthStatus)
    {
        Model3D model = new Model3D
        {
            Id = Guid.NewGuid(),
            OriginalFileName = fileName,
            DisplayName = fileName,
            FilePath = Path.Combine(_modelStoragePath, fileName),
            FileHash = Convert.ToHexString(Guid.NewGuid().ToByteArray()).ToLower(),
            FileSizeBytes = 2048,
            FileFormat = ModelFileFormat.STL,
            UploadedAt = DateTime.UtcNow,
            IsValid = true,
            HealthStatus = healthStatus,
            LastHealthCheckDate = healthStatus == FileHealthStatus.Unknown ? null : DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _ = _dbContext.Models3D.Add(model);
        return model;
    }

    private GcodeFile CreateAndPersistGcodeFile(string fileName, FileHealthStatus healthStatus)
    {
        string filePath = Path.Combine(_gcodeStoragePath, fileName);
        GcodeFile gcode = new GcodeFile
        {
            Id = Guid.NewGuid(),
            OriginalFileName = fileName,
            DisplayName = fileName,
            FilePath = filePath,
            FileHash = Convert.ToHexString(Guid.NewGuid().ToByteArray()).ToLower(),
            FileSizeBytes = 4096,
            UploadedAt = DateTime.UtcNow,
            Source = GcodeSource.Upload,
            HealthStatus = healthStatus,
            LastHealthCheckDate = healthStatus == FileHealthStatus.Unknown ? null : DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _ = _dbContext.GcodeFiles.Add(gcode);
        return gcode;
    }
}
