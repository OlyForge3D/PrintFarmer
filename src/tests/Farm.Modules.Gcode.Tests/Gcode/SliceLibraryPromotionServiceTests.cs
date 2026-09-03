using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Modules.Calibration.Services.Calibration;
using Farm.Modules.Calibration.Services.Gcode;
using Farm.Modules.Gcode.Services.Gcode;
using Farm.Slicer.Module.Data;
using Farm.Slicer.Module.Domain;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Farm.Modules.Gcode.Tests.Gcode;

/// <summary>Tests automatic promotion discovery for completed user slice jobs.</summary>
public sealed class SliceLibraryPromotionServiceTests : IAsyncDisposable
{
    private readonly ServiceProvider _services;
    private readonly Mock<IGcodeArtifactPromoter> _promoter = new(MockBehavior.Strict);
    private readonly SliceLibraryPromotionService _service;

    public SliceLibraryPromotionServiceTests()
    {
        string slicerDatabaseName = $"slice-library-slicer-{Guid.NewGuid():N}";
        string appDatabaseName = $"slice-library-app-{Guid.NewGuid():N}";
        var serviceCollection = new ServiceCollection();
        _ = serviceCollection.AddDbContext<SlicerDbContext>(options =>
            options.UseInMemoryDatabase(slicerDatabaseName));
        _ = serviceCollection.AddDbContext<AppDbContext>(options =>
            options.UseInMemoryDatabase(appDatabaseName));
        _ = serviceCollection.AddScoped(_ => _promoter.Object);
        _services = serviceCollection.BuildServiceProvider();
        _service = new SliceLibraryPromotionService(
            _services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<SliceLibraryPromotionService>.Instance);
    }

    [Fact]
    public async Task PromoteMissingAsync_CompletedGcode_UsesOwnerAndStableArtifactIdentity()
    {
        (SliceJob job, Artifact artifact) = await SeedArtifactAsync(
            SliceJobStatus.Completed,
            SlicerArtifactKinds.Gcode);
        GcodeArtifactPromotionRequest? capturedRequest = null;
        CalibrationActor? capturedActor = null;
        SetupOperationalCapability();
        _promoter
            .Setup(service => service.PromoteAsync(
                It.IsAny<GcodeArtifactPromotionRequest>(),
                It.IsAny<CalibrationActor>(),
                It.IsAny<CancellationToken>()))
            .Callback<GcodeArtifactPromotionRequest, CalibrationActor, CancellationToken>(
                (request, actor, _) =>
                {
                    capturedRequest = request;
                    capturedActor = actor;
                })
            .ReturnsAsync(CreateSuccess(job, artifact));

        int count = await _service.PromoteMissingAsync(CancellationToken.None);

        count.Should().Be(1);
        capturedRequest.Should().BeEquivalentTo(new
        {
            OperationId = SliceLibraryPromotionService.BuildOperationId(job.Id, artifact.Id),
            SourceArtifactId = artifact.Id,
            SourceSliceJobId = job.Id,
            SourceWorkerId = artifact.WorkerId,
            ExpectedSha256 = artifact.Sha256,
            ExpectedSizeBytes = artifact.SizeBytes,
        });
        capturedActor.Should().BeEquivalentTo(new
        {
            UserId = job.UserId,
            Subject = $"slice-library-promotion:{job.Id:N}",
            IsFarmAdmin = false,
        });
    }

    [Fact]
    public async Task PromoteMissingAsync_HistoricalCompletedJob_UsesSamePromotionPath()
    {
        (SliceJob job, Artifact artifact) = await SeedArtifactAsync(
            SliceJobStatus.Completed,
            SlicerArtifactKinds.Gcode,
            createdAtUtc: DateTime.UtcNow.AddYears(-1));
        SetupOperationalCapability();
        _promoter
            .Setup(service => service.PromoteAsync(
                It.Is<GcodeArtifactPromotionRequest>(request =>
                    request.SourceSliceJobId == job.Id &&
                    request.SourceArtifactId == artifact.Id),
                It.Is<CalibrationActor>(actor => actor.UserId == job.UserId && !actor.IsFarmAdmin),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateSuccess(job, artifact));

        int count = await _service.PromoteMissingAsync(CancellationToken.None);

        count.Should().Be(1);
    }

    [Fact]
    public async Task PromoteMissingAsync_ExistingDurableJobPromotion_DoesNotPromoteAgain()
    {
        (SliceJob job, _) = await SeedArtifactAsync(
            SliceJobStatus.Completed,
            SlicerArtifactKinds.Gcode);
        await SeedPromotedGcodeAsync(job.Id);
        SetupOperationalCapability();

        int count = await _service.PromoteMissingAsync(CancellationToken.None);

        count.Should().Be(0);
        _promoter.Verify(
            service => service.PromoteAsync(
                It.IsAny<GcodeArtifactPromotionRequest>(),
                It.IsAny<CalibrationActor>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task PromoteMissingAsync_UnavailableCapability_IdlesWithoutScanning()
    {
        _ = await SeedArtifactAsync(SliceJobStatus.Completed, SlicerArtifactKinds.Gcode);
        _promoter
            .Setup(service => service.GetCapabilityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GcodePromotionCapabilityDto
            {
                Operational = false,
                ArtifactSourceAvailable = false,
                LibraryStorageWritable = true,
                CheckpointStoreAvailable = true,
                ReconcilerHealthy = true,
                UnavailableCode = "promotion_dependency_unavailable",
            });

        int count = await _service.PromoteMissingAsync(CancellationToken.None);

        count.Should().Be(0);
        _promoter.Verify(
            service => service.PromoteAsync(
                It.IsAny<GcodeArtifactPromotionRequest>(),
                It.IsAny<CalibrationActor>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Theory]
    [InlineData(SliceJobStatus.Processing, SlicerArtifactKinds.Gcode)]
    [InlineData(SliceJobStatus.Completed, SlicerArtifactKinds.Log)]
    public async Task PromoteMissingAsync_IneligibleSource_DoesNotPromote(
        string status,
        string artifactKind)
    {
        _ = await SeedArtifactAsync(status, artifactKind);
        SetupOperationalCapability();

        int count = await _service.PromoteMissingAsync(CancellationToken.None);

        count.Should().Be(0);
        _promoter.Verify(
            service => service.PromoteAsync(
                It.IsAny<GcodeArtifactPromotionRequest>(),
                It.IsAny<CalibrationActor>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    public async ValueTask DisposeAsync()
    {
        _service.Dispose();
        await _services.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    private void SetupOperationalCapability()
    {
        _promoter
            .Setup(service => service.GetCapabilityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GcodePromotionCapabilityDto
            {
                Operational = true,
                ArtifactSourceAvailable = true,
                LibraryStorageWritable = true,
                CheckpointStoreAvailable = true,
                ReconcilerHealthy = true,
            });
    }

    private async Task<(SliceJob Job, Artifact Artifact)> SeedArtifactAsync(
        string status,
        string artifactKind,
        DateTime? createdAtUtc = null)
    {
        DateTime createdAt = createdAtUtc ?? DateTime.UtcNow;
        var job = new SliceJob
        {
            Id = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            ModelFileUrl = "/api/3d-models/test",
            ModelFileName = "test.3mf",
            Status = status,
            QueuedAt = createdAt.AddMinutes(-1),
            CompletedAt = status == SliceJobStatus.Completed ? createdAt : null,
        };
        var artifact = new Artifact
        {
            Id = Guid.NewGuid(),
            JobId = job.Id,
            WorkerId = Guid.NewGuid(),
            Kind = artifactKind,
            FileName = artifactKind == SlicerArtifactKinds.Gcode ? "result.gcode" : "slice.log",
            RelativePath = "artifact/result",
            ContentType = artifactKind == SlicerArtifactKinds.Gcode
                ? "text/x.gcode"
                : "text/plain",
            SizeBytes = 123,
            Sha256 = new string('A', 64),
            CreatedAt = createdAt,
        };

        await using AsyncServiceScope scope = _services.CreateAsyncScope();
        SlicerDbContext slicerDb = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();
        _ = slicerDb.SliceJobs.Add(job);
        _ = slicerDb.Artifacts.Add(artifact);
        _ = await slicerDb.SaveChangesAsync();
        return (job, artifact);
    }

    private async Task SeedPromotedGcodeAsync(Guid sourceSliceJobId)
    {
        DateTime now = DateTime.UtcNow;
        await using AsyncServiceScope scope = _services.CreateAsyncScope();
        AppDbContext appDb = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        _ = appDb.GcodeFiles.Add(new GcodeFile
        {
            Id = Guid.NewGuid(),
            Name = "result.gcode",
            FileName = "result.gcode",
            FilePath = "/gcode-library/result.gcode",
            FolderId = Guid.NewGuid(),
            FileSizeBytes = 123,
            FileHash = new string('A', 64),
            Source = GcodeSource.Upload,
            SourceSliceJobId = sourceSliceJobId,
            UploadedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        });
        _ = await appDb.SaveChangesAsync();
    }

    private static CalibrationApiResult<GcodePromotionDto> CreateSuccess(
        SliceJob job,
        Artifact artifact) =>
        CalibrationApiResult<GcodePromotionDto>.Success(
            new GcodePromotionDto
            {
                OperationId = SliceLibraryPromotionService.BuildOperationId(job.Id, artifact.Id),
                SourceArtifactId = artifact.Id,
                SourceSliceJobId = job.Id,
                GcodeFileId = Guid.NewGuid(),
                ContentSha256 = artifact.Sha256,
                SizeBytes = artifact.SizeBytes,
                Status = "Completed",
                SourceAcknowledged = true,
            },
            StatusCodes.Status201Created);
}
