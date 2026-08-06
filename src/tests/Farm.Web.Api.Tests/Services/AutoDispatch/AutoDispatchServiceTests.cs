using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos.PrintQueue;
using Farm.Infrastructure.Security;
using Farm.Infrastructure.Services.AutoDispatch;
using Farm.Infrastructure.Services.Interfaces;
using Farm.Infrastructure.Services.Queue;
using Farm.Infrastructure.Services.Queue.Dispatch;
using Farm.Infrastructure.Services.SignalR;
using Farm.Infrastructure.Services.Spoolman;
using Farm.Infrastructure.Services.Webhooks;
using Farm.Web.Api.Tests.TestInfrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services.AutoDispatch;

public sealed class AutoDispatchServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;

    public AutoDispatchServiceTests()
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

    [Fact]
    public async Task MarkPreClearAsync_WhenQueuedJobsAlreadyExist_NotifiesDispatchTrigger()
    {
        Printer printer = await CreatePrinterAsync();
        printer.CurrentSpoolId = 42;
        PrintJob queuedJob = await CreateQueuedJobAsync(printer, "queued-job", queuePosition: 1);
        queuedJob.RequiredMaterialType = "PLA";
        queuedJob.EstimatedFilamentUsage = 10;
        await _db.SaveChangesAsync();

        var (hubContext, _) = CreateHubContextMockWithProxy();
        Mock<IAutoDispatchTrigger> dispatchTrigger = new();
        Mock<ISpoolmanService> spoolmanService = new();
        spoolmanService
            .Setup(service => service.GetSpoolByIdAsync(42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SpoolmanSpoolDto(42, "PLA spool", "PLA", 1000, null, false));
        AutoDispatchService service = new(
            _db,
            hubContext.Object,
            NullLogger<AutoDispatchService>.Instance,
            spoolmanService: spoolmanService.Object,
            dispatchTrigger: dispatchTrigger.Object);

        AutoDispatchStatusDto status = await service.MarkPreClearAsync(printer.Id);

        status.BedPreConfirmed.Should().BeTrue();
        dispatchTrigger.Verify(trigger => trigger.NotifyJobQueued(printer.Id), Times.Once);
    }

    [Fact]
    public async Task MarkPreClearAsync_WhenQueuedJobFilamentIsUnknown_RequiresReadyConfirmation()
    {
        Printer printer = await CreatePrinterAsync();
        await CreateQueuedJobAsync(printer, "queued-job", queuePosition: 1);

        var (hubContext, _) = CreateHubContextMockWithProxy();
        Mock<IAutoDispatchTrigger> dispatchTrigger = new();
        Mock<IWebhookService> webhookService = new();
        AutoDispatchService service = new(
            _db,
            hubContext.Object,
            NullLogger<AutoDispatchService>.Instance,
            webhookService: webhookService.Object,
            dispatchTrigger: dispatchTrigger.Object);

        AutoDispatchStatusDto status = await service.MarkPreClearAsync(printer.Id);

        status.BedPreConfirmed.Should().BeFalse();
        status.State.Should().Be(nameof(AutoDispatchState.PendingReady));
        dispatchTrigger.Verify(
            trigger => trigger.NotifyJobQueued(It.IsAny<Guid>()),
            Times.Never);
        webhookService.Verify(
            service => service.Enqueue(
                "printer.bed_pre_confirmed",
                It.IsAny<object>()),
            Times.Never);
    }

    [Fact]
    public async Task MarkPreClearAsync_WhenPrinterHasPausedJob_RejectsBedPreClear()
    {
        Printer printer = await CreatePrinterAsync();
        await CreateJobAsync(printer, "paused-job", PrintJobStatus.Paused);
        await CreateQueuedJobAsync(printer, "queued-job", queuePosition: 1);

        var (hubContext, _) = CreateHubContextMockWithProxy();
        Mock<IAutoDispatchTrigger> dispatchTrigger = new();
        AutoDispatchService service = new(
            _db,
            hubContext.Object,
            NullLogger<AutoDispatchService>.Instance,
            dispatchTrigger: dispatchTrigger.Object);

        Func<Task> act = async () => _ = await service.MarkPreClearAsync(printer.Id);

        await act.Should().ThrowAsync<InvalidOperationException>();
        (await _db.Printers.Include(p => p.DispatchState).SingleAsync(p => p.Id == printer.Id))
            .DispatchState.Should().BeNull();
        dispatchTrigger.Verify(trigger => trigger.NotifyJobQueued(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task TransitionToPendingReadyAsync_WhenQueuedJobsExistAndBedIsNotPreCleared_SetsPendingReadyAndBroadcastsStatus()
    {
        Printer printer = await CreatePrinterAsync();
        await CreateQueuedJobAsync(printer, "queued-job-1", queuePosition: 1);

        var (hubContext, clientProxy) = CreateHubContextMockWithProxy();
        Mock<IAutoDispatchTrigger> dispatchTrigger = new();
        Mock<IWebhookService> webhookService = new();
        AutoDispatchService service = new(
            _db,
            hubContext.Object,
            NullLogger<AutoDispatchService>.Instance,
            webhookService: webhookService.Object,
            dispatchTrigger: dispatchTrigger.Object);

        await service.TransitionToPendingReadyAsync(printer.Id);

        Printer persistedPrinter = await _db.Printers.Include(p => p.DispatchState).SingleAsync(p => p.Id == printer.Id);
        persistedPrinter.DispatchState!.AutoDispatchState.Should().Be(AutoDispatchState.PendingReady);
        persistedPrinter.DispatchState!.BedPreConfirmed.Should().BeFalse();

        clientProxy.Verify(
            proxy => proxy.SendCoreAsync(
                "autodispatchstatechanged",
                It.Is<object?[]>(args => MatchesStatusEvent(
                    args,
                    printer.Id,
                    nameof(AutoDispatchState.PendingReady),
                    1,
                    "Bed Clear Confirmed",
                    false,
                    "Waiting for operator")),
                It.IsAny<CancellationToken>()),
            Times.Once);
        webhookService.Verify(service => service.Enqueue("printer.autodispatch_pending", It.IsAny<object>()), Times.Once);
        dispatchTrigger.Verify(trigger => trigger.NotifyJobQueued(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task TransitionToPendingReadyAsync_WhenPrinterHasPausedJob_RemainsNotReady()
    {
        Printer printer = await CreatePrinterAsync();
        await CreateJobAsync(printer, "paused-job", PrintJobStatus.Paused);
        await CreateQueuedJobAsync(printer, "queued-job", queuePosition: 1);

        var (hubContext, clientProxy) = CreateHubContextMockWithProxy();
        Mock<IAutoDispatchTrigger> dispatchTrigger = new();
        Mock<IWebhookService> webhookService = new();
        AutoDispatchService service = new(
            _db,
            hubContext.Object,
            NullLogger<AutoDispatchService>.Instance,
            webhookService: webhookService.Object,
            dispatchTrigger: dispatchTrigger.Object);

        await service.TransitionToPendingReadyAsync(printer.Id);

        (await _db.Printers.Include(p => p.DispatchState).SingleAsync(p => p.Id == printer.Id))
            .DispatchState.Should().BeNull();
        clientProxy.Verify(
            proxy => proxy.SendCoreAsync(
                It.IsAny<string>(),
                It.IsAny<object?[]>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        webhookService.Verify(service => service.Enqueue(It.IsAny<string>(), It.IsAny<object>()), Times.Never);
        dispatchTrigger.Verify(trigger => trigger.NotifyJobQueued(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task MarkReadyAsync_WhenPrinterIsPendingReadyWithQueuedJob_TransitionsToReadyAndNotifiesDispatchTrigger()
    {
        Printer printer = await CreatePrinterAsync();
        printer.DispatchState = new PrinterDispatchState { PrinterId = printer.Id, AutoDispatchState = AutoDispatchState.PendingReady };
        printer.CurrentSpoolId = 42;
        await _db.SaveChangesAsync();

        PrintJob queuedJob = await CreateQueuedJobAsync(printer, "queued-job-1", queuePosition: 1);
        queuedJob.RequiredMaterialType = "PLA";
        queuedJob.EstimatedFilamentUsage = 10;
        await _db.SaveChangesAsync();

        var (hubContext, clientProxy) = CreateHubContextMockWithProxy();
        Mock<IAutoDispatchTrigger> dispatchTrigger = new();
        Mock<ISpoolmanService> spoolmanService = new();
        spoolmanService
            .Setup(service => service.GetSpoolByIdAsync(42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SpoolmanSpoolDto(42, "PLA spool", "PLA", 1000, null, false));
        Mock<IJobDispatchService> jobDispatchService = new();
        jobDispatchService
            .Setup(service => service.DispatchReviewedJobAsync(
                queuedJob.Id,
                printer.Id,
                QueueActorIdentity.AutoDispatch,
                It.IsAny<string>(),
                It.IsAny<byte[]>(),
                It.Is<FilamentOverrideAuthorization>(authorization =>
                    !authorization.OverrideApproved),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(AcceptedDispatch(queuedJob.Id, printer.Id));
        AutoDispatchService service = new(
            _db,
            hubContext.Object,
            NullLogger<AutoDispatchService>.Instance,
            spoolmanService: spoolmanService.Object,
            dispatchTrigger: dispatchTrigger.Object,
            jobDispatchService: jobDispatchService.Object);

        AutoDispatchReadyResult result = await service.MarkReadyAsync(printer.Id);

        result.NextJob.Should().NotBeNull();
        result.NextJob!.Id.Should().Be(queuedJob.Id);
        result.FilamentCheck.Should().NotBeNull();
        result.FilamentCheck!.Sufficient.Should().BeTrue();
        result.FilamentCheck.Outcome.Should().Be(FilamentCheckOutcome.Compatible);
        result.DispatchInitiated.Should().BeTrue();
        result.RequiresFilamentOverride.Should().BeFalse();

        clientProxy.Verify(
            proxy => proxy.SendCoreAsync(
                "autodispatchstatechanged",
                It.IsAny<object?[]>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
        dispatchTrigger.Verify(trigger => trigger.NotifyJobQueued(It.IsAny<Guid>()), Times.Never);
        jobDispatchService.VerifyAll();
    }

    [Fact]
    public async Task MarkReadyAsync_WhenDispatchOutcomeIsUnknown_ReturnsReconciliationPending()
    {
        Printer printer = await CreatePrinterAsync();
        printer.DispatchState = new PrinterDispatchState
        {
            PrinterId = printer.Id,
            AutoDispatchState = AutoDispatchState.PendingReady,
        };
        printer.CurrentSpoolId = 42;
        await _db.SaveChangesAsync();
        PrintJob queuedJob = await CreateQueuedJobAsync(
            printer,
            "unknown-outcome-job",
            queuePosition: 1);
        queuedJob.RequiredMaterialType = "PLA";
        queuedJob.EstimatedFilamentUsage = 10;
        await _db.SaveChangesAsync();
        Mock<ISpoolmanService> spoolmanService = new();
        spoolmanService
            .Setup(service => service.GetSpoolByIdAsync(
                42,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(
                new SpoolmanSpoolDto(
                    42,
                    "PLA spool",
                    "PLA",
                    1000,
                    null,
                    false));
        Mock<IJobDispatchService> jobDispatchService = new();
        jobDispatchService
            .Setup(service => service.DispatchReviewedJobAsync(
                queuedJob.Id,
                printer.Id,
                QueueActorIdentity.AutoDispatch,
                It.IsAny<string>(),
                It.IsAny<byte[]>(),
                It.Is<FilamentOverrideAuthorization>(authorization =>
                    !authorization.OverrideApproved),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueuedPrintJobDto
            {
                Id = queuedJob.Id.ToString(),
                AssignedPrinterId = printer.Id.ToString(),
                Status = nameof(PrintJobStatus.Starting),
                DispatchResult = new DispatchAttemptResultDto
                {
                    Outcome = DispatchAttemptOutcome.Unknown,
                },
            });
        var (hubContext, _) = CreateHubContextMockWithProxy();
        AutoDispatchService service = new(
            _db,
            hubContext.Object,
            NullLogger<AutoDispatchService>.Instance,
            spoolmanService: spoolmanService.Object,
            jobDispatchService: jobDispatchService.Object);

        AutoDispatchReadyResult result =
            await service.MarkReadyAsync(printer.Id);

        result.DispatchInitiated.Should().BeTrue();
        result.DispatchOutcome.Should().Be(
            nameof(DispatchAttemptOutcome.Unknown));
        result.DispatchReconciliationPending.Should().BeTrue();
        jobDispatchService.VerifyAll();
    }

    [Fact]
    public async Task TransitionToPendingReadyAsync_WhenBedWasPreCleared_DispatchesExactReviewedJob()
    {
        Printer printer = await CreatePrinterAsync();
        printer.DispatchState = new PrinterDispatchState
        {
            PrinterId = printer.Id,
            AutoDispatchState = AutoDispatchState.None,
            BedPreConfirmed = true,
        };
        printer.CurrentSpoolId = 42;
        await _db.SaveChangesAsync();
        PrintJob reviewedJob = await CreateQueuedJobAsync(
            printer,
            "reviewed-pre-clear-job",
            queuePosition: 1);
        reviewedJob.RequiredMaterialType = "PLA";
        reviewedJob.EstimatedFilamentUsage = 10;
        await _db.SaveChangesAsync();
        Mock<ISpoolmanService> spoolmanService = new();
        spoolmanService
            .Setup(service => service.GetSpoolByIdAsync(
                42,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(
                new SpoolmanSpoolDto(
                    42,
                    "PLA spool",
                    "PLA",
                    1000,
                    null,
                    false));
        Mock<IJobDispatchService> jobDispatchService = new();
        jobDispatchService
            .Setup(service => service.DispatchReviewedJobAsync(
                reviewedJob.Id,
                printer.Id,
                QueueActorIdentity.AutoDispatch,
                Convert.ToBase64String(reviewedJob.RowVersion!),
                It.IsAny<byte[]>(),
                It.Is<FilamentOverrideAuthorization>(authorization =>
                    !authorization.OverrideApproved),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(AcceptedDispatch(reviewedJob.Id, printer.Id));
        Mock<IAutoDispatchTrigger> dispatchTrigger = new();
        var (hubContext, _) = CreateHubContextMockWithProxy();
        AutoDispatchService service = new(
            _db,
            hubContext.Object,
            NullLogger<AutoDispatchService>.Instance,
            spoolmanService: spoolmanService.Object,
            dispatchTrigger: dispatchTrigger.Object,
            jobDispatchService: jobDispatchService.Object);

        await service.TransitionToPendingReadyAsync(printer.Id);

        jobDispatchService.VerifyAll();
        dispatchTrigger.Verify(
            trigger => trigger.NotifyJobQueued(It.IsAny<Guid>()),
            Times.Never);
    }

    [Fact]
    public async Task MarkReadyAsync_WhenMaterialIsIncompatible_DoesNotDispatch()
    {
        Printer printer = await CreatePrinterAsync();
        printer.DispatchState = new PrinterDispatchState
        {
            PrinterId = printer.Id,
            AutoDispatchState = AutoDispatchState.PendingReady,
        };
        printer.CurrentSpoolId = 42;
        await _db.SaveChangesAsync();
        PrintJob queuedJob = await CreateQueuedJobAsync(printer, "mismatch-job", queuePosition: 1);
        queuedJob.RequiredMaterialType = "PETG";
        queuedJob.EstimatedFilamentUsage = 100;
        await _db.SaveChangesAsync();

        Mock<ISpoolmanService> spoolmanService = new();
        spoolmanService
            .Setup(service => service.GetSpoolByIdAsync(42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SpoolmanSpoolDto(42, "PLA spool", "PLA", 500, null, false));
        var (hubContext, clientProxy) = CreateHubContextMockWithProxy();
        Mock<IAutoDispatchTrigger> dispatchTrigger = new();
        AutoDispatchService service = new(
            _db,
            hubContext.Object,
            NullLogger<AutoDispatchService>.Instance,
            spoolmanService: spoolmanService.Object,
            dispatchTrigger: dispatchTrigger.Object);

        AutoDispatchReadyResult result = await service.MarkReadyAsync(printer.Id);

        result.DispatchInitiated.Should().BeFalse();
        result.RequiresFilamentOverride.Should().BeTrue();
        result.Status.State.Should().Be(nameof(AutoDispatchState.PendingReady));
        result.FilamentCheck!.Outcome.Should().Be(FilamentCheckOutcome.Incompatible);
        result.FilamentCheck.MaterialMismatch.Should().BeTrue();
        result.FilamentCheck.Message.Should().Be("Material mismatch: loaded PLA, job requires PETG");
        dispatchTrigger.Verify(trigger => trigger.NotifyJobQueued(It.IsAny<Guid>()), Times.Never);
        clientProxy.Verify(
            proxy => proxy.SendCoreAsync(
                It.IsAny<string>(),
                It.IsAny<object?[]>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        (await _db.Printers.Include(candidate => candidate.DispatchState)
                .SingleAsync(candidate => candidate.Id == printer.Id))
            .DispatchState!.AutoDispatchState.Should().Be(AutoDispatchState.PendingReady);
    }

    [Fact]
    public async Task MarkReadyAsync_WhenMaterialOverrideIsConfirmed_DispatchesAndLogsMismatch()
    {
        Printer printer = await CreatePrinterAsync();
        printer.DispatchState = new PrinterDispatchState
        {
            PrinterId = printer.Id,
            AutoDispatchState = AutoDispatchState.PendingReady,
        };
        printer.CurrentSpoolId = 42;
        await _db.SaveChangesAsync();
        PrintJob queuedJob = await CreateQueuedJobAsync(printer, "mismatch-job", queuePosition: 1);
        queuedJob.RequiredMaterialType = "PETG";
        queuedJob.EstimatedFilamentUsage = 100;
        await _db.SaveChangesAsync();

        Mock<ISpoolmanService> spoolmanService = new();
        spoolmanService
            .Setup(service => service.GetSpoolByIdAsync(42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SpoolmanSpoolDto(42, "PLA spool", "PLA", 500, null, false));
        var (hubContext, _) = CreateHubContextMockWithProxy();
        Mock<IAutoDispatchTrigger> dispatchTrigger = new();
        const string actorSubject = "8a196a30-022e-4c5f-92e6-dc13677b5bf6";
        Mock<IJobDispatchService> jobDispatchService = new();
        jobDispatchService
            .Setup(service => service.DispatchReviewedJobAsync(
                queuedJob.Id,
                printer.Id,
                actorSubject,
                It.IsAny<string>(),
                It.IsAny<byte[]>(),
                It.Is<FilamentOverrideAuthorization>(authorization =>
                    authorization.Outcome == nameof(FilamentCheckOutcome.Incompatible) &&
                    authorization.Reason == "Material mismatch: loaded PLA, job requires PETG"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Farm.Infrastructure.Dtos.PrintQueue.QueuedPrintJobDto
            {
                Id = queuedJob.Id.ToString(),
                AssignedPrinterId = printer.Id.ToString(),
                Status = nameof(PrintJobStatus.Printing),
                DispatchResult = new Farm.Infrastructure.Dtos.PrintQueue.DispatchAttemptResultDto
                {
                    Outcome = DispatchAttemptOutcome.Accepted,
                },
            });
        Mock<ILogger<AutoDispatchService>> logger = new();
        AutoDispatchService service = new(
            _db,
            hubContext.Object,
            logger.Object,
            spoolmanService: spoolmanService.Object,
            dispatchTrigger: dispatchTrigger.Object,
            jobDispatchService: jobDispatchService.Object);

        AutoDispatchReadyResult challenge = await service.MarkReadyAsync(printer.Id);
        AutoDispatchReadyResult result = await service.MarkReadyAsync(
            printer.Id,
            printer.DispatchState.RowVersion ?? [],
            confirmFilamentOverride: true,
            actorSubject: actorSubject,
            expectedOverrideJobVersion: queuedJob.RowVersion,
            expectedFilamentCheckVersion: Convert.FromBase64String(challenge.FilamentCheckETag!));

        result.DispatchInitiated.Should().BeTrue();
        result.RequiresFilamentOverride.Should().BeFalse();
        result.FilamentOverrideApplied.Should().BeTrue();
        result.FilamentCheck!.Message.Should().Be("Material mismatch: loaded PLA, job requires PETG");
        dispatchTrigger.Verify(
            trigger => trigger.NotifyJobQueued(It.IsAny<Guid>()),
            Times.Never);
        jobDispatchService.VerifyAll();
        logger.Verify(
            candidate => candidate.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((value, _) =>
                    value.ToString()!.Contains(actorSubject, StringComparison.Ordinal) &&
                    value.ToString()!.Contains("Material mismatch: loaded PLA, job requires PETG", StringComparison.Ordinal)),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task MarkReadyAsync_WhenNoSpoolIsAssigned_ReturnsUnknownAndRequiresConfirmation()
    {
        Printer printer = await CreatePrinterAsync();
        printer.DispatchState = new PrinterDispatchState
        {
            PrinterId = printer.Id,
            AutoDispatchState = AutoDispatchState.PendingReady,
        };
        await _db.SaveChangesAsync();
        await CreateQueuedJobAsync(printer, "unknown-filament-job", queuePosition: 1);
        var (hubContext, _) = CreateHubContextMockWithProxy();
        Mock<IAutoDispatchTrigger> dispatchTrigger = new();
        AutoDispatchService service = new(
            _db,
            hubContext.Object,
            NullLogger<AutoDispatchService>.Instance,
            dispatchTrigger: dispatchTrigger.Object);

        AutoDispatchReadyResult result = await service.MarkReadyAsync(printer.Id);

        result.DispatchInitiated.Should().BeFalse();
        result.RequiresFilamentOverride.Should().BeTrue();
        result.FilamentCheck!.Outcome.Should().Be(FilamentCheckOutcome.Unknown);
        result.FilamentCheck.Sufficient.Should().BeFalse();
        result.FilamentCheck.Message.Should().Be("No spool is assigned to the printer.");
        dispatchTrigger.Verify(trigger => trigger.NotifyJobQueued(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task MarkReadyAsync_WhenUnknownFilamentOverrideIsConfirmed_DispatchesExactJob()
    {
        Printer printer = await CreatePrinterAsync();
        printer.DispatchState = new PrinterDispatchState
        {
            PrinterId = printer.Id,
            AutoDispatchState = AutoDispatchState.PendingReady,
        };
        await _db.SaveChangesAsync();
        PrintJob queuedJob = await CreateQueuedJobAsync(
            printer,
            "unknown-filament-job",
            queuePosition: 1);
        const string actorSubject = "operator-unknown";
        Mock<IJobDispatchService> jobDispatchService = new();
        jobDispatchService
            .Setup(service => service.DispatchReviewedJobAsync(
                queuedJob.Id,
                printer.Id,
                actorSubject,
                It.IsAny<string>(),
                It.IsAny<byte[]>(),
                It.Is<FilamentOverrideAuthorization>(authorization =>
                    authorization.Outcome == nameof(FilamentCheckOutcome.Unknown) &&
                    authorization.Reason == "No spool is assigned to the printer."),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Farm.Infrastructure.Dtos.PrintQueue.QueuedPrintJobDto
            {
                Id = queuedJob.Id.ToString(),
                AssignedPrinterId = printer.Id.ToString(),
                Status = nameof(PrintJobStatus.Printing),
                DispatchResult = new Farm.Infrastructure.Dtos.PrintQueue.DispatchAttemptResultDto
                {
                    Outcome = DispatchAttemptOutcome.Accepted,
                },
            });
        var (hubContext, _) = CreateHubContextMockWithProxy();
        AutoDispatchService service = new(
            _db,
            hubContext.Object,
            NullLogger<AutoDispatchService>.Instance,
            jobDispatchService: jobDispatchService.Object);

        AutoDispatchReadyResult challenge = await service.MarkReadyAsync(printer.Id);
        AutoDispatchReadyResult result = await service.MarkReadyAsync(
            printer.Id,
            printer.DispatchState.RowVersion ?? [],
            confirmFilamentOverride: true,
            actorSubject,
            queuedJob.RowVersion,
            Convert.FromBase64String(challenge.FilamentCheckETag!));

        result.DispatchInitiated.Should().BeTrue();
        result.FilamentOverrideApplied.Should().BeTrue();
        result.FilamentCheck!.Outcome.Should().Be(FilamentCheckOutcome.Unknown);
        jobDispatchService.VerifyAll();
    }

    [Fact]
    public async Task MarkReadyAsync_WhenFilamentReasonChanges_RequiresReviewOfNewReason()
    {
        Printer printer = await CreatePrinterAsync();
        printer.DispatchState = new PrinterDispatchState
        {
            PrinterId = printer.Id,
            AutoDispatchState = AutoDispatchState.PendingReady,
        };
        printer.CurrentSpoolId = 42;
        await _db.SaveChangesAsync();
        PrintJob queuedJob = await CreateQueuedJobAsync(
            printer,
            "changed-filament-job",
            queuePosition: 1);
        queuedJob.RequiredMaterialType = "PETG";
        queuedJob.EstimatedFilamentUsage = 100;
        await _db.SaveChangesAsync();
        Mock<ISpoolmanService> spoolmanService = new();
        spoolmanService
            .SetupSequence(service => service.GetSpoolByIdAsync(
                42,
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Spoolman unavailable"))
            .ReturnsAsync(new SpoolmanSpoolDto(
                42,
                "PLA spool",
                "PLA",
                500,
                null,
                false));
        Mock<IJobDispatchService> jobDispatchService = new(MockBehavior.Strict);
        var (hubContext, _) = CreateHubContextMockWithProxy();
        AutoDispatchService service = new(
            _db,
            hubContext.Object,
            NullLogger<AutoDispatchService>.Instance,
            spoolmanService: spoolmanService.Object,
            jobDispatchService: jobDispatchService.Object);

        AutoDispatchReadyResult initialChallenge = await service.MarkReadyAsync(printer.Id);
        AutoDispatchReadyResult changedChallenge = await service.MarkReadyAsync(
            printer.Id,
            printer.DispatchState.RowVersion ?? [],
            confirmFilamentOverride: true,
            actorSubject: "operator-changed-reason",
            expectedOverrideJobVersion: queuedJob.RowVersion,
            expectedFilamentCheckVersion: Convert.FromBase64String(
                initialChallenge.FilamentCheckETag!));

        changedChallenge.DispatchInitiated.Should().BeFalse();
        changedChallenge.FilamentCheckChanged.Should().BeTrue();
        changedChallenge.RequiresFilamentOverride.Should().BeTrue();
        changedChallenge.FilamentCheck!.Outcome.Should().Be(
            FilamentCheckOutcome.Incompatible);
        changedChallenge.FilamentCheck.Message.Should().Be(
            "Material mismatch: loaded PLA, job requires PETG");
        changedChallenge.FilamentCheckETag.Should().NotBe(
            initialChallenge.FilamentCheckETag);
        jobDispatchService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task MarkReadyAsync_WhenSpoolmanThrows_ReturnsUnknownAndDoesNotDispatch()
    {
        Printer printer = await CreatePrinterAsync();
        printer.DispatchState = new PrinterDispatchState
        {
            PrinterId = printer.Id,
            AutoDispatchState = AutoDispatchState.PendingReady,
        };
        printer.CurrentSpoolId = 42;
        await _db.SaveChangesAsync();
        PrintJob queuedJob = await CreateQueuedJobAsync(printer, "spoolman-error-job", queuePosition: 1);
        queuedJob.RequiredMaterialType = "PLA";
        queuedJob.EstimatedFilamentUsage = 100;
        await _db.SaveChangesAsync();

        Mock<ISpoolmanService> spoolmanService = new();
        spoolmanService
            .Setup(service => service.GetSpoolByIdAsync(42, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Spoolman unavailable"));
        var (hubContext, _) = CreateHubContextMockWithProxy();
        Mock<IAutoDispatchTrigger> dispatchTrigger = new();
        AutoDispatchService service = new(
            _db,
            hubContext.Object,
            NullLogger<AutoDispatchService>.Instance,
            spoolmanService: spoolmanService.Object,
            dispatchTrigger: dispatchTrigger.Object);

        AutoDispatchReadyResult result = await service.MarkReadyAsync(printer.Id);

        result.DispatchInitiated.Should().BeFalse();
        result.RequiresFilamentOverride.Should().BeTrue();
        result.FilamentCheck!.Outcome.Should().Be(FilamentCheckOutcome.Unknown);
        result.FilamentCheck.Sufficient.Should().BeFalse();
        result.FilamentCheck.Message.Should().Be(
            "Filament verification failed because Spoolman could not be reached.");
        dispatchTrigger.Verify(trigger => trigger.NotifyJobQueued(It.IsAny<Guid>()), Times.Never);
    }

    private static Farm.Infrastructure.Dtos.PrintQueue.QueuedPrintJobDto AcceptedDispatch(
        Guid jobId,
        Guid printerId) =>
        new()
        {
            Id = jobId.ToString(),
            AssignedPrinterId = printerId.ToString(),
            Status = nameof(PrintJobStatus.Printing),
            DispatchResult = new Farm.Infrastructure.Dtos.PrintQueue.DispatchAttemptResultDto
            {
                Outcome = DispatchAttemptOutcome.Accepted,
            },
        };

    [Fact]
    public async Task MarkReadyAsync_WhenPrinterHasPausedJob_RejectsStaleReadyConfirmation()
    {
        Printer printer = await CreatePrinterAsync();
        printer.DispatchState = new PrinterDispatchState
        {
            PrinterId = printer.Id,
            AutoDispatchState = AutoDispatchState.PendingReady,
            BedPreConfirmed = true,
        };
        await _db.SaveChangesAsync();
        await CreateJobAsync(printer, "paused-job", PrintJobStatus.Paused);
        await CreateQueuedJobAsync(printer, "queued-job", queuePosition: 1);

        var (hubContext, clientProxy) = CreateHubContextMockWithProxy();
        Mock<IAutoDispatchTrigger> dispatchTrigger = new();
        AutoDispatchService service = new(
            _db,
            hubContext.Object,
            NullLogger<AutoDispatchService>.Instance,
            dispatchTrigger: dispatchTrigger.Object);

        Func<Task> act = async () => _ = await service.MarkReadyAsync(printer.Id);

        await act.Should().ThrowAsync<InvalidOperationException>();
        Printer persistedPrinter = await _db.Printers
            .Include(p => p.DispatchState)
            .SingleAsync(p => p.Id == printer.Id);
        persistedPrinter.DispatchState!.AutoDispatchState.Should().Be(AutoDispatchState.PendingReady);
        persistedPrinter.DispatchState.BedPreConfirmed.Should().BeTrue();
        clientProxy.Verify(
            proxy => proxy.SendCoreAsync(
                It.IsAny<string>(),
                It.IsAny<object?[]>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        dispatchTrigger.Verify(trigger => trigger.NotifyJobQueued(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task SkipNextJobAsync_WhenQueuedJobsRemain_StaysPendingReadyAndCancelsOnlyNextJob()
    {
        Printer printer = await CreatePrinterAsync();
        printer.DispatchState = new PrinterDispatchState { PrinterId = printer.Id, AutoDispatchState = AutoDispatchState.PendingReady };
        await _db.SaveChangesAsync();

        PrintJob firstJob = await CreateQueuedJobAsync(printer, "queued-job-1", queuePosition: 1);
        PrintJob secondJob = await CreateQueuedJobAsync(printer, "queued-job-2", queuePosition: 2);

        var (hubContext, clientProxy) = CreateHubContextMockWithProxy();
        Mock<IFilamentCoverageBroadcaster> coverageBroadcaster = new(MockBehavior.Strict);
        coverageBroadcaster.Setup(b => b.BroadcastPrinterChangedAsync(
                printer.Id,
                FilamentCoverageChangeReasons.QueueChanged,
                It.IsAny<CancellationToken>()))
            .Callback(() => _db.PrintJobs.Single(job => job.Id == firstJob.Id).Status
                .Should().Be(PrintJobStatus.Cancelled))
            .Returns(Task.CompletedTask);
        AutoDispatchService service = new(
            _db,
            hubContext.Object,
            NullLogger<AutoDispatchService>.Instance,
            coverageBroadcaster: coverageBroadcaster.Object);

        AutoDispatchStatusDto status = await service.SkipNextJobAsync(printer.Id);

        status.State.Should().Be(nameof(AutoDispatchState.PendingReady));
        status.QueueDepth.Should().Be(1);
        status.ReadyGateChecks.Should().Contain(check =>
            check.Name == "Jobs in Queue"
            && check.Passed
            && check.Message.Contains("1 job queued"));

        PrintJob persistedFirstJob = await _db.PrintJobs.SingleAsync(job => job.Id == firstJob.Id);
        PrintJob persistedSecondJob = await _db.PrintJobs.SingleAsync(job => job.Id == secondJob.Id);
        persistedFirstJob.Status.Should().Be(PrintJobStatus.Cancelled);
        persistedSecondJob.Status.Should().Be(PrintJobStatus.Queued);

        Printer persistedPrinter = await _db.Printers.Include(p => p.DispatchState).SingleAsync(p => p.Id == printer.Id);
        persistedPrinter.DispatchState!.AutoDispatchState.Should().Be(AutoDispatchState.PendingReady);

        clientProxy.Verify(
            proxy => proxy.SendCoreAsync(
                "autodispatchstatechanged",
                It.Is<object?[]>(args => MatchesStatusEvent(
                    args,
                    printer.Id,
                    nameof(AutoDispatchState.PendingReady),
                    1)),
                It.IsAny<CancellationToken>()),
            Times.Once);
        coverageBroadcaster.VerifyAll();
    }

    [Fact]
    public async Task SkipNextJobAsync_WhenNoQueuedJob_DoesNotBroadcastCoverage()
    {
        Printer printer = await CreatePrinterAsync();
        var (hubContext, _) = CreateHubContextMockWithProxy();
        Mock<IFilamentCoverageBroadcaster> coverageBroadcaster = new(MockBehavior.Strict);
        AutoDispatchService service = new(
            _db,
            hubContext.Object,
            NullLogger<AutoDispatchService>.Instance,
            coverageBroadcaster: coverageBroadcaster.Object);

        _ = await service.SkipNextJobAsync(printer.Id);

        coverageBroadcaster.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task GetStatusAsync_WhenPrinterIsPendingReady_PopulatesAttentionDetails()
    {
        Printer printer = await CreatePrinterAsync();
        printer.DispatchState = new PrinterDispatchState { PrinterId = printer.Id, AutoDispatchState = AutoDispatchState.PendingReady };
        await _db.SaveChangesAsync();
        await CreateQueuedJobAsync(printer, "queued-job-1", queuePosition: 1);

        var (hubContext, _) = CreateHubContextMockWithProxy();
        AutoDispatchService service = new(
            _db,
            hubContext.Object,
            NullLogger<AutoDispatchService>.Instance);

        AutoDispatchStatusDto status = await service.GetStatusAsync(printer.Id);

        status.AttentionMessage.Should().Be("Print completed. 1 queued job is blocked until you clear the bed and confirm ready. Once confirmed, the next queued job will start automatically.");
    }

    [Fact]
    public async Task GetStatusAsync_WhenPrinterHasPausedJob_ReportsPausedWithoutClearBedPrompt()
    {
        Printer printer = await CreatePrinterAsync();
        printer.DispatchState = new PrinterDispatchState
        {
            PrinterId = printer.Id,
            AutoDispatchState = AutoDispatchState.PendingReady,
            BedPreConfirmed = true,
        };
        await _db.SaveChangesAsync();
        await CreateJobAsync(printer, "paused-job", PrintJobStatus.Paused);
        await CreateQueuedJobAsync(printer, "queued-job", queuePosition: 1);

        var (hubContext, _) = CreateHubContextMockWithProxy();
        AutoDispatchService service = new(
            _db,
            hubContext.Object,
            NullLogger<AutoDispatchService>.Instance);

        AutoDispatchStatusDto status = await service.GetStatusAsync(printer.Id);

        status.State.Should().Be(nameof(PrintJobStatus.Paused));
        status.IsReady.Should().BeFalse();
        status.CurrentJobName.Should().Be("paused-job");
        status.AttentionMessage.Should().BeNull();
        status.ReadyGateChecks.Should().Contain(check =>
            check.Name == "Bed Clear Confirmed"
            && !check.Passed
            && check.Message == "Paused job still occupies the printer");
    }

    [Fact]
    public async Task GetAllStatusAsync_WhenPrinterHasPausedJob_ReportsPrinterBusy()
    {
        Printer printer = await CreatePrinterAsync();
        printer.DispatchState = new PrinterDispatchState
        {
            PrinterId = printer.Id,
            AutoDispatchState = AutoDispatchState.Ready,
        };
        await _db.SaveChangesAsync();
        await CreateJobAsync(printer, "paused-job", PrintJobStatus.Paused);
        await CreateQueuedJobAsync(printer, "queued-job", queuePosition: 1);

        var (hubContext, _) = CreateHubContextMockWithProxy();
        AutoDispatchService service = new(
            _db,
            hubContext.Object,
            NullLogger<AutoDispatchService>.Instance);

        AutoDispatchGlobalStatusDto result = await service.GetAllStatusAsync();

        AutoDispatchStatusDto status = result.Printers.Should().ContainSingle().Subject;
        status.State.Should().Be(nameof(PrintJobStatus.Paused));
        status.IsReady.Should().BeFalse();
        status.CurrentJobName.Should().Be("paused-job");
        status.AttentionMessage.Should().BeNull();
    }

    [Theory]
    [InlineData(PrintJobStatus.Starting)]
    [InlineData(PrintJobStatus.Printing)]
    public async Task GetStatusAsync_WhenPrinterHasActiveJob_ReportsOccupyingState(
        PrintJobStatus jobStatus)
    {
        Printer printer = await CreatePrinterAsync();
        printer.DispatchState = new PrinterDispatchState
        {
            PrinterId = printer.Id,
            AutoDispatchState = AutoDispatchState.PendingReady,
        };
        await _db.SaveChangesAsync();
        await CreateJobAsync(printer, "active-job", jobStatus);
        await CreateQueuedJobAsync(printer, "queued-job", queuePosition: 1);

        var (hubContext, _) = CreateHubContextMockWithProxy();
        AutoDispatchService service = new(
            _db,
            hubContext.Object,
            NullLogger<AutoDispatchService>.Instance);

        AutoDispatchStatusDto status = await service.GetStatusAsync(printer.Id);

        status.State.Should().Be(jobStatus.ToString());
        status.IsReady.Should().BeFalse();
        status.CurrentJobName.Should().Be("active-job");
        status.AttentionMessage.Should().BeNull();
        status.ReadyGateChecks.Should().Contain(check =>
            check.Name == "Bed Clear Confirmed"
            && !check.Passed
            && check.Message == "Active job still occupies the printer");
    }

    [Fact]
    public async Task MarkPreClearAsync_WhenQueuedJobExists_PopulatesReadyAttentionMessage()
    {
        Printer printer = await CreatePrinterAsync();
        printer.CurrentSpoolId = 42;
        PrintJob queuedJob = await CreateQueuedJobAsync(
            printer,
            "queued-job-1",
            queuePosition: 1);
        queuedJob.RequiredMaterialType = "PLA";
        queuedJob.EstimatedFilamentUsage = 10;
        await _db.SaveChangesAsync();

        var (hubContext, _) = CreateHubContextMockWithProxy();
        Mock<IAutoDispatchTrigger> dispatchTrigger = new();
        Mock<ISpoolmanService> spoolmanService = new();
        spoolmanService
            .Setup(service => service.GetSpoolByIdAsync(42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SpoolmanSpoolDto(42, "PLA spool", "PLA", 1000, null, false));
        AutoDispatchService service = new(
            _db,
            hubContext.Object,
            NullLogger<AutoDispatchService>.Instance,
            spoolmanService: spoolmanService.Object,
            dispatchTrigger: dispatchTrigger.Object);

        AutoDispatchStatusDto status = await service.MarkPreClearAsync(printer.Id);

        status.BedPreConfirmed.Should().BeTrue();
        status.AttentionMessage.Should().Be("Bed is clear. The next queued job will start automatically.");
    }

    [Fact]
    public async Task GetStatusAsync_WhenPrinterIsInMaintenanceWithQueuedJob_PopulatesAttentionMessage()
    {
        Printer printer = await CreatePrinterAsync();
        printer.InMaintenance = true;
        await _db.SaveChangesAsync();
        await CreateQueuedJobAsync(printer, "queued-job-1", queuePosition: 1);

        var (hubContext, _) = CreateHubContextMockWithProxy();
        AutoDispatchService service = new(
            _db,
            hubContext.Object,
            NullLogger<AutoDispatchService>.Instance);

        AutoDispatchStatusDto status = await service.GetStatusAsync(printer.Id);

        status.AttentionMessage.Should().Be("Printer is in maintenance mode. 1 queued job will not start until maintenance is complete and the printer is available.");
    }

    private async Task<Printer> CreatePrinterAsync()
    {
        Manufacturer manufacturer = new()
        {
            Id = Guid.NewGuid(),
            Name = "Test Manufacturer",
        };
        PrinterModel model = new()
        {
            Id = Guid.NewGuid(),
            Name = "Test Model",
            ManufacturerId = manufacturer.Id,
        };
        Printer printer = new()
        {
            Id = Guid.NewGuid(),
            Name = "AutoDispatch Service Test Printer",
            ServerUrl = $"http://autodispatch-service-test-{Guid.NewGuid():N}.local",
            BackendPort = 7125,
            Backend = (int)PrinterBackend.Moonraker,
            ManufacturerId = manufacturer.Id,
            ModelId = model.Id,
            AutoDispatchEnabled = true,
            IsEnabled = true,
            IsAvailable = true,
        };

        _db.Manufacturers.Add(manufacturer);
        _db.PrinterModels.Add(model);
        _db.Printers.Add(printer);
        await _db.SaveChangesAsync();

        return printer;
    }

    private Task<PrintJob> CreateQueuedJobAsync(Printer printer, string name, int queuePosition) =>
        CreateJobAsync(printer, name, PrintJobStatus.Queued, queuePosition);

    private async Task<PrintJob> CreateJobAsync(
        Printer printer,
        string name,
        PrintJobStatus status,
        int queuePosition = 0)
    {
        PrintJob job = new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            AssignedPrinterId = printer.Id,
            Status = status,
            Priority = 0,
            QueuePosition = queuePosition,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            QueuedAt = DateTime.UtcNow,
        };

        _db.PrintJobs.Add(job);
        await _db.SaveChangesAsync();
        return job;
    }

    private static (Mock<IHubContext<PrinterHub>> Hub, Mock<IClientProxy> Proxy) CreateHubContextMockWithProxy()
    {
        Mock<IClientProxy> proxy = new();
        proxy.Setup(x => x.SendCoreAsync(
                It.IsAny<string>(),
                It.IsAny<object?[]>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        Mock<IHubClients> clients = new();
        clients.Setup(x => x.Group(It.IsAny<string>())).Returns(proxy.Object);

        Mock<IHubContext<PrinterHub>> hub = new();
        hub.Setup(x => x.Clients).Returns(clients.Object);
        return (hub, proxy);
    }

    private static bool MatchesStatusEvent(
        object?[] args,
        Guid printerId,
        string expectedState,
        int expectedQueueDepth,
        string? gateName = null,
        bool? gatePassed = null,
        string? gateMessageFragment = null)
    {
        if (args.Length != 1)
        {
            return false;
        }

        AutoDispatchStatusDto? status = args[0] as AutoDispatchStatusDto;
        if (status is null)
        {
            return false;
        }

        if (status.PrinterId != printerId || status.State != expectedState || status.QueueDepth != expectedQueueDepth)
        {
            return false;
        }

        if (gateName is null)
        {
            return true;
        }

        ReadyGateCheckDto? gate = status.ReadyGateChecks.FirstOrDefault(check => check.Name == gateName);
        if (gate is null)
        {
            return false;
        }

        if (gatePassed.HasValue && gate.Passed != gatePassed.Value)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(gateMessageFragment) && !gate.Message.Contains(gateMessageFragment, StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }
}
