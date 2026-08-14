using System.Text.Json;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos.PrintQueue;
using Farm.Infrastructure.Services;
using Farm.Infrastructure.Services.Interfaces;
using Farm.Infrastructure.Services.Queue;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Farm.Web.Api.Tests.Dispatch;

public sealed class SchedulerOccurrenceSemanticsTests
{
    [Theory]
    [InlineData(false, DispatchAttemptOutcome.Accepted)]
    [InlineData(false, DispatchAttemptOutcome.Rejected)]
    [InlineData(false, DispatchAttemptOutcome.FailedBeforeStart)]
    [InlineData(false, DispatchAttemptOutcome.Unknown)]
    [InlineData(true, DispatchAttemptOutcome.Accepted)]
    [InlineData(true, DispatchAttemptOutcome.Rejected)]
    [InlineData(true, DispatchAttemptOutcome.FailedBeforeStart)]
    [InlineData(true, DispatchAttemptOutcome.Unknown)]
    public async Task TriggerScheduledJobs_OutcomeControlsOccurrenceConsumption(
        bool recurring,
        DispatchAttemptOutcome outcome)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        DbContextOptions<AppDbContext> options =
            new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(connection)
                .Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        DateTime due = new(2026, 7, 27, 12, 0, 0, DateTimeKind.Utc);
        Guid actorId = Guid.NewGuid();
        (PrintJob job, JobSchedule _) = await SeedScheduleAsync(
            db,
            actorId,
            due,
            recurring);
        Guid attemptId = Guid.NewGuid();
        var management = new Mock<IPrintJobManagementService>();
        management.Setup(service => service.DispatchJobAsync(
                job.Id.ToString(),
                actorId.ToString(),
                null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueuedPrintJobDto
            {
                Id = job.Id.ToString(),
                DispatchResult = new DispatchAttemptResultDto
                {
                    AttemptId = attemptId,
                    AttemptNumber = 1,
                    Outcome = outcome,
                    RequiresReconciliation =
                        outcome == DispatchAttemptOutcome.Unknown,
                },
            });
        JobSchedulingService service = CreateService(db, management.Object);

        await service.TriggerScheduledJobsAsync();
        if (outcome == DispatchAttemptOutcome.Unknown)
        {
            await service.TriggerScheduledJobsAsync();
        }

        db.ChangeTracker.Clear();
        JobSchedule persisted = await db.JobSchedules
            .Include(candidate => candidate.PrintJob)
            .SingleAsync(candidate => candidate.RootPrintJobId == job.Id);
        List<JobExecution> executions = await db.JobExecutions
            .Where(execution => execution.JobScheduleId == persisted.Id)
            .ToListAsync();

        if (outcome == DispatchAttemptOutcome.Accepted && recurring)
        {
            persisted.IsActive.Should().BeTrue();
            persisted.ScheduledStartTime.Should().Be(due.AddDays(1));
            persisted.PrintJobId.Should().NotBe(job.Id);
            persisted.PrintJob.Status.Should().Be(PrintJobStatus.Assigned);
            (await db.PrintJobs.CountAsync()).Should().Be(2);
        }
        else if (outcome == DispatchAttemptOutcome.Accepted)
        {
            persisted.IsActive.Should().BeFalse();
            persisted.PrintJobId.Should().Be(job.Id);
        }
        else
        {
            persisted.IsActive.Should().BeTrue();
            persisted.ScheduledStartTime.Should().Be(due);
            persisted.PrintJobId.Should().Be(job.Id);
        }

        executions.Should().ContainSingle();
        executions[0].OccurrencePrintJobId.Should().Be(job.Id);
        executions[0].Status.Should().Be(outcome switch
        {
            DispatchAttemptOutcome.Accepted => "Completed",
            DispatchAttemptOutcome.Rejected => "Rejected",
            DispatchAttemptOutcome.FailedBeforeStart => "FailedBeforeStart",
            _ => "Unknown",
        });
        management.Verify(service => service.DispatchJobAsync(
                job.Id.ToString(),
                actorId.ToString(),
                null,
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task UnknownOccurrence_ReconciledAccepted_ConsumesWithoutRedispatch()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        DbContextOptions<AppDbContext> options =
            new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(connection)
                .Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        Guid actorId = Guid.NewGuid();
        DateTime due = DateTime.UtcNow.AddMinutes(-1);
        (PrintJob job, JobSchedule schedule) = await SeedScheduleAsync(
            db,
            actorId,
            due,
            recurring: false);
        var attempt = new QueueDispatchAttempt
        {
            Id = Guid.NewGuid(),
            PrintJobId = job.Id,
            PrinterId = job.AssignedPrinterId!.Value,
            PrinterConfigRevision = 1,
            AttemptNumber = 1,
            ActorSubject = actorId.ToString(),
            StartPathKind = "Scheduled",
            ClaimedAtUtc = DateTime.UtcNow,
            Outcome = DispatchAttemptOutcome.Unknown,
            BackendCallPhase =
                DispatchBackendCallPhase.AwaitingReconciliation,
            UpdatedAtUtc = DateTime.UtcNow,
        };
        db.QueueDispatchAttempts.Add(attempt);
        await db.SaveChangesAsync();
        var management = new Mock<IPrintJobManagementService>();
        management.Setup(service => service.DispatchJobAsync(
                job.Id.ToString(),
                actorId.ToString(),
                null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(DispatchResponse(
                job.Id,
                attempt.Id,
                DispatchAttemptOutcome.Unknown));
        JobSchedulingService service = CreateService(db, management.Object);

        await service.TriggerScheduledJobsAsync();
        attempt.Outcome = DispatchAttemptOutcome.Accepted;
        attempt.BackendCallPhase = DispatchBackendCallPhase.Accepted;
        attempt.BackendAcceptedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();
        await service.TriggerScheduledJobsAsync();

        db.ChangeTracker.Clear();
        (await db.JobSchedules.SingleAsync(candidate => candidate.Id == schedule.Id))
            .IsActive.Should().BeFalse();
        management.Verify(service => service.DispatchJobAsync(
                job.Id.ToString(),
                actorId.ToString(),
                null,
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RejectedOccurrence_RetriesAndCanLaterBeAccepted()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        DbContextOptions<AppDbContext> options =
            new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(connection)
                .Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        Guid actorId = Guid.NewGuid();
        (PrintJob job, JobSchedule schedule) = await SeedScheduleAsync(
            db,
            actorId,
            DateTime.UtcNow.AddMinutes(-1),
            recurring: false);
        var management = new Mock<IPrintJobManagementService>();
        management.SetupSequence(service => service.DispatchJobAsync(
                job.Id.ToString(),
                actorId.ToString(),
                null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(DispatchResponse(
                job.Id,
                Guid.NewGuid(),
                DispatchAttemptOutcome.Rejected))
            .ReturnsAsync(DispatchResponse(
                job.Id,
                Guid.NewGuid(),
                DispatchAttemptOutcome.Accepted));
        JobSchedulingService service = CreateService(db, management.Object);

        await service.TriggerScheduledJobsAsync();
        await service.TriggerScheduledJobsAsync();

        db.ChangeTracker.Clear();
        (await db.JobSchedules.SingleAsync(candidate => candidate.Id == schedule.Id))
            .IsActive.Should().BeFalse();
        (await db.JobExecutions.CountAsync(execution =>
            execution.JobScheduleId == schedule.Id)).Should().Be(2);
        management.Verify(service => service.DispatchJobAsync(
                job.Id.ToString(),
                actorId.ToString(),
                null,
                It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task PostAcceptClientException_UsesDurableAttemptAndConsumesOccurrence()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        DbContextOptions<AppDbContext> options =
            new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(connection)
                .Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        Guid actorId = Guid.NewGuid();
        (PrintJob job, JobSchedule schedule) = await SeedScheduleAsync(
            db,
            actorId,
            DateTime.UtcNow.AddMinutes(-1),
            recurring: false);
        var management = new Mock<IPrintJobManagementService>();
        management.Setup(service => service.DispatchJobAsync(
                job.Id.ToString(),
                actorId.ToString(),
                null,
                It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                db.QueueDispatchAttempts.Add(new QueueDispatchAttempt
                {
                    Id = Guid.NewGuid(),
                    PrintJobId = job.Id,
                    PrinterId = job.AssignedPrinterId!.Value,
                    PrinterConfigRevision = 1,
                    AttemptNumber = 1,
                    ActorSubject = actorId.ToString(),
                    StartPathKind = "Scheduled",
                    ClaimedAtUtc = DateTime.UtcNow,
                    BackendAcceptedAtUtc = DateTime.UtcNow,
                    Outcome = DispatchAttemptOutcome.Accepted,
                    BackendCallPhase = DispatchBackendCallPhase.PostAccept,
                    UpdatedAtUtc = DateTime.UtcNow,
                });
                await db.SaveChangesAsync();
                throw new InvalidOperationException(
                    "secret=/private/path?token=do-not-persist");
            });
        JobSchedulingService service = CreateService(db, management.Object);

        await service.TriggerScheduledJobsAsync();

        db.ChangeTracker.Clear();
        (await db.JobSchedules.SingleAsync(candidate => candidate.Id == schedule.Id))
            .IsActive.Should().BeFalse();
        JobExecution execution = await db.JobExecutions.SingleAsync(candidate =>
            candidate.JobScheduleId == schedule.Id);
        execution.Status.Should().Be("Completed");
        execution.Message.Should().NotContain("private");
        execution.Message.Should().NotContain("token");
    }

    [Fact]
    public async Task ExecutionHistory_UnspecifiedProviderValues_SerializeAsUtc()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        DbContextOptions<AppDbContext> options =
            new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(connection)
                .Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        Guid actorId = Guid.NewGuid();
        (PrintJob job, JobSchedule schedule) = await SeedScheduleAsync(
            db,
            actorId,
            DateTime.UtcNow.AddDays(1),
            recurring: false);
        DateTime scheduled = new(
            2026,
            11,
            1,
            8,
            30,
            0,
            DateTimeKind.Unspecified);
        DateTime actual = scheduled.AddSeconds(5);
        db.JobExecutions.Add(new JobExecution
        {
            Id = Guid.NewGuid(),
            JobScheduleId = schedule.Id,
            OccurrencePrintJobId = job.Id,
            ScheduledExecutionTime = scheduled,
            ActualStartTime = actual,
            Status = "Completed",
            CreatedAt = scheduled,
            UpdatedAt = actual,
        });
        await db.SaveChangesAsync();
        JobSchedulingService service = CreateService(
            db,
            Mock.Of<IPrintJobManagementService>());

        IReadOnlyList<JobExecutionDto>? history =
            await service.GetExecutionHistoryAsync(
                job.Id,
                actorId.ToString());

        JobExecutionDto execution = history.Should().ContainSingle().Subject;
        execution.ScheduledExecutionTime.Kind.Should().Be(DateTimeKind.Utc);
        execution.ActualStartTime!.Value.Kind.Should().Be(DateTimeKind.Utc);
        using JsonDocument wire = JsonDocument.Parse(JsonSerializer.Serialize(
            execution,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        string scheduledWire = wire.RootElement
            .GetProperty("scheduledExecutionTime")
            .GetString()!;
        string actualWire = wire.RootElement
            .GetProperty("actualStartTime")
            .GetString()!;
        scheduledWire.Should().Be("2026-11-01T08:30:00Z");
        actualWire.Should().Be("2026-11-01T08:30:05Z");
    }

    [Fact]
    public async Task TriggerScheduledJobs_WeeklyRecurrenceWithOverflowingInterval_DoesNotSilentlyCorruptScheduledDate()
    {
        // Regression test for CodeQL cs/loss-of-precision alert #716.
        //
        // interval = 613,566,756 makes the true (mathematically correct) product
        // 7 * interval = 4,294,967,292 - a magnitude far beyond what DateTime.AddDays
        // can ever represent (DateTime's whole range spans only ~3.65 million days),
        // so a fully-correct computation must fail loudly here rather than
        // "succeed" with a nonsense date.
        //
        // Before the fix, `7 * interval` was computed as 32-bit `int` and silently
        // wrapped (unchecked overflow) to -4, so `AddDays(-4)` would SUCCEED and
        // silently move the schedule 4 days *backward* - silent data corruption
        // that is much worse than an exception, because nothing would ever surface it.
        //
        // After the fix, the multiplication happens in double space, so AddDays
        // receives the true out-of-range magnitude and throws. That throw is caught
        // and logged per-schedule by TriggerScheduledJobsAsync's outer try/catch
        // (it does not propagate to the caller and does not affect other schedules),
        // and - critically - the schedule's ScheduledStartTime is left untouched
        // instead of being silently set to the wrong date.
        const int overflowingInterval = 613_566_756;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        DbContextOptions<AppDbContext> options =
            new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(connection)
                .Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        DateTime due = new(2026, 7, 27, 12, 0, 0, DateTimeKind.Utc);
        Guid actorId = Guid.NewGuid();
        (PrintJob job, JobSchedule _) = await SeedScheduleAsync(
            db,
            actorId,
            due,
            recurring: true,
            recurrencePattern: "Weekly",
            recurrenceInterval: overflowingInterval);
        Guid attemptId = Guid.NewGuid();
        var management = new Mock<IPrintJobManagementService>();
        management.Setup(service => service.DispatchJobAsync(
                job.Id.ToString(),
                actorId.ToString(),
                null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(DispatchResponse(job.Id, attemptId, DispatchAttemptOutcome.Accepted));
        JobSchedulingService service = CreateService(db, management.Object);

        // Must not throw out to the caller: the per-schedule failure is caught and logged.
        await service.TriggerScheduledJobsAsync();

        db.ChangeTracker.Clear();
        JobSchedule persisted = await db.JobSchedules
            .SingleAsync(candidate => candidate.RootPrintJobId == job.Id);

        // The schedule must NOT have silently advanced to a wrong nearby date
        // (e.g. due.AddDays(-4), which is what the pre-fix int-overflow bug would
        // have silently produced). Since the failure happens before the schedule's
        // occurrence is persisted, the original ScheduledStartTime remains intact.
        persisted.ScheduledStartTime.Should().Be(due);
        persisted.PrintJobId.Should().Be(job.Id);
    }

    private static QueuedPrintJobDto DispatchResponse(
        Guid jobId,
        Guid attemptId,
        DispatchAttemptOutcome outcome) =>
        new()
        {
            Id = jobId.ToString(),
            DispatchResult = new DispatchAttemptResultDto
            {
                AttemptId = attemptId,
                AttemptNumber = 1,
                Outcome = outcome,
                RequiresReconciliation =
                    outcome == DispatchAttemptOutcome.Unknown,
            },
        };

    private static JobSchedulingService CreateService(
        AppDbContext db,
        IPrintJobManagementService management)
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
        return new JobSchedulingService(
            db,
            NullLogger<JobSchedulingService>.Instance,
            management,
            authorization.Object);
    }

    private static async Task<(PrintJob Job, JobSchedule Schedule)> SeedScheduleAsync(
        AppDbContext db,
        Guid actorId,
        DateTime due,
        bool recurring,
        string? recurrencePattern = null,
        int recurrenceInterval = 1)
    {
        DateTime now = DateTime.UtcNow;
        var manufacturer = new Manufacturer
        {
            Id = Guid.NewGuid(),
            Name = $"Scheduler maker {Guid.NewGuid():N}",
        };
        var model = new PrinterModel
        {
            Id = Guid.NewGuid(),
            ManufacturerId = manufacturer.Id,
            Name = $"Scheduler model {Guid.NewGuid():N}",
        };
        var printer = new Printer
        {
            Id = Guid.NewGuid(),
            Name = $"Scheduler printer {Guid.NewGuid():N}",
            ServerUrl = $"http://scheduler-{Guid.NewGuid():N}",
            ManufacturerId = manufacturer.Id,
            ModelId = model.Id,
            IsEnabled = true,
            IsAvailable = true,
        };
        var job = new PrintJob
        {
            Id = Guid.NewGuid(),
            Name = "Scheduled occurrence",
            AssignedPrinterId = printer.Id,
            CreatorSubject = actorId.ToString(),
            JobKind = JobKind.Standard,
            Status = PrintJobStatus.Assigned,
            Priority = (int)PrintJobPriority.Normal,
            QueuePosition = 1,
            Copies = 1,
            CreatedAt = now,
            UpdatedAt = now,
            QueuedAt = now,
        };
        var schedule = new JobSchedule
        {
            Id = Guid.NewGuid(),
            PrintJobId = job.Id,
            RootPrintJobId = job.Id,
            ScheduledStartTime = due,
            TimeZone = "UTC",
            RecurrencePattern = recurring ? (recurrencePattern ?? "Daily") : null,
            RecurrenceInterval = recurrenceInterval,
            IsActive = true,
            IsPaused = false,
            InitiatingActorSubject = actorId.ToString(),
            RequiresOperatorReauthorization = false,
            ScheduledAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.AddRange(manufacturer, model, printer, job, schedule);
        db.QueuePositionStates.Add(new QueuePositionState
        {
            ScopeId = printer.Id,
            NextPosition = 1,
        });
        await db.SaveChangesAsync();
        return (job, schedule);
    }
}
