using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Settings;
using Farm.Web.Api.Services.Artifacts;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Farm.Slicer.Module.Tests.Services;

[Collection(IntegrationTestCollection.Name)]
public class ArtifactCleanupServiceTests(CustomWebApplicationFactory factory) : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory = factory;

    [Fact]
    public async Task ScanAndCleanupAsync_DryRunMode_OnlyLogsWithoutDeleting()
    {
        // Arrange
        using IServiceScope scope = _factory.Services.CreateScope();
        IDbContextFactory<SlicerDbContext> dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<SlicerDbContext>>();
        IArtifactsRepository artifactsRepo = scope.ServiceProvider.GetRequiredService<IArtifactsRepository>();
        IWebHostEnvironment env = scope.ServiceProvider.GetRequiredService<IWebHostEnvironment>();
        ILogger<ArtifactCleanupService> logger = scope.ServiceProvider.GetRequiredService<ILogger<ArtifactCleanupService>>();

        ArtifactStorageSettings settings = new ArtifactStorageSettings
        {
            MaxAgeDays = 1, // 1 day age limit
            MaxTotalBytes = null,
            EnableCleanupDryRun = true, // Dry-run mode
            RootPath = "artifacts"
        };

        ArtifactCleanupService cleanupService = new ArtifactCleanupService(artifactsRepo, Options.Create(settings), env, logger);

        // Create an old artifact (2 days ago)
        using (SlicerDbContext db = dbFactory.CreateDbContext())
        {
            Artifact oldArtifact = new Artifact
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
            _ = db.Set<Artifact>().Add(oldArtifact);
            _ = await db.SaveChangesAsync();
        }

        // Act
        int deletedCount = await cleanupService.ScanAndCleanupAsync(CancellationToken.None);

        // Assert
        _ = deletedCount.Should().Be(1, "one artifact should be identified for cleanup");

        // Verify artifact still exists (dry-run didn't delete)
        using (SlicerDbContext db = dbFactory.CreateDbContext())
        {
            Artifact? stillExists = await db.Set<Artifact>().FirstOrDefaultAsync(a => a.RelativePath == "2023/01/01/test.gcode");
            _ = stillExists.Should().NotBeNull("dry-run mode should not delete artifacts");
        }
    }

    [Fact]
    public async Task ScanAndCleanupAsync_AgeBasedCleanup_DeletesOldArtifacts()
    {
        // Arrange
        using IServiceScope scope = _factory.Services.CreateScope();
        IDbContextFactory<SlicerDbContext> dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<SlicerDbContext>>();
        IArtifactsRepository artifactsRepo = scope.ServiceProvider.GetRequiredService<IArtifactsRepository>();
        IWebHostEnvironment env = scope.ServiceProvider.GetRequiredService<IWebHostEnvironment>();
        ILogger<ArtifactCleanupService> logger = scope.ServiceProvider.GetRequiredService<ILogger<ArtifactCleanupService>>();

        ArtifactStorageSettings settings = new ArtifactStorageSettings
        {
            MaxAgeDays = 1, // 1 day age limit
            MaxTotalBytes = null,
            EnableCleanupDryRun = false, // Actual deletion
            RootPath = "artifacts"
        };

        ArtifactCleanupService cleanupService = new ArtifactCleanupService(artifactsRepo, Options.Create(settings), env, logger);

        // Create an old artifact (2 days ago) and a new one (today)
        using (SlicerDbContext db = dbFactory.CreateDbContext())
        {
            Artifact oldArtifact = new Artifact
            {
                Id = Guid.NewGuid(),
                JobId = Guid.NewGuid(),
                WorkerId = null,
                Kind = "gcode",
                FileName = "old.gcode",
                RelativePath = "2023/01/01/old.gcode",
                SizeBytes = 1000,
                Sha256 = "abc123",
                CreatedAt = DateTime.UtcNow.AddDays(-2)
            };

            Artifact newArtifact = new Artifact
            {
                Id = Guid.NewGuid(),
                JobId = Guid.NewGuid(),
                WorkerId = null,
                Kind = "gcode",
                FileName = "new.gcode",
                RelativePath = "2023/12/31/new.gcode",
                SizeBytes = 1000,
                Sha256 = "def456",
                CreatedAt = DateTime.UtcNow
            };
            _ = db.Set<Artifact>().Add(oldArtifact);
            _ = db.Set<Artifact>().Add(newArtifact);
            _ = await db.SaveChangesAsync();
        }

        // Act
        int deletedCount = await cleanupService.ScanAndCleanupAsync(CancellationToken.None);

        // Assert
        _ = deletedCount.Should().Be(1, "one old artifact should be deleted");

        // Verify old artifact is gone, new one remains
        using (SlicerDbContext db = dbFactory.CreateDbContext())
        {
            Artifact? oldStillExists = await db.Set<Artifact>().FirstOrDefaultAsync(a => a.RelativePath == "2023/01/01/old.gcode");
            _ = oldStillExists.Should().BeNull("old artifact should be deleted");

            Artifact? newStillExists = await db.Set<Artifact>().FirstOrDefaultAsync(a => a.RelativePath == "2023/12/31/new.gcode");
            _ = newStillExists.Should().NotBeNull("new artifact should remain");
        }
    }
}
