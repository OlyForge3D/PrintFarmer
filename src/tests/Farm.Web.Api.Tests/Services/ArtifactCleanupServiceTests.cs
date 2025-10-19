using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Settings;
using Farm.Web.Api.Services.Artifacts;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Farm.Web.Api.Tests.Services;

public class ArtifactCleanupServiceTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public ArtifactCleanupServiceTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task ScanAndCleanupAsync_DryRunMode_OnlyLogsWithoutDeleting()
    {
        // Arrange
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Farm.Infrastructure.Data.AppDbContext>();
        var env = scope.ServiceProvider.GetRequiredService<IWebHostEnvironment>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<ArtifactCleanupService>>();

        var settings = new ArtifactStorageSettings
        {
            MaxAgeDays = 1, // 1 day age limit
            MaxTotalBytes = null,
            EnableCleanupDryRun = true, // Dry-run mode
            RootPath = "artifacts"
        };

        var cleanupService = new ArtifactCleanupService(db, Options.Create(settings), env, logger);

        // Create an old artifact (2 days ago)
        var oldArtifact = new Artifact
        {
            Id = Guid.NewGuid(),
            JobId = Guid.NewGuid(),
            WorkerId = null,
            Kind = "gcode",
            FileName = "test.gcode",
            RelativePath = "2023/01/01/test.gcode",
            SizeBytes = 1000,
            Sha256 = "abc123",
            CreatedAt = DateTime.UtcNow.AddDays(-2)
        };
        db.Artifacts.Add(oldArtifact);
        await db.SaveChangesAsync();

        // Act
        int deletedCount = await cleanupService.ScanAndCleanupAsync(CancellationToken.None);

        // Assert
        deletedCount.Should().Be(1, "one artifact should be identified for cleanup");

        // Verify artifact still exists (dry-run didn't delete)
        var stillExists = await db.Artifacts.FindAsync(oldArtifact.Id);
        stillExists.Should().NotBeNull("dry-run mode should not delete artifacts");
    }

    [Fact]
    public async Task ScanAndCleanupAsync_AgeBasedCleanup_DeletesOldArtifacts()
    {
        // Arrange
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Farm.Infrastructure.Data.AppDbContext>();
        var env = scope.ServiceProvider.GetRequiredService<IWebHostEnvironment>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<ArtifactCleanupService>>();

        var settings = new ArtifactStorageSettings
        {
            MaxAgeDays = 1, // 1 day age limit
            MaxTotalBytes = null,
            EnableCleanupDryRun = false, // Actual deletion
            RootPath = "artifacts"
        };

        var cleanupService = new ArtifactCleanupService(db, Options.Create(settings), env, logger);

        // Create an old artifact (2 days ago) and a new one (today)
        var oldArtifact = new Artifact
        {
            Id = Guid.NewGuid(),
            JobId = Guid.NewGuid(),
            WorkerId = null,
            Kind = "gcode",
            FileName = "old.gcode",
            RelativePath = "2023/01/01/old.gcode",
            SizeBytes = 1000,
            Sha256 = "old123",
            CreatedAt = DateTime.UtcNow.AddDays(-2)
        };
        var newArtifact = new Artifact
        {
            Id = Guid.NewGuid(),
            JobId = Guid.NewGuid(),
            WorkerId = null,
            Kind = "gcode",
            FileName = "new.gcode",
            RelativePath = "2023/01/01/new.gcode",
            SizeBytes = 1000,
            Sha256 = "new123",
            CreatedAt = DateTime.UtcNow
        };
        db.Artifacts.Add(oldArtifact);
        db.Artifacts.Add(newArtifact);
        await db.SaveChangesAsync();

        // Act
        int deletedCount = await cleanupService.ScanAndCleanupAsync(CancellationToken.None);

        // Assert
        deletedCount.Should().Be(1, "one old artifact should be deleted");

        // Verify old artifact deleted, new artifact remains
        var oldStillExists = await db.Artifacts.FindAsync(oldArtifact.Id);
        oldStillExists.Should().BeNull("old artifact should be deleted");

        var newStillExists = await db.Artifacts.FindAsync(newArtifact.Id);
        newStillExists.Should().NotBeNull("new artifact should remain");
    }

    [Fact]
    public async Task ScanAndCleanupAsync_SizeBasedCleanup_DeletesOldestWhenOverThreshold()
    {
        // Arrange
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Farm.Infrastructure.Data.AppDbContext>();
        var env = scope.ServiceProvider.GetRequiredService<IWebHostEnvironment>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<ArtifactCleanupService>>();

        var settings = new ArtifactStorageSettings
        {
            MaxAgeDays = null, // Disable age-based cleanup
            MaxTotalBytes = 1500, // 1.5KB limit (will trigger cleanup with 3x 1KB artifacts)
            EnableCleanupDryRun = false,
            RootPath = "artifacts"
        };

        var cleanupService = new ArtifactCleanupService(db, Options.Create(settings), env, logger);

        // Create 3 artifacts (total 3KB > 1.5KB threshold)
        var artifact1 = new Artifact
        {
            Id = Guid.NewGuid(),
            JobId = Guid.NewGuid(),
            Kind = "gcode",
            FileName = "1.gcode",
            RelativePath = "2023/01/01/1.gcode",
            SizeBytes = 1000,
            Sha256 = "hash1",
            CreatedAt = DateTime.UtcNow.AddDays(-3) // Oldest
        };
        var artifact2 = new Artifact
        {
            Id = Guid.NewGuid(),
            JobId = Guid.NewGuid(),
            Kind = "gcode",
            FileName = "2.gcode",
            RelativePath = "2023/01/01/2.gcode",
            SizeBytes = 1000,
            Sha256 = "hash2",
            CreatedAt = DateTime.UtcNow.AddDays(-2) // Middle
        };
        var artifact3 = new Artifact
        {
            Id = Guid.NewGuid(),
            JobId = Guid.NewGuid(),
            Kind = "gcode",
            FileName = "3.gcode",
            RelativePath = "2023/01/01/3.gcode",
            SizeBytes = 1000,
            Sha256 = "hash3",
            CreatedAt = DateTime.UtcNow.AddDays(-1) // Newest
        };
        db.Artifacts.Add(artifact1);
        db.Artifacts.Add(artifact2);
        db.Artifacts.Add(artifact3);
        await db.SaveChangesAsync();

        // Act
        int deletedCount = await cleanupService.ScanAndCleanupAsync(CancellationToken.None);

        // Assert
        deletedCount.Should().BeGreaterOrEqualTo(1, "at least one artifact should be deleted to reduce size");

        // Verify at least artifact1 (oldest) was deleted
        var artifact1Exists = await db.Artifacts.FindAsync(artifact1.Id);
        artifact1Exists.Should().BeNull("oldest artifact should be deleted first");

        // Verify total size is now under threshold
        var totalSize = db.Artifacts.Sum(a => a.SizeBytes);
        totalSize.Should().BeLessOrEqualTo(settings.MaxTotalBytes.Value, "total size should be under threshold");
    }

    [Fact]
    public async Task ScanAndCleanupAsync_NoCandidates_ReturnsZero()
    {
        // Arrange
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Farm.Infrastructure.Data.AppDbContext>();
        var env = scope.ServiceProvider.GetRequiredService<IWebHostEnvironment>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<ArtifactCleanupService>>();

        var settings = new ArtifactStorageSettings
        {
            MaxAgeDays = 365, // Very long retention
            MaxTotalBytes = 1_000_000_000, // 1GB limit (very high)
            EnableCleanupDryRun = false,
            RootPath = "artifacts"
        };

        var cleanupService = new ArtifactCleanupService(db, Options.Create(settings), env, logger);

        // Create a recent small artifact
        var artifact = new Artifact
        {
            Id = Guid.NewGuid(),
            JobId = Guid.NewGuid(),
            Kind = "gcode",
            FileName = "test.gcode",
            RelativePath = "2023/01/01/test.gcode",
            SizeBytes = 1000,
            Sha256 = "hash",
            CreatedAt = DateTime.UtcNow
        };
        db.Artifacts.Add(artifact);
        await db.SaveChangesAsync();

        // Act
        int deletedCount = await cleanupService.ScanAndCleanupAsync(CancellationToken.None);

        // Assert
        deletedCount.Should().Be(0, "no artifacts should be eligible for cleanup");

        // Verify artifact still exists
        var stillExists = await db.Artifacts.FindAsync(artifact.Id);
        stillExists.Should().NotBeNull("artifact should not be deleted");
    }
}
