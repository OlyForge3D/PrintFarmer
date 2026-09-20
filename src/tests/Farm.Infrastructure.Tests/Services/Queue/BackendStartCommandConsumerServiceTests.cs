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

    // ------------------------------------------------------------------
    // CompletedAtUtc leak on the claim-committed path
    // (BackendStartCommandConsumerService.cs claimCommitted branch, PR #2877 panel
    // round 3, Bishop's Critical -- the FailureCode part of that claim does not hold
    // at this head, see the commit message, but the underlying ReloadAsync removal
    // does leak CompletedAtUtc onto a Processing/unknown-outcome row.)
    // ------------------------------------------------------------------

    [Fact]
    public async Task PersistDeadlineCancellationWithinDeadlineAsync_ClaimCommittedAfterCallerPreSetCompletedAtUtc_ClearsCompletedAtUtc()
    {
        // Reproduces the race: the caller (the OperationCanceledException handler) took
        // the dead-letter path and set evt.CompletedAtUtc BEFORE discovering the
        // dispatch claim actually committed. A committed claim means the outcome is
        // UNKNOWN and pending reconciliation, not complete, so that stale timestamp
        // must not survive onto the persisted row.
        DateTime anchor = new(2031, 4, 5, 6, 7, 8, DateTimeKind.Utc);
        var clock = new FixedTimeProvider(anchor);
        await using TestHarness harness = await TestHarness.CreateAsync(clock);

        Guid eventId = await harness.SeedOutboxEventAsync(status: QueueOutboxEventStatus.Processing);
        await harness.SeedBedClearCommandAsync(eventId, BedClearCommandStatus.Claimed);

        await using AsyncServiceScope scope = harness.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        QueueDispatchOutbox evt = await db.QueueDispatchOutbox.SingleAsync(e => e.Id == eventId);

        evt.Status = QueueOutboxEventStatus.DeadLettered;
        evt.LastError = "Pre-dispatch iteration deadline reached the maximum attempt count.";
        evt.CompletedAtUtc = anchor;
        evt.RetryAfterUtc = null;

        bool claimCommitted = await harness.Service.PersistDeadlineCancellationWithinDeadlineAsync(
            db,
            evt,
            BedClearCommandStatus.Rejected,
            CancellationToken.None);

        claimCommitted.Should().BeTrue();

        QueueDispatchOutbox persisted = await harness.GetOutboxEventAsync(eventId);
        persisted.Status.Should().Be(QueueOutboxEventStatus.Processing);
        persisted.FailureCode.Should().Be("backend_outcome_unknown");

        // The load-bearing assertion: before the fix, the claimCommitted branch never
        // touched CompletedAtUtc, so the caller's stale dead-letter timestamp survived
        // onto a row that is actually left Processing/unknown-outcome. If
        // `evt.CompletedAtUtc = null;` is removed from that branch, this fails:
        // CompletedAtUtc stays == anchor instead of null.
        persisted.CompletedAtUtc.Should().BeNull();
    }

    // ------------------------------------------------------------------
    // OperationCanceledException escaping the concurrency-retry backoff delay
    // (BackendStartCommandConsumerService.cs concurrency-retry catch block,
    // PR #2877 panel round 3, Bishop's Major finding.)
    // ------------------------------------------------------------------

    [Fact]
    public async Task PersistDeadlineCancellationWithinDeadlineAsync_PersistenceDeadlineElapsesDuringConcurrencyBackoff_ReturnsFalseInsteadOfThrowing()
    {
        // OutcomePersistenceDeadline is 4 seconds in production. This fake TimeProvider
        // fires that CancellationTokenSource's underlying timer after ~20ms of real
        // time instead, while leaving the concurrency-retry backoff delay (100ms on
        // attempt 1) unscaled -- so the persistence deadline reliably elapses WHILE the
        // backoff Task.Delay is still in progress, reproducing the exact race Bishop
        // flagged: a cancellation raised inside the `catch (DbUpdateConcurrencyException
        // ...)` block cannot be observed by the sibling `catch (OperationCanceledException
        // ...)` of the same try, because C# does not let a catch block handle an
        // exception raised by another catch block of the same try.
        var clock = new DeadlineDuringBackoffTimeProvider();
        await using TestHarness harness = await TestHarness.CreateAsync(clock);

        Guid eventId = await harness.SeedOutboxEventAsync(status: QueueOutboxEventStatus.Processing);
        await harness.SeedBedClearCommandAsync(eventId, BedClearCommandStatus.Pending);

        await using AsyncServiceScope scope = harness.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        QueueDispatchOutbox evt = await db.QueueDispatchOutbox.SingleAsync(e => e.Id == eventId);

        evt.Status = QueueOutboxEventStatus.DeadLettered;
        evt.LastError = "Pre-dispatch iteration deadline reached the maximum attempt count.";
        evt.CompletedAtUtc = DateTime.UtcNow;
        evt.RetryAfterUtc = null;

        // Force a genuine DbUpdateConcurrencyException on the first SaveChangesAsync so
        // the method enters the concurrency-retry catch block and its backoff delay.
        await harness.BumpOutboxRevisionDirectlyAsync(eventId);

        bool claimCommitted = false;
        Exception? escaped = null;
        try
        {
            claimCommitted = await harness.Service.PersistDeadlineCancellationWithinDeadlineAsync(
                db,
                evt,
                BedClearCommandStatus.Rejected,
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            escaped = ex;
        }

        // The load-bearing assertion: before the fix, the cancellation raised by
        // Task.Delay during backoff is unhandled inside the outer catch clause and
        // propagates out of this call as an OperationCanceledException/
        // TaskCanceledException instead of the method returning false.
        escaped.Should().BeNull(
            "a persistence-deadline timeout during concurrency backoff must be handled " +
            "internally and return false, not propagate out of the method");
        claimCommitted.Should().BeFalse();
    }

    // ------------------------------------------------------------------
    // Hosted ExecuteAsync loop end-to-end (PR #2877 panel round, Hicks's finding:
    // no test supplies a fake TimeProvider to the hosted service and drives its
    // poll-delay/iteration-deadline loop directly.)
    // ------------------------------------------------------------------

    [Fact]
    public async Task ExecuteAsync_HostedLoopWithFutureRetryAfter_ProcessesEventAfterPollCycleWithoutRealTimeWaiting()
    {
        // Drives the actual hosted BackgroundService loop (ExecuteAsync ->
        // WaitForIntervalOrPauseAsync's poll/delay cycle -> ProcessPendingCommandsAsync),
        // not the internal methods directly as every other test in this file does. Every
        // timer the service schedules -- the 250ms poll-delay ticks inside
        // WaitForIntervalOrPauseAsync and the 310s IterationDeadline CancellationTokenSource
        // -- is scaled down 200x in real time by AcceleratedTimeProvider, while GetUtcNow
        // advances at the same accelerated rate, so the loop's own `while` conditions see
        // simulated time pass consistently with the timers actually firing.
        DateTime anchor = new(2031, 4, 5, 6, 7, 8, DateTimeKind.Utc);
        const double accelerationFactor = 200;
        var clock = new AcceleratedTimeProvider(anchor, accelerationFactor);

        // The clock is intentionally NOT started yet: GetUtcNow() stays frozen at
        // `anchor` for the entire setup/seeding block below, no matter how long that
        // setup actually takes in real wall-clock time. This closes a race a panel
        // review caught: with the clock running from construction, real time spent on
        // SQLite provisioning and seeding (which is unbounded and can legitimately take
        // tens of milliseconds) could, at this acceleration factor, silently carry the
        // virtual clock past the seeded RetryAfterUtc below BEFORE the hosted loop even
        // starts -- making the event eligible on the very first
        // ProcessPendingCommandsAsync pass and letting the test's "load-bearing"
        // assertion pass without WaitForIntervalOrPauseAsync's poll/delay cycle ever
        // actually running.
        await using TestHarness harness = await TestHarness.CreateAsync(clock);

        // RetryAfterUtc is 3 simulated seconds in the future: inside the 5-second
        // PollInterval, but strictly after the very first ProcessPendingCommandsAsync
        // call (which runs at simulated time ~= anchor, since the clock has not been
        // started yet). The event is therefore NOT eligible on the hosted loop's first
        // iteration and can only be picked up after WaitForIntervalOrPauseAsync's
        // poll/delay cycle has advanced the clock past it.
        Guid eventId = await harness.SeedOutboxEventAsync(
            status: QueueOutboxEventStatus.Pending,
            retryAfterUtc: anchor + TimeSpan.FromSeconds(3));

        harness.PrintJobManagement
            .Setup(mgmt => mgmt.DispatchJobWithAckAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(BackendStartOutcome.Accepted(Guid.NewGuid()));

        // Deterministic proof the race is closed: no matter how much real wall-clock
        // time the setup above actually took, the virtual clock is still exactly
        // `anchor` right up to this point, because AcceleratedTimeProvider.Start() has
        // not been called yet. This assertion alone would fail immediately (not
        // flakily) if the clock were still started eagerly in its constructor and the
        // test happened to run on a slow machine/CI runner.
        clock.GetUtcNow().Should().Be(
            new DateTimeOffset(anchor, TimeSpan.Zero),
            "the accelerated clock must stay frozen at the seed instant throughout test " +
            "setup so setup time can never race past the seeded RetryAfterUtc before the " +
            "hosted loop starts");

        // Start the hosted loop while the clock is STILL frozen. AcceleratedTimeProvider's
        // CreateTimer scales its dueTime purely from real elapsed wall-clock time and does
        // NOT depend on Start() having been called (only GetUtcNow()/GetTimestamp() do), so
        // the loop's poll-interval timer still fires -- and ProcessPendingCommandsAsync still
        // runs one or more real passes -- well before Start() below, even though "now" stays
        // pinned at anchor throughout. This closes a narrower version of the same race a
        // panel review found in the first fix: previously the clock resumed ticking
        // immediately before StartAsync, so real time spent on the hosted loop's OWN startup
        // overhead (deadline-timer creation, RecoverStaleLeasesAsync's EF/SQLite work on its
        // very first pass) happened while the clock was already live, and at this
        // acceleration factor as little as ~15ms of that overhead could carry "now" past the
        // seeded RetryAfterUtc before the loop had even reached its first poll/delay wait --
        // making the event eligible on the very first pass and defeating the coverage again,
        // just from a narrower window than before.
        await harness.Service.StartAsync(CancellationToken.None);

        // Wait for an explicit signal instead of a fixed real-time delay:
        // `PollIntervalTimerCreated` completes only when a `CreateTimer` call's due-time
        // exactly matches `WaitForIntervalOrPauseAsync`'s literal 250ms poll-delay -- NOT
        // on just any `CreateTimer` call. A panel review caught that an earlier version of
        // this signal fired on ANY timer creation, which is wrong: `ProcessPendingCommandsAsync`
        // itself creates a (310s iteration-deadline) timer at its own first line, before doing
        // any real work, so that version of the signal resolved almost instantly and proved
        // nothing about the first pass having completed. Matching the specific 250ms due-time
        // is what actually proves `WaitForIntervalOrPauseAsync` was reached, which production
        // code can only do after `ExecuteAsync`'s `ProcessPendingCommandsAsync` call has
        // returned (see `AcceleratedTimeProvider.PollIntervalTimerCreated`'s doc comment for
        // why no other timer this service creates can share that due-time). A fixed real-time
        // delay here was not guaranteed to outlast that first pass under scheduler/SQLite
        // contention, so the "still Pending" assertion below could spuriously pass without the
        // pass having actually finished; this signal cannot complete before it has.
        bool firstPassCompleted = await Task.WhenAny(
            clock.PollIntervalTimerCreated,
            Task.Delay(TimeSpan.FromSeconds(20))) == clock.PollIntervalTimerCreated;
        firstPassCompleted.Should().BeTrue(
            "the hosted loop must reach WaitForIntervalOrPauseAsync's poll-delay timer " +
            "creation -- i.e. complete its first ProcessPendingCommandsAsync pass -- within " +
            "20 real seconds; if it never does, the loop is stuck or was never started");

        // Deterministic proof the freeze held through the loop's own startup overhead, not
        // just through this test's setup code above: while the clock remains frozen at
        // anchor, RetryAfterUtc (anchor + 3s) can never be satisfied, so the event MUST still
        // be Pending no matter how many passes just ran or how long they took in real time.
        // If this were anything else, the freeze failed to hold through the loop's runtime,
        // which is exactly the remaining race a panel review flagged against the first fix.
        QueueDispatchOutbox stillFrozen = await harness.GetOutboxEventAsync(eventId);
        stillFrozen.Status.Should().Be(
            QueueOutboxEventStatus.Pending,
            "the event must still be Pending after the hosted loop's own startup passes " +
            "while the clock remains frozen at anchor -- otherwise the freeze did not hold " +
            "through the loop's startup overhead (deadline-timer creation, the first " +
            "RecoverStaleLeasesAsync pass), only through this test's own setup code");

        var wallClock = System.Diagnostics.Stopwatch.StartNew();
        clock.Start();
        bool published;
        try
        {
            // 20 real seconds is a generous bound chosen for stability under a
            // contended, fully-parallel test run (observed up to ~3s of real time for
            // a 200x-accelerated 3-simulated-second wait when many other test classes
            // are competing for CPU/thread-pool time). It remains far smaller than
            // what an un-accelerated reversion would need: RetryAfterUtc is seeded
            // relative to `anchor` (2031), a date that never matches the real system
            // clock, so if WaitForIntervalOrPauseAsync were reverted to ignore the
            // injected TimeProvider, this event could NEVER become eligible under the
            // real clock and this SpinWait would time out completely regardless of how
            // generous the budget is -- this is the actual load-bearing property, not
            // the specific numeric threshold.
            published = await SpinWaitUntilPublishedAsync(harness, eventId, TimeSpan.FromSeconds(20));
        }
        finally
        {
            await harness.Service.StopAsync(CancellationToken.None);
        }

        wallClock.Stop();

        // The load-bearing assertion for the hosted-loop requirement: this event is
        // constructed (via the 3-simulated-second-future RetryAfterUtc above) so that it
        // can ONLY be published after at least one full pass through
        // WaitForIntervalOrPauseAsync's poll/delay loop. If PollInterval's delay, or the
        // `_timeProvider.GetUtcNow() < until` loop condition, were reverted to raw
        // `Task.Delay`/`DateTime.UtcNow` (i.e. ignoring the injected TimeProvider), this
        // fake clock could never advance the loop's notion of "now" past RetryAfterUtc
        // (RetryAfterUtc is anchored to the fictitious year 2031, so a real clock could
        // never reach it either), and the event would still be Pending when the
        // 20-second real-time SpinWait above times out.
        published.Should().BeTrue(
            "the hosted loop must advance past the poll/delay cycle and retry the event " +
            "once its RetryAfterUtc has elapsed");

        // Secondary (non-flaky-by-design) sanity check: proves the loop advanced via
        // the accelerated TimeProvider rather than genuinely blocking for real
        // wall-clock time. The threshold is deliberately generous -- it only needs to
        // be far below what an un-accelerated reversion would require (which, per the
        // comment above, is effectively unbounded, not merely "5 seconds") -- so this
        // assertion stays stable under a fully-parallel, CPU-contended test run rather
        // than flaking on an incidental scheduling delay.
        wallClock.Elapsed.Should().BeLessThan(
            TimeSpan.FromSeconds(15),
            "the accelerated TimeProvider must let the hosted loop advance through the " +
            "poll/delay cycle without the test blocking anywhere near as long as an " +
            "un-accelerated wait for a RetryAfterUtc anchored in the year 2031 would take");

        QueueDispatchOutbox persisted = await harness.GetOutboxEventAsync(eventId);
        persisted.Status.Should().Be(QueueOutboxEventStatus.Published);
    }

    /// <summary>
    /// Polls until the row is <see cref="QueueOutboxEventStatus.Published"/> or <paramref
    /// name="timeout"/> elapses. This runs concurrently with the hosted
    /// <see cref="BackendStartCommandConsumerService"/>'s own background scopes, both of
    /// which share the single physical <see cref="SqliteConnection"/>
    /// <see cref="TestHarness.CreateAsync"/> hands every <c>AppDbContext</c> (so all scopes
    /// observe the same in-memory database). Each new <c>AppDbContext</c>'s first use of that
    /// shared connection registers Sqlite user-defined functions
    /// (<c>SqliteRelationalConnection.InitializeDbConnection</c>); if that registration lands
    /// on the exact instant another scope (foreground or background) has an active statement
    /// open on the same connection, Sqlite raises "SQLite Error 5: unable to delete/modify
    /// user-function due to active statements" -- a transient condition, not a correctness
    /// bug (verified empirically: reproduces intermittently under back-to-back local runs of
    /// this test, at roughly the same rate whether or not the PollIntervalTimerCreated fix
    /// above is applied, i.e. it predates and is unrelated to that fix). Production code
    /// already tolerates this the same way: <c>ExecuteAsync</c>'s outer
    /// <c>catch (Exception ex)</c> logs and retries on the next loop iteration. This spin-wait
    /// is already a bounded polling retry by design, so it must extend that same tolerance to
    /// its own read instead of letting the transient exception escape as a hard test failure.
    /// </summary>
    private static async Task<bool> SpinWaitUntilPublishedAsync(
        TestHarness harness, Guid eventId, TimeSpan timeout)
    {
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        while (deadline.Elapsed < timeout)
        {
            try
            {
                QueueDispatchOutbox row = await harness.GetOutboxEventAsync(eventId);
                if (row.Status == QueueOutboxEventStatus.Published)
                {
                    return true;
                }
            }
            catch (SqliteException)
            {
                // Transient contention on the shared in-memory connection (see doc comment
                // above) -- treat exactly like "not yet published" and keep polling.
            }

            await Task.Delay(10);
        }

        return false;
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
            // A single physical SqliteConnection object shared directly across every
            // AppDbContext (the previous "Data Source=:memory:" + UseSqlite(connection)
            // approach) is not safe for genuine concurrent multi-threaded access: each new
            // AppDbContext's first use of that connection registers Sqlite user-defined
            // functions (SqliteRelationalConnection.InitializeDbConnection), and if that
            // registration lands while another scope has an active statement open on the
            // same connection, Sqlite raises "SQLite Error 5: unable to delete/modify
            // user-function due to active statements". Only the hosted-loop test below
            // actually runs the background service concurrently with foreground DB access,
            // and hit this intermittently. The fix -- and this repo's established pattern
            // for tests needing genuine cross-context concurrency, see
            // AutoDispatchConcurrencyTests -- is a uniquely-named Mode=Memory;Cache=Shared
            // database referenced by connection STRING (so every AppDbContext gets its own
            // separate, properly-locked connection to the same shared-cache in-memory
            // database), with one anchor connection kept open for the harness's lifetime so
            // the shared-cache database isn't dropped once all per-context connections close.
            string connectionString =
                $"Data Source=backend-start-consumer-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Default Timeout=5";
            var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync();

            var printJobManagement = new Mock<IPrintJobManagementService>();

            var services = new ServiceCollection();
            services.AddDbContext<AppDbContext>(options => options.UseSqlite(connectionString));
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
            long sequence = 1,
            DateTime? retryAfterUtc = null)
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
                RetryAfterUtc = retryAfterUtc,
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

    /// <summary>
    /// A <see cref="TimeProvider"/> whose <see cref="GetUtcNow"/>/<see cref="GetTimestamp"/>
    /// delegate to the real system clock (so ordinary timestamp reads elsewhere in the
    /// method are unaffected), but whose <see cref="CreateTimer"/> fires any real-timer
    /// request of one second or more almost immediately. This is used to reliably force
    /// the <c>OutcomePersistenceDeadline</c> (4 seconds in production)
    /// <see cref="CancellationTokenSource"/> to elapse in ~20ms of real test time, while
    /// leaving the much shorter concurrency-retry backoff delay (100-300ms) unscaled --
    /// reproducing the race where the persistence deadline fires WHILE that backoff
    /// delay is still in progress.
    /// </summary>
    private sealed class DeadlineDuringBackoffTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => System.GetUtcNow();

        public override long GetTimestamp() => System.GetTimestamp();

        public override long TimestampFrequency => System.TimestampFrequency;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            TimeSpan effectiveDueTime = dueTime >= TimeSpan.FromSeconds(1)
                ? TimeSpan.FromMilliseconds(20)
                : dueTime;
            return System.CreateTimer(callback, state, effectiveDueTime, period);
        }
    }

    /// <summary>
    /// A <see cref="TimeProvider"/> that reports every wall-clock/monotonic read as if real
    /// time were flowing <c>accelerationFactor</c> times faster, and scales down every real
    /// timer/delay it schedules by that same factor. Because <see cref="GetUtcNow"/>,
    /// <see cref="GetTimestamp"/>, and <see cref="CreateTimer"/> all derive from the same
    /// underlying real <see cref="System.Diagnostics.Stopwatch"/>, the simulated clock and the
    /// real timers that drive the hosted loop stay mutually consistent -- unlike firing timers
    /// instantly regardless of due time, which would let the virtual "now" outrun timers that
    /// have not actually completed yet. This lets a test drive
    /// <see cref="BackendStartCommandConsumerService"/>'s full hosted <c>ExecuteAsync</c> loop
    /// (poll interval, iteration deadline) through multiple simulated seconds in well under a
    /// second of real test time.
    /// </summary>
    private sealed class AcceleratedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _startUtc;
        private readonly double _accelerationFactor;
        private readonly object _gate = new();
        private global::System.Diagnostics.Stopwatch? _stopwatch;
        private readonly TaskCompletionSource _pollIntervalTimerCreated =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// The exact literal due-time <c>WaitForIntervalOrPauseAsync</c> passes to
        /// <c>Task.Delay(TimeSpan.FromMilliseconds(250), _timeProvider, ...)</c>. A prior
        /// version of this fake signalled on ANY <see cref="CreateTimer"/> call, which a
        /// panel review found was wrong: <c>ProcessPendingCommandsAsync</c> itself calls
        /// <c>CreateTimer</c> (via <c>new CancellationTokenSource(IterationDeadline,
        /// _timeProvider)</c>) at its own very first line, before any real work -- so "any
        /// timer created" fired almost instantly and proved nothing about the pass having
        /// completed. Matching this specific literal due-time is what actually
        /// distinguishes "WaitForIntervalOrPauseAsync's poll-delay timer" from every other
        /// timer this service creates (the 310s iteration deadline, the 4s outcome
        /// persistence deadline, and the concurrency backoff's
        /// <c>100 * concurrencyAttempt</c> ms delays, none of which can ever equal exactly
        /// 250ms for any integer attempt count).
        /// </summary>
        private static readonly TimeSpan PollDelayDueTime = TimeSpan.FromMilliseconds(250);

        public AcceleratedTimeProvider(DateTime utcNow, double accelerationFactor)
        {
            _startUtc = new DateTimeOffset(utcNow, TimeSpan.Zero);
            _accelerationFactor = accelerationFactor;
        }

        /// <summary>
        /// Completes the first time <see cref="CreateTimer"/> is invoked with a due-time
        /// exactly matching <see cref="PollDelayDueTime"/> -- i.e. the first time
        /// <c>WaitForIntervalOrPauseAsync</c>'s poll-delay <c>Task.Delay</c> is reached.
        /// Production code can only reach that call after <c>ExecuteAsync</c>'s call to
        /// <c>ProcessPendingCommandsAsync</c> has returned (see
        /// <c>ExecuteAsync</c>: the try/catch around <c>ProcessPendingCommandsAsync</c> is
        /// unconditionally followed by <c>WaitForIntervalOrPauseAsync</c>), so awaiting this
        /// task is a deterministic proof the hosted loop's first frozen-time processing pass
        /// has fully completed -- unlike a fixed real-time delay, which cannot guarantee
        /// that under scheduler/SQLite contention, and unlike matching any <see
        /// cref="CreateTimer"/> call, which fires far too early because
        /// <c>ProcessPendingCommandsAsync</c> itself creates a (much longer-lived) timer at
        /// its own first line.
        /// </summary>
        public Task PollIntervalTimerCreated => _pollIntervalTimerCreated.Task;

        /// <summary>
        /// Begins advancing this clock's virtual time. Before this is called,
        /// <see cref="GetUtcNow"/>/<see cref="GetTimestamp"/> stay frozen at the
        /// constructor-supplied instant no matter how much real wall-clock time
        /// elapses. This closes a race a panel review caught (PR #2877): the previous
        /// design started its Stopwatch in the constructor, so real time spent on test
        /// setup performed AFTER construction but BEFORE the code under test actually
        /// runs (SQLite provisioning, seeding, mock wiring) leaked into the virtual
        /// clock and could silently carry it past a seeded RetryAfterUtc/deadline
        /// before the hosted loop had even started -- letting the test's "load-bearing"
        /// assertion pass without the poll/delay cycle it claims to exercise ever
        /// actually running. Callers must do all setup first, then call Start()
        /// immediately before invoking the code under test.
        /// </summary>
        public void Start()
        {
            lock (_gate)
            {
                _stopwatch ??= global::System.Diagnostics.Stopwatch.StartNew();
            }
        }

        private TimeSpan AcceleratedElapsed
        {
            get
            {
                global::System.Diagnostics.Stopwatch? stopwatch;
                lock (_gate)
                {
                    stopwatch = _stopwatch;
                }

                return stopwatch is null
                    ? TimeSpan.Zero
                    : TimeSpan.FromTicks((long)(stopwatch.Elapsed.Ticks * _accelerationFactor));
            }
        }

        public override DateTimeOffset GetUtcNow() => _startUtc + AcceleratedElapsed;

        public override long GetTimestamp() => AcceleratedElapsed.Ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (dueTime == PollDelayDueTime)
            {
                _pollIntervalTimerCreated.TrySetResult();
            }

            return System.CreateTimer(callback, state, ScaleDown(dueTime), ScaleDown(period));
        }

        private TimeSpan ScaleDown(TimeSpan value) => TimeSpan.FromTicks((long)(value.Ticks / _accelerationFactor));
    }
}
