using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos.PrintQueue;
using Farm.Infrastructure.Services.Interfaces;
using Farm.Infrastructure.Services.OperatorFeatures;
using Farm.Infrastructure.Services.PartsInventory;
using Farm.Infrastructure.Services.Queue;
using Farm.Infrastructure.Services.Queue.Dispatch;
using Farm.Infrastructure.Services.Spoolman;
using Farm.Web.Api.Tests.TestInfrastructure;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Dispatch;

public sealed class JobDispatchServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly FailingSaveChangesInterceptor _saveInterceptor = new();
    private readonly AppDbContext _db;
    private readonly Guid _jobId = Guid.NewGuid();
    private readonly Guid _printerId = Guid.NewGuid();

    public JobDispatchServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        TestSqlitePragmaEnforcer.EnsureForeignKeysEnabled(_connection);

        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(_saveInterceptor)
            .Options;
        _db = new AppDbContext(options);
        _db.Database.EnsureCreated();
        SeedDispatchData();
    }

    [Fact]
    public async Task DispatchJobAsync_DownstreamFailure_PersistsAssignmentAndSpoolBeforeSingleBroadcast()
    {
        bool broadcastObserved = false;
        Mock<IPrintJobManagementService> management = new(MockBehavior.Strict);
        management
            .Setup(x => x.DispatchJobAsync(_jobId.ToString(), "operator", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (string _, string _, string? _, CancellationToken ct) =>
            {
                broadcastObserved.Should().BeTrue();
                PrintJob job = await _db.PrintJobs.SingleAsync(x => x.Id == _jobId, ct);
                job.AssignedPrinterId.Should().Be(_printerId);
                job.SpoolmanSpoolId.Should().Be(41);
                job.SpoolmanFilamentId.Should().Be(73);
                job.Status = PrintJobStatus.Assigned;
                job.FailureReason = "Upload failed";
                await _db.SaveChangesAsync(ct);
                return new QueuedPrintJobDto
                {
                    Id = _jobId.ToString(),
                    AssignedPrinterId = _printerId.ToString(),
                    Status = nameof(PrintJobStatus.Assigned),
                    FailureReason = job.FailureReason,
                };
            });
        Mock<IFilamentCoverageBroadcaster> broadcaster = Broadcaster(
            () => broadcastObserved = true);
        JobDispatchService service = CreateService(management, broadcaster, SpoolmanWithFilament(73));

        QueuedPrintJobDto result = await service.DispatchJobAsync(
            _jobId,
            _printerId,
            "operator",
            Score(),
            CancellationToken.None);

        _db.ChangeTracker.Clear();
        PrintJob persisted = await _db.PrintJobs.SingleAsync(x => x.Id == _jobId);
        persisted.AssignedPrinterId.Should().Be(_printerId);
        persisted.SpoolmanSpoolId.Should().Be(41);
        persisted.SpoolmanFilamentId.Should().Be(73);
        persisted.Status.Should().Be(PrintJobStatus.Assigned);
        result.Status.Should().Be(nameof(PrintJobStatus.Assigned));
        broadcaster.Verify(
            x => x.BroadcastPrinterChangedAsync(
                _printerId,
                FilamentCoverageChangeReasons.JobAssignment,
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task DispatchJobAsync_AssignmentPersistenceFails_DoesNotBroadcastOrDispatch()
    {
        Mock<IPrintJobManagementService> management = new(MockBehavior.Strict);
        Mock<IFilamentCoverageBroadcaster> broadcaster = new(MockBehavior.Strict);
        var service = new JobDispatchService(
            new Mock<IDispatchScorer>(MockBehavior.Strict).Object,
            management.Object,
            SpoolmanWithFilament(73).Object,
            _db,
            NullLogger<JobDispatchService>.Instance,
            broadcaster.Object,
            CreateRealSnapshotService(),
            resourceAuthorization: null,
            positionAllocator: CreateAllocatorMock().Object);
        _saveInterceptor.FailNextSave = true;

        Func<Task> act = () => service.DispatchJobAsync(
            _jobId,
            _printerId,
            "operator",
            Score(),
            CancellationToken.None);

        await act.Should().ThrowAsync<DbUpdateException>();
        broadcaster.VerifyNoOtherCalls();
        management.VerifyNoOtherCalls();
        _db.ChangeTracker.Clear();
        PrintJob persisted = await _db.PrintJobs.SingleAsync(x => x.Id == _jobId);
        persisted.AssignedPrinterId.Should().BeNull();
        persisted.SpoolmanSpoolId.Should().BeNull();
        persisted.SpoolmanFilamentId.Should().BeNull();
        (await _db.DispatchLogs.CountAsync()).Should().Be(0);
        (await _db.PrintJobPartOutputSnapshots.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task DispatchJobAsync_FirstDispatchCapturesSnapshot_RetryDoesNotOverwrite()
    {
        Mock<IPrintJobManagementService> management = new(MockBehavior.Strict);
        management
            .Setup(x => x.DispatchJobAsync(_jobId.ToString(), "operator", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueuedPrintJobDto
            {
                Id = _jobId.ToString(),
                AssignedPrinterId = _printerId.ToString(),
                Status = nameof(PrintJobStatus.Assigned),
            });
        Mock<IFilamentCoverageBroadcaster> broadcaster = Broadcaster();
        PartOutputSnapshotService snapshots = CreateRealSnapshotService();
        var service = new JobDispatchService(
            new Mock<IDispatchScorer>(MockBehavior.Strict).Object,
            management.Object,
            SpoolmanWithFilament(73).Object,
            _db,
            NullLogger<JobDispatchService>.Instance,
            broadcaster.Object,
            snapshots,
            resourceAuthorization: null,
            positionAllocator: CreateAllocatorMock().Object);

        _ = await service.DispatchJobAsync(
            _jobId,
            _printerId,
            "operator",
            Score(),
            CancellationToken.None);
        PartOutputMapping mapping = await _db.PartOutputMappings.SingleAsync();
        mapping.Quantity = 9;
        _ = await _db.SaveChangesAsync();
        _ = await service.DispatchJobAsync(
            _jobId,
            _printerId,
            "operator",
            Score(),
            CancellationToken.None);

        _db.ChangeTracker.Clear();
        PrintJobPartOutputSnapshot snapshot =
            await _db.PrintJobPartOutputSnapshots.SingleAsync();
        snapshot.QuantityPerPrint.Should().Be(2);
        (await _db.DispatchLogs.CountAsync()).Should().Be(2);
        management.Verify(
            value => value.DispatchJobAsync(
                _jobId.ToString(),
                "operator",
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task DispatchJobWithFilamentOverrideAsync_AssignsAndDispatchesOnlyReviewedJob()
    {
        var dispatchState = new PrinterDispatchState
        {
            PrinterId = _printerId,
            AutoDispatchState = AutoDispatchState.PendingReady,
        };
        _db.PrinterDispatchStates.Add(dispatchState);
        await _db.SaveChangesAsync();
        PrintJob reviewedJob = await _db.PrintJobs.SingleAsync(job => job.Id == _jobId);
        string reviewedJobEtag = Convert.ToBase64String(reviewedJob.RowVersion ?? []);
        byte[] reviewedDispatchVersion = dispatchState.RowVersion?.ToArray() ?? [];
        var authorization = new FilamentOverrideAuthorization(
            "Incompatible",
            "Material mismatch: loaded PLA, job requires PETG",
            "PLA",
            "PETG",
            500,
            100);

        Mock<IPrintJobManagementService> management = new(MockBehavior.Strict);
        management
            .Setup(service => service.DispatchReviewedJobAsync(
                _jobId.ToString(),
                "operator",
                It.IsAny<string>(),
                It.Is<byte[]>(version => version.Length > 0),
                authorization,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueuedPrintJobDto
            {
                Id = _jobId.ToString(),
                AssignedPrinterId = _printerId.ToString(),
                Status = nameof(PrintJobStatus.Printing),
            });
        Mock<IFilamentCoverageBroadcaster> broadcaster = Broadcaster();
        Mock<IDispatchScorer> scorer = new(MockBehavior.Strict);
        scorer
            .Setup(service => service.ScorePrintersForJobAsync(
                _jobId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([Score(materialMatchFailure: true)]);
        JobDispatchService service = CreateService(
            management,
            broadcaster,
            SpoolmanWithFilament(73),
            scorer);

        QueuedPrintJobDto result =
            await service.DispatchJobWithFilamentOverrideAsync(
                _jobId,
                _printerId,
                "operator",
                reviewedJobEtag,
                reviewedDispatchVersion,
                authorization);

        result.Status.Should().Be(nameof(PrintJobStatus.Printing));
        _db.ChangeTracker.Clear();
        (await _db.PrintJobs.SingleAsync(job => job.Id == _jobId))
            .AssignedPrinterId.Should().Be(_printerId);
        management.VerifyAll();
    }

    [Fact]
    public async Task DispatchJobWithFilamentOverrideAsync_NonFilamentHardGateFails_DoesNotDispatch()
    {
        var dispatchState = new PrinterDispatchState
        {
            PrinterId = _printerId,
            AutoDispatchState = AutoDispatchState.PendingReady,
        };
        _db.PrinterDispatchStates.Add(dispatchState);
        await _db.SaveChangesAsync();
        PrintJob reviewedJob = await _db.PrintJobs.SingleAsync(job => job.Id == _jobId);
        var authorization = new FilamentOverrideAuthorization(
            "Incompatible",
            "Material mismatch: loaded PLA, job requires PETG",
            "PLA",
            "PETG",
            500,
            100);
        Mock<IDispatchScorer> scorer = new(MockBehavior.Strict);
        scorer
            .Setup(service => service.ScorePrintersForJobAsync(
                _jobId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([Score(materialMatchFailure: true, nonFilamentFailure: true)]);
        Mock<IPrintJobManagementService> management = new(MockBehavior.Strict);
        Mock<IFilamentCoverageBroadcaster> broadcaster = new(MockBehavior.Strict);
        JobDispatchService service = CreateService(
            management,
            broadcaster,
            SpoolmanWithFilament(73),
            scorer);

        Func<Task> act = () => service.DispatchJobWithFilamentOverrideAsync(
            _jobId,
            _printerId,
            "operator",
            Convert.ToBase64String(reviewedJob.RowVersion ?? []),
            dispatchState.RowVersion ?? [],
            authorization);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*enclosure*");
        management.VerifyNoOtherCalls();
        broadcaster.VerifyNoOtherCalls();
        _db.ChangeTracker.Clear();
        (await _db.PrintJobs.SingleAsync(job => job.Id == _jobId))
            .AssignedPrinterId.Should().BeNull();
    }

    [Fact]
    public async Task DispatchJobAsync_EliminatedPrinter_DoesNotBroadcastOrPersistAssignment()
    {
        Mock<IPrintJobManagementService> management = new(MockBehavior.Strict);
        Mock<IFilamentCoverageBroadcaster> broadcaster = new(MockBehavior.Strict);
        JobDispatchService service = CreateService(management, broadcaster, SpoolmanWithFilament(73));
        DispatchScore eliminated = Score(eliminated: true);

        Func<Task> act = () => service.DispatchJobAsync(
            _jobId,
            _printerId,
            "operator",
            eliminated,
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        broadcaster.VerifyNoOtherCalls();
        management.VerifyNoOtherCalls();
        _db.ChangeTracker.Clear();
        (await _db.PrintJobs.SingleAsync(x => x.Id == _jobId)).AssignedPrinterId.Should().BeNull();
    }

    [Fact]
    public async Task DispatchJobAsync_SlowDownstream_BroadcastsBeforeDispatchCompletes()
    {
        TaskCompletionSource broadcastObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource downstreamEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<QueuedPrintJobDto> releaseDownstream =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        Mock<IDispatchScorer> scorer = new(MockBehavior.Strict);
        scorer.Setup(x => x.ScorePrintersForJobAsync(_jobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([Score()]);
        Mock<IFilamentCoverageBroadcaster> broadcaster = new(MockBehavior.Strict);
        broadcaster
            .Setup(x => x.BroadcastPrinterChangedAsync(
                _printerId,
                FilamentCoverageChangeReasons.JobAssignment,
                It.IsAny<CancellationToken>()))
            .Callback(() => broadcastObserved.TrySetResult())
            .Returns(Task.CompletedTask);
        Mock<IPrintJobManagementService> management = new(MockBehavior.Strict);
        management
            .Setup(x => x.DispatchJobAsync(_jobId.ToString(), "operator", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                downstreamEntered.TrySetResult();
                return releaseDownstream.Task;
            });
        JobDispatchService service = CreateService(
            management,
            broadcaster,
            SpoolmanWithFilament(73),
            scorer);

        Task<QueuedPrintJobDto> dispatch = service.DispatchJobAsync(
            _jobId,
            _printerId,
            "operator",
            CancellationToken.None);

        await broadcastObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await downstreamEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        dispatch.IsCompleted.Should().BeFalse(
            "coverage invalidation must not wait for downstream upload/start completion");

        releaseDownstream.SetResult(new QueuedPrintJobDto
        {
            Id = _jobId.ToString(),
            AssignedPrinterId = _printerId.ToString(),
            Status = nameof(PrintJobStatus.Assigned),
        });
        _ = await dispatch;
        broadcaster.VerifyAll();
        management.VerifyAll();
        scorer.VerifyAll();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private JobDispatchService CreateService(
        Mock<IPrintJobManagementService> management,
        Mock<IFilamentCoverageBroadcaster> broadcaster,
        Mock<ISpoolmanService> spoolman,
        Mock<IDispatchScorer>? scorer = null,
        Mock<IPartOutputSnapshotService>? snapshots = null)
    {
        snapshots ??= new Mock<IPartOutputSnapshotService>();
        snapshots
            .Setup(value => value.CaptureJobSnapshotIfAbsentAsync(
                It.IsAny<PrintJob>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        return new(
            (scorer ?? new Mock<IDispatchScorer>(MockBehavior.Strict)).Object,
            management.Object,
            spoolman.Object,
            _db,
            NullLogger<JobDispatchService>.Instance,
            broadcaster.Object,
            snapshots.Object,
            resourceAuthorization: null,
            positionAllocator: CreateAllocatorMock().Object);
    }

    private static Mock<IQueuePositionAllocator> CreateAllocatorMock()
    {
        var allocator = new Mock<IQueuePositionAllocator>();
        allocator
            .Setup(value => value.AllocateAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(2);
        return allocator;
    }

    private Mock<IFilamentCoverageBroadcaster> Broadcaster(Action? onBroadcast = null)
    {
        Mock<IFilamentCoverageBroadcaster> broadcaster = new(MockBehavior.Strict);
        broadcaster
            .Setup(x => x.BroadcastPrinterChangedAsync(
                _printerId,
                FilamentCoverageChangeReasons.JobAssignment,
                It.IsAny<CancellationToken>()))
            .Callback(() => onBroadcast?.Invoke())
            .Returns(Task.CompletedTask);
        return broadcaster;
    }

    private PartOutputSnapshotService CreateRealSnapshotService()
    {
        var gate = new Mock<IOperatorFeatureGate>(MockBehavior.Strict);
        gate.Setup(value => value.IsEnabled(OperatorFeature.PrintedPartsInventory)).Returns(true);
        gate.Setup(value => value.IsEnabledAsync(OperatorFeature.PrintedPartsInventory, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        return new PartOutputSnapshotService(_db, gate.Object);
    }

    private Mock<ISpoolmanService> SpoolmanWithFilament(int filamentId)
    {
        Mock<ISpoolmanService> spoolman = new(MockBehavior.Strict);
        spoolman.Setup(x => x.GetSpoolByIdAsync(41, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SpoolmanSpoolDto(
                Id: 41,
                Name: "PLA spool",
                Material: "PLA",
                RemainingWeightG: 500,
                ColorHex: null,
                InUse: true,
                FilamentId: filamentId));
        return spoolman;
    }

    private DispatchScore Score(
        bool eliminated = false,
        bool materialMatchFailure = false,
        bool nonFilamentFailure = false)
    {
        Dictionary<string, FactorScore> breakdown = [];
        List<string> reasons = [];
        if (materialMatchFailure)
        {
            breakdown["MaterialMatch"] = new(
                "Material Match",
                0,
                100,
                0,
                true,
                "Material mismatch");
            reasons.Add("Material mismatch");
        }

        if (nonFilamentFailure)
        {
            breakdown["Enclosure"] = new(
                "Enclosure",
                0,
                80,
                0,
                true,
                "Printer lacks required enclosure");
            reasons.Add("Printer lacks required enclosure");
        }

        bool isEliminated = eliminated || materialMatchFailure || nonFilamentFailure;
        if (eliminated && reasons.Count == 0)
        {
            reasons.Add("Printer unavailable");
        }

        return new(
            _printerId,
            "Dispatch Printer",
            isEliminated ? 0 : 90,
            breakdown,
            isEliminated,
            reasons);
    }

    private void SeedDispatchData()
    {
        Manufacturer manufacturer = new()
        {
            Id = Guid.NewGuid(),
            Name = "Dispatch Manufacturer",
        };
        PrinterModel model = new()
        {
            Id = Guid.NewGuid(),
            ManufacturerId = manufacturer.Id,
            Name = "Dispatch Model",
        };
        Printer printer = new()
        {
            Id = _printerId,
            Name = "Dispatch Printer",
            ServerUrl = "http://dispatch-printer",
            BackendPort = 80,
            ManufacturerId = manufacturer.Id,
            ModelId = model.Id,
            CurrentSpoolId = 41,
            IsEnabled = true,
            IsAvailable = true,
        };
        var folder = new FolderNode
        {
            Id = Guid.NewGuid(),
            Path = "/dispatch",
            FolderType = "gcode",
        };
        var gcode = new GcodeFile
        {
            Id = Guid.NewGuid(),
            Name = "dispatch.gcode",
            FileName = "dispatch.gcode",
            FilePath = "/dispatch",
            FolderId = folder.Id,
            FileHash = "dispatch",
            FileSizeBytes = 1,
            UploadedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        var bin = new Bin
        {
            Id = Guid.NewGuid(),
            Code = "BIN-DISPATCH",
            Name = "Dispatch",
            IsActive = true,
        };
        var part = new PartInventory
        {
            Id = Guid.NewGuid(),
            Sku = "SKU-DISPATCH",
            Name = "Dispatch",
            DefaultBinId = bin.Id,
            IsActive = true,
        };
        var mapping = new PartOutputMapping
        {
            Id = Guid.NewGuid(),
            PartInventoryId = part.Id,
            GcodeFileId = gcode.Id,
            Quantity = 2,
        };
        PrintJob job = new()
        {
            Id = _jobId,
            Name = "Dispatch Job",
            GcodeFileId = gcode.Id,
            Status = PrintJobStatus.Queued,
            QueuePosition = 1,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            QueuedAt = DateTime.UtcNow,
        };

        _db.AddRange(manufacturer, model, printer, folder, gcode, bin, part, mapping, job);
        _db.SaveChanges();
    }

    private sealed class FailingSaveChangesInterceptor : SaveChangesInterceptor
    {
        public bool FailNextSave { get; set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (FailNextSave)
            {
                FailNextSave = false;
                throw new DbUpdateException("Assignment persistence failed.");
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}
