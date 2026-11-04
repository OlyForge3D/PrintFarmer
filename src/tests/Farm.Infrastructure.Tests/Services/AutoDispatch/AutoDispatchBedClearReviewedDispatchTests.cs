using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos.PrintQueue;
using Farm.Infrastructure.Services.AutoDispatch;
using Farm.Infrastructure.Services.Interfaces;
using Farm.Infrastructure.Services.PartsInventory;
using Farm.Infrastructure.Services.Queue;
using Farm.Infrastructure.Services.Queue.Dispatch;
using Farm.Infrastructure.Services.SignalR;
using Farm.Infrastructure.Services.Spoolman;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Infrastructure.Tests.Services.AutoDispatch;

/// <summary>
/// Exercises the bed-clear confirmation handoff through the real ready gate,
/// <see cref="JobDispatchService"/>, and <see cref="DispatchScorer"/>. Only the
/// printer-facing <see cref="IPrintJobManagementService"/> boundary is faked, so the
/// scorer's PendingReady availability gate is evaluated exactly as in production.
/// </summary>
public sealed class AutoDispatchBedClearReviewedDispatchTests : IDisposable
{
    private const int SpoolId = 42;
    private const string ActorSubject = "operator-bed-clear";

    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly Mock<IPrintJobManagementService> _management = new(MockBehavior.Strict);
    private readonly Mock<ISpoolmanService> _spoolman = new();

    public AutoDispatchBedClearReviewedDispatchTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        TestSqlitePragmaEnforcer.EnsureForeignKeysEnabled(_connection);
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;
        _db = new AppDbContext(options);
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MarkReady_CompatibleFilament_DispatchesReviewedJobOnceWhilePendingReady(bool failEvidence)
    {
        (Printer printer, PrintJob job) = await SeedPendingReadyAsync(remainingGrams: 900);
        if (failEvidence)
        {
            await FailEvidenceInsertsAsync();
        }

        SetupAcceptedDispatch(job, printer, expectOverride: false);
        AutoDispatchService service = CreateService();

        AutoDispatchReadyResult result = await service.MarkReadyAsync(
            printer.Id,
            printer.DispatchState!.RowVersion!.ToArray());

        result.DispatchInitiated.Should().BeTrue();
        result.RequiresFilamentOverride.Should().BeFalse();
        result.FilamentOverrideApplied.Should().BeFalse();
        VerifyDispatchedOnce(job);
        (await _db.DispatchLogs.CountAsync()).Should().Be(1);
        if (failEvidence)
        {
            (await _db.BedClearEvaluations.CountAsync()).Should().Be(0);
            _db.ChangeTracker.Entries<BedClearEvaluation>().Should().BeEmpty();
            return;
        }

        BedClearEvaluation sample = await _db.BedClearEvaluations.SingleAsync();
        sample.PrinterId.Should().Be(printer.Id);
        sample.PrintJobId.Should().Be(job.Id);
        sample.State.Should().Be(BedClearEvaluationState.Pending);
        sample.Verdict.Should().Be(BedClearDetectorVerdict.Unknown);
        sample.Score.Should().BeNull();
        sample.IsActiveReference.Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MarkReady_EmptyQueue_EvidenceFailureDoesNotPreventReady(bool failEvidence)
    {
        (Printer printer, PrintJob job) = await SeedPendingReadyAsync(remainingGrams: 900);
        _db.PrintJobs.Remove(job);
        await _db.SaveChangesAsync();
        if (failEvidence)
        {
            await FailEvidenceInsertsAsync();
        }

        AutoDispatchReadyResult result = await CreateService().MarkReadyAsync(
            printer.Id,
            printer.DispatchState!.RowVersion!.ToArray());

        result.DispatchInitiated.Should().BeFalse();
        result.NextJob.Should().BeNull();
        _db.ChangeTracker.Clear();
        PrinterDispatchState state = await _db.PrinterDispatchStates.SingleAsync();
        state.AutoDispatchState.Should().Be(AutoDispatchState.None);
        state.BedPreConfirmed.Should().BeFalse();
        (await _db.BedClearEvaluations.CountAsync()).Should().Be(failEvidence ? 0 : 1);
        _management.VerifyNoOtherCalls();
    }

    private Task<int> FailEvidenceInsertsAsync() => _db.Database.ExecuteSqlRawAsync(
        """
        CREATE TRIGGER fail_bed_clear_evidence BEFORE INSERT ON BedClearEvaluations
        BEGIN SELECT RAISE(FAIL, 'Injected shadow evidence failure'); END;
        """);

    [Fact]
    public async Task MarkReady_InsufficientFilamentOverrideConfirmed_DispatchesReviewedJobOnce()
    {
        (Printer printer, PrintJob job) = await SeedPendingReadyAsync(remainingGrams: 0);
        SetupAcceptedDispatch(job, printer, expectOverride: true);
        AutoDispatchService service = CreateService();
        byte[] reviewedDispatchVersion = printer.DispatchState!.RowVersion!.ToArray();

        AutoDispatchReadyResult challenge = await service.MarkReadyAsync(
            printer.Id,
            reviewedDispatchVersion);

        challenge.DispatchInitiated.Should().BeFalse();
        challenge.RequiresFilamentOverride.Should().BeTrue();
        challenge.FilamentCheck!.Sufficient.Should().BeFalse();
        challenge.FilamentCheck.Outcome.Should().NotBe(FilamentCheckOutcome.Compatible);
        _management.VerifyNoOtherCalls();

        AutoDispatchReadyResult result = await ConfirmOverrideAsync(
            service,
            printer,
            job,
            reviewedDispatchVersion,
            challenge);

        result.DispatchInitiated.Should().BeTrue();
        result.FilamentOverrideApplied.Should().BeTrue();
        VerifyDispatchedOnce(job);

        // Replaying the same consent after dispatch must not dispatch twice.
        Func<Task> replay = () => ConfirmOverrideAsync(
            service,
            printer,
            job,
            reviewedDispatchVersion,
            challenge);
        await replay.Should().ThrowAsync<QueueRevisionConflictException>();
        VerifyDispatchedOnce(job);
    }

    [Theory]
    [InlineData("maintenance")]
    [InlineData("not available")]
    public async Task MarkReady_OverrideConfirmed_OtherAvailabilityGateStillRejects(string condition)
    {
        (Printer printer, PrintJob job) = await SeedPendingReadyAsync(remainingGrams: 0);
        AutoDispatchService service = CreateService();
        byte[] reviewedDispatchVersion = printer.DispatchState!.RowVersion!.ToArray();
        AutoDispatchReadyResult challenge = await service.MarkReadyAsync(
            printer.Id,
            reviewedDispatchVersion);
        if (condition == "maintenance")
        {
            printer.InMaintenance = true;
        }
        else
        {
            printer.IsAvailable = false;
        }

        await _db.SaveChangesAsync();

        Func<Task> act = () => ConfirmOverrideAsync(
            service,
            printer,
            job,
            reviewedDispatchVersion,
            challenge);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain(condition)
            .And.NotContain("waiting for bed clear confirmation");
        _management.VerifyNoOtherCalls();
        (await _db.DispatchLogs.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task MarkReady_OverrideConfirmed_NonFilamentCompatibilityGateStillRejects()
    {
        (Printer printer, PrintJob job) = await SeedPendingReadyAsync(remainingGrams: 0);
        AutoDispatchService service = CreateService();
        byte[] reviewedDispatchVersion = printer.DispatchState!.RowVersion!.ToArray();
        AutoDispatchReadyResult challenge = await service.MarkReadyAsync(
            printer.Id,
            reviewedDispatchVersion);
        job.RequiredNozzleDiameter = 0.8m;
        _db.Toolheads.Add(new Toolhead
        {
            Id = Guid.NewGuid(),
            PrinterId = printer.Id,
            Name = "T0",
            Index = 0,
            IsPrimary = true,
            NozzleDiameter = 0.4,
            CurrentMaterial = "ASA",
        });
        await _db.SaveChangesAsync();

        Func<Task> act = () => ConfirmOverrideAsync(
            service,
            printer,
            job,
            reviewedDispatchVersion,
            challenge);

        (await act.Should().ThrowAsync<Exception>())
            .Which.Message.Should().NotContain("waiting for bed clear confirmation");
        _management.VerifyNoOtherCalls();
        (await _db.DispatchLogs.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task MarkReady_OverrideWithStaleDispatchState_IsRejectedWithoutDispatch()
    {
        (Printer printer, PrintJob job) = await SeedPendingReadyAsync(remainingGrams: 0);
        AutoDispatchService service = CreateService();
        byte[] reviewedDispatchVersion = printer.DispatchState!.RowVersion!.ToArray();
        AutoDispatchReadyResult challenge = await service.MarkReadyAsync(
            printer.Id,
            reviewedDispatchVersion);
        printer.DispatchState.QueueRevision++;
        await _db.SaveChangesAsync();

        Func<Task> act = () => ConfirmOverrideAsync(
            service,
            printer,
            job,
            reviewedDispatchVersion,
            challenge);

        await act.Should().ThrowAsync<Exception>();
        _management.VerifyNoOtherCalls();
        (await _db.DispatchLogs.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task MarkReady_OverrideAfterFilamentConditionsChange_RequiresNewReview()
    {
        (Printer printer, PrintJob job) = await SeedPendingReadyAsync(remainingGrams: 0);
        AutoDispatchService service = CreateService();
        byte[] reviewedDispatchVersion = printer.DispatchState!.RowVersion!.ToArray();
        AutoDispatchReadyResult challenge = await service.MarkReadyAsync(
            printer.Id,
            reviewedDispatchVersion);
        SetupSpool(remainingGrams: 40);

        AutoDispatchReadyResult result = await ConfirmOverrideAsync(
            service,
            printer,
            job,
            reviewedDispatchVersion,
            challenge);

        result.DispatchInitiated.Should().BeFalse();
        result.FilamentCheckChanged.Should().BeTrue();
        result.FilamentCheckETag.Should().NotBe(challenge.FilamentCheckETag);
        _management.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task PendingReadyGate_StillBlocksDispatchOutsideBedClearConfirmation()
    {
        (Printer printer, PrintJob job) = await SeedPendingReadyAsync(remainingGrams: 0);
        DispatchScorer scorer = new(_db, NullLogger<DispatchScorer>.Instance);
        JobDispatchService jobDispatch = CreateJobDispatchService(scorer);

        List<DispatchScore> scores = await scorer.ScorePrintersForJobAsync(job.Id);
        DispatchScore score = scores.Single(candidate => candidate.PrinterId == printer.Id);
        score.Eliminated.Should().BeTrue();
        score.EliminationReasons.Should().Contain(reason =>
            reason.Contains("waiting for bed clear confirmation", StringComparison.Ordinal));

        Func<Task> manual = () => jobDispatch.DispatchJobAsync(job.Id, printer.Id, ActorSubject);
        await manual.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*waiting for bed clear confirmation*");

        // A filament override alone is not authority to bypass the bed-clear gate.
        FilamentOverrideAuthorization filamentOnly = new(
            nameof(FilamentCheckOutcome.Incompatible),
            "Insufficient filament",
            "ASA",
            "ASA",
            0,
            83.5,
            new byte[32],
            printer.RowVersion!.ToArray(),
            OverrideApproved: true);
        Func<Task> overrideOnly = () => jobDispatch.DispatchJobWithFilamentOverrideAsync(
            job.Id,
            printer.Id,
            ActorSubject,
            Convert.ToBase64String(job.RowVersion!),
            printer.DispatchState!.RowVersion!.ToArray(),
            filamentOnly);
        await overrideOnly.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*waiting for bed clear confirmation*");
        _management.VerifyNoOtherCalls();
    }

    private Task<AutoDispatchReadyResult> ConfirmOverrideAsync(
        AutoDispatchService service,
        Printer printer,
        PrintJob job,
        byte[] reviewedDispatchVersion,
        AutoDispatchReadyResult challenge) =>
        service.MarkReadyAsync(
            printer.Id,
            reviewedDispatchVersion,
            confirmFilamentOverride: true,
            actorSubject: ActorSubject,
            expectedOverrideJobVersion: job.RowVersion,
            expectedFilamentCheckVersion: Convert.FromBase64String(challenge.FilamentCheckETag!));

    private void SetupAcceptedDispatch(PrintJob job, Printer printer, bool expectOverride) =>
        _management
            .Setup(service => service.DispatchReviewedJobAsync(
                job.Id.ToString(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<byte[]>(),
                It.Is<FilamentOverrideAuthorization?>(authorization =>
                    authorization != null && authorization.OverrideApproved == expectOverride),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueuedPrintJobDto
            {
                Id = job.Id.ToString(),
                AssignedPrinterId = printer.Id.ToString(),
                Status = nameof(PrintJobStatus.Starting),
                DispatchResult = new DispatchAttemptResultDto
                {
                    Outcome = DispatchAttemptOutcome.Accepted,
                },
            });

    private void VerifyDispatchedOnce(PrintJob job) =>
        _management.Verify(
            service => service.DispatchReviewedJobAsync(
                job.Id.ToString(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<byte[]>(),
                It.IsAny<FilamentOverrideAuthorization?>(),
                It.IsAny<CancellationToken>()),
            Times.Once);

    private AutoDispatchService CreateService()
    {
        DispatchScorer scorer = new(_db, NullLogger<DispatchScorer>.Instance);
        Mock<IClientProxy> proxy = new();
        proxy.Setup(value => value.SendCoreAsync(
                It.IsAny<string>(),
                It.IsAny<object?[]>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        Mock<IHubClients> clients = new();
        clients.Setup(value => value.Group(It.IsAny<string>())).Returns(proxy.Object);
        Mock<IHubContext<PrinterHub>> hub = new();
        hub.Setup(value => value.Clients).Returns(clients.Object);
        return new AutoDispatchService(
            _db,
            hub.Object,
            NullLogger<AutoDispatchService>.Instance,
            spoolmanService: _spoolman.Object,
            jobDispatchService: CreateJobDispatchService(scorer));
    }

    private JobDispatchService CreateJobDispatchService(IDispatchScorer scorer)
    {
        Mock<IPartOutputSnapshotService> snapshots = new();
        snapshots
            .Setup(value => value.CaptureJobSnapshotIfAbsentAsync(
                It.IsAny<PrintJob>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        return new JobDispatchService(
            scorer,
            _management.Object,
            _spoolman.Object,
            _db,
            NullLogger<JobDispatchService>.Instance,
            Mock.Of<IFilamentCoverageBroadcaster>(),
            snapshots.Object);
    }

    private void SetupSpool(double remainingGrams) =>
        _spoolman
            .Setup(service => service.GetSpoolByIdAsync(SpoolId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SpoolmanSpoolDto(SpoolId, "ASA spool", "ASA", remainingGrams, null, true));

    private async Task<(Printer Printer, PrintJob Job)> SeedPendingReadyAsync(double remainingGrams)
    {
        SetupSpool(remainingGrams);
        Manufacturer manufacturer = new() { Id = Guid.NewGuid(), Name = "Bed Clear Manufacturer" };
        PrinterModel model = new()
        {
            Id = Guid.NewGuid(),
            Name = "Bed Clear Model",
            ManufacturerId = manufacturer.Id,
        };
        Printer printer = new()
        {
            Id = Guid.NewGuid(),
            Name = "qp4-1",
            ServerUrl = $"http://bed-clear-{Guid.NewGuid():N}.local",
            BackendPort = 7125,
            Backend = (int)PrinterBackend.Moonraker,
            ManufacturerId = manufacturer.Id,
            ModelId = model.Id,
            AutoDispatchEnabled = true,
            IsEnabled = true,
            IsAvailable = true,
            CurrentMaterial = "ASA",
            CurrentSpoolId = SpoolId,
        };
        _db.AddRange(manufacturer, model, printer);
        await _db.SaveChangesAsync();
        printer.DispatchState = new PrinterDispatchState
        {
            PrinterId = printer.Id,
            AutoDispatchState = AutoDispatchState.PendingReady,
        };
        PrintJob job = new()
        {
            Id = Guid.NewGuid(),
            Name = "queued-asa-job",
            AssignedPrinterId = printer.Id,
            Status = PrintJobStatus.Queued,
            QueuePosition = 1,
            RequiredMaterialType = "ASA",
            EstimatedFilamentUsage = 83.5,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            QueuedAt = DateTime.UtcNow,
        };
        _db.PrintJobs.Add(job);
        await _db.SaveChangesAsync();
        return (printer, job);
    }
}
