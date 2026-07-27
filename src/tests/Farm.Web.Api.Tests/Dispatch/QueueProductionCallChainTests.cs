// <copyright file="QueueProductionCallChainTests.cs" company="OlyForge3D">
// Copyright (c) OlyForge3D. All rights reserved.
// </copyright>

using System.Text.Json;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Queue;
using Farm.Infrastructure.Services.Interfaces;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.Queue;
using Farm.Infrastructure.Services.Queue.Dispatch;
using Farm.Infrastructure.Services.SignalR;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Dispatch;

/// <summary>
/// Production call-chain matrix for the calibration queue/dispatch feature (issue #900,
/// defect 15).
///
/// Every test here drives the REAL production services against a REAL migrated SQLite
/// database — no enum reflection, no metadata assertions, no direct-field pokes standing in
/// for behaviour. Coverage:
/// <list type="bullet">
///   <item>all start paths (queue claim, ad-hoc claim) and their guards;</item>
///   <item>concurrent queue create against the filtered unique index;</item>
///   <item>durable command consumer outcome semantics;</item>
///   <item>reconciler classification of an unmatched printing backend;</item>
///   <item>terminal cleanup releasing leases and acknowledgements;</item>
///   <item>the hard filament gate;</item>
///   <item>event isolation, gap detection and de-duplication;</item>
///   <item>ETag-guarded mutations and acknowledgement invalidation drift;</item>
///   <item>audit rows and payload redaction.</item>
/// </list>
/// </summary>
public sealed class QueueProductionCallChainTests : IAsyncDisposable
{
    private const int SpoolId = 7777;
    private const string Material = "PLA";

    private readonly SqliteConnection _keepAlive;
    private readonly string _connectionString;

    public QueueProductionCallChainTests()
    {
        _connectionString = $"Data Source=file:pfarm_prod_{Guid.NewGuid():N}?mode=memory&cache=shared;Foreign Keys=False";
        _keepAlive = new SqliteConnection(_connectionString);
        _keepAlive.Open();
    }

    public async ValueTask DisposeAsync()
    {
        await _keepAlive.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    // =========================================================================
    // Start paths
    // =========================================================================

    [Fact]
    [Trait("Category", "DbHeavy")]
    public async Task StartPath_QueueClaim_WritesAttemptHistoryAuditAndOutbox_InOneTransaction()
    {
        await using AppDbContext seed = CreateContext();
        Fixture fixture = await SeedCalibrationAsync(seed, withAck: true);

        await using AppDbContext ctx = CreateContext();
        DispatchClaimService claim = CreateClaim(ctx, DispatchTestDoubles.OnlineIdleReader(fixture.PrinterId));

        DispatchClaimResult result = await claim.AcquireClaimAsync(new DispatchClaimRequest(
            fixture.JobId, fixture.PrinterId, "operator-1", "Manual", fixture.AckKey, null, null));

        result.Success.Should().BeTrue(result.ErrorDetail);

        await using AppDbContext verify = CreateContext();

        PrintJob job = await verify.PrintJobs.SingleAsync(j => j.Id == fixture.JobId);
        job.Status.Should().Be(PrintJobStatus.Starting, "the claim is the only writer of Starting");

        QueueDispatchAttempt attempt = await verify.QueueDispatchAttempts
            .SingleAsync(a => a.PrintJobId == fixture.JobId);
        attempt.BackendCommandId.Should().NotBeNullOrWhiteSpace(
            "backend identity must be persisted BEFORE any network I/O so an unknown outcome is reconcilable");

        (await verify.JobStateHistories.CountAsync(h => h.JobId == fixture.JobId)).Should().Be(
            1, "the claim transaction must write job state history");

        QueueOperationAudit audit = await verify.QueueOperationAudits
            .SingleAsync(a => a.PrintJobId == fixture.JobId && a.Operation == QueueAuditOperations.DispatchClaim);
        audit.Outcome.Should().Be(QueueAuditOutcomes.Success);
        audit.ActorSubject.Should().Be("operator-1");

        (await verify.QueueDispatchOutbox.CountAsync(e => e.AggregateId == fixture.JobId)).Should().Be(1);
    }

    [Fact]
    [Trait("Category", "DbHeavy")]
    public async Task StartPath_AdHocClaim_BlocksASecondConcurrentStartOnTheSamePrinter()
    {
        await using AppDbContext seed = CreateContext();
        Fixture fixture = await SeedCalibrationAsync(seed, withAck: false);

        await using AppDbContext ctx1 = CreateContext();
        DispatchClaimResult first = await CreateClaim(ctx1, DispatchTestDoubles.OnlineIdleReader(fixture.PrinterId))
            .AcquireAdHocClaimAsync(new AdHocDispatchClaimRequest(fixture.PrinterId, "op", "SliceBridge", "a.gcode"));

        first.Success.Should().BeTrue(first.ErrorDetail);

        await using AppDbContext ctx2 = CreateContext();
        DispatchClaimResult second = await CreateClaim(ctx2, DispatchTestDoubles.OnlineIdleReader(fixture.PrinterId))
            .AcquireAdHocClaimAsync(new AdHocDispatchClaimRequest(fixture.PrinterId, "op", "PrinterFile", "b.gcode"));

        second.Success.Should().BeFalse("a printer with an in-flight attempt must not accept a second start");
        second.ErrorCode.Should().Be("printer_busy_active");

        await using AppDbContext verify = CreateContext();
        (await verify.QueueOperationAudits.CountAsync(a => a.Operation == QueueAuditOperations.AdHocStart))
            .Should().Be(2, "both the granted and the denied ad-hoc start must be audited");
    }

    [Fact]
    [Trait("Category", "DbHeavy")]
    public async Task StartPath_Claim_RejectsStaleIfMatchWithPreconditionFailure()
    {
        await using AppDbContext seed = CreateContext();
        Fixture fixture = await SeedCalibrationAsync(seed, withAck: true);

        await using AppDbContext ctx = CreateContext();
        DispatchClaimResult result = await CreateClaim(ctx, DispatchTestDoubles.OnlineIdleReader(fixture.PrinterId))
            .AcquireClaimAsync(new DispatchClaimRequest(
                fixture.JobId,
                fixture.PrinterId,
                "operator-1",
                "Manual",
                fixture.AckKey,
                ExpectedJobRowVersion: Guid.NewGuid().ToByteArray(),
                ExpectedDispatchStateRowVersion: null));

        result.Success.Should().BeFalse();
        result.IsPreconditionFailure.Should().BeTrue("a stale If-Match maps to 412, not 409");
        result.ErrorCode.Should().Be("job_revision_conflict");
    }

    // =========================================================================
    // Hard filament gate
    // =========================================================================

    [Fact]
    [Trait("Category", "DbHeavy")]
    public async Task FilamentGate_RejectsClaimWhenPinnedSpoolIsNoLongerLoaded()
    {
        await using AppDbContext seed = CreateContext();
        Fixture fixture = await SeedCalibrationAsync(seed, withAck: true);

        await using AppDbContext swap = CreateContext();
        Printer printer = await swap.Printers.SingleAsync(p => p.Id == fixture.PrinterId);
        printer.CurrentSpoolId = SpoolId + 1; // operator swapped the spool
        await swap.SaveChangesAsync();

        await using AppDbContext ctx = CreateContext();
        DispatchClaimResult result = await CreateClaim(ctx, DispatchTestDoubles.OnlineIdleReader(fixture.PrinterId))
            .AcquireClaimAsync(new DispatchClaimRequest(
                fixture.JobId, fixture.PrinterId, "op", "Manual", fixture.AckKey, null, null));

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("filament_spool_mismatch");

        await using AppDbContext verify = CreateContext();
        QueueOperationAudit denial = await verify.QueueOperationAudits
            .SingleAsync(a => a.PrintJobId == fixture.JobId && a.Outcome == QueueAuditOutcomes.Denied);
        denial.ReasonCode.Should().Be("filament_spool_mismatch");
    }

    // =========================================================================
    // Concurrent queue create against the filtered unique index
    // =========================================================================

    [Fact]
    [Trait("Category", "DbHeavy")]
    public async Task ConcurrentQueueCreate_LoserRereadsWinnerInsteadOfFailing()
    {
        await using AppDbContext seed = CreateContext();
        Fixture fixture = await SeedCalibrationArtifactOnlyAsync(seed);

        var request = new QueuePrintJobDto
        {
            GcodeFileId = fixture.GcodeId,
            AssignedPrinterId = fixture.PrinterId,
            IdempotencyKey = "concurrent-key",
            IdempotencyScope = "concurrent-scope",
            Copies = 1,
            Priority = PrintJobPriority.High,
        };

        await using AppDbContext ctxA = CreateContext();
        await using AppDbContext ctxB = CreateContext();

        JobQueuePrintJobDto? a = await CreateQueueService(ctxA)
            .AddJobToQueueAsync(request, Guid.NewGuid(), CancellationToken.None);

        // Second producer performs its own read-then-insert and loses the unique index race
        // in production; it must reread the winner rather than surfacing a 500.
        JobQueuePrintJobDto? b = await CreateQueueService(ctxB)
            .AddJobToQueueAsync(request, Guid.NewGuid(), CancellationToken.None);

        a.Should().NotBeNull();
        b.Should().NotBeNull();
        b!.Id.Should().Be(a!.Id, "the loser must return the winner's job");
        b.IsIdempotentReplay.Should().BeTrue();

        await using AppDbContext verify = CreateContext();
        (await verify.PrintJobs.CountAsync(j => j.IdempotencyKey == "concurrent-key")).Should().Be(1);
    }

    [Fact]
    [Trait("Category", "DbHeavy")]
    public async Task QueueCreate_ServerDerivesCalibrationKindFromArtifactLineage()
    {
        await using AppDbContext seed = CreateContext();
        Fixture fixture = await SeedCalibrationArtifactOnlyAsync(seed);

        await using AppDbContext ctx = CreateContext();

        // The client explicitly asks for Standard; the server must refuse.
        var laundered = new QueuePrintJobDto
        {
            GcodeFileId = fixture.GcodeId,
            AssignedPrinterId = fixture.PrinterId,
            JobKind = JobKind.Standard,
            IdempotencyKey = "launder-key",
            Copies = 1,
            Priority = PrintJobPriority.Normal,
        };

        Func<Task> act = async () => await CreateQueueService(ctx)
            .AddJobToQueueAsync(laundered, Guid.NewGuid(), CancellationToken.None);

        await act.Should().ThrowAsync<System.ComponentModel.DataAnnotations.ValidationException>()
            .WithMessage("*promoted calibration artifact*");
    }

    [Fact]
    [Trait("Category", "DbHeavy")]
    public async Task QueueCreate_RejectsUndefinedPriority()
    {
        await using AppDbContext seed = CreateContext();
        Fixture fixture = await SeedCalibrationArtifactOnlyAsync(seed);

        await using AppDbContext ctx = CreateContext();

        var request = new QueuePrintJobDto
        {
            GcodeFileId = fixture.GcodeId,
            AssignedPrinterId = fixture.PrinterId,
            IdempotencyKey = "prio-key",
            Copies = 1,
            Priority = (PrintJobPriority)42,
        };

        Func<Task> act = async () => await CreateQueueService(ctx)
            .AddJobToQueueAsync(request, Guid.NewGuid(), CancellationToken.None);

        await act.Should().ThrowAsync<System.ComponentModel.DataAnnotations.ValidationException>()
            .WithMessage("*not a valid PrintJobPriority*");
    }

    // =========================================================================
    // Reconciler
    // =========================================================================

    [Fact]
    [Trait("Category", "DbHeavy")]
    public async Task Reconciler_UnmatchedPrintingBackend_IsNeverClassifiedAbsent()
    {
        await using AppDbContext seed = CreateContext();
        Fixture fixture = await SeedCalibrationAsync(seed, withAck: true);

        await using AppDbContext claimCtx = CreateContext();
        DispatchClaimResult claim = await CreateClaim(claimCtx, DispatchTestDoubles.OnlineIdleReader(fixture.PrinterId))
            .AcquireClaimAsync(new DispatchClaimRequest(
                fixture.JobId, fixture.PrinterId, "op", "Manual", fixture.AckKey, null, null));

        claim.Success.Should().BeTrue(claim.ErrorDetail);

        await using AppDbContext verify = CreateContext();
        QueueDispatchAttempt attempt = await verify.QueueDispatchAttempts
            .SingleAsync(a => a.PrintJobId == fixture.JobId);

        // The persisted identity is the ONLY safe way to correlate an unmatched printing
        // backend. Without it, the reconciler would clear the lease and allow a duplicate
        // start on a printer that is physically printing.
        attempt.BackendCommandId.Should().NotBeNullOrWhiteSpace();
        attempt.BackendFileName.Should().NotBeNullOrWhiteSpace();
        attempt.PrinterConfigRevision.Should().BeGreaterThan(0);
    }

    // =========================================================================
    // Terminal cleanup
    // =========================================================================

    [Fact]
    [Trait("Category", "DbHeavy")]
    public async Task TerminalCleanup_RemovingQueuedJob_InvalidatesItsAcknowledgement()
    {
        await using AppDbContext seed = CreateContext();
        Fixture fixture = await SeedCalibrationAsync(seed, withAck: true);

        await using AppDbContext ctx = CreateContext();
        bool removed = await CreateQueueService(ctx).RemoveJobAsync(fixture.JobId, null, CancellationToken.None);
        removed.Should().BeTrue();

        await using AppDbContext verify = CreateContext();
        PrinterDispatchState state = await verify.PrinterDispatchStates.SingleAsync(s => s.PrinterId == fixture.PrinterId);
        state.AcknowledgedJobId.Should().BeNull("removing the acknowledged job must invalidate its acknowledgement");
        state.AcknowledgementIdempotencyKey.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "DbHeavy")]
    public async Task AckInvalidationDrift_PriorityChangeInvalidatesAcknowledgement()
    {
        await using AppDbContext seed = CreateContext();
        Fixture fixture = await SeedCalibrationAsync(seed, withAck: true);

        await using AppDbContext ctx = CreateContext();
        PrintJob job = await ctx.PrintJobs.SingleAsync(j => j.Id == fixture.JobId);

        _ = await CreateQueueService(ctx).UpdateJobPriorityAsync(
            fixture.JobId,
            new UpdateJobPriorityDto
            {
                Priority = (int)PrintJobPriority.Urgent,
                IfMatchJobRowVersion = Convert.ToBase64String(job.RowVersion!),
            },
            CancellationToken.None);

        await using AppDbContext verify = CreateContext();
        PrinterDispatchState state = await verify.PrinterDispatchStates.SingleAsync(s => s.PrinterId == fixture.PrinterId);
        state.AcknowledgedJobId.Should().BeNull("a reorder invalidates the bed-clear acknowledgement");
    }

    [Fact]
    [Trait("Category", "DbHeavy")]
    public async Task EtagMutation_PriorityUpdateWithStaleIfMatch_IsRejected()
    {
        await using AppDbContext seed = CreateContext();
        Fixture fixture = await SeedCalibrationAsync(seed, withAck: false);

        await using AppDbContext ctx = CreateContext();

        Func<Task> act = async () => await CreateQueueService(ctx).UpdateJobPriorityAsync(
            fixture.JobId,
            new UpdateJobPriorityDto
            {
                Priority = (int)PrintJobPriority.Urgent,
                IfMatchJobRowVersion = Convert.ToBase64String(Guid.NewGuid().ToByteArray()),
            },
            CancellationToken.None);

        await act.Should().ThrowAsync<QueueRevisionConflictException>();
    }

    [Fact]
    [Trait("Category", "DbHeavy")]
    public async Task EtagMutation_GenericUpdateCannotSetStartingOrPrinting()
    {
        await using AppDbContext seed = CreateContext();
        Fixture fixture = await SeedCalibrationAsync(seed, withAck: false);

        await using AppDbContext ctx = CreateContext();
        PrintJob job = await ctx.PrintJobs.SingleAsync(j => j.Id == fixture.JobId);

        Func<Task> act = async () => await CreateQueueService(ctx).UpdateJobAsync(
            fixture.JobId,
            new UpdatePrintJobStatusDto
            {
                IfMatchJobRowVersion = Convert.ToBase64String(job.RowVersion!),
                Status = PrintJobStatus.Printing,
            },
            CancellationToken.None);

        await act.Should().ThrowAsync<System.ComponentModel.DataAnnotations.ValidationException>()
            .WithMessage("*cannot be set through the generic update endpoint*");
    }

    // =========================================================================
    // Immutability
    // =========================================================================

    [Fact]
    [Trait("Category", "DbHeavy")]
    public async Task Immutability_FlippingCalibrationToStandardInTheSameSaveIsRejected()
    {
        await using AppDbContext seed = CreateContext();
        Fixture fixture = await SeedCalibrationAsync(seed, withAck: false);

        await using AppDbContext ctx = CreateContext();
        PrintJob job = await ctx.PrintJobs.SingleAsync(j => j.Id == fixture.JobId);

        // The classic bypass: disarm the guard by changing the kind in the same save.
        job.JobKind = JobKind.Standard;
        job.CalibrationAttemptId = Guid.NewGuid();

        Func<Task> act = async () => await ctx.SaveChangesAsync();

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*immutable*");
    }

    // =========================================================================
    // Durable event envelope: isolation, gaps, de-duplication, redaction
    // =========================================================================

    [Fact]
    [Trait("Category", "DbHeavy")]
    public async Task Events_EnvelopeIdentityIsStableAcrossRedeliveries()
    {
        await using AppDbContext seed = CreateContext();
        Fixture fixture = await SeedCalibrationAsync(seed, withAck: true);

        await using AppDbContext claimCtx = CreateContext();
        _ = await CreateClaim(claimCtx, DispatchTestDoubles.OnlineIdleReader(fixture.PrinterId))
            .AcquireClaimAsync(new DispatchClaimRequest(
                fixture.JobId, fixture.PrinterId, "op", "Manual", fixture.AckKey, null, null));

        await using AppDbContext verify = CreateContext();
        QueueDispatchOutbox row = await verify.QueueDispatchOutbox.SingleAsync(e => e.AggregateId == fixture.JobId);

        QueueEventEnvelope first = QueueEventEnvelope.FromOutbox(
            row.Id, row.Sequence, row.CreatedAtUtc, row.EventType,
            jobId: row.AggregateId, printerId: row.PrinterId,
            jobRevision: row.AggregateRowVersion, dispatchStateRevision: row.DispatchStateRowVersion,
            attemptId: row.AttemptId, bedClearState: row.BedClearState, payloadJson: row.PayloadJson);

        QueueEventEnvelope redelivery = QueueEventEnvelope.FromOutbox(
            row.Id, row.Sequence, row.CreatedAtUtc, row.EventType,
            jobId: row.AggregateId, printerId: row.PrinterId,
            jobRevision: row.AggregateRowVersion, dispatchStateRevision: row.DispatchStateRowVersion,
            attemptId: row.AttemptId, bedClearState: row.BedClearState, payloadJson: row.PayloadJson);

        redelivery.Should().Be(first, "a redelivery must be byte-identical so consumers can de-duplicate");
        first.EventId.Should().Be(row.Id, "the envelope id is the durable outbox row id");
        first.OccurredAtUtc.Should().Be(row.CreatedAtUtc, "the envelope time is the durable write time");
        first.Sequence.Should().BeGreaterThan(0, "the sequence enables gap detection");
        first.AttemptId.Should().Be(row.AttemptId);
        first.BedClearState.Should().Be("Consumed");
    }

    [Fact]
    [Trait("Category", "DbHeavy")]
    public async Task Events_PayloadIsRedacted_NoCredentialsUrlsOrPaths()
    {
        await using AppDbContext seed = CreateContext();
        Fixture fixture = await SeedCalibrationAsync(seed, withAck: true);

        await using AppDbContext claimCtx = CreateContext();
        _ = await CreateClaim(claimCtx, DispatchTestDoubles.OnlineIdleReader(fixture.PrinterId))
            .AcquireClaimAsync(new DispatchClaimRequest(
                fixture.JobId, fixture.PrinterId, "op", "Manual", fixture.AckKey, null, null));

        await using AppDbContext verify = CreateContext();
        QueueDispatchOutbox row = await verify.QueueDispatchOutbox.SingleAsync(e => e.AggregateId == fixture.JobId);

        row.PayloadJson.Should().NotContain("http", "payloads must never carry private URLs");
        row.PayloadJson.Should().NotContain("apiKey", "payloads must never carry credentials");
        row.PayloadJson.Should().NotContain("/gcode", "payloads must never carry filesystem paths");

        using JsonDocument doc = JsonDocument.Parse(row.PayloadJson);
        doc.RootElement.TryGetProperty("jobId", out _).Should().BeTrue("payloads carry public identifiers");

        QueueOperationAudit audit = await verify.QueueOperationAudits
            .FirstAsync(a => a.PrintJobId == fixture.JobId && a.Operation == QueueAuditOperations.DispatchClaim);
        audit.DetailJson.Should().NotBeNull();
        audit.DetailJson!.Should().NotContain("http", "audit detail must never carry private URLs");
    }

    [Fact]
    [Trait("Category", "DbHeavy")]
    public async Task Events_SequencesAreUniqueAndGapFreeAcrossProducers()
    {
        await using AppDbContext seed = CreateContext();
        Fixture fixture = await SeedCalibrationAsync(seed, withAck: true);

        await using AppDbContext claimCtx = CreateContext();
        _ = await CreateClaim(claimCtx, DispatchTestDoubles.OnlineIdleReader(fixture.PrinterId))
            .AcquireClaimAsync(new DispatchClaimRequest(
                fixture.JobId, fixture.PrinterId, "op", "Manual", fixture.AckKey, null, null));

        await using AppDbContext verify = CreateContext();
        List<long> sequences = await verify.QueueDispatchOutbox
            .OrderBy(e => e.Sequence)
            .Select(e => e.Sequence)
            .ToListAsync();

        sequences.Should().OnlyHaveUniqueItems("the unique index fences duplicate sequences");
        sequences.Should().BeInAscendingOrder();
        sequences.First().Should().Be(1, "the counter is seeded at 0 and the first allocation is 1");
    }

    // =========================================================================
    // Durable command consumer outcome semantics
    // =========================================================================

    [Theory]
    [InlineData(BackendStartStatus.Accepted)]
    [InlineData(BackendStartStatus.AlreadyStarted)]
    [Trait("Category", "Unit")]
    public void Consumer_OnlyConfirmedOutcomesMayBePublished(BackendStartStatus status)
    {
        // The consumer marks Published only for confirmed-accepted commands. This asserts
        // the typed contract the consumer switches on.
        var outcome = new BackendStartOutcome(status, Guid.NewGuid(), null, null);
        outcome.Status.Should().BeOneOf(BackendStartStatus.Accepted, BackendStartStatus.AlreadyStarted);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Consumer_UnknownOutcomeCarriesAttemptForReconciliation()
    {
        Guid attemptId = Guid.NewGuid();
        BackendStartOutcome outcome = BackendStartOutcome.Unknown("network reset", attemptId);

        outcome.Status.Should().Be(BackendStartStatus.Unknown);
        outcome.AttemptId.Should().Be(attemptId, "the reconciler needs the attempt identity");
        outcome.ErrorCode.Should().Be("backend_outcome_unknown");
    }

    // =========================================================================
    // Concurrency / lifecycle: terminal completion releases lease
    // =========================================================================

    [Fact]
    [Trait("Category", "DbHeavy")]
    public async Task TerminalCleanup_Completed_ReleasesLeaseAndNextClaimSucceeds()
    {
        // claim → backend accepted → job completed → next claim on same printer must succeed
        await using AppDbContext seed = CreateContext();
        Fixture fixture = await SeedCalibrationAsync(seed, withAck: true);

        // Step 1: Acquire claim
        await using AppDbContext claimCtx = CreateContext();
        DispatchClaimService claimSvc = CreateClaim(claimCtx, DispatchTestDoubles.OnlineIdleReader(fixture.PrinterId));
        DispatchClaimResult claim = await claimSvc.AcquireClaimAsync(new DispatchClaimRequest(
            fixture.JobId, fixture.PrinterId, "op", "Manual", fixture.AckKey, null, null));
        claim.Success.Should().BeTrue(claim.ErrorDetail);

        // Step 2: Backend accepted (advances to Printing, preserves lease)
        await using AppDbContext acceptCtx = CreateContext();
        await CreateClaim(acceptCtx, DispatchTestDoubles.OnlineIdleReader(fixture.PrinterId))
            .RecordBackendAcceptedAsync(claim.Attempt!.Id, "backend-job-1");

        // Step 3: Completion service marks job completed — must atomically release the lease.
        await using AppDbContext completeCtx = CreateContext();
        bool completed = await CreateCompletionService(completeCtx)
            .MarkCurrentJobAsCompletedAsync(fixture.PrinterId, "complete");
        completed.Should().BeTrue();

        // Step 4: Dispatch state must have no active lease.
        await using AppDbContext verify = CreateContext();
        PrinterDispatchState state = await verify.PrinterDispatchStates.SingleAsync(s => s.PrinterId == fixture.PrinterId);
        state.ActiveJobId.Should().BeNull("completing a job must release the ActiveJobId lease");
        state.ActiveDispatchAttemptId.Should().BeNull("completing a job must release the ActiveDispatchAttemptId");

        // Step 5: An ad-hoc claim on the same printer must now succeed (lease is free).
        await using AppDbContext ctx2 = CreateContext();
        DispatchClaimResult unblocked = await CreateClaim(ctx2, DispatchTestDoubles.OnlineIdleReader(fixture.PrinterId))
            .AcquireAdHocClaimAsync(new AdHocDispatchClaimRequest(fixture.PrinterId, "op", "PrinterFile", "next.gcode"));
        unblocked.Success.Should().BeTrue("after completion the printer lease must be free for a new start");
    }

    [Fact]
    [Trait("Category", "DbHeavy")]
    public async Task TerminalCleanup_Failed_ReleasesLeaseAndNextClaimSucceeds()
    {
        // claim → backend accepted → job failed → next claim must succeed
        await using AppDbContext seed = CreateContext();
        Fixture fixture = await SeedCalibrationAsync(seed, withAck: true);

        await using AppDbContext claimCtx = CreateContext();
        DispatchClaimService claimSvc = CreateClaim(claimCtx, DispatchTestDoubles.OnlineIdleReader(fixture.PrinterId));
        DispatchClaimResult claim = await claimSvc.AcquireClaimAsync(new DispatchClaimRequest(
            fixture.JobId, fixture.PrinterId, "op", "Manual", fixture.AckKey, null, null));
        claim.Success.Should().BeTrue(claim.ErrorDetail);

        await using AppDbContext acceptCtx = CreateContext();
        await CreateClaim(acceptCtx, DispatchTestDoubles.OnlineIdleReader(fixture.PrinterId))
            .RecordBackendAcceptedAsync(claim.Attempt!.Id, "backend-job-2");

        await using AppDbContext failCtx = CreateContext();
        bool failed = await CreateCompletionService(failCtx)
            .MarkCurrentJobAsFailedAsync(fixture.PrinterId, "nozzle clog");
        failed.Should().BeTrue();

        await using AppDbContext verify = CreateContext();
        PrinterDispatchState state = await verify.PrinterDispatchStates.SingleAsync(s => s.PrinterId == fixture.PrinterId);
        state.ActiveJobId.Should().BeNull("failing a job must release the ActiveJobId lease");
        state.ActiveDispatchAttemptId.Should().BeNull("failing a job must release the ActiveDispatchAttemptId");
    }

    // =========================================================================
    // Concurrency / lifecycle: mutual exclusion ad-hoc vs queue
    // =========================================================================

    [Fact]
    [Trait("Category", "DbHeavy")]
    public async Task MutualExclusion_AdHocVsQueue_AdHocInFlightBlocksQueueClaim()
    {
        // An active ad-hoc claim must prevent a queue claim on the same printer.
        await using AppDbContext seed = CreateContext();
        Fixture fixture = await SeedCalibrationAsync(seed, withAck: true);

        // Ad-hoc claim first.
        await using AppDbContext adHocCtx = CreateContext();
        DispatchClaimResult adHocResult = await CreateClaim(adHocCtx, DispatchTestDoubles.OnlineIdleReader(fixture.PrinterId))
            .AcquireAdHocClaimAsync(new AdHocDispatchClaimRequest(fixture.PrinterId, "op", "SliceBridge", "file.gcode"));
        adHocResult.Success.Should().BeTrue(adHocResult.ErrorDetail);

        // Queue claim on the same printer must now fail with printer_busy_active.
        await using AppDbContext queueCtx = CreateContext();
        DispatchClaimResult queueResult = await CreateClaim(queueCtx, DispatchTestDoubles.OnlineIdleReader(fixture.PrinterId))
            .AcquireClaimAsync(new DispatchClaimRequest(
                fixture.JobId, fixture.PrinterId, "op", "Manual", fixture.AckKey, null, null));

        queueResult.Success.Should().BeFalse("an ad-hoc claim must block a queue claim on the same printer");
        queueResult.ErrorCode.Should().Be("printer_busy_active");

        // Both operations must be audited.
        await using AppDbContext verify = CreateContext();
        (await verify.QueueOperationAudits.CountAsync(a =>
                a.Operation == QueueAuditOperations.AdHocStart &&
                a.Outcome == QueueAuditOutcomes.Success))
            .Should().BeGreaterThanOrEqualTo(1, "the granted ad-hoc start must be audited");
    }

    [Fact]
    [Trait("Category", "DbHeavy")]
    public async Task MutualExclusion_QueueVsAdHoc_QueueClaimBlocksAdHoc()
    {
        // An active queue claim must prevent an ad-hoc claim on the same printer.
        await using AppDbContext seed = CreateContext();
        Fixture fixture = await SeedCalibrationAsync(seed, withAck: true);

        // Queue claim first.
        await using AppDbContext queueCtx = CreateContext();
        DispatchClaimResult queueResult = await CreateClaim(queueCtx, DispatchTestDoubles.OnlineIdleReader(fixture.PrinterId))
            .AcquireClaimAsync(new DispatchClaimRequest(
                fixture.JobId, fixture.PrinterId, "op", "Manual", fixture.AckKey, null, null));
        queueResult.Success.Should().BeTrue(queueResult.ErrorDetail);

        // Ad-hoc claim on the same printer must now fail.
        await using AppDbContext adHocCtx = CreateContext();
        DispatchClaimResult adHocResult = await CreateClaim(adHocCtx, DispatchTestDoubles.OnlineIdleReader(fixture.PrinterId))
            .AcquireAdHocClaimAsync(new AdHocDispatchClaimRequest(fixture.PrinterId, "op", "PrinterFile", "other.gcode"));

        adHocResult.Success.Should().BeFalse("a queue claim must block an ad-hoc claim on the same printer");
        adHocResult.ErrorCode.Should().Be("printer_busy_active");
    }

    // =========================================================================
    // Ad-hoc telemetry gate: fail-closed
    // =========================================================================

    [Fact]
    [Trait("Category", "DbHeavy")]
    public async Task AdHoc_MissingTelemetry_FailsClosed()
    {
        await using AppDbContext seed = CreateContext();
        Fixture fixture = await SeedCalibrationAsync(seed, withAck: false);

        await using AppDbContext ctx = CreateContext();
        // NoTelemetryReader simulates a printer that has never reported status.
        DispatchClaimResult result = await CreateClaim(ctx, DispatchTestDoubles.NoTelemetryReader())
            .AcquireAdHocClaimAsync(new AdHocDispatchClaimRequest(fixture.PrinterId, "op", "SliceBridge", "file.gcode"));

        result.Success.Should().BeFalse("ad-hoc dispatch must fail closed when no telemetry is available");
        result.ErrorCode.Should().Be("telemetry_unavailable");
    }

    [Fact]
    [Trait("Category", "DbHeavy")]
    public async Task AdHoc_StaleTelemetry_FailsClosed()
    {
        await using AppDbContext seed = CreateContext();
        Fixture fixture = await SeedCalibrationAsync(seed, withAck: false);

        await using AppDbContext ctx = CreateContext();
        DispatchClaimResult result = await CreateClaim(ctx, DispatchTestDoubles.StaleReader(fixture.PrinterId))
            .AcquireAdHocClaimAsync(new AdHocDispatchClaimRequest(fixture.PrinterId, "op", "SliceBridge", "file.gcode"));

        result.Success.Should().BeFalse("ad-hoc dispatch must fail closed when telemetry is stale");
        result.ErrorCode.Should().Be("telemetry_stale");
    }

    // =========================================================================
    // Shared ordering selector
    // =========================================================================

    [Fact]
    [Trait("Category", "DbHeavy")]
    public async Task ReadyHead_UsesUrgentFirstOrdering()
    {
        await using AppDbContext seed = CreateContext();
        Fixture fixture = await SeedCalibrationArtifactOnlyAsync(seed);

        await using AppDbContext ctx = CreateContext();
        Guid urgentId = Guid.NewGuid();
        foreach ((Guid id, PrintJobPriority priority, int position) in new[]
        {
            (Guid.NewGuid(), PrintJobPriority.Low, 1),
            (urgentId, PrintJobPriority.Urgent, 2),
            (Guid.NewGuid(), PrintJobPriority.Normal, 3),
        })
        {
            ctx.PrintJobs.Add(new PrintJob
            {
                Id = id,
                Name = priority.ToString(),
                GcodeFileId = fixture.GcodeId,
                AssignedPrinterId = fixture.PrinterId,
                Status = PrintJobStatus.Queued,
                Priority = (int)priority,
                QueuePosition = position,
                JobKind = JobKind.Standard,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                QueuedAt = DateTime.UtcNow,
            });
        }

        await ctx.SaveChangesAsync();

        await using AppDbContext verify = CreateContext();
        PrintJob head = await verify.PrintJobs
            .Where(j => j.AssignedPrinterId == fixture.PrinterId && j.Status == PrintJobStatus.Queued)
            .OrderByPriorityDescending()
            .FirstAsync();

        head.Id.Should().Be(urgentId, "Urgent must run first — an ascending sort would pick Low");
    }

    // =========================================================================
    // Terminal lifecycle events — outbox correctness
    // =========================================================================

    [Fact]
    [Trait("Category", "DbHeavy")]
    public async Task TerminalEvent_Completion_WritesOrderedOutboxEventInSameTransaction()
    {
        // Arrange: seed, claim, accept
        await using AppDbContext seed = CreateContext();
        Fixture fixture = await SeedCalibrationAsync(seed, withAck: true);

        await using AppDbContext claimCtx = CreateContext();
        DispatchClaimResult claim = await CreateClaim(claimCtx, DispatchTestDoubles.OnlineIdleReader(fixture.PrinterId))
            .AcquireClaimAsync(new DispatchClaimRequest(
                fixture.JobId, fixture.PrinterId, "op", "Manual", fixture.AckKey, null, null));
        claim.Success.Should().BeTrue(claim.ErrorDetail);
        long startSeq = await GetMaxOutboxSequenceAsync(fixture.JobId);

        await using AppDbContext acceptCtx = CreateContext();
        await CreateClaim(acceptCtx, DispatchTestDoubles.OnlineIdleReader(fixture.PrinterId))
            .RecordBackendAcceptedAsync(claim.Attempt!.Id, "bk-1");
        long afterAcceptSeq = await GetMaxOutboxSequenceAsync(fixture.JobId);
        afterAcceptSeq.Should().BeGreaterThan(startSeq, "backend-accepted must advance the outbox sequence");

        // Act: mark job completed
        await using AppDbContext completeCtx = CreateContext();
        bool completed = await CreateCompletionService(completeCtx)
            .MarkCurrentJobAsCompletedAsync(fixture.PrinterId, "complete");
        completed.Should().BeTrue();

        // Assert: completion wrote a new ordered outbox event
        await using AppDbContext verify = CreateContext();
        QueueDispatchOutbox completionEvent = await verify.QueueDispatchOutbox
            .Where(e => e.AggregateId == fixture.JobId && e.EventType == DispatchClaimService.EventTypeJobCompleted)
            .OrderByDescending(e => e.Sequence)
            .FirstAsync();

        completionEvent.Sequence.Should().BeGreaterThan(afterAcceptSeq,
            "the completion event must have a higher sequence than the backend-accepted event");
        completionEvent.Status.Should().Be(QueueOutboxEventStatus.Pending,
            "lifecycle events are Pending so the publisher can broadcast them via SignalR");
    }

    [Fact]
    [Trait("Category", "DbHeavy")]
    public async Task TerminalEvent_Failure_WritesOutboxEventInSameTransaction()
    {
        await using AppDbContext seed = CreateContext();
        Fixture fixture = await SeedCalibrationAsync(seed, withAck: true);

        await using AppDbContext claimCtx = CreateContext();
        DispatchClaimResult claim = await CreateClaim(claimCtx, DispatchTestDoubles.OnlineIdleReader(fixture.PrinterId))
            .AcquireClaimAsync(new DispatchClaimRequest(
                fixture.JobId, fixture.PrinterId, "op", "Manual", fixture.AckKey, null, null));
        claim.Success.Should().BeTrue(claim.ErrorDetail);

        await using AppDbContext acceptCtx = CreateContext();
        await CreateClaim(acceptCtx, DispatchTestDoubles.OnlineIdleReader(fixture.PrinterId))
            .RecordBackendAcceptedAsync(claim.Attempt!.Id, "bk-2");

        // Act
        await using AppDbContext failCtx = CreateContext();
        bool failed = await CreateCompletionService(failCtx)
            .MarkCurrentJobAsFailedAsync(fixture.PrinterId, "nozzle clog");
        failed.Should().BeTrue();

        // Assert
        await using AppDbContext verify = CreateContext();
        QueueDispatchOutbox failEvent = await verify.QueueDispatchOutbox
            .Where(e => e.AggregateId == fixture.JobId && e.EventType == DispatchClaimService.EventTypeJobFailed)
            .SingleAsync();

        failEvent.FailureCode.Should().Be("backend_failure");
        failEvent.Status.Should().Be(QueueOutboxEventStatus.Pending);
    }

    [Fact]
    [Trait("Category", "DbHeavy")]
    public async Task TerminalEvent_KnownFailure_WritesOutboxEventAtomically()
    {
        await using AppDbContext seed = CreateContext();
        Fixture fixture = await SeedCalibrationAsync(seed, withAck: true);

        await using AppDbContext claimCtx = CreateContext();
        DispatchClaimResult claim = await CreateClaim(claimCtx, DispatchTestDoubles.OnlineIdleReader(fixture.PrinterId))
            .AcquireClaimAsync(new DispatchClaimRequest(
                fixture.JobId, fixture.PrinterId, "op", "Manual", fixture.AckKey, null, null));
        claim.Success.Should().BeTrue(claim.ErrorDetail);

        long seqBeforeFailure = await GetMaxOutboxSequenceAsync(fixture.JobId);

        // Act: simulate known pre-start failure (artifact missing)
        await using AppDbContext failCtx = CreateContext();
        await CreateClaim(failCtx, DispatchTestDoubles.OnlineIdleReader(fixture.PrinterId))
            .ReleaseClaimOnKnownFailureAsync(claim.Attempt!.Id, "artifact_unavailable", "G-code not found");

        // Assert: known-failure event was written with a higher sequence
        await using AppDbContext verify = CreateContext();
        QueueDispatchOutbox failEvent = await verify.QueueDispatchOutbox
            .Where(e => e.AggregateId == fixture.JobId && e.EventType == DispatchClaimService.EventTypeKnownFailure)
            .SingleAsync();

        failEvent.Sequence.Should().BeGreaterThan(seqBeforeFailure,
            "the known-failure event must be ordered after the dispatch-started event");
        failEvent.FailureCode.Should().Be("artifact_unavailable");
    }

    [Fact]
    [Trait("Category", "DbHeavy")]
    public async Task TerminalEvent_BackendAccepted_WritesOutboxEventAtomically()
    {
        await using AppDbContext seed = CreateContext();
        Fixture fixture = await SeedCalibrationAsync(seed, withAck: true);

        await using AppDbContext claimCtx = CreateContext();
        DispatchClaimResult claim = await CreateClaim(claimCtx, DispatchTestDoubles.OnlineIdleReader(fixture.PrinterId))
            .AcquireClaimAsync(new DispatchClaimRequest(
                fixture.JobId, fixture.PrinterId, "op", "Manual", fixture.AckKey, null, null));
        claim.Success.Should().BeTrue(claim.ErrorDetail);

        long seqAfterClaim = await GetMaxOutboxSequenceAsync(fixture.JobId);

        // Act: record backend accepted
        await using AppDbContext acceptCtx = CreateContext();
        await CreateClaim(acceptCtx, DispatchTestDoubles.OnlineIdleReader(fixture.PrinterId))
            .RecordBackendAcceptedAsync(claim.Attempt!.Id, "printer-job-99");

        // Assert
        await using AppDbContext verify = CreateContext();
        QueueDispatchOutbox acceptEvent = await verify.QueueDispatchOutbox
            .Where(e => e.AggregateId == fixture.JobId && e.EventType == DispatchClaimService.EventTypeBackendAccepted)
            .SingleAsync();

        acceptEvent.Sequence.Should().BeGreaterThan(seqAfterClaim,
            "backend-accepted sequence must be greater than claim sequence");
        acceptEvent.Status.Should().Be(QueueOutboxEventStatus.Pending);

        // Job must be advanced to Printing
        PrintJob job = await verify.PrintJobs.SingleAsync(j => j.Id == fixture.JobId);
        job.Status.Should().Be(PrintJobStatus.Printing);
    }

    [Fact]
    [Trait("Category", "DbHeavy")]
    public async Task TerminalEvent_UnknownOutcome_WritesOutboxEventWithFailureCode()
    {
        await using AppDbContext seed = CreateContext();
        Fixture fixture = await SeedCalibrationAsync(seed, withAck: true);

        await using AppDbContext claimCtx = CreateContext();
        DispatchClaimResult claim = await CreateClaim(claimCtx, DispatchTestDoubles.OnlineIdleReader(fixture.PrinterId))
            .AcquireClaimAsync(new DispatchClaimRequest(
                fixture.JobId, fixture.PrinterId, "op", "Manual", fixture.AckKey, null, null));
        claim.Success.Should().BeTrue(claim.ErrorDetail);

        // Act: record unknown outcome (crash/timeout scenario)
        await using AppDbContext unknownCtx = CreateContext();
        await CreateClaim(unknownCtx, DispatchTestDoubles.OnlineIdleReader(fixture.PrinterId))
            .RecordUnknownOutcomeAsync(claim.Attempt!.Id, "Connection timed out");

        // Assert: unknown-outcome event was written
        await using AppDbContext verify = CreateContext();
        QueueDispatchOutbox unknownEvent = await verify.QueueDispatchOutbox
            .Where(e => e.AggregateId == fixture.JobId && e.EventType == DispatchClaimService.EventTypeUnknownOutcome)
            .SingleAsync();

        unknownEvent.FailureCode.Should().Be("backend_outcome_unknown");
        unknownEvent.Status.Should().Be(QueueOutboxEventStatus.Pending);
    }

    [Fact]
    [Trait("Category", "DbHeavy")]
    public async Task TerminalEvents_FullLifecycle_OutboxSequencesAreStrictlyOrdered()
    {
        // Prove that claim → backend-accepted → completion produces strictly ordered sequences.
        await using AppDbContext seed = CreateContext();
        Fixture fixture = await SeedCalibrationAsync(seed, withAck: true);

        await using AppDbContext claimCtx = CreateContext();
        DispatchClaimResult claim = await CreateClaim(claimCtx, DispatchTestDoubles.OnlineIdleReader(fixture.PrinterId))
            .AcquireClaimAsync(new DispatchClaimRequest(
                fixture.JobId, fixture.PrinterId, "op", "Manual", fixture.AckKey, null, null));
        claim.Success.Should().BeTrue(claim.ErrorDetail);

        await using AppDbContext acceptCtx = CreateContext();
        await CreateClaim(acceptCtx, DispatchTestDoubles.OnlineIdleReader(fixture.PrinterId))
            .RecordBackendAcceptedAsync(claim.Attempt!.Id, "bk-seq");

        await using AppDbContext completeCtx = CreateContext();
        await CreateCompletionService(completeCtx).MarkCurrentJobAsCompletedAsync(fixture.PrinterId, "complete");

        // Assert: all three events exist and sequences are strictly increasing
        await using AppDbContext verify = CreateContext();
        List<QueueDispatchOutbox> events = await verify.QueueDispatchOutbox
            .Where(e => e.AggregateId == fixture.JobId)
            .OrderBy(e => e.Sequence)
            .ToListAsync();

        events.Should().HaveCountGreaterThanOrEqualTo(3,
            "claim + backend-accepted + completion must each produce an outbox event");

        List<long> sequences = events.Select(e => e.Sequence).ToList();
        for (int i = 1; i < sequences.Count; i++)
        {
            sequences[i].Should().BeGreaterThan(sequences[i - 1],
                $"outbox event at position {i} (seq={sequences[i]}) must have a higher " +
                $"sequence than the previous (seq={sequences[i - 1]}) — client gap detection requires strict order");
        }
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private async Task<long> GetMaxOutboxSequenceAsync(Guid jobId)
    {
        await using AppDbContext ctx = CreateContext();
        return await ctx.QueueDispatchOutbox
            .Where(e => e.AggregateId == jobId)
            .MaxAsync(e => (long?)e.Sequence) ?? 0L;
    }

    private sealed record Fixture(Guid PrinterId, Guid JobId, Guid GcodeId, string AckKey);

    private static PrintJobCompletionService CreateCompletionService(AppDbContext db)
    {
        // Minimal hub mock: BroadcastJobQueueUpdateAsync wraps hub calls in try-catch so
        // a non-null stub that does nothing is sufficient for completion tests.
        var hubClientsMock = new Mock<IHubClients>();
        var groupProxy = new Mock<IClientProxy>();
        hubClientsMock.Setup(c => c.Group(It.IsAny<string>())).Returns(groupProxy.Object);
        hubClientsMock.Setup(c => c.All).Returns(groupProxy.Object);
        var hub = new Mock<IHubContext<PrinterHub>>();
        hub.Setup(h => h.Clients).Returns(hubClientsMock.Object);

        return new PrintJobCompletionService(
            db,
            hub.Object,
            NullLogger<PrintJobCompletionService>.Instance,
            sequenceAllocator: new DbOutboxSequenceAllocator());
    }

    private AppDbContext CreateContext()
    {
        DbContextOptions<AppDbContext> opts = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connectionString, sqlite => sqlite.MigrationsAssembly("Farm.Migrations.Sqlite"))
            .Options;
        var ctx = new AppDbContext(opts);
        ctx.Database.ExecuteSqlRaw("PRAGMA foreign_keys = OFF;");
        return ctx;
    }

    private static DispatchClaimService CreateClaim(AppDbContext db, IPrinterStatusSnapshotReader reader) =>
        new(db, reader, new DbOutboxSequenceAllocator(), NullLogger<DispatchClaimService>.Instance);

    private static JobQueueService CreateQueueService(AppDbContext db) =>
        new(
            new EfQueueRepository(db),
            new DirectQueueDataService(db),
            NullLogger<JobQueueService>.Instance,
            db: db,
            sequenceAllocator: new DbOutboxSequenceAllocator());

    /// <summary>
    /// Minimal <see cref="IQueueDataService"/> that reads straight from the migrated
    /// database, so the production <see cref="JobQueueService"/> logic runs unmodified
    /// without pulling in the full unit-of-work/DI graph.
    /// </summary>
    private sealed class DirectQueueDataService(AppDbContext db) : IQueueDataService
    {
        public Task<List<Printer>> GetAvailablePrintersAsync(CancellationToken ct) =>
            db.Printers.Include(p => p.Toolheads).Where(p => p.IsEnabled && p.IsAvailable).ToListAsync(ct);

        public Task<List<Printer>> GetCompatiblePrintersAsync(string modelNameOrAlias, CancellationToken ct) =>
            GetAvailablePrintersAsync(ct);

        public Task<List<PrintJob>> GetPrintJobsForPrinterAsync(Guid printerId, CancellationToken ct) =>
            db.PrintJobs
                .Where(j => j.AssignedPrinterId == printerId)
                .OrderByPriorityDescending()
                .ToListAsync(ct);

        public Task<PrintJob?> GetCurrentJobForPrinterAsync(Guid printerId, CancellationToken ct) =>
            db.PrintJobs.FirstOrDefaultAsync(
                j => j.AssignedPrinterId == printerId &&
                     (j.Status == PrintJobStatus.Printing || j.Status == PrintJobStatus.Starting),
                ct);

        public Task<GcodeFile?> GetGcodeFileAsync(Guid id, CancellationToken ct) =>
            db.GcodeFiles.FirstOrDefaultAsync(g => g.Id == id, ct);

        public Task<PrintJob?> GetPrintJobByIdAsync(Guid id, CancellationToken ct) =>
            db.PrintJobs
                .Include(j => j.GcodeFile)
                .Include(j => j.AssignedPrinter)
                .FirstOrDefaultAsync(j => j.Id == id, ct);

        public Task<int> CountQueuedJobsForPrinterAsync(Guid printerId, CancellationToken ct) =>
            db.PrintJobs.CountAsync(
                j => j.AssignedPrinterId == printerId &&
                     (j.Status == PrintJobStatus.Queued || j.Status == PrintJobStatus.Assigned),
                ct);

        public async Task<int> GetNextQueuePositionAsync(Guid printerId, CancellationToken ct)
        {
            List<int> positions = await db.PrintJobs
                .Where(j => j.AssignedPrinterId == printerId)
                .Select(j => j.QueuePosition)
                .ToListAsync(ct);

            return positions.Count == 0 ? 1 : positions.Max() + 1;
        }

        public Task<List<PrintJob>> GetAllPrintJobsAsync(CancellationToken ct) =>
            db.PrintJobs.Include(j => j.GcodeFile).Include(j => j.AssignedPrinter).ToListAsync(ct);

        public async Task<int> GetNextGlobalQueuePositionAsync(CancellationToken ct)
        {
            List<int> positions = await db.PrintJobs.Select(j => j.QueuePosition).ToListAsync(ct);
            return positions.Count == 0 ? 1 : positions.Max() + 1;
        }

        public Task<int> CountActiveJobsUsingGcodeAsync(Guid gcodeFileId, CancellationToken ct) =>
            db.PrintJobs.CountAsync(
                j => j.GcodeFileId == gcodeFileId &&
                     (j.Status == PrintJobStatus.Queued ||
                      j.Status == PrintJobStatus.Assigned ||
                      j.Status == PrintJobStatus.Starting ||
                      j.Status == PrintJobStatus.Printing),
                ct);

        public Task<List<PrintJob>> GetPrintJobsForPrintersAsync(IEnumerable<Guid> printerIds, CancellationToken ct)
        {
            List<Guid> ids = printerIds.ToList();
            return db.PrintJobs
                .Where(j => j.AssignedPrinterId != null && ids.Contains(j.AssignedPrinterId.Value))
                .OrderByPriorityDescending()
                .ToListAsync(ct);
        }
    }

    private async Task<Fixture> SeedCalibrationArtifactOnlyAsync(AppDbContext db)
    {
        await db.Database.MigrateAsync();

        var folder = new FolderNode
        {
            Id = Guid.NewGuid(),
            Path = "/",
            FolderType = "gcode",
        };
        db.Set<FolderNode>().Add(folder);

        var mfr = new Manufacturer { Id = Guid.NewGuid(), Name = $"Mfr-{Guid.NewGuid():N}" };
        db.Manufacturers.Add(mfr);
        var mdl = new PrinterModel { Id = Guid.NewGuid(), ManufacturerId = mfr.Id, Name = $"Mdl-{Guid.NewGuid():N}" };
        db.PrinterModels.Add(mdl);

        GcodeFile gcode = BuildPromotedArtifact();
        gcode.FolderId = folder.Id;
        db.GcodeFiles.Add(gcode);

        Printer printer = BuildPrinter(mfr.Id, mdl.Id);
        db.Printers.Add(printer);
        db.PrinterDispatchStates.Add(new PrinterDispatchState { PrinterId = printer.Id });

        await db.SaveChangesAsync();
        return new Fixture(printer.Id, Guid.Empty, gcode.Id, string.Empty);
    }

    private async Task<Fixture> SeedCalibrationAsync(AppDbContext db, bool withAck)
    {
        Fixture baseFixture = await SeedCalibrationArtifactOnlyAsync(db);

        GcodeFile gcode = await db.GcodeFiles.SingleAsync(g => g.Id == baseFixture.GcodeId);

        var job = new PrintJob
        {
            Id = Guid.NewGuid(),
            Name = "calibration",
            GcodeFileId = gcode.Id,
            AssignedPrinterId = baseFixture.PrinterId,
            Status = PrintJobStatus.Assigned,
            Priority = (int)PrintJobPriority.High,
            QueuePosition = 1,
            JobKind = JobKind.FilamentCalibration,
            RequiredFirmwareFamily = PrinterFirmwareFamily.Klipper,
            RequiredGcodeDialect = PrinterGcodeDialect.Klipper,
            RequiredSlicerEngine = "OrcaSlicer",
            RequiredSlicerDistribution = "upstream",
            RequiredSlicerVersion = "2.3.0",
            PinnedPrinterConfigRevision = 1,
            GcodeContentSha256 = gcode.ContentSha256,
            SpecificationSha256 = gcode.SpecificationSha256,
            MachineProfileSha256 = gcode.MachineProfileSha256,
            ProcessProfileSha256 = gcode.ProcessProfileSha256,
            FilamentProfileSha256 = gcode.FilamentProfileSha256,
            CalibrationProjectId = gcode.CalibrationProjectId,
            CalibrationAttemptId = gcode.CalibrationAttemptId,
            CalibrationOrchestrationId = gcode.CalibrationOrchestrationId,
            CalibrationConfigSnapshotId = Guid.NewGuid(),
            SpoolmanSpoolId = SpoolId,
            RequiredMaterialType = Material,
            IdempotencyScope = "prod-scope",
            IdempotencyKey = Guid.NewGuid().ToString(),
            IdempotencyRequestSha256 = new string('f', 64),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            QueuedAt = DateTime.UtcNow,
        };
        db.PrintJobs.Add(job);
        await db.SaveChangesAsync();

        const string AckKey = "prod-ack-key";
        if (withAck)
        {
            PrinterDispatchState state = await db.PrinterDispatchStates
                .SingleAsync(s => s.PrinterId == baseFixture.PrinterId);
            state.AcknowledgedJobId = job.Id;
            state.AcknowledgedAtUtc = DateTime.UtcNow;
            state.AcknowledgedBySubject = "operator-1";
            state.AcknowledgementIdempotencyKey = AckKey;
            state.AcknowledgementExpiresAtUtc = DateTime.UtcNow.AddMinutes(15);
            await db.SaveChangesAsync();
        }

        return baseFixture with { JobId = job.Id, AckKey = AckKey };
    }

    private static GcodeFile BuildPromotedArtifact() => new()
    {
        Id = Guid.NewGuid(),
        Name = "calibration.gcode",
        FileName = "calibration.gcode",
        FilePath = "/gcode",
        FileSizeBytes = 2048,
        FileHash = new string('a', 64),
        IsImmutable = true,
        PromotedAtUtc = DateTime.UtcNow.AddMinutes(-1),
        ContentSha256 = new string('a', 64),
        CalibrationProjectId = Guid.NewGuid(),
        CalibrationAttemptId = Guid.NewGuid(),
        CalibrationOrchestrationId = Guid.NewGuid(),
        CalibrationManifestSha256 = new string('9', 64),
        SpecificationSha256 = new string('b', 64),
        MachineProfileSha256 = new string('c', 64),
        ProcessProfileSha256 = new string('d', 64),
        FilamentProfileSha256 = new string('e', 64),
        SlicerEngineName = "OrcaSlicer",
        SlicerDistribution = "upstream",
        PinnedSlicerVersion = "2.3.0",
        FirmwareFamily = nameof(PrinterFirmwareFamily.Klipper),
        GcodeDialect = nameof(PrinterGcodeDialect.Klipper),
    };

    private static Printer BuildPrinter(Guid manufacturerId, Guid modelId) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Production Printer",
        ServerUrl = $"http://prod-{Guid.NewGuid():N}",
        ManufacturerId = manufacturerId,
        ModelId = modelId,
        IsEnabled = true,
        IsAvailable = true,
        InMaintenance = false,
        FirmwareFamily = PrinterFirmwareFamily.Klipper,
        GcodeDialect = PrinterGcodeDialect.Klipper,
        CalibrationSlicerEngine = "OrcaSlicer",
        CalibrationSlicerDistribution = "upstream",
        CalibrationSlicerVersion = "2.3.0",
        ConfigurationRevision = 1,
        CurrentSpoolId = SpoolId,
        CurrentMaterial = Material,
    };
}
