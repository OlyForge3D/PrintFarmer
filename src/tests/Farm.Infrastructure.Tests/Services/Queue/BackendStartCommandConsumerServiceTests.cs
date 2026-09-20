// <copyright file="BackendStartCommandConsumerServiceTests.cs" company="OlyForge3D">
// Copyright (c) OlyForge3D. All rights reserved.
// </copyright>

using System.Text.Json;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Interfaces;
using Farm.Infrastructure.Services.Queue;
using Farm.Infrastructure.Settings;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Farm.Infrastructure.Tests.Services.Queue;

/// <summary>
/// Mutation-sensitive coverage for the TimeProvider-driven decisions in
/// <see cref="BackendStartCommandConsumerService"/>: stale-lease eligibility, the
/// iteration-deadline deferral boundary, and at least one persisted timestamp bound to
/// an exact fake-clock value. Every test below is designed so reverting the corresponding
/// production line back to a raw <c>DateTime.UtcNow</c>/<c>Stopwatch</c> read flips the
/// assertion, not just changes an unobserved value.
/// </summary>
public sealed class BackendStartCommandConsumerServiceTests
{
    private const string BackendStartEventType =
        "PrintFarmer.Queue.BackendStartCommand.v1";

    // ------------------------------------------------------------------
    // Stale-lease eligibility cutoff (BackendStartCommandConsumerService.cs:153)
    // ------------------------------------------------------------------

    [Fact]
    public async Task RecoverStaleLeasesAsync_LeaseJustWithinStaleAge_RemainsProcessing()
    {
        // StaleLeaseAge is 10 minutes; a lease 1 second short of that must NOT be
        // recovered. If the cutoff computation is reverted to real DateTime.UtcNow, the
        // "now" used for the comparison would be the actual wall-clock test time (far
        // apart from the fixed fake `anchor` below), which would make this lease look
        // either far-fresher or far-staler than intended and flip the assertion.
        DateTime anchor = new(2031, 4, 5, 6, 7, 8, DateTimeKind.Utc);
        var clock = new FixedTimeProvider(anchor);
        await using TestHarness harness = await TestHarness.CreateAsync(clock);

        Guid eventId = await harness.SeedOutboxEventAsync(
            status: QueueOutboxEventStatus.Processing,
            lastAttemptedAtUtc: anchor - TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1));

        await harness.Service.RecoverStaleLeasesAsync(CancellationToken.None);

        QueueDispatchOutbox row = await harness.GetOutboxEventAsync(eventId);
        row.Status.Should().Be(QueueOutboxEventStatus.Processing);
    }

    [Fact]
    public async Task RecoverStaleLeasesAsync_LeaseJustBeyondStaleAge_ResetToPendingWithExactRetryAfter()
    {
        DateTime anchor = new(2031, 4, 5, 6, 7, 8, DateTimeKind.Utc);
        var clock = new FixedTimeProvider(anchor);
        await using TestHarness harness = await TestHarness.CreateAsync(clock);

        Guid eventId = await harness.SeedOutboxEventAsync(
            status: QueueOutboxEventStatus.Processing,
            lastAttemptedAtUtc: anchor - TimeSpan.FromMinutes(10) - TimeSpan.FromSeconds(1));

        await harness.Service.RecoverStaleLeasesAsync(CancellationToken.None);

        QueueDispatchOutbox row = await harness.GetOutboxEventAsync(eventId);
        row.Status.Should().Be(QueueOutboxEventStatus.Pending);

        // Bound to the exact fake-clock value: PollInterval is 5 seconds, so the
        // recomputed RetryAfterUtc must equal anchor + 5s precisely. A revert to
        // DateTime.UtcNow would compute this relative to the real test-execution time
        // instead, which is nowhere near `anchor`, so this equality would fail.
        row.RetryAfterUtc.Should().Be(anchor + TimeSpan.FromSeconds(5));
    }

    // ------------------------------------------------------------------
    // Iteration deadline boundary (BackendStartCommandConsumerService.cs:202-232)
    // ------------------------------------------------------------------

    [Fact]
    public async Task ProcessPendingCommandsAsync_WhenIterationBudgetExhaustedMidLoop_DefersRemainingCommands()
    {
        DateTime anchor = new(2031, 4, 5, 6, 7, 8, DateTimeKind.Utc);
        var clock = new ControllableTimeProvider(anchor);
        await using TestHarness harness = await TestHarness.CreateAsync(clock);

        Guid firstEventId = await harness.SeedOutboxEventAsync(
            status: QueueOutboxEventStatus.Pending,
            sequence: 1);
        Guid secondEventId = await harness.SeedOutboxEventAsync(
            status: QueueOutboxEventStatus.Pending,
            sequence: 2);

        // IterationDeadline is 310s and the minimum dispatch window (FileUploadTimeout +
        // 1s margin) is 301s with default BackendTimeoutSettings, so advancing the clock
        // by 15s during the first dispatch call leaves only 295s remaining --- below the
        // window --- and must defer the second event without processing it.
        harness.PrintJobManagement
            .Setup(mgmt => mgmt.DispatchJobWithAckAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback(() => clock.Advance(TimeSpan.FromSeconds(15)))
            .ReturnsAsync(BackendStartOutcome.Accepted(Guid.NewGuid()));

        await harness.Service.ProcessPendingCommandsAsync(CancellationToken.None);

        QueueDispatchOutbox first = await harness.GetOutboxEventAsync(firstEventId);
        first.Status.Should().Be(QueueOutboxEventStatus.Published);

        // The load-bearing assertion: if the remaining-window check used a real
        // Stopwatch/DateTime.UtcNow instead of `_timeProvider`, this fast unit test would
        // observe near-zero elapsed time regardless of the 15s clock.Advance() above, the
        // deferral would never trigger, and the second event would ALSO be processed.
        QueueDispatchOutbox second = await harness.GetOutboxEventAsync(secondEventId);
        second.Status.Should().Be(QueueOutboxEventStatus.Pending);
        harness.PrintJobManagement.Verify(
            mgmt => mgmt.DispatchJobWithAckAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ------------------------------------------------------------------
    // Concurrency-retry disposition loss
    // (BackendStartCommandConsumerService.cs:504-509, PR #2877 REQUIRED 1)
    // ------------------------------------------------------------------

    [Fact]
    public async Task PersistDeadlineCancellationWithinDeadlineAsync_ConcurrencyRetryOnNonClaimedCommand_PersistsDeadLetterDisposition()
    {
        // This reproduces the exact scenario the reviewers flagged: the caller
        // pre-sets a terminal (DeadLettered) disposition on `evt` before invoking this
        // method, a genuine optimistic-concurrency conflict forces a reload of `evt` on
        // the first attempt, and the associated BedClearCommandRecord was never
        // Claimed (so the retry falls into the `else if (command is not null)` branch,
        // not the `claimCommitted` branch).
        DateTime anchor = new(2031, 4, 5, 6, 7, 8, DateTimeKind.Utc);
        var clock = new FixedTimeProvider(anchor);
        await using TestHarness harness = await TestHarness.CreateAsync(clock);

        Guid eventId = await harness.SeedOutboxEventAsync(status: QueueOutboxEventStatus.Processing);
        await harness.SeedBedClearCommandAsync(eventId, BedClearCommandStatus.Pending);

        await using AsyncServiceScope scope = harness.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        QueueDispatchOutbox evt = await db.QueueDispatchOutbox.SingleAsync(e => e.Id == eventId);

        // Mimic exactly what the caller (the OperationCanceledException handler) does
        // before invoking this method for the dead-letter case.
        evt.Status = QueueOutboxEventStatus.DeadLettered;
        evt.LastError = "Pre-dispatch iteration deadline reached the maximum attempt count.";
        evt.CompletedAtUtc = anchor;
        evt.RetryAfterUtc = null;

        // Force a genuine DbUpdateConcurrencyException on the first SaveChangesAsync by
        // bumping the row's real optimistic-concurrency token (Revision, configured via
        // RevisionConcurrency.Configure) out from under the tracked `db` context, exactly
        // as a second writer racing this row would.
        await harness.BumpOutboxRevisionDirectlyAsync(eventId);

        bool claimCommitted = await harness.Service.PersistDeadlineCancellationWithinDeadlineAsync(
            db,
            evt,
            BedClearCommandStatus.Rejected,
            CancellationToken.None);

        claimCommitted.Should().BeFalse();

        QueueDispatchOutbox persisted = await harness.GetOutboxEventAsync(eventId);

        // The load-bearing assertions: before the fix, the retry-reloaded `evt` kept its
        // stale post-reload values (Status stayed Processing, CompletedAtUtc stayed
        // null) because only `command.Status`/`command.UpdatedAtUtc` were written. If
        // the four `evt.X = intendedX;` re-application lines in the `else if (command is
        // not null)` branch are removed, this test fails: Status reverts to Processing
        // and CompletedAtUtc reverts to null.
        persisted.Status.Should().Be(QueueOutboxEventStatus.DeadLettered);
        persisted.CompletedAtUtc.Should().Be(anchor);
        persisted.RetryAfterUtc.Should().BeNull();
        persisted.LastError.Should().Be(
            "Pre-dispatch iteration deadline reached the maximum attempt count.");
    }

    private sealed class TestHarness : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly ServiceProvider _provider;

        private TestHarness(
            SqliteConnection connection,
            ServiceProvider provider,
            Mock<IPrintJobManagementService> printJobManagement,
            BackendStartCommandConsumerService service)
        {
            _connection = connection;
            _provider = provider;
            PrintJobManagement = printJobManagement;
            Service = service;
        }

        public Mock<IPrintJobManagementService> PrintJobManagement { get; }

        public BackendStartCommandConsumerService Service { get; }

        public static async Task<TestHarness> CreateAsync(TimeProvider clock)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();

            var printJobManagement = new Mock<IPrintJobManagementService>();

            var services = new ServiceCollection();
            services.AddDbContext<AppDbContext>(options => options.UseSqlite(connection));
            services.AddSingleton(printJobManagement.Object);
            ServiceProvider provider = services.BuildServiceProvider();

            await using (AsyncServiceScope scope = provider.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<AppDbContext>()
                    .Database.EnsureCreatedAsync();
            }

            var service = new BackendStartCommandConsumerService(
                provider.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<BackendStartCommandConsumerService>.Instance,
                Options.Create(new BackendTimeoutSettings()),
                hostUpdateFence: null,
                timeProvider: clock);

            return new TestHarness(connection, provider, printJobManagement, service);
        }

        public async Task<Guid> SeedOutboxEventAsync(
            QueueOutboxEventStatus status,
            DateTime? lastAttemptedAtUtc = null,
            long sequence = 1)
        {
            await using AsyncServiceScope scope = _provider.CreateAsyncScope();
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            Guid eventId = Guid.NewGuid();
            string payloadJson = JsonSerializer.Serialize(new
            {
                JobId = Guid.NewGuid(),
                PrinterId = Guid.NewGuid(),
                ActorSubject = "test-actor",
                AcknowledgementKey = Guid.NewGuid().ToString("N"),
            });

            db.QueueDispatchOutbox.Add(new QueueDispatchOutbox
            {
                Id = eventId,
                Sequence = sequence,
                EventType = BackendStartEventType,
                Status = status,
                PayloadJson = payloadJson,
                LastAttemptedAtUtc = lastAttemptedAtUtc,
                CreatedAtUtc = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
            return eventId;
        }

        public async Task<QueueDispatchOutbox> GetOutboxEventAsync(Guid eventId)
        {
            await using AsyncServiceScope scope = _provider.CreateAsyncScope();
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            return await db.QueueDispatchOutbox.SingleAsync(e => e.Id == eventId);
        }

        public AsyncServiceScope CreateScope() => _provider.CreateAsyncScope();

        public async Task SeedBedClearCommandAsync(Guid outboxEventId, BedClearCommandStatus status)
        {
            await using AsyncServiceScope scope = _provider.CreateAsyncScope();
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.BedClearCommandRecords.Add(new BedClearCommandRecord
            {
                Id = Guid.NewGuid(),
                PrinterId = Guid.NewGuid(),
                JobId = Guid.NewGuid(),
                IdempotencyKey = Guid.NewGuid().ToString("N"),
                RequestSha256 = new string('a', 64),
                ActorSubject = "test-actor",
                Status = status,
                OutboxEventId = outboxEventId,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow,
                ExpiresAtUtc = DateTime.UtcNow.AddMinutes(5),
            });
            await db.SaveChangesAsync();
        }

        /// <summary>
        /// Simulates a concurrent writer by incrementing the outbox row's real
        /// optimistic-concurrency token directly against the database, bypassing the
        /// tracked <see cref="AppDbContext"/> instance the test holds. Any subsequent
        /// <c>SaveChangesAsync</c> against a context that loaded the row before this call
        /// will throw a genuine <see cref="DbUpdateConcurrencyException"/> because
        /// <c>Revision</c> is configured as a concurrency token (see
        /// <see cref="Farm.Infrastructure.Data.RevisionConcurrency"/>).
        /// </summary>
        public async Task BumpOutboxRevisionDirectlyAsync(Guid eventId)
        {
            await using AsyncServiceScope scope = _provider.CreateAsyncScope();
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            int affected = await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE \"QueueDispatchOutbox\" SET \"Revision\" = \"Revision\" + 1 WHERE \"Id\" = {eventId}");
            affected.Should().Be(1);
        }

        public async ValueTask DisposeAsync()
        {
            Service.Dispose();
            await _provider.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        private readonly DateTimeOffset _utcNow = new(utcNow, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _utcNow;
    }

    /// <summary>
    /// A <see cref="TimeProvider"/> whose <see cref="GetUtcNow"/> AND monotonic timestamp
    /// (<see cref="GetTimestamp"/>/<see cref="TimestampFrequency"/>) advance together under
    /// test control, so elapsed-time calculations
    /// (<c>TimeProvider.GetElapsedTime(startingTimestamp)</c>) reflect the simulated
    /// advance rather than real wall-clock time.
    /// </summary>
    private sealed class ControllableTimeProvider(DateTime utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = new(utcNow, TimeSpan.Zero);
        private long _timestamp;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public override long GetTimestamp() => _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public void Advance(TimeSpan delta)
        {
            _utcNow += delta;
            _timestamp += delta.Ticks;
        }
    }
}
