using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos.PrintQueue;
using Farm.Infrastructure.Services.Interfaces;
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
            .Setup(x => x.DispatchJobAsync(_jobId.ToString(), "operator", It.IsAny<CancellationToken>()))
            .Returns(async (string _, string _, CancellationToken ct) =>
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
        JobDispatchService service = CreateService(management, broadcaster, SpoolmanWithFilament(73));
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
            .Setup(x => x.DispatchJobAsync(_jobId.ToString(), "operator", It.IsAny<CancellationToken>()))
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
        Mock<IDispatchScorer>? scorer = null)
        => new(
            (scorer ?? new Mock<IDispatchScorer>(MockBehavior.Strict)).Object,
            management.Object,
            spoolman.Object,
            _db,
            NullLogger<JobDispatchService>.Instance,
            broadcaster.Object);

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

    private DispatchScore Score(bool eliminated = false)
        => new(
            _printerId,
            "Dispatch Printer",
            90,
            new Dictionary<string, FactorScore>(),
            eliminated,
            eliminated ? ["Printer unavailable"] : []);

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
        PrintJob job = new()
        {
            Id = _jobId,
            Name = "Dispatch Job",
            Status = PrintJobStatus.Queued,
            QueuePosition = 1,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            QueuedAt = DateTime.UtcNow,
        };

        _db.AddRange(manufacturer, model, printer, job);
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
