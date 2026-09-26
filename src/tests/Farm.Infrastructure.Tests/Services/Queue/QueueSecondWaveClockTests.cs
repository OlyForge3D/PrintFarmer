// <copyright file="QueueSecondWaveClockTests.cs" company="OlyForge3D">
// Copyright (c) OlyForge3D. All rights reserved.
// </copyright>

using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.AutoDispatch;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.Queue;
using Farm.Infrastructure.Services.Queue.Dispatch;
using Farm.Infrastructure.Services.SignalR;
using Farm.Infrastructure.Tests.Builders;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Farm.Infrastructure.Tests.Services.Queue;

/// <summary>
/// Fake-clock regressions for the second-wave queue TimeProvider migration (#2972).
/// Every case pins a persisted timestamp or boundary to an exact fake instant far from real
/// wall time, so reverting a converted read to <c>DateTime.UtcNow</c> flips the assertion.
/// </summary>
public sealed class QueueSecondWaveClockTests : IAsyncDisposable
{
    private static readonly DateTimeOffset Anchor = new(2031, 4, 5, 6, 7, 8, TimeSpan.Zero);
    private static readonly DateTime AnchorUtc = Anchor.UtcDateTime;

    private readonly string _connectionString =
        $"Data Source=file:second_wave_clock_{Guid.NewGuid():N}?mode=memory&cache=shared;Foreign Keys=False";
    private readonly SqliteConnection _keepAlive;
    private readonly ServiceProvider _provider;
    private long _nextSequence = 1_000_000;

    public QueueSecondWaveClockTests()
    {
        _keepAlive = new SqliteConnection(_connectionString);
        _keepAlive.Open();

        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(options => options.UseSqlite(_connectionString));
        services.AddSingleton<IDbOutboxSequenceAllocator, DbOutboxSequenceAllocator>();
        services.AddSingleton(Mock.Of<IPrintersService>());
        _provider = services.BuildServiceProvider();

        using AppDbContext db = CreateContext();
        db.Database.EnsureCreated();
    }

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _keepAlive.DisposeAsync();
    }

    // ------------------------------------------------------------------
    // BackendControlCommandConsumerService
    // ------------------------------------------------------------------

    [Fact]
    public async Task BackendControlConsumer_ProcessPending_RetryDueBoundaryIsInclusiveAndStampsFakeClock()
    {
        var clock = new ManualTimeProvider(Anchor);
        Guid due = await SeedControlCommandAsync(QueueOutboxEventStatus.Pending, retryAfterUtc: AnchorUtc);
        Guid notDue = await SeedControlCommandAsync(
            QueueOutboxEventStatus.Pending,
            retryAfterUtc: AnchorUtc.AddTicks(1));

        await CreateControlConsumer(clock).ProcessPendingAsync(CancellationToken.None);

        QueueDispatchOutbox dueRow = await GetOutboxAsync(due);
        dueRow.Status.Should().Be(QueueOutboxEventStatus.DeadLettered);
        dueRow.FailureCode.Should().Be("invalid_control_command");
        dueRow.CompletedAtUtc.Should().Be(AnchorUtc);
        (await GetOutboxAsync(notDue)).Status.Should().Be(QueueOutboxEventStatus.Pending);
    }

    [Fact]
    public async Task BackendControlConsumer_RecoverStaleLeases_StaleCutoffIsStrict()
    {
        var clock = new ManualTimeProvider(Anchor);
        Guid atCutoff = await SeedControlCommandAsync(
            QueueOutboxEventStatus.Processing,
            lastAttemptedAtUtc: AnchorUtc - TimeSpan.FromMinutes(5));
        Guid pastCutoff = await SeedControlCommandAsync(
            QueueOutboxEventStatus.Processing,
            lastAttemptedAtUtc: AnchorUtc - TimeSpan.FromMinutes(5) - TimeSpan.FromTicks(1));

        await CreateControlConsumer(clock).RecoverStaleLeasesAsync(CancellationToken.None);

        (await GetOutboxAsync(atCutoff)).Status.Should().Be(QueueOutboxEventStatus.Processing);
        QueueDispatchOutbox recovered = await GetOutboxAsync(pastCutoff);
        recovered.Status.Should().Be(QueueOutboxEventStatus.DeadLettered);
        recovered.CompletedAtUtc.Should().Be(AnchorUtc);
    }

    [Fact]
    public async Task BackendControlConsumer_PollInterval_WaitsOnInjectedTimerBeforeNextScan()
    {
        var clock = new ManualTimeProvider(Anchor);
        using BackendControlCommandConsumerService consumer = CreateControlConsumer(clock);
        await consumer.StartAsync(CancellationToken.None);
        try
        {
            await clock.WaitForActiveTimersAsync(1, TimeSpan.FromSeconds(10));
            Guid commandId = await SeedControlCommandAsync(QueueOutboxEventStatus.Pending);

            clock.Advance(TimeSpan.FromSeconds(5) - TimeSpan.FromMilliseconds(250));
            await clock.WaitForActiveTimersAsync(1, TimeSpan.FromSeconds(10));
            (await GetOutboxAsync(commandId)).Status.Should().Be(
                QueueOutboxEventStatus.Pending,
                "the 5s poll interval has not elapsed on the injected clock");

            clock.Advance(TimeSpan.FromMilliseconds(250));
            QueueDispatchOutbox row = await WaitForOutboxStatusAsync(
                commandId,
                QueueOutboxEventStatus.DeadLettered);
            row.CompletedAtUtc.Should().Be(AnchorUtc + TimeSpan.FromSeconds(5));
        }
        finally
        {
            await consumer.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    // ------------------------------------------------------------------
    // BedClearAcknowledgementService
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(0L, true)]
    [InlineData(1L, false)]
    public async Task BedClearAcknowledgement_Invalidate_ExpiryBoundaryIsInclusiveAndStampsFakeClock(
        long expiresAfterAnchorTicks,
        bool expectExpired)
    {
        var clock = new ManualTimeProvider(Anchor);
        (Guid printerId, Guid commandId, Guid startCommandId) =
            await SeedAcknowledgementAsync(AnchorUtc.AddTicks(expiresAfterAnchorTicks));

        await using (AsyncServiceScope scope = _provider.CreateAsyncScope())
        {
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var service = new BedClearAcknowledgementService(
                db,
                scope.ServiceProvider.GetRequiredService<IDbOutboxSequenceAllocator>(),
                Mock.Of<IPrinterStatusSnapshotReader>(),
                NullLogger<BedClearAcknowledgementService>.Instance,
                Mock.Of<IPrinterTelemetryFreshnessPolicy>(),
                timeProvider: clock);
            await service.InvalidateStaleAcknowledgementsAsync(printerId, CancellationToken.None);
        }

        await using AppDbContext verify = CreateContext();
        BedClearCommandRecord command = await verify.BedClearCommandRecords.SingleAsync(c => c.Id == commandId);
        PrinterDispatchState state = await verify.PrinterDispatchStates.SingleAsync(s => s.PrinterId == printerId);
        QueueDispatchOutbox startCommand = await verify.QueueDispatchOutbox.SingleAsync(e => e.Id == startCommandId);
        if (!expectExpired)
        {
            command.Status.Should().Be(BedClearCommandStatus.Pending);
            state.AcknowledgedJobId.Should().NotBeNull();
            startCommand.Status.Should().Be(QueueOutboxEventStatus.Pending);
            return;
        }

        command.Status.Should().Be(BedClearCommandStatus.Expired);
        command.UpdatedAtUtc.Should().Be(AnchorUtc);
        state.AcknowledgedJobId.Should().BeNull();
        startCommand.Status.Should().Be(QueueOutboxEventStatus.DeadLettered);
        startCommand.CompletedAtUtc.Should().Be(AnchorUtc);
        QueueDispatchOutbox lifecycle = await verify.QueueDispatchOutbox.SingleAsync(
            e => e.EventType == QueueLifecycleEventWriter.EventTypeBedClearExpired);
        lifecycle.CreatedAtUtc.Should().Be(AnchorUtc);
    }

    // ------------------------------------------------------------------
    // PrinterPhysicalActuationService
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(0L, true)]
    [InlineData(1L, false)]
    public async Task PhysicalActuation_AcquireDirect_ManualMotionReclaimBoundaryUsesFakeClock(
        long startedAfterBoundaryTicks,
        bool expectReclaimed)
    {
        var clock = new ManualTimeProvider(Anchor);
        Guid printerId = Guid.NewGuid();
        DateTime started = AnchorUtc - PrinterDirectControl.CommandTimeout - TimeSpan.FromSeconds(45);
        await using (AppDbContext seed = CreateContext())
        {
            seed.PrinterDispatchStates.Add(new PrinterDispatchState
            {
                PrinterId = printerId,
                PhysicalControlCommandId = Guid.NewGuid(),
                PhysicalControlOperation = "home",
                PhysicalControlActorSubject = "previous-actor",
                PhysicalControlStartedAtUtc = started.AddTicks(startedAfterBoundaryTicks),
            });
            await seed.SaveChangesAsync();
        }

        PrinterActuationResult result;
        await using (AsyncServiceScope scope = _provider.CreateAsyncScope())
        {
            var authorization = new Mock<IQueueResourceAuthorizationService>();
            authorization
                .Setup(a => a.CanActorAccessPrinterAsync(
                    It.IsAny<string>(),
                    printerId,
                    It.IsAny<PrinterGroupAccessLevel>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
            var service = new PrinterPhysicalActuationService(
                scope.ServiceProvider.GetRequiredService<AppDbContext>(),
                scope.ServiceProvider.GetRequiredService<IDbOutboxSequenceAllocator>(),
                authorization.Object,
                NullLogger<PrinterPhysicalActuationService>.Instance,
                clock);
            result = await service.AcquireDirectAsync(printerId, "actor", "move", CancellationToken.None);
        }

        await using AppDbContext verify = CreateContext();
        List<QueueOperationAudit> audits = await verify.QueueOperationAudits.ToListAsync();
        audits.Should().NotBeEmpty();
        audits.Should().OnlyContain(audit => audit.OccurredAtUtc == AnchorUtc);
        PrinterDispatchState state = await verify.PrinterDispatchStates.SingleAsync(s => s.PrinterId == printerId);
        if (!expectReclaimed)
        {
            result.Code.Should().Be(PrinterActuationResultCode.FenceConflict);
            state.PhysicalControlOperation.Should().Be("home");
            return;
        }

        result.Code.Should().Be(PrinterActuationResultCode.Accepted);
        state.PhysicalControlCommandId.Should().Be(result.CommandId);
        state.PhysicalControlStartedAtUtc.Should().Be(AnchorUtc);
        QueueDispatchOutbox started_event = await verify.QueueDispatchOutbox.SingleAsync(
            e => e.EventType == PrinterPhysicalActuationService.EventTypeStarted);
        started_event.CreatedAtUtc.Should().Be(AnchorUtc);
    }

    // ------------------------------------------------------------------
    // BatchDispatchService
    // ------------------------------------------------------------------

    [Fact]
    public async Task BatchDispatch_QueueStatus_StatisticsCutoffIsInclusiveOnFakeClock()
    {
        var clock = new ManualTimeProvider(Anchor);
        await using (AppDbContext seed = CreateContext())
        {
            seed.DispatchLogs.Add(new DispatchLog(Anchor - TimeSpan.FromHours(24))
            {
                Id = Guid.NewGuid(),
                PrintJobId = Guid.NewGuid(),
                PrinterId = Guid.NewGuid(),
                Action = DispatchAction.Dispatched,
            });
            seed.DispatchLogs.Add(new DispatchLog(Anchor - TimeSpan.FromHours(24) - TimeSpan.FromTicks(1))
            {
                Id = Guid.NewGuid(),
                PrintJobId = Guid.NewGuid(),
                PrinterId = Guid.NewGuid(),
                Action = DispatchAction.Dispatched,
            });
            await seed.SaveChangesAsync();
        }

        await using AppDbContext db = CreateContext();
        using var coordinator = new DispatchConcurrencyCoordinator();
        var service = new BatchDispatchService(
            Mock.Of<IDispatchScorer>(),
            db,
            Mock.Of<IServiceScopeFactory>(),
            coordinator,
            Mock.Of<IHubContext<PrinterHub>>(),
            NullLogger<BatchDispatchService>.Instance,
            clock);

        DispatchQueueStatusDto status = await service.GetQueueStatusAsync(CancellationToken.None);

        status.Stats.DispatchesLast24Hours.Should().Be(1);
    }

    // ------------------------------------------------------------------
    // AutoDispatchService
    // ------------------------------------------------------------------

    [Fact]
    public async Task AutoDispatch_GetStatus_ReadyGateChecksCarryFakeClockInstant()
    {
        var clock = new ManualTimeProvider(Anchor);
        Printer printer = new PrinterBuilder()
            .WithId(Guid.NewGuid())
            .WithName("Clock Printer")
            .WithServerUrl("http://192.168.1.50")
            .Build();
        printer.ManufacturerId = Guid.NewGuid();
        printer.ModelId = Guid.NewGuid();
        await using (AppDbContext seed = CreateContext())
        {
            seed.Printers.Add(printer);
            await seed.SaveChangesAsync();
        }

        await using AppDbContext db = CreateContext();
        var service = new AutoDispatchService(
            db,
            Mock.Of<IHubContext<PrinterHub>>(),
            NullLogger<AutoDispatchService>.Instance,
            timeProvider: clock);

        AutoDispatchStatusDto status = await service.GetStatusAsync(printer.Id, CancellationToken.None);

        status.ReadyGateChecks.Should().NotBeEmpty();
        status.ReadyGateChecks.Should().OnlyContain(check => check.CheckedAt == AnchorUtc.ToString("o"));
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connectionString).Options);

    private BackendControlCommandConsumerService CreateControlConsumer(TimeProvider clock) =>
        new(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<BackendControlCommandConsumerService>.Instance,
            hostUpdateFence: null,
            timeProvider: clock);

    private async Task<Guid> SeedControlCommandAsync(
        QueueOutboxEventStatus status,
        DateTime? retryAfterUtc = null,
        DateTime? lastAttemptedAtUtc = null)
    {
        await using AppDbContext db = CreateContext();
        Guid id = Guid.NewGuid();
        db.QueueDispatchOutbox.Add(new QueueDispatchOutbox
        {
            Id = id,
            Sequence = Interlocked.Increment(ref _nextSequence),
            AggregateType = nameof(PrintJob),
            AggregateId = Guid.NewGuid(),
            EventType = BackendControlCommandConsumerService.EventType,
            SchemaVersion = QueueEventSchemaVersions.Current,
            PayloadJson = "not-json",
            Status = status,
            RetryAfterUtc = retryAfterUtc,
            LastAttemptedAtUtc = lastAttemptedAtUtc,
            CreatedAtUtc = AnchorUtc - TimeSpan.FromMinutes(30),
        });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<QueueDispatchOutbox> GetOutboxAsync(Guid id)
    {
        await using AppDbContext db = CreateContext();
        return await db.QueueDispatchOutbox.AsNoTracking().SingleAsync(e => e.Id == id);
    }

    private async Task<QueueDispatchOutbox> WaitForOutboxStatusAsync(
        Guid id,
        QueueOutboxEventStatus expected)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            QueueDispatchOutbox row = await GetOutboxAsync(id);
            if (row.Status == expected)
            {
                return row;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10), timeout.Token);
        }
    }

    private async Task<(Guid PrinterId, Guid CommandId, Guid StartCommandId)> SeedAcknowledgementAsync(
        DateTime expiresAtUtc)
    {
        Guid printerId = Guid.NewGuid();
        Guid jobId = Guid.NewGuid();
        Guid commandId = Guid.NewGuid();
        Guid startCommandId = Guid.NewGuid();
        const string idempotencyKey = "ack-key";

        await using AppDbContext db = CreateContext();
        Printer printer = new PrinterBuilder()
            .WithId(printerId)
            .WithName("Ack Printer")
            .WithServerUrl("http://192.168.1.60")
            .Build();
        printer.ManufacturerId = Guid.NewGuid();
        printer.ModelId = Guid.NewGuid();
        db.Printers.Add(printer);
        db.PrintJobs.Add(new PrintJobBuilder()
            .WithId(jobId)
            .WithName("ack-job")
            .WithGcodeFileId(Guid.NewGuid())
            .WithAssignedPrinterId(printerId)
            .WithStatus(PrintJobStatus.Queued)
            .Build());
        await db.SaveChangesAsync();

        PrintJob job = await db.PrintJobs.SingleAsync(candidate => candidate.Id == jobId);
        long printerRevision = await db.Printers
            .Where(candidate => candidate.Id == printerId)
            .Select(candidate => candidate.ConfigurationRevision)
            .SingleAsync();
        db.PrinterDispatchStates.Add(new PrinterDispatchState
        {
            PrinterId = printerId,
            QueueRevision = 3,
            AcknowledgedJobId = jobId,
            AcknowledgedAtUtc = AnchorUtc - TimeSpan.FromMinutes(15),
            AcknowledgedBySubject = "actor",
            AcknowledgementIdempotencyKey = idempotencyKey,
            AcknowledgementExpiresAtUtc = expiresAtUtc,
            AcknowledgedJobRowVersion = job.RowVersion,
            AcknowledgedQueueRevision = 3,
            AcknowledgedPrinterConfigRevision = printerRevision,
        });
        db.QueueDispatchOutbox.Add(new QueueDispatchOutbox
        {
            Id = startCommandId,
            Sequence = Interlocked.Increment(ref _nextSequence),
            AggregateType = nameof(PrintJob),
            AggregateId = jobId,
            PrinterId = printerId,
            EventType = BedClearAcknowledgementService.BackendStartCommandEventType,
            SchemaVersion = QueueEventSchemaVersions.Current,
            PayloadJson = "{}",
            Status = QueueOutboxEventStatus.Pending,
            CreatedAtUtc = AnchorUtc - TimeSpan.FromMinutes(15),
        });
        db.BedClearCommandRecords.Add(new BedClearCommandRecord
        {
            Id = commandId,
            PrinterId = printerId,
            JobId = jobId,
            IdempotencyKey = idempotencyKey,
            RequestSha256 = new string('a', 64),
            ActorSubject = "actor",
            Status = BedClearCommandStatus.Pending,
            OutboxEventId = startCommandId,
            CreatedAtUtc = AnchorUtc - TimeSpan.FromMinutes(15),
            UpdatedAtUtc = AnchorUtc - TimeSpan.FromMinutes(15),
            ExpiresAtUtc = expiresAtUtc,
        });
        await db.SaveChangesAsync();
        return (printerId, commandId, startCommandId);
    }
}
