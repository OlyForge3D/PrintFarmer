// <copyright file="FinalFactCheckerRemediationTests.cs" company="OlyForge3D">
// Copyright (c) OlyForge3D. All rights reserved.
// </copyright>

using System.Security.Claims;
using System.Text.Json;
using Farm.Infrastructure;
using Farm.Infrastructure.Authorization;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Discovery;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos.PrintQueue;
using Farm.Infrastructure.Network;
using Farm.Infrastructure.Security;
using Farm.Infrastructure.Services;
using Farm.Infrastructure.Services.AutoDispatch;
using Farm.Infrastructure.Services.Interfaces;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.Queue;
using Farm.Infrastructure.Services.Queue.Dispatch;
using Farm.Infrastructure.Services.SignalR;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Controllers;
using Farm.Web.Api.Controllers.Requests;
using FluentAssertions;
using FluentValidation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Dispatch;

/// <summary>Regression coverage for the final issue #900 physical and business-race packet.</summary>
[Trait("Category", "DbHeavy")]
public sealed class FinalFactCheckerRemediationTests : IAsyncDisposable
{
    private readonly string _connectionString =
        $"Data Source=file:final_fact_{Guid.NewGuid():N}?mode=memory&cache=shared;Foreign Keys=False";
    private readonly SqliteConnection _keepAlive;

    public FinalFactCheckerRemediationTests()
    {
        _keepAlive = new SqliteConnection(_connectionString);
        _keepAlive.Open();
    }

    public async ValueTask DisposeAsync()
    {
        await _keepAlive.DisposeAsync();
    }

    [Fact]
    public async Task CancelA_BackendCallDelayed_BCannotClaimUntilPhysicalBarrierReleases()
    {
        await using AppDbContext seed = CreateContext();
        ControlFixture fixture = await SeedControlFixtureAsync(seed, includeControlCommand: true);
        var enteredBackend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBackend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var printers = new Mock<IPrintersService>();
        printers.Setup(service => service.ExecuteControlAsync(
                fixture.PrinterId,
                BackendControlOperation.Cancel,
                It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                enteredBackend.SetResult();
                await releaseBackend.Task;
                return BackendControlOutcome.Accepted();
            });

        using ServiceProvider provider = CreateQueueProvider(printers.Object);
        var consumer = new BackendControlCommandConsumerService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<BackendControlCommandConsumerService>.Instance);
        Task processing = consumer.ProcessPendingAsync(CancellationToken.None);
        await enteredBackend.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await using (AppDbContext verifyBarrier = CreateContext())
        {
            PrinterDispatchState state = await verifyBarrier.PrinterDispatchStates
                .SingleAsync(candidate => candidate.PrinterId == fixture.PrinterId);
            state.PhysicalControlCommandId.Should().Be(fixture.ControlCommandId);
            state.PhysicalControlAttemptId.Should().Be(fixture.AttemptAId);
        }

        var reconciler = new QueueReconciliationService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<QueueReconciliationService>.Instance);
        await reconciler.ReconcileStaleAttemptsAsync(CancellationToken.None);

        await using (AppDbContext blockedContext = CreateContext())
        {
            DispatchClaimResult blocked = await CreateClaimService(
                    blockedContext,
                    fixture.PrinterId)
                .AcquireClaimAsync(new DispatchClaimRequest(
                    fixture.JobBId,
                    fixture.PrinterId,
                    "operator-b",
                    "Manual",
                    null,
                    null,
                    null));
            blocked.Success.Should().BeFalse();
            blocked.ErrorCode.Should().BeOneOf(
                "printer_physical_control_in_flight",
                "printer_busy_active",
                "printer_busy_database");
        }

        releaseBackend.SetResult();
        await processing.WaitAsync(TimeSpan.FromSeconds(10));

        await using AppDbContext claimContext = CreateContext();
        DispatchClaimResult claimB = await CreateClaimService(
                claimContext,
                fixture.PrinterId)
            .AcquireClaimAsync(new DispatchClaimRequest(
                fixture.JobBId,
                fixture.PrinterId,
                "operator-b",
                "Manual",
                null,
                null,
                null));
        claimB.Success.Should().BeTrue(claimB.ErrorDetail);

        printers.Verify(service => service.ExecuteControlAsync(
            fixture.PrinterId,
            BackendControlOperation.Cancel,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("accepted")]
    [InlineData("failed")]
    [InlineData("unknown")]
    [InlineData("started")]
    public async Task LateAttemptAOutcome_AfterAttemptBOwnsPrinter_DoesNotMutateB(
        string outcome)
    {
        await using AppDbContext seed = CreateContext();
        ControlFixture fixture = await SeedControlFixtureAsync(seed, includeControlCommand: false);
        await using AppDbContext mutate = CreateContext();
        DispatchClaimService service = CreateClaimService(mutate, fixture.PrinterId);

        bool applied = outcome switch
        {
            "accepted" => await service.RecordBackendAcceptedAsync(
                    fixture.AttemptAId,
                    "provider-a",
                    "a.gcode"),
            "failed" => await service.ReleaseClaimOnKnownFailureAsync(
                    fixture.AttemptAId,
                    "late_failure",
                    "late A failure"),
            "started" => await service.RecordBackendCallStartedAsync(
                    fixture.AttemptAId),
            _ => await service.RecordUnknownOutcomeAsync(
                    fixture.AttemptAId,
                    "late A unknown"),
        };

        await using AppDbContext verify = CreateContext();
        PrinterDispatchState state = await verify.PrinterDispatchStates
            .SingleAsync(candidate => candidate.PrinterId == fixture.PrinterId);
        PrintJob job = await verify.PrintJobs
            .SingleAsync(candidate => candidate.Id == fixture.JobAId);
        QueueDispatchAttempt attemptA = await verify.QueueDispatchAttempts
            .SingleAsync(candidate => candidate.Id == fixture.AttemptAId);
        applied.Should().BeFalse();
        state.ActiveDispatchAttemptId.Should().Be(fixture.AttemptBId);
        state.ActiveJobId.Should().Be(fixture.JobAId);
        job.Status.Should().Be(PrintJobStatus.Starting);
        attemptA.Outcome.Should().Be(DispatchAttemptOutcome.Unknown);
        attemptA.BackendJobId.Should().BeNull();
    }

    [Fact]
    public async Task ReconciliationA_AfterAttemptBOwnsPrinter_MarksOnlyASuperseded()
    {
        await using AppDbContext seed = CreateContext();
        ControlFixture fixture = await SeedControlFixtureAsync(seed, includeControlCommand: false);
        var printers = new Mock<IPrintersService>(MockBehavior.Strict);
        using ServiceProvider provider = CreateQueueProvider(printers.Object);
        var reconciler = new QueueReconciliationService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<QueueReconciliationService>.Instance);

        await reconciler.ReconcileStaleAttemptsAsync(CancellationToken.None);

        await using AppDbContext verify = CreateContext();
        QueueDispatchAttempt attemptA = await verify.QueueDispatchAttempts
            .SingleAsync(candidate => candidate.Id == fixture.AttemptAId);
        PrinterDispatchState state = await verify.PrinterDispatchStates
            .SingleAsync(candidate => candidate.PrinterId == fixture.PrinterId);
        PrintJob job = await verify.PrintJobs
            .SingleAsync(candidate => candidate.Id == fixture.JobAId);
        attemptA.ErrorCode.Should().Be("attempt_superseded");
        state.ActiveDispatchAttemptId.Should().Be(fixture.AttemptBId);
        job.Status.Should().Be(PrintJobStatus.Starting);
        printers.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Reconciliation_PreClaimUnknownNullAttempt_RequeuesWithoutBackendProbe()
    {
        await using AppDbContext seed = CreateContext();
        Guid printerId = await SeedPrinterAsync(seed);
        var gcode = new GcodeFile
        {
            Id = Guid.NewGuid(),
            Name = "preclaim.gcode",
            FileName = "preclaim.gcode",
            FilePath = "/",
            FileSizeBytes = 10,
        };
        var job = new PrintJob
        {
            Id = Guid.NewGuid(),
            Name = "Preclaim",
            GcodeFileId = gcode.Id,
            AssignedPrinterId = printerId,
            Status = PrintJobStatus.Assigned,
            Priority = (int)PrintJobPriority.Normal,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            QueuedAt = DateTime.UtcNow,
        };
        seed.GcodeFiles.Add(gcode);
        seed.PrintJobs.Add(job);
        await seed.SaveChangesAsync();
        Guid commandId = Guid.NewGuid();
        await using (var transaction = await seed.Database.BeginTransactionAsync())
        {
            seed.QueueDispatchOutbox.Add(new QueueDispatchOutbox
            {
                Id = commandId,
                Sequence = await new DbOutboxSequenceAllocator().AllocateAsync(seed),
                AggregateType = nameof(PrintJob),
                AggregateId = job.Id,
                PrinterId = printerId,
                EventType = BedClearAcknowledgementService.BackendStartCommandEventType,
                SchemaVersion = "1",
                PayloadJson = "{}",
                Status = QueueOutboxEventStatus.Processing,
                FailureCode = "backend_outcome_unknown",
                CreatedAtUtc = DateTime.UtcNow.AddMinutes(-20),
                LastAttemptedAtUtc = DateTime.UtcNow.AddMinutes(-20),
            });
            seed.BedClearCommandRecords.Add(new BedClearCommandRecord
            {
                Id = Guid.NewGuid(),
                PrinterId = printerId,
                JobId = job.Id,
                IdempotencyKey = "preclaim-key",
                RequestSha256 = new string('a', 64),
                ActorSubject = "operator",
                JobRowVersion = job.RowVersion ?? [],
                DispatchStateRowVersion = [],
                Status = BedClearCommandStatus.Unknown,
                OutboxEventId = commandId,
                CreatedAtUtc = DateTime.UtcNow.AddMinutes(-20),
                UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-20),
                ExpiresAtUtc = DateTime.UtcNow.AddMinutes(10),
            });
            await seed.SaveChangesAsync();
            await transaction.CommitAsync();
        }

        var printers = new Mock<IPrintersService>(MockBehavior.Strict);
        using ServiceProvider provider = CreateQueueProvider(printers.Object);
        var reconciler = new QueueReconciliationService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<QueueReconciliationService>.Instance);

        await reconciler.ReconcileStaleAttemptsAsync(CancellationToken.None);

        await using AppDbContext verify = CreateContext();
        QueueDispatchOutbox command = await verify.QueueDispatchOutbox
            .SingleAsync(candidate => candidate.Id == commandId);
        BedClearCommandRecord record = await verify.BedClearCommandRecords.SingleAsync();
        command.Status.Should().Be(QueueOutboxEventStatus.Pending);
        command.AttemptId.Should().BeNull();
        command.FailureCode.Should().BeNull();
        record.Status.Should().Be(BedClearCommandStatus.Pending);
        printers.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Reconciliation_FileHistoryMatch_PersistsRealProviderJobId()
    {
        await using (AppDbContext seed = CreateContext())
        {
            ControlFixture fixture = await SeedControlFixtureAsync(
                seed,
                includeControlCommand: false);
            PrinterDispatchState state = await seed.PrinterDispatchStates
                .SingleAsync(candidate => candidate.PrinterId == fixture.PrinterId);
            state.ActiveDispatchAttemptId = fixture.AttemptAId;
            await seed.SaveChangesAsync();
        }

        await using AppDbContext ids = CreateContext();
        QueueDispatchAttempt seededAttempt = await ids.QueueDispatchAttempts
            .SingleAsync(candidate => candidate.Outcome == DispatchAttemptOutcome.Unknown);
        Guid jobId = seededAttempt.PrintJobId!.Value;
        Guid printerId = seededAttempt.PrinterId;
        var printers = new Mock<IPrintersService>();
        printers.Setup(service => service.GetStatusDtoAsync(
                printerId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PrinterStatusDto(
                printerId,
                IsOnline: true,
                State: "idle"));
        printers.Setup(service => service.ProbeHistoryListAsync(
                printerId,
                It.IsAny<int?>(),
                It.IsAny<int?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(HistoryListProbeResult.Authoritative(
                new HistoryListResponse
                {
                    Count = 1,
                    Jobs =
                    [
                        new HistoryJob
                        {
                            JobId = "provider-uid-123",
                            Filename = seededAttempt.BackendFileIdentity!,
                            Status = "completed",
                            StartTime = new DateTimeOffset(
                                DateTime.SpecifyKind(
                                    seededAttempt.ClaimedAtUtc.AddSeconds(1),
                                    DateTimeKind.Utc)).ToUnixTimeSeconds(),
                        },
                    ],
                }));
        using ServiceProvider provider = CreateQueueProvider(printers.Object);
        var reconciler = new QueueReconciliationService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<QueueReconciliationService>.Instance);

        await reconciler.ReconcileStaleAttemptsAsync(CancellationToken.None);

        await using AppDbContext verify = CreateContext();
        QueueDispatchAttempt attempt = await verify.QueueDispatchAttempts
            .SingleAsync(candidate => candidate.Id == seededAttempt.Id);
        attempt.BackendJobId.Should().Be("provider-uid-123");
        attempt.BackendFileIdentity.Should().Be("a.gcode");
        (await verify.PrintJobs.FindAsync(jobId))!.Status.Should().Be(
            PrintJobStatus.Completed);
        printers.Verify(service => service.ProbeHistoryJobAsync(
            It.IsAny<Guid>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExternalPrint_ConcurrentObservers_OneTransactionalWinner()
    {
        await using (AppDbContext seed = CreateContext())
        {
            await SeedPrinterAsync(seed);
        }

        Guid printerId;
        await using (AppDbContext read = CreateContext())
        {
            printerId = await read.Printers.Select(candidate => candidate.Id).SingleAsync();
        }

        await using AppDbContext firstContext = CreateContext();
        await using AppDbContext secondContext = CreateContext();
        IHubContext<PrinterHub> hub = CreateHub();
        var first = new PrintJobCompletionService(
            firstContext,
            hub,
            NullLogger<PrintJobCompletionService>.Instance);
        var second = new PrintJobCompletionService(
            secondContext,
            hub,
            NullLogger<PrintJobCompletionService>.Instance);

        bool[] results = await Task.WhenAll(
            first.EnsureExternalPrintJobExistsAsync(printerId, "external.gcode"),
            second.EnsureExternalPrintJobExistsAsync(printerId, "external.gcode"));

        results.Count(result => result).Should().Be(1);
        await using AppDbContext verify = CreateContext();
        (await verify.PrintJobs.CountAsync(job =>
            job.ActiveExternalPrinterId == printerId &&
            job.IsExternalPrint &&
            job.Status == PrintJobStatus.Printing)).Should().Be(1);
    }

    [Fact]
    public async Task AutoDispatch_TwoContextsSameEtag_SecondMutationFails()
    {
        await using (AppDbContext seed = CreateContext())
        {
            await SeedPrinterAsync(seed);
        }

        Guid printerId;
        byte[] expected;
        await using (AppDbContext read = CreateContext())
        {
            PrinterDispatchState state = await read.PrinterDispatchStates.SingleAsync();
            printerId = state.PrinterId;
            expected = state.RowVersion!.ToArray();
        }

        await using AppDbContext firstContext = CreateContext();
        await using AppDbContext secondContext = CreateContext();
        IHubContext<PrinterHub> hub = CreateHub();
        var first = new AutoDispatchService(
            firstContext,
            hub,
            NullLogger<AutoDispatchService>.Instance);
        var second = new AutoDispatchService(
            secondContext,
            hub,
            NullLogger<AutoDispatchService>.Instance);

        _ = await first.CancelAutoAsync(printerId, expected);
        Func<Task> stale = async () =>
            _ = await second.CancelAutoAsync(printerId, expected);

        await stale.Should().ThrowAsync<DbUpdateConcurrencyException>();
    }

    [Fact]
    public async Task ScheduleCreation_ForeignProjectDenied_ThenPersistsInitiatingActor()
    {
        await using AppDbContext db = CreateContext();
        (Guid printerId, Guid jobId, Guid projectId) = await SeedScheduledJobAsync(db);
        string actor = Guid.NewGuid().ToString();
        var authorization = new Mock<IQueueResourceAuthorizationService>();
        authorization.Setup(service => service.CanActorAccessJobAsync(
                actor,
                jobId,
                It.IsAny<PrinterGroupAccessLevel>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        authorization.Setup(service => service.CanActorAccessPrinterAsync(
                actor,
                printerId,
                It.IsAny<PrinterGroupAccessLevel>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        authorization.Setup(service => service.CanActorAccessProjectAsync(
                actor,
                projectId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var service = new JobSchedulingService(
            db,
            NullLogger<JobSchedulingService>.Instance,
            resourceAuthorization: authorization.Object);

        Func<Task> denied = () => service.ScheduleJobAsync(
            jobId,
            DateTime.SpecifyKind(DateTime.UtcNow.AddMinutes(5), DateTimeKind.Unspecified),
            "UTC",
            null,
            1,
            null,
            actor);
        await denied.Should().ThrowAsync<UnauthorizedAccessException>();
        (await db.JobSchedules.CountAsync()).Should().Be(0);

        authorization.Setup(service => service.CanActorAccessProjectAsync(
                actor,
                projectId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        await service.ScheduleJobAsync(
            jobId,
            DateTime.SpecifyKind(DateTime.UtcNow.AddMinutes(5), DateTimeKind.Unspecified),
            "UTC",
            null,
            1,
            null,
            actor);

        JobSchedule schedule = await db.JobSchedules.SingleAsync();
        schedule.InitiatingActorSubject.Should().Be(actor);
    }

    [Fact]
    public async Task DueScheduler_OrdersUrgentFirstWithDeterministicTotalOrder()
    {
        await using AppDbContext db = CreateContext();
        await db.Database.EnsureCreatedAsync();
        DateTime due = DateTime.UtcNow.AddMinutes(-1);
        Guid[] printerIds =
        [
            await SeedPrinterAsync(db),
            await SeedPrinterAsync(db),
            await SeedPrinterAsync(db),
            await SeedPrinterAsync(db),
        ];
        var jobs = new[]
        {
            CreateScheduledJob(PrintJobPriority.Low, queuePosition: 1, due, printerIds[0]),
            CreateScheduledJob(PrintJobPriority.Urgent, queuePosition: 2, due, printerIds[1]),
            CreateScheduledJob(PrintJobPriority.High, queuePosition: 1, due, printerIds[2]),
            CreateScheduledJob(PrintJobPriority.Urgent, queuePosition: 1, due, printerIds[3]),
        };
        db.PrintJobs.AddRange(jobs);
        await db.SaveChangesAsync();

        var observed = new List<Guid>();
        var management = new Mock<IPrintJobManagementService>();
        management.Setup(service => service.DispatchJobAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Returns((
                string jobId,
                string _,
                string? _,
                CancellationToken _) =>
            {
                observed.Add(Guid.Parse(jobId));
                return Task.FromResult(new QueuedPrintJobDto
                {
                    DispatchResult = new DispatchAttemptResultDto
                    {
                        Outcome = DispatchAttemptOutcome.Accepted,
                    },
                });
            });
        var scheduler = new JobSchedulingService(
            db,
            NullLogger<JobSchedulingService>.Instance,
            management.Object,
            AllowAllSchedulingAuthorization());

        await scheduler.TriggerScheduledJobsAsync();

        observed.Should().Equal(
            jobs[3].Id,
            jobs[1].Id,
            jobs[2].Id,
            jobs[0].Id);
    }

    [Fact]
    public async Task SchedulingWallTime_NonUtcDailyRecurrence_PreservesLocalTimeAcrossDst()
    {
        await using AppDbContext db = CreateContext();
        await db.Database.EnsureCreatedAsync();
        Guid printerId = await SeedPrinterAsync(db);
        var job = new PrintJob
        {
            Id = Guid.NewGuid(),
            Name = "DST recurrence",
            AssignedPrinterId = printerId,
            Status = PrintJobStatus.Assigned,
            Priority = (int)PrintJobPriority.Normal,
            QueuePosition = 1,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            QueuedAt = DateTime.UtcNow,
        };
        db.PrintJobs.Add(job);
        await db.SaveChangesAsync();
        string actor = Guid.NewGuid().ToString();
        var management = new Mock<IPrintJobManagementService>();
        management.Setup(service => service.DispatchJobAsync(
                job.Id.ToString(),
                actor,
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueuedPrintJobDto
            {
                DispatchResult = new DispatchAttemptResultDto
                {
                    Outcome = DispatchAttemptOutcome.Accepted,
                },
            });
        Exception? schedulerException = null;
        var schedulerLogger = new Mock<ILogger<JobSchedulingService>>();
        schedulerLogger.Setup(logger => logger.Log(
                It.IsAny<LogLevel>(),
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()))
            .Callback(new InvocationAction(invocation =>
            {
                if (invocation.Arguments[3] is Exception exception)
                {
                    schedulerException = exception;
                }
            }));
        var service = new JobSchedulingService(
            db,
            schedulerLogger.Object,
            management.Object,
            AllowAllSchedulingAuthorization());

        ScheduledJobDto created = await service.ScheduleJobAsync(
            job.Id,
            new DateTime(2026, 3, 7, 9, 30, 0, DateTimeKind.Unspecified),
            "America/New_York",
            "Daily",
            1,
            null,
            actor);

        created.ScheduledStartTimeUtc.Should().Be(
            new DateTime(2026, 3, 7, 14, 30, 0, DateTimeKind.Utc));
        created.ScheduledLocalTime.Should().Be(
            new DateTime(2026, 3, 7, 9, 30, 0, DateTimeKind.Unspecified));

        await service.TriggerScheduledJobsAsync();
        schedulerException.Should().BeNull(schedulerException?.ToString());
        db.ChangeTracker.Clear();
        JobSchedule advanced = await db.JobSchedules.SingleAsync(
            schedule => schedule.RootPrintJobId == job.Id);
        advanced.PrintJobId.Should().NotBe(
            job.Id,
            "a recurring occurrence must use a fresh dispatchable job");
        advanced.ScheduledStartTime.Should().Be(
            new DateTime(2026, 3, 8, 13, 30, 0, DateTimeKind.Utc));
        service.ConvertFromUtc(
                advanced.ScheduledStartTime,
                "America/New_York")
            .Should().Be(new DateTime(2026, 3, 8, 9, 30, 0));
    }

    [Theory]
    [InlineData(2026, 3, 8, 2, 30)]
    [InlineData(2026, 11, 1, 1, 30)]
    public async Task SchedulingWallTime_InvalidOrAmbiguousDstInput_FailsClosed(
        int year,
        int month,
        int day,
        int hour,
        int minute)
    {
        await using AppDbContext db = CreateContext();
        await db.Database.EnsureCreatedAsync();
        Guid printerId = await SeedPrinterAsync(db);
        var job = new PrintJob
        {
            Id = Guid.NewGuid(),
            Name = "DST invalid",
            AssignedPrinterId = printerId,
            Status = PrintJobStatus.Assigned,
            Priority = (int)PrintJobPriority.Normal,
            QueuePosition = 1,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            QueuedAt = DateTime.UtcNow,
        };
        db.PrintJobs.Add(job);
        await db.SaveChangesAsync();
        var service = new JobSchedulingService(
            db,
            NullLogger<JobSchedulingService>.Instance,
            resourceAuthorization: AllowAllSchedulingAuthorization());

        Func<Task> schedule = () => service.ScheduleJobAsync(
            job.Id,
            new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified),
            "America/New_York",
            null,
            1,
            null,
            Guid.NewGuid().ToString());

        await schedule.Should().ThrowAsync<ArgumentException>();
        (await db.JobSchedules.CountAsync()).Should().Be(0);
    }

    [Fact]
    public void PhysicalRoutes_HaveExactQueuePermissionMatrix()
    {
        var expected = new Dictionary<string, string>
        {
            [nameof(PrintersController.ExcludePrintJobObjectAsync)] = PrintFarmerPermissions.Queue.Start,
            [nameof(PrintersController.HomeAsync)] = PrintFarmerPermissions.Queue.Start,
            [nameof(PrintersController.HomeXYAsync)] = PrintFarmerPermissions.Queue.Start,
            [nameof(PrintersController.HomeZAsync)] = PrintFarmerPermissions.Queue.Start,
            [nameof(PrintersController.SetTempsAsync)] = PrintFarmerPermissions.Queue.Start,
            [nameof(PrintersController.MoveAsync)] = PrintFarmerPermissions.Queue.Start,
            [nameof(PrintersController.MoveToAsync)] = PrintFarmerPermissions.Queue.Start,
            [nameof(PrintersController.PauseAsync)] = PrintFarmerPermissions.Queue.Start,
            [nameof(PrintersController.ResumeAsync)] = PrintFarmerPermissions.Queue.Start,
            [nameof(PrintersController.CancelAsync)] = PrintFarmerPermissions.Queue.Cancel,
            [nameof(PrintersController.EmergencyStopAsync)] = PrintFarmerPermissions.Queue.Cancel,
            [nameof(PrintersController.StopAsync)] = PrintFarmerPermissions.Queue.Cancel,
            [nameof(PrintersController.FirmwareRestartAsync)] = PrintFarmerPermissions.Queue.Start,
            [nameof(PrintersController.DisableMotorsAsync)] = PrintFarmerPermissions.Queue.Start,
            [nameof(PrintersController.SaveZOffsetAsync)] = PrintFarmerPermissions.Queue.Start,
            [nameof(PrintersController.LoadFilamentAsync)] = PrintFarmerPermissions.Queue.Start,
            [nameof(PrintersController.UnloadFilamentAsync)] = PrintFarmerPermissions.Queue.Start,
            [nameof(PrintersController.ChangeFilamentAsync)] = PrintFarmerPermissions.Queue.Start,
            [nameof(PrintersController.MmuChangeToolAsync)] = PrintFarmerPermissions.Queue.Start,
            [nameof(PrintersController.MmuEjectAsync)] = PrintFarmerPermissions.Queue.Start,
            [nameof(PrintersController.MmuLoadAsync)] = PrintFarmerPermissions.Queue.Start,
            [nameof(PrintersController.MmuHomeAsync)] = PrintFarmerPermissions.Queue.Start,
            [nameof(PrintersController.MmuSelectToolAsync)] = PrintFarmerPermissions.Queue.Start,
            [nameof(PrintersController.MmuRecoverAsync)] = PrintFarmerPermissions.Queue.Start,
            [nameof(PrintersController.SetActiveSpoolAsync)] = PrintFarmerPermissions.Queue.Write,
            [nameof(PrintersController.SetToolheadSpoolAsync)] = PrintFarmerPermissions.Queue.Write,
            [nameof(PrintersController.ClearToolheadSpoolAsync)] = PrintFarmerPermissions.Queue.Write,
            [nameof(PrintersController.EnsureMmuToolheadsAsync)] = PrintFarmerPermissions.Queue.Write,
            [nameof(PrintersController.UploadGcodeAsync)] = PrintFarmerPermissions.Queue.Write,
            [nameof(PrintersController.StartPrintAsync)] = PrintFarmerPermissions.Queue.Start,
            [nameof(PrintersController.EnableCameraAsync)] = PrintFarmerPermissions.Queue.Write,
            [nameof(PrintersController.DisableCameraAsync)] = PrintFarmerPermissions.Queue.Write,
            [nameof(PrintersController.DeleteHistoryJobAsync)] = PrintFarmerPermissions.Queue.Write,
        };

        foreach ((string methodName, string permission) in expected)
        {
            var method = typeof(PrintersController).GetMethod(methodName);
            method.Should().NotBeNull();
            method!.GetCustomAttributes(typeof(RequirePermissionAttribute), inherit: true)
                .Cast<RequirePermissionAttribute>()
                .Select(attribute => attribute.Permission)
                .Should()
                .Contain(permission, $"{methodName} must require {permission}");
        }
    }

    [Fact]
    public async Task PhysicalRoutes_ResourceDenied_ProduceZeroBackendEffects()
    {
        Guid printerId = Guid.NewGuid();
        var printers = new Mock<IPrintersService>(MockBehavior.Strict);
        var actuation = new Mock<IPrinterPhysicalActuationService>();
        var denied = new PrinterActuationResult(
            PrinterActuationResultCode.PrinterNotFound,
            Detail: "resource denied");
        actuation.Setup(service => service.AcquireDirectAsync(
                printerId,
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(denied);
        actuation.Setup(service => service.AcquireActiveAsync(
                printerId,
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(denied);
        actuation.Setup(service => service.QueueLifecycleAsync(
                printerId,
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(denied);
        PrintersController controller = CreateDeniedController(
            printers.Object,
            actuation.Object);
        var upload = new FormFile(
            new MemoryStream("G28"u8.ToArray()),
            0,
            3,
            "file",
            "safe.gcode");

        _ = await controller.HomeAsync(printerId, CancellationToken.None);
        _ = await controller.HomeXYAsync(printerId, CancellationToken.None);
        _ = await controller.HomeZAsync(printerId, CancellationToken.None);
        _ = await controller.SetTempsAsync(
            printerId,
            new TempTargets(200, 60),
            CancellationToken.None);
        _ = await controller.MoveAsync(
            printerId,
            new MoveRequest(1, null, null, 1000),
            CancellationToken.None);
        _ = await controller.MoveToAsync(
            printerId,
            new MoveRequest(1, 2, 3, 1000),
            CancellationToken.None);
        _ = await controller.PauseAsync(printerId, CancellationToken.None);
        _ = await controller.ResumeAsync(printerId, CancellationToken.None);
        _ = await controller.CancelAsync(printerId, CancellationToken.None);
        _ = await controller.EmergencyStopAsync(printerId, CancellationToken.None);
        _ = await controller.StopAsync(printerId, CancellationToken.None);
        _ = await controller.FirmwareRestartAsync(printerId, CancellationToken.None);
        _ = await controller.DisableMotorsAsync(printerId, CancellationToken.None);
        _ = await controller.LoadFilamentAsync(printerId, CancellationToken.None);
        _ = await controller.UnloadFilamentAsync(printerId, null, CancellationToken.None);
        _ = await controller.SetToolheadSpoolAsync(
            printerId,
            0,
            new SetActiveSpoolRequest { SpoolId = 1 },
            Mock.Of<IPrinterToolheadSwapValidator>(),
            Mock.Of<Farm.Infrastructure.Services.OperatorFeatures.IOperatorFeatureGate>(),
            CancellationToken.None);
        _ = await controller.ChangeFilamentAsync(printerId, CancellationToken.None);
        _ = await controller.MmuChangeToolAsync(printerId, 1, CancellationToken.None);
        _ = await controller.MmuEjectAsync(printerId, CancellationToken.None);
        _ = await controller.MmuLoadAsync(printerId, CancellationToken.None);
        _ = await controller.MmuHomeAsync(printerId, CancellationToken.None);
        _ = await controller.MmuSelectToolAsync(printerId, 1, CancellationToken.None);
        _ = await controller.MmuRecoverAsync(printerId, CancellationToken.None);
        _ = await controller.SetActiveSpoolAsync(
            printerId,
            new SetActiveSpoolRequest { SpoolId = 42 },
            CancellationToken.None);
        _ = await controller.ClearToolheadSpoolAsync(printerId, 0, CancellationToken.None);
        _ = await controller.EnableCameraAsync(printerId, CancellationToken.None);
        _ = await controller.DisableCameraAsync(printerId, CancellationToken.None);
        _ = await controller.DeleteHistoryJobAsync(printerId, "job-1", CancellationToken.None);
        _ = await controller.UploadGcodeAsync(
            printerId,
            upload,
            CancellationToken.None);
        _ = await controller.ExcludePrintJobObjectAsync(
            printerId,
            new ExcludePrintJobObjectRequest("part"),
            CancellationToken.None);

        // FindByIdAsync is invoked by both SetActiveSpoolAsync and ClearToolheadSpoolAsync
        // (each fetches the printer for the If-Match precondition before routing through the
        // actuation service, which then denies the request) — exactly twice, never more.
        printers.Verify(service => service.FindByIdAsync(
            printerId,
            It.IsAny<CancellationToken>()), Times.Exactly(2));

        // UnloadFilamentAsync now enforces the same PrinterGroup access check as the other
        // physical-actuation routes (issue #1292). This controller has no
        // IQueueResourceAuthorizationService wired, so the check fails closed and the
        // backend service must never be invoked here — the opposite of the pre-fix behavior
        // where unload requests bypassed the resource check entirely.
        printers.Verify(service => service.UnloadFilamentAsync(
            It.IsAny<Guid>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Never);

        // SetToolheadSpoolAsync (controller) also enforces its own fail-closed
        // CanAccessPrinterAsync guard ahead of its direct backend call (issue #1292 round 3):
        // it does not route through the physical-actuation service, so it needed its own
        // explicit gate. Assert the backend spool bind is never reached under this
        // denied-authorization construction.
        printers.Verify(service => service.SetToolheadSpoolAsync(
            It.IsAny<Guid>(),
            It.IsAny<int>(),
            It.IsAny<int>(),
            It.IsAny<FilamentSwapOverrideContext?>(),
            It.IsAny<SpoolBindPolicy>(),
            It.IsAny<CancellationToken>()), Times.Never);

        // ClearToolheadSpoolAsync routes through ExecuteDirectCommandControlAsync /
        // BeginPhysicalControlAsync, whose actuation.AcquireDirectAsync mock is configured
        // above to always deny — the backend clear must never be invoked either.
        printers.Verify(service => service.ClearToolheadSpoolAsync(
            It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);

        // EnableCameraAsync/DisableCameraAsync/DeleteHistoryJobAsync each gained their own
        // fail-closed CanAccessPrinterAsync guard in round 3/4 remediation (issue #1292) —
        // assert the underlying backend calls are never reached under denied authorization.
        printers.Verify(service => service.EnableCameraAsync(
            It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        printers.Verify(service => service.DisableCameraAsync(
            It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        printers.Verify(service => service.DeleteHistoryJobAsync(
            It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        printers.VerifyNoOtherCalls();
    }

    private AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connectionString)
            .Options;
        var db = new AppDbContext(options);
        db.Database.ExecuteSqlRaw("PRAGMA foreign_keys = OFF;");
        return db;
    }

    private ServiceProvider CreateQueueProvider(IPrintersService printers)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AppDbContext>(options => options.UseSqlite(_connectionString));
        services.AddScoped<IDbOutboxSequenceAllocator, DbOutboxSequenceAllocator>();
        services.AddSingleton(printers);
        return services.BuildServiceProvider();
    }

    private static DispatchClaimService CreateClaimService(
        AppDbContext db,
        Guid printerId) =>
        new(
            db,
            DispatchTestDoubles.OnlineIdleReader(printerId),
            new DbOutboxSequenceAllocator(),
            NullLogger<DispatchClaimService>.Instance,
            DispatchTestDoubles.TelemetryFreshnessPolicy());

    private async Task<ControlFixture> SeedControlFixtureAsync(
        AppDbContext db,
        bool includeControlCommand)
    {
        Guid printerId = await SeedPrinterAsync(db);
        var gcode = new GcodeFile
        {
            Id = Guid.NewGuid(),
            Name = "control.gcode",
            FileName = "control.gcode",
            FilePath = "/",
            FileSizeBytes = 10,
        };
        var jobA = new PrintJob
        {
            Id = Guid.NewGuid(),
            Name = "A",
            GcodeFileId = gcode.Id,
            AssignedPrinterId = printerId,
            Status = PrintJobStatus.Starting,
            JobKind = JobKind.Standard,
            Priority = (int)PrintJobPriority.Normal,
            QueuePosition = 1,
            CreatedAt = DateTime.UtcNow.AddMinutes(-30),
            UpdatedAt = DateTime.UtcNow.AddMinutes(-30),
            QueuedAt = DateTime.UtcNow.AddMinutes(-30),
            ActualStartTime = DateTime.UtcNow.AddMinutes(-30),
        };
        var jobB = new PrintJob
        {
            Id = Guid.NewGuid(),
            Name = "B",
            GcodeFileId = gcode.Id,
            AssignedPrinterId = printerId,
            Status = PrintJobStatus.Assigned,
            JobKind = JobKind.Standard,
            Priority = (int)PrintJobPriority.High,
            QueuePosition = 2,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            QueuedAt = DateTime.UtcNow,
        };
        var attemptA = new QueueDispatchAttempt
        {
            Id = Guid.NewGuid(),
            PrintJobId = jobA.Id,
            PrinterId = printerId,
            PrinterConfigRevision = 1,
            AttemptNumber = 1,
            ActorSubject = "operator-a",
            StartPathKind = "Manual",
            ClaimedAtUtc = DateTime.UtcNow.AddMinutes(-30),
            Outcome = DispatchAttemptOutcome.Unknown,
            RequiresReconciliation = true,
            BackendFileName = "a.gcode",
            BackendFileIdentity = "a.gcode",
            BackendCallPhase = DispatchBackendCallPhase.AwaitingReconciliation,
            UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-30),
        };
        var attemptB = new QueueDispatchAttempt
        {
            Id = Guid.NewGuid(),
            PrintJobId = jobA.Id,
            PrinterId = printerId,
            PrinterConfigRevision = 1,
            AttemptNumber = 2,
            ActorSubject = "operator-b",
            StartPathKind = "Manual",
            ClaimedAtUtc = DateTime.UtcNow,
            Outcome = DispatchAttemptOutcome.InProgress,
            BackendFileName = "b.gcode",
            BackendFileIdentity = "b.gcode",
            BackendCallPhase = DispatchBackendCallPhase.PreCall,
            UpdatedAtUtc = DateTime.UtcNow,
        };
        db.GcodeFiles.Add(gcode);
        db.PrintJobs.AddRange(jobA, jobB);
        db.QueueDispatchAttempts.AddRange(attemptA, attemptB);
        PrinterDispatchState state = await db.PrinterDispatchStates
            .SingleAsync(candidate => candidate.PrinterId == printerId);
        state.ActiveJobId = jobA.Id;
        state.ActiveDispatchAttemptId = includeControlCommand
            ? attemptA.Id
            : attemptB.Id;
        await db.SaveChangesAsync();

        Guid? commandId = null;
        if (includeControlCommand)
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            commandId = Guid.NewGuid();
            db.QueueDispatchOutbox.Add(new QueueDispatchOutbox
            {
                Id = commandId.Value,
                Sequence = await new DbOutboxSequenceAllocator().AllocateAsync(db),
                AggregateType = nameof(PrintJob),
                AggregateId = jobA.Id,
                PrinterId = printerId,
                AttemptId = attemptA.Id,
                EventType = BackendControlCommandConsumerService.EventType,
                SchemaVersion = "1",
                PayloadJson = JsonSerializer.Serialize(new
                {
                    jobId = jobA.Id,
                    printerId,
                    attemptId = attemptA.Id,
                    backendJobId = (string?)null,
                    backendFileIdentity = "a.gcode",
                    operation = "cancel",
                    actorSubject = "operator-a",
                }),
                Status = QueueOutboxEventStatus.Pending,
                CreatedAtUtc = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }

        return new ControlFixture(
            printerId,
            jobA.Id,
            jobB.Id,
            attemptA.Id,
            attemptB.Id,
            commandId);
    }

    private static async Task<Guid> SeedPrinterAsync(AppDbContext db)
    {
        await db.Database.EnsureCreatedAsync();
        string token = Guid.NewGuid().ToString("N");
        var manufacturer = new Manufacturer
        {
            Id = Guid.NewGuid(),
            Name = $"Race maker {token}",
        };
        var model = new PrinterModel
        {
            Id = Guid.NewGuid(),
            ManufacturerId = manufacturer.Id,
            Name = $"Race model {token}",
        };
        var printer = new Printer
        {
            Id = Guid.NewGuid(),
            Name = $"Race printer {token}",
            ServerUrl = $"http://race-{Guid.NewGuid():N}",
            ManufacturerId = manufacturer.Id,
            ModelId = model.Id,
            IsEnabled = true,
            IsAvailable = true,
            ConfigurationRevision = 1,
        };
        db.Manufacturers.Add(manufacturer);
        db.PrinterModels.Add(model);
        db.Printers.Add(printer);
        db.PrinterDispatchStates.Add(new PrinterDispatchState { PrinterId = printer.Id });
        await db.SaveChangesAsync();
        return printer.Id;
    }

    private static async Task<(Guid PrinterId, Guid JobId, Guid ProjectId)>
        SeedScheduledJobAsync(AppDbContext db)
    {
        Guid printerId = await SeedPrinterAsync(db);
        Guid projectId = Guid.NewGuid();
        var gcode = new GcodeFile
        {
            Id = Guid.NewGuid(),
            Name = "scheduled.gcode",
            FileName = "scheduled.gcode",
            FilePath = "/",
            FileSizeBytes = 10,
        };
        var project = new CalibrationProject
        {
            Id = projectId,
            OwnerUserId = Guid.NewGuid(),
            Name = "Foreign project",
            PrinterId = printerId,
        };
        var job = new PrintJob
        {
            Id = Guid.NewGuid(),
            Name = "Scheduled",
            GcodeFileId = gcode.Id,
            AssignedPrinterId = printerId,
            CalibrationProjectId = projectId,
            Status = PrintJobStatus.Assigned,
            Priority = (int)PrintJobPriority.Normal,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            QueuedAt = DateTime.UtcNow,
        };
        db.GcodeFiles.Add(gcode);
        db.CalibrationProjects.Add(project);
        db.PrintJobs.Add(job);
        await db.SaveChangesAsync();
        return (printerId, job.Id, projectId);
    }

    private static PrintJob CreateScheduledJob(
        PrintJobPriority priority,
        int queuePosition,
        DateTime due,
        Guid printerId)
    {
        var job = new PrintJob
        {
            Id = Guid.NewGuid(),
            Name = $"{priority}-{queuePosition}",
            Status = PrintJobStatus.Assigned,
            Priority = (int)priority,
            QueuePosition = queuePosition,
            AssignedPrinterId = printerId,
            CreatedAt = due.AddMinutes(-1),
            UpdatedAt = due.AddMinutes(-1),
            QueuedAt = due.AddMinutes(-1),
        };
        job.Schedule = new JobSchedule
        {
            Id = Guid.NewGuid(),
            PrintJobId = job.Id,
            PrintJob = job,
            ScheduledStartTime = due,
            InitiatingActorSubject = Guid.NewGuid().ToString(),
            RequiresOperatorReauthorization = false,
            RecurrenceInterval = 1,
        };
        return job;
    }

    private static IQueueResourceAuthorizationService
        AllowAllSchedulingAuthorization()
    {
        var authorization = new Mock<IQueueResourceAuthorizationService>();
        authorization.Setup(service => service.CanActorAccessJobAsync(
                It.IsAny<string>(),
                It.IsAny<Guid>(),
                It.IsAny<PrinterGroupAccessLevel>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        authorization.Setup(service => service.CanActorAccessPrinterAsync(
                It.IsAny<string>(),
                It.IsAny<Guid>(),
                It.IsAny<PrinterGroupAccessLevel>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        authorization.Setup(service => service.CanActorAccessProjectAsync(
                It.IsAny<string>(),
                It.IsAny<Guid>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        return authorization.Object;
    }

    private static IHubContext<PrinterHub> CreateHub()
    {
        var client = new Mock<IClientProxy>();
        client.Setup(proxy => proxy.SendCoreAsync(
                It.IsAny<string>(),
                It.IsAny<object?[]>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var clients = new Mock<IHubClients>();
        clients.Setup(value => value.Group(It.IsAny<string>())).Returns(client.Object);
        var hub = new Mock<IHubContext<PrinterHub>>();
        hub.SetupGet(value => value.Clients).Returns(clients.Object);
        return hub.Object;
    }

    private static PrintersController CreateDeniedController(
        IPrintersService printers,
        IPrinterPhysicalActuationService actuation)
    {
        byte[] rowVersion = RevisionETag.EncodeBytes(1);
        Mock.Get(printers).Setup(service => service.FindByIdAsync(
                It.IsAny<Guid>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid printerId, CancellationToken _) => new Printer
            {
                Id = printerId,
                Name = "Denied printer",
                ServerUrl = "http://denied.invalid",
                Revision = 1,
            });
        Mock.Get(printers).Setup(service => service.UnloadFilamentAsync(
                It.IsAny<Guid>(),
                It.IsAny<int?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FilamentUnloadResult(true, "unloaded"));
        var db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase($"denied-{Guid.NewGuid():N}")
                .Options);
        var controller = new PrintersController(
            logger: Mock.Of<Microsoft.Extensions.Logging.ILogger<PrintersController>>(),
            printersService: printers,
            catalogService: Mock.Of<Farm.Web.Api.Services.Catalog.ICatalogService>(),
            validator: Mock.Of<IValidator<CreatePrinterFromDiscoveryDto>>(),
            discoveryProxyService: Mock.Of<
                Farm.Infrastructure.Services.Discovery.IDiscoveryProxyService>(),
            discoverySessions: Mock.Of<
                Farm.Infrastructure.Services.Discovery.IDiscoverySessionRegistry>(),
            printerBackendCapabilitiesService: Mock.Of<IPrinterBackendCapabilitiesService>(),
            backendClientFactory: Mock.Of<IBackendClientFactory>(),
            httpClientFactory: Mock.Of<IHttpClientFactory>(),
            egressGuard: Farm.Web.Api.Tests.TestInfrastructure.TestHelpers.PermissiveEgressGuard(),
            obicoServerAssignment: Mock.Of<
                Farm.Infrastructure.Services.FailureDetection.IObicoServerAssignmentService>(),
            settingsService: Mock.Of<Farm.Infrastructure.Settings.ISettingsService>(),
            printerSessionTimelineService: Mock.Of<IPrinterSessionTimelineService>(),
            telemetryService: Mock.Of<IPrintFarmerTelemetryService>(),
            bedTypeService: Mock.Of<Farm.Infrastructure.Services.BedTypes.IBedTypeService>(),
            physicalActuationService: actuation,
            appDbContext: db);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                ], "test")),
            },
        };
        controller.Request.Headers.IfMatch =
            $"\"{Convert.ToBase64String(rowVersion)}\"";
        return controller;
    }

    private sealed record ControlFixture(
        Guid PrinterId,
        Guid JobAId,
        Guid JobBId,
        Guid AttemptAId,
        Guid AttemptBId,
        Guid? ControlCommandId);
}
