using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Security;
using Farm.Infrastructure.Services.SignalR;
using Farm.Modules.Calibration.Services.Calibration;
using Farm.Modules.Calibration.Services.Gcode;
using Farm.Modules.Gcode.Services.Gcode;
using Farm.Slicer.Module.Data;
using Farm.Slicer.Module.Domain;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
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
    private readonly Mock<IHubContext<PrinterHub>> _hub = new(MockBehavior.Strict);
    private readonly Mock<IHubClients> _hubClients = new(MockBehavior.Strict);
    private readonly Mock<IClientProxy> _clientProxy = new(MockBehavior.Strict);
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
        _hub.SetupGet(context => context.Clients).Returns(_hubClients.Object);
        _hubClients
            .Setup(clients => clients.Group(It.IsAny<string>()))
            .Returns(_clientProxy.Object);
        _clientProxy
            .Setup(proxy => proxy.SendCoreAsync(
                It.IsAny<string>(),
                It.IsAny<object?[]>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _services = serviceCollection.BuildServiceProvider();
        _service = new SliceLibraryPromotionService(
            _services.GetRequiredService<IServiceScopeFactory>(),
            _hub.Object,
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
                    VerifyNoLibraryUpdate();
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
        VerifyLibraryUpdate(job.UserId, Times.Once());
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
    public async Task PromoteMissingAsync_ExistingDurableArtifactPromotion_DoesNotPromoteAgain()
    {
        (_, Artifact artifact) = await SeedArtifactAsync(
            SliceJobStatus.Completed,
            SlicerArtifactKinds.Gcode);
        await SeedPromotedGcodeAsync(artifact.JobId, artifact.Id);
        SetupOperationalCapability();

        int count = await _service.PromoteMissingAsync(CancellationToken.None);

        count.Should().Be(0);
        _promoter.Verify(
            service => service.PromoteAsync(
                It.IsAny<GcodeArtifactPromotionRequest>(),
                It.IsAny<CalibrationActor>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        VerifyNoLibraryUpdate();
    }

    [Fact]
    public async Task PromoteMissingAsync_TerminalFailuresFillOldestWindow_PromotesHealthyNewerArtifact()
    {
        (SliceJob job, IReadOnlyList<Artifact> artifacts) = await SeedCompletedArtifactsAsync(
            count: 201,
            createdAtUtc: DateTime.UtcNow.AddDays(-1));
        await SeedCheckpointsAsync(artifacts.Take(200), GcodePromotionState.Failed);
        Artifact healthy = artifacts[^1];
        SetupOperationalCapability();
        _promoter
            .Setup(service => service.PromoteAsync(
                It.Is<GcodeArtifactPromotionRequest>(request => request.SourceArtifactId == healthy.Id),
                It.Is<CalibrationActor>(actor => actor.UserId == job.UserId),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateSuccess(job, healthy));

        int count = await _service.PromoteMissingAsync(CancellationToken.None);

        count.Should().Be(1);
        _promoter.Verify(
            service => service.PromoteAsync(
                It.IsAny<GcodeArtifactPromotionRequest>(),
                It.IsAny<CalibrationActor>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
        VerifyLibraryUpdate(job.UserId, Times.Once());
    }

    [Fact]
    public async Task PromoteMissingAsync_ClearedFailedCheckpoint_RetriesArtifact()
    {
        (SliceJob job, Artifact artifact) = await SeedArtifactAsync(
            SliceJobStatus.Completed,
            SlicerArtifactKinds.Gcode);
        await SeedCheckpointsAsync([artifact], GcodePromotionState.Pending);
        SetupOperationalCapability();
        _promoter
            .Setup(service => service.PromoteAsync(
                It.Is<GcodeArtifactPromotionRequest>(request => request.SourceArtifactId == artifact.Id),
                It.IsAny<CalibrationActor>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateSuccess(job, artifact));

        int count = await _service.PromoteMissingAsync(CancellationToken.None);

        count.Should().Be(1);
        VerifyLibraryUpdate(job.UserId, Times.Once());
    }

    [Fact]
    public async Task PromoteMissingAsync_TwoArtifactsForOneJob_PromotesBoth()
    {
        (SliceJob job, IReadOnlyList<Artifact> artifacts) = await SeedCompletedArtifactsAsync(2);
        SetupOperationalCapability();
        foreach (Artifact artifact in artifacts)
        {
            _promoter
                .Setup(service => service.PromoteAsync(
                    It.Is<GcodeArtifactPromotionRequest>(request =>
                        request.SourceArtifactId == artifact.Id &&
                        request.OperationId == SliceLibraryPromotionService.BuildOperationId(job.Id, artifact.Id)),
                    It.Is<CalibrationActor>(actor => actor.UserId == job.UserId),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(CreateSuccess(job, artifact));
        }

        int count = await _service.PromoteMissingAsync(CancellationToken.None);

        count.Should().Be(2);
        _promoter.Verify(
            service => service.PromoteAsync(
                It.IsAny<GcodeArtifactPromotionRequest>(),
                It.IsAny<CalibrationActor>(),
                It.IsAny<CancellationToken>()),
            Times.Exactly(2));
        VerifyLibraryUpdate(job.UserId, Times.Exactly(2));
    }

    [Fact]
    public async Task PromoteMissingAsync_OneArtifactAlreadyDurable_PromotesSiblingOnly()
    {
        (SliceJob job, IReadOnlyList<Artifact> artifacts) = await SeedCompletedArtifactsAsync(2);
        await SeedPromotedGcodeAsync(job.Id, artifacts[0].Id);
        SetupOperationalCapability();
        _promoter
            .Setup(service => service.PromoteAsync(
                It.Is<GcodeArtifactPromotionRequest>(request => request.SourceArtifactId == artifacts[1].Id),
                It.Is<CalibrationActor>(actor => actor.UserId == job.UserId),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateSuccess(job, artifacts[1]));

        int count = await _service.PromoteMissingAsync(CancellationToken.None);

        count.Should().Be(1);
        _promoter.Verify(
            service => service.PromoteAsync(
                It.IsAny<GcodeArtifactPromotionRequest>(),
                It.IsAny<CalibrationActor>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
        VerifyLibraryUpdate(job.UserId, Times.Once());
    }

    [Fact]
    public async Task PromoteMissingAsync_PromotionFailure_DoesNotNotifyLibraryUpdate()
    {
        _ = await SeedArtifactAsync(SliceJobStatus.Completed, SlicerArtifactKinds.Gcode);
        SetupOperationalCapability();
        _promoter
            .Setup(service => service.PromoteAsync(
                It.IsAny<GcodeArtifactPromotionRequest>(),
                It.IsAny<CalibrationActor>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(CalibrationApiResult<GcodePromotionDto>.Failure(
                StatusCodes.Status409Conflict,
                "promotion_failed"));

        int count = await _service.PromoteMissingAsync(CancellationToken.None);

        count.Should().Be(0);
        VerifyNoLibraryUpdate();
    }

    [Fact]
    public async Task PromoteMissingAsync_ReplayedPromotion_DoesNotNotifyLibraryUpdate()
    {
        (SliceJob job, Artifact artifact) = await SeedArtifactAsync(
            SliceJobStatus.Completed,
            SlicerArtifactKinds.Gcode);
        SetupOperationalCapability();
        _promoter
            .Setup(service => service.PromoteAsync(
                It.IsAny<GcodeArtifactPromotionRequest>(),
                It.IsAny<CalibrationActor>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateSuccess(job, artifact, replayed: true));

        int count = await _service.PromoteMissingAsync(CancellationToken.None);

        count.Should().Be(1);
        VerifyNoLibraryUpdate();
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
        VerifyNoLibraryUpdate();
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
        VerifyNoLibraryUpdate();
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

    private async Task<(SliceJob Job, IReadOnlyList<Artifact> Artifacts)> SeedCompletedArtifactsAsync(
        int count,
        DateTime? createdAtUtc = null)
    {
        DateTime createdAt = createdAtUtc ?? DateTime.UtcNow;
        var job = new SliceJob
        {
            Id = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            ModelFileUrl = "/api/3d-models/test",
            ModelFileName = "test.3mf",
            Status = SliceJobStatus.Completed,
            QueuedAt = createdAt.AddMinutes(-1),
            CompletedAt = createdAt,
        };
        List<Artifact> artifacts = Enumerable.Range(0, count)
            .Select(index => new Artifact
            {
                Id = Guid.NewGuid(),
                JobId = job.Id,
                WorkerId = Guid.NewGuid(),
                Kind = SlicerArtifactKinds.Gcode,
                FileName = $"result-{index}.gcode",
                RelativePath = $"artifact/result-{index}",
                ContentType = "text/x.gcode",
                SizeBytes = 123 + index,
                Sha256 = index.ToString("X64"),
                CreatedAt = createdAt.AddSeconds(index),
            })
            .ToList();

        await using AsyncServiceScope scope = _services.CreateAsyncScope();
        SlicerDbContext slicerDb = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();
        _ = slicerDb.SliceJobs.Add(job);
        slicerDb.Artifacts.AddRange(artifacts);
        _ = await slicerDb.SaveChangesAsync();
        return (job, artifacts);
    }

    private async Task SeedCheckpointsAsync(
        IEnumerable<Artifact> artifacts,
        GcodePromotionState state)
    {
        DateTime now = DateTime.UtcNow;
        await using AsyncServiceScope scope = _services.CreateAsyncScope();
        AppDbContext appDb = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        foreach (Artifact artifact in artifacts)
        {
            _ = appDb.GcodePromotionCheckpoints.Add(new GcodePromotionCheckpoint
            {
                Id = Guid.NewGuid(),
                OwnerUserId = Guid.NewGuid(),
                OperationScope = $"test:{artifact.Id:N}",
                OperationId = $"test:{artifact.Id:N}",
                RequestSha256 = artifact.Sha256,
                SourceArtifactId = artifact.Id,
                SourceSliceJobId = artifact.JobId,
                SourceWorkerId = artifact.WorkerId,
                SourceContentSha256 = artifact.Sha256,
                SourceSizeBytes = artifact.SizeBytes,
                GcodeFileId = Guid.NewGuid(),
                State = state,
                FailureCode = state == GcodePromotionState.Failed ? "invalid_gcode" : null,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                CompletedAtUtc = state == GcodePromotionState.Failed ? now : null,
            });
        }

        _ = await appDb.SaveChangesAsync();
    }

    private async Task SeedPromotedGcodeAsync(Guid sourceSliceJobId, Guid sourceArtifactId)
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
            SourceArtifactId = sourceArtifactId,
            UploadedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        });
        _ = await appDb.SaveChangesAsync();
    }

    private static CalibrationApiResult<GcodePromotionDto> CreateSuccess(
        SliceJob job,
        Artifact artifact,
        bool replayed = false) =>
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
            StatusCodes.Status201Created,
            replayed);

    private void VerifyLibraryUpdate(Guid ownerUserId, Times times)
    {
        _hubClients.Verify(
            clients => clients.Group(AuthorizedHubGroups.User(ownerUserId)),
            times);
        _clientProxy.Verify(
            proxy => proxy.SendCoreAsync(
                "gcodelibraryupdated",
                It.Is<object?[]>(arguments => arguments.Length == 0),
                It.IsAny<CancellationToken>()),
            times);
    }

    private void VerifyNoLibraryUpdate()
    {
        _hubClients.Verify(
            clients => clients.Group(It.IsAny<string>()),
            Times.Never);
        _clientProxy.Verify(
            proxy => proxy.SendCoreAsync(
                It.IsAny<string>(),
                It.IsAny<object?[]>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
