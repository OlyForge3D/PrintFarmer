using System.Security.Claims;
using System.Text.Json;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Security;
using Farm.Infrastructure.Services.Queue;
using Farm.Infrastructure.Services.Queue.Dispatch;
using Farm.Infrastructure.Tests.Dispatch;
using Farm.Modules.PrintQueue.Controllers;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Dispatch;

/// <summary>Regression coverage for the indeterminate dispatch recovery escape hatch.</summary>
public sealed class DispatchRecoveryServiceTests : IAsyncDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 18, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _keepAlive;
    private readonly string _connectionString;
    private readonly FixedTimeProvider _clock = new(Now);

    public DispatchRecoveryServiceTests()
    {
        _connectionString = $"Data Source=file:dispatch_recovery_{Guid.NewGuid():N}?mode=memory&cache=shared;Foreign Keys=False";
        _keepAlive = new SqliteConnection(_connectionString);
        _keepAlive.Open();
    }

    public async ValueTask DisposeAsync()
    {
        await _keepAlive.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    [Fact]
    [Trait("Category", "DbHeavy")]
    public async Task RecoverAsync_IndeterminateClaim_ReleasesStateAndPersistsAudit()
    {
        RecoveryFixture fixture = await SeedIndeterminateClaimAsync(senderSettled: true);
        DispatchRecoveryService sut = CreateRecoveryService(CreateContext());
        DateTime clientReportedAtUtc = Now.AddMinutes(-3).UtcDateTime;

        DispatchRecoveryResult result = await sut.RecoverAsync(
            fixture.PrinterId,
            "operator-1",
            fixture.StateRevision,
            RevisionETag.EncodeQuoted(fixture.StateRevision),
            "recover-success",
            new DispatchRecoveryRequest
            {
                DispatchAttemptId = fixture.AttemptId,
                ClaimRevision = fixture.AttemptRevision,
                PhysicalCheckConfirmed = true,
                SenderIsolationConfirmed = false,
                ClientReportedAtUtc = clientReportedAtUtc,
                Note = "physically empty",
            },
            "corr-1",
            CancellationToken.None);

        result.StatusCode.Should().Be(StatusCodes.Status200OK);
        result.ETag.Should().NotBeNullOrWhiteSpace();
        using JsonDocument body = JsonDocument.Parse(result.BodyJson);
        body.RootElement.GetProperty("hasIndeterminateClaim").GetBoolean().Should().BeFalse();
        body.RootElement.GetProperty("recoveryAuditId").GetGuid().Should().NotBeEmpty();

        await using AppDbContext verify = CreateContext();
        QueueDispatchAttempt attempt = await verify.QueueDispatchAttempts.SingleAsync(a => a.Id == fixture.AttemptId);
        PrintJob job = await verify.PrintJobs.SingleAsync(j => j.Id == fixture.JobId);
        PrinterDispatchState state = await verify.PrinterDispatchStates.SingleAsync(s => s.PrinterId == fixture.PrinterId);
        QueueDispatchOutbox start = await verify.QueueDispatchOutbox.SingleAsync(o => o.Id == fixture.StartOutboxId);
        QueueDispatchOutbox control = await verify.QueueDispatchOutbox.SingleAsync(o => o.Id == fixture.ControlOutboxId);
        BedClearCommandRecord bedClear = await verify.BedClearCommandRecords.SingleAsync(r => r.Id == fixture.BedClearRecordId);
        DispatchRecoveryJournalEntry journal = await verify.DispatchRecoveryJournalEntries.SingleAsync();

        attempt.Outcome.Should().Be(DispatchAttemptOutcome.OperatorRecovered);
        attempt.BackendCallPhase.Should().Be(DispatchBackendCallPhase.Terminal);
        attempt.IsRetryable.Should().BeFalse();
        attempt.RequiresReconciliation.Should().BeFalse();
        attempt.TerminalAtUtc.Should().Be(Now.UtcDateTime);
        job.Status.Should().Be(PrintJobStatus.Queued);
        job.BlockedReasonCode.Should().Be(JobBlockedReasonCode.OperatorRecoveryRequired);
        job.AssignedPrinterId.Should().Be(fixture.PrinterId);
        state.ActiveJobId.Should().BeNull();
        state.ActiveDispatchAttemptId.Should().BeNull();
        state.PhysicalControlCommandId.Should().BeNull();
        start.Status.Should().Be(QueueOutboxEventStatus.DeadLettered);
        start.FailureCode.Should().Be("operator_recovery");
        control.Status.Should().Be(QueueOutboxEventStatus.DeadLettered);
        control.FailureCode.Should().Be("superseded_by_operator_recovery");
        bedClear.Status.Should().Be(BedClearCommandStatus.Rejected);
        journal.Transition.Should().Be(DispatchRecoveryService.TransitionAccepted);
        journal.ActorRecordedAtUtc.Should().Be(Now.UtcDateTime);
        journal.ServerRecordedAtUtc.Should().Be(Now.UtcDateTime);
        journal.ClientReportedAtUtc.Should().Be(clientReportedAtUtc);
        journal.ResponseETag.Should().Be(result.ETag);
        journal.ResponseBodyJson.Should().Be(result.BodyJson);
        (await verify.QueueDispatchOutbox.AnyAsync(o => o.EventType == DispatchClaimService.EventTypeDispatchOperatorRecovered))
            .Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "DbHeavy")]
    public async Task RecoverAsync_SameIdempotencyKey_ReplaysOrRejectsFingerprintReuse()
    {
        RecoveryFixture fixture = await SeedIndeterminateClaimAsync(senderSettled: true);
        await using AppDbContext db = CreateContext();
        DispatchRecoveryService sut = CreateRecoveryService(db);
        var request = new DispatchRecoveryRequest
        {
            DispatchAttemptId = fixture.AttemptId,
            ClaimRevision = fixture.AttemptRevision,
            PhysicalCheckConfirmed = true,
        };

        DispatchRecoveryResult first = await RecoverAsync(sut, fixture, "same-key", request);
        DispatchRecoveryResult replay = await RecoverAsync(sut, fixture, "same-key", request);
        request.Note = "different";
        DispatchRecoveryResult reused = await RecoverAsync(sut, fixture, "same-key", request);

        replay.StatusCode.Should().Be(first.StatusCode);
        replay.ETag.Should().Be(first.ETag);
        replay.BodyJson.Should().Be(first.BodyJson);
        reused.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        JsonError(reused).Should().Be("idempotency_key_reused");
        (await db.DispatchRecoveryJournalEntries.CountAsync()).Should().Be(1);
    }

    [Fact]
    [Trait("Category", "DbHeavy")]
    public async Task RecoverAsync_ReplayWithNewIfMatch_ReplaysAndClientTimeIsFingerprinted()
    {
        RecoveryFixture fixture = await SeedIndeterminateClaimAsync(senderSettled: true);
        await using AppDbContext db = CreateContext();
        DispatchRecoveryService sut = CreateRecoveryService(db);
        var request = new DispatchRecoveryRequest
        {
            DispatchAttemptId = fixture.AttemptId,
            ClaimRevision = fixture.AttemptRevision,
            PhysicalCheckConfirmed = true,
            ClientReportedAtUtc = Now.AddMinutes(-1).UtcDateTime,
        };

        DispatchRecoveryResult first = await RecoverAsync(sut, fixture, "replay-key", request);
        DispatchRecoveryResult replayWithStaleETag = await sut.RecoverAsync(
            fixture.PrinterId,
            "operator-1",
            fixture.StateRevision + 7,
            RevisionETag.EncodeQuoted(fixture.StateRevision + 7),
            "replay-key",
            request,
            null,
            CancellationToken.None);
        request.ClientReportedAtUtc = Now.UtcDateTime;
        DispatchRecoveryResult reused = await RecoverAsync(sut, fixture, "replay-key", request);

        first.StatusCode.Should().Be(StatusCodes.Status200OK);
        replayWithStaleETag.StatusCode.Should().Be(first.StatusCode);
        replayWithStaleETag.BodyJson.Should().Be(first.BodyJson);
        reused.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        JsonError(reused).Should().Be("idempotency_key_reused");
        (await db.DispatchRecoveryJournalEntries.CountAsync()).Should().Be(1);
    }

    [Theory]
    [InlineData(false, StatusCodes.Status400BadRequest, "physical_check_required", null, null)]
    [InlineData(true, StatusCodes.Status412PreconditionFailed, "rejected_stale", 99L, null)]
    [InlineData(true, StatusCodes.Status412PreconditionFailed, "rejected_stale", null, 99L)]
    public async Task RecoverAsync_InvalidPhysicalCheckOrStaleRevision_ReturnsExpectedError(
        bool physicalCheck,
        int expectedStatus,
        string expectedError,
        long? expectedStateRevision,
        long? claimRevision)
    {
        RecoveryFixture fixture = await SeedIndeterminateClaimAsync(senderSettled: true);
        DispatchRecoveryService sut = CreateRecoveryService(CreateContext());

        DispatchRecoveryResult result = await sut.RecoverAsync(
            fixture.PrinterId,
            "operator-1",
            expectedStateRevision ?? fixture.StateRevision,
            RevisionETag.EncodeQuoted(fixture.StateRevision),
            $"key-{Guid.NewGuid():N}",
            new DispatchRecoveryRequest
            {
                DispatchAttemptId = fixture.AttemptId,
                ClaimRevision = claimRevision ?? fixture.AttemptRevision,
                PhysicalCheckConfirmed = physicalCheck,
            },
            null,
            CancellationToken.None);

        result.StatusCode.Should().Be(expectedStatus);
        JsonError(result).Should().Be(expectedError);
    }

    [Fact]
    [Trait("Category", "DbHeavy")]
    public async Task RecoverAsync_NotIndeterminateClaim_ReturnsConflictAndJournalsDenial()
    {
        RecoveryFixture fixture = await SeedIndeterminateClaimAsync(senderSettled: true);
        await using (AppDbContext mutate = CreateContext())
        {
            QueueDispatchAttempt attempt = await mutate.QueueDispatchAttempts.SingleAsync(a => a.Id == fixture.AttemptId);
            attempt.Outcome = DispatchAttemptOutcome.Accepted;
            attempt.RequiresReconciliation = false;
            await mutate.SaveChangesAsync();
        }

        DispatchRecoveryResult result = await CreateRecoveryService(CreateContext()).RecoverAsync(
            fixture.PrinterId,
            "operator-1",
            fixture.StateRevision,
            RevisionETag.EncodeQuoted(fixture.StateRevision),
            "not-indeterminate",
            new DispatchRecoveryRequest
            {
                DispatchAttemptId = fixture.AttemptId,
                ClaimRevision = fixture.AttemptRevision + 1,
                PhysicalCheckConfirmed = true,
            },
            null,
            CancellationToken.None);

        result.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        JsonError(result).Should().Be(DispatchRecoveryService.TransitionRejectedNotIndeterminate);
        await using AppDbContext verify = CreateContext();
        (await verify.DispatchRecoveryJournalEntries.SingleAsync()).Transition
            .Should().Be(DispatchRecoveryService.TransitionRejectedNotIndeterminate);
    }

    [Theory]
    [InlineData(LiveSenderKind.StartProcessing, "start_command_processing")]
    [InlineData(LiveSenderKind.ControlProcessing, "control_command_processing")]
    public async Task RecoverAsync_LiveSender_ReturnsConflictWithoutRelease(
        LiveSenderKind liveSenderKind,
        string expectedLiveSender)
    {
        RecoveryFixture fixture = await SeedIndeterminateClaimAsync(senderSettled: true, liveSenderKind: liveSenderKind);

        DispatchRecoveryResult result = await CreateRecoveryService(CreateContext()).RecoverAsync(
            fixture.PrinterId,
            "operator-1",
            fixture.StateRevision,
            RevisionETag.EncodeQuoted(fixture.StateRevision),
            $"live-{liveSenderKind}",
            new DispatchRecoveryRequest
            {
                DispatchAttemptId = fixture.AttemptId,
                ClaimRevision = fixture.AttemptRevision,
                PhysicalCheckConfirmed = true,
            },
            null,
            CancellationToken.None);

        result.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        JsonError(result).Should().Be(DispatchRecoveryService.TransitionRejectedSenderLive);
        using JsonDocument body = JsonDocument.Parse(result.BodyJson);
        body.RootElement.GetProperty("liveSender").GetString().Should().Be(expectedLiveSender);
        await using AppDbContext verify = CreateContext();
        (await verify.PrinterDispatchStates.SingleAsync(s => s.PrinterId == fixture.PrinterId))
            .ActiveDispatchAttemptId.Should().Be(fixture.AttemptId);
    }

    [Fact]
    [Trait("Category", "DbHeavy")]
    public async Task RecoverAsync_NoSenderSettlement_RequiresIsolationButAcceptsConfirmedIsolation()
    {
        RecoveryFixture denied = await SeedIndeterminateClaimAsync(senderSettled: false);
        DispatchRecoveryResult denial = await RecoverAsync(
            CreateRecoveryService(CreateContext()),
            denied,
            "isolation-denied",
            new DispatchRecoveryRequest
            {
                DispatchAttemptId = denied.AttemptId,
                ClaimRevision = denied.AttemptRevision,
                PhysicalCheckConfirmed = true,
                SenderIsolationConfirmed = false,
            });

        denial.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        JsonError(denial).Should().Be(DispatchRecoveryService.TransitionRejectedSenderIsolationRequired);

        RecoveryFixture accepted = await SeedIndeterminateClaimAsync(senderSettled: false);
        DispatchRecoveryResult success = await RecoverAsync(
            CreateRecoveryService(CreateContext()),
            accepted,
            "isolation-accepted",
            new DispatchRecoveryRequest
            {
                DispatchAttemptId = accepted.AttemptId,
                ClaimRevision = accepted.AttemptRevision,
                PhysicalCheckConfirmed = true,
                SenderIsolationConfirmed = true,
            });

        success.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    [Fact]
    [Trait("Category", "DbHeavy")]
    public async Task RecoveryBlockedJob_GateAndFilterRejectUntilClearSucceeds()
    {
        RecoveryFixture fixture = await SeedIndeterminateClaimAsync(senderSettled: true);
        DispatchRecoveryResult recovered = await RecoverAsync(
            CreateRecoveryService(CreateContext()),
            fixture,
            "block-clear",
            new DispatchRecoveryRequest
            {
                DispatchAttemptId = fixture.AttemptId,
                ClaimRevision = fixture.AttemptRevision,
                PhysicalCheckConfirmed = true,
            });
        recovered.StatusCode.Should().Be(StatusCodes.Status200OK);

        await using (AppDbContext blockedContext = CreateContext())
        {
            DispatchClaimResult blocked = await CreateClaim(blockedContext).AcquireClaimAsync(new DispatchClaimRequest(
                fixture.JobId,
                fixture.PrinterId,
                "operator-1",
                "Manual",
                null,
                null,
                null));
            blocked.Success.Should().BeFalse();
            blocked.ErrorCode.Should().Be("operator_recovery_required");
            (await blockedContext.PrintJobs.WhereNotOperatorRecoveryBlocked().AnyAsync(j => j.Id == fixture.JobId))
                .Should().BeFalse();
        }

        await using (AppDbContext clearContext = CreateContext())
        {
            DispatchRecoveryService sut = CreateRecoveryService(clearContext);
            PrintJob blockedJob = await clearContext.PrintJobs.SingleAsync(j => j.Id == fixture.JobId);
            (await sut.ClearRecoveryBlockAsync(fixture.JobId, "operator-1", blockedJob.Revision + 1, CancellationToken.None))
                .StatusCode.Should().Be(StatusCodes.Status412PreconditionFailed);
            (await sut.ClearRecoveryBlockAsync(Guid.NewGuid(), "operator-1", 1, CancellationToken.None))
                .StatusCode.Should().Be(StatusCodes.Status404NotFound);
            DispatchRecoveryResult cleared = await sut.ClearRecoveryBlockAsync(
                fixture.JobId,
                "operator-1",
                blockedJob.Revision,
                CancellationToken.None);
            cleared.StatusCode.Should().Be(StatusCodes.Status200OK);
            JsonError(await sut.ClearRecoveryBlockAsync(fixture.JobId, "operator-1", blockedJob.Revision, CancellationToken.None))
                .Should().Be("job_not_recovery_blocked");
        }

        await using AppDbContext verify = CreateContext();
        PrintJob job = await verify.PrintJobs.SingleAsync(j => j.Id == fixture.JobId);
        job.BlockedReasonCode.Should().BeNull();
        (await verify.PrintJobs.WhereNotOperatorRecoveryBlocked().AnyAsync(j => j.Id == fixture.JobId))
            .Should().BeTrue();
        (await verify.QueueDispatchOutbox.AnyAsync(o => o.EventType == DispatchClaimService.EventTypeDispatchRecoveryCleared))
            .Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "DbHeavy")]
    public async Task ScanOnceAsync_MoreClaimsThanPageSize_RaisesMarkersForEveryClaim()
    {
        DateTime claimedAt = Now.AddHours(-2).UtcDateTime;
        for (int i = 0; i < 3; i++)
        {
            _ = await SeedIndeterminateClaimAsync(senderSettled: true, claimedAtUtc: claimedAt.AddMinutes(i));
        }

        DispatchEscalationOptions policy = new()
        {
            PolicyRevision = 1,
            OperationalAfter = TimeSpan.FromMinutes(15),
            CriticalAfter = TimeSpan.FromHours(1),
            HardLimitAfter = TimeSpan.FromHours(24),
        };
        await using ServiceProvider provider = CreateServiceProvider();
        var service = new DispatchEscalationService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(policy),
            _clock,
            NullLogger<DispatchEscalationService>.Instance)
        {
            ScanBatchSize = 1,
        };

        (await service.ScanOnceAsync(CancellationToken.None)).Should().Be(9);
        (await service.ScanOnceAsync(CancellationToken.None)).Should().Be(0);
        await using AppDbContext verify = CreateContext();
        (await verify.DispatchEscalationMarkers.Select(m => m.DispatchAttemptId).Distinct().CountAsync()).Should().Be(3);
    }

    [Fact]
    [Trait("Category", "DbHeavy")]
    public async Task ScanOnceAsync_DueClaim_RaisesMarkersOnceAndPolicyRevisionBumpRaisesAgain()
    {
        RecoveryFixture fixture = await SeedIndeterminateClaimAsync(senderSettled: true, claimedAtUtc: Now.AddHours(-2).UtcDateTime);
        DispatchEscalationOptions policy = new()
        {
            PolicyRevision = 1,
            OperationalAfter = TimeSpan.FromMinutes(15),
            CriticalAfter = TimeSpan.FromHours(1),
            HardLimitAfter = TimeSpan.FromHours(24),
        };
        await using ServiceProvider provider = CreateServiceProvider();
        var service = new DispatchEscalationService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(policy),
            _clock,
            NullLogger<DispatchEscalationService>.Instance);

        (await service.ScanOnceAsync(CancellationToken.None)).Should().Be(3);
        (await service.ScanOnceAsync(CancellationToken.None)).Should().Be(0);
        policy.PolicyRevision = 2;
        (await service.ScanOnceAsync(CancellationToken.None)).Should().Be(3);

        await using AppDbContext verify = CreateContext();
        (await verify.DispatchEscalationMarkers.CountAsync()).Should().Be(6);
        (await verify.QueueDispatchOutbox.CountAsync(o => o.EventType == DispatchClaimService.EventTypeDispatchIndeterminateEscalated))
            .Should().Be(6);
        (await verify.PrinterDispatchStates.SingleAsync(s => s.PrinterId == fixture.PrinterId))
            .ActiveDispatchAttemptId.Should().Be(fixture.AttemptId);
        (await verify.PrintJobs.SingleAsync(j => j.Id == fixture.JobId)).Status.Should().Be(PrintJobStatus.Starting);
        (await verify.QueueDispatchAttempts.SingleAsync(a => a.Id == fixture.AttemptId)).Outcome.Should().Be(DispatchAttemptOutcome.Unknown);
    }

    [Fact]
    [Trait("Category", "DbHeavy")]
    public async Task SaveChanges_DispatchEvidenceMutatedOrDeleted_Throws()
    {
        RecoveryFixture fixture = await SeedIndeterminateClaimAsync(senderSettled: true);
        _ = await RecoverAsync(
            CreateRecoveryService(CreateContext()),
            fixture,
            "immutability",
            new DispatchRecoveryRequest
            {
                DispatchAttemptId = fixture.AttemptId,
                ClaimRevision = fixture.AttemptRevision,
                PhysicalCheckConfirmed = true,
            });
        await using (AppDbContext journalContext = CreateContext())
        {
            DispatchRecoveryJournalEntry journal = await journalContext.DispatchRecoveryJournalEntries.SingleAsync();
            journal.Note = "tamper";
            await journalContext.Invoking(context => context.SaveChangesAsync())
                .Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("Dispatch recovery journal and escalation markers are append-only evidence.");
        }

        RecoveryFixture escalation = await SeedIndeterminateClaimAsync(senderSettled: true, claimedAtUtc: Now.AddHours(-2).UtcDateTime);
        await using ServiceProvider provider = CreateServiceProvider();
        var service = new DispatchEscalationService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new DispatchEscalationOptions()),
            _clock,
            NullLogger<DispatchEscalationService>.Instance);
        (await service.ScanOnceAsync(CancellationToken.None)).Should().BeGreaterThan(0);
        await using AppDbContext markerContext = CreateContext();
        DispatchEscalationMarker marker = await markerContext.DispatchEscalationMarkers.FirstAsync(m => m.DispatchAttemptId == escalation.AttemptId);
        markerContext.DispatchEscalationMarkers.Remove(marker);
        await markerContext.Invoking(context => context.SaveChangesAsync())
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Dispatch recovery journal and escalation markers are append-only evidence.");
    }

    [Fact]
    public void Validate_NonIncreasingThresholds_ReturnsFailure()
    {
        DispatchEscalationOptionsValidator validator = new();

        validator.Validate(null, new DispatchEscalationOptions
        {
            OperationalAfter = TimeSpan.FromMinutes(5),
            CriticalAfter = TimeSpan.FromMinutes(5),
            HardLimitAfter = TimeSpan.FromHours(1),
        }).Failed.Should().BeTrue();
        validator.Validate(null, new DispatchEscalationOptions
        {
            OperationalAfter = TimeSpan.FromMinutes(5),
            CriticalAfter = TimeSpan.FromMinutes(10),
            HardLimitAfter = TimeSpan.FromMinutes(10),
        }).Failed.Should().BeTrue();
    }

    private static Task<DispatchRecoveryResult> RecoverAsync(
        DispatchRecoveryService sut,
        RecoveryFixture fixture,
        string idempotencyKey,
        DispatchRecoveryRequest request) =>
        sut.RecoverAsync(
            fixture.PrinterId,
            "operator-1",
            fixture.StateRevision,
            RevisionETag.EncodeQuoted(fixture.StateRevision),
            idempotencyKey,
            request,
            null,
            CancellationToken.None);

    private async Task<RecoveryFixture> SeedIndeterminateClaimAsync(
        bool senderSettled,
        LiveSenderKind liveSenderKind = LiveSenderKind.None,
        DateTime? claimedAtUtc = null)
    {
        await using AppDbContext db = CreateContext();
        await db.Database.EnsureCreatedAsync();
        await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction = await db.Database.BeginTransactionAsync();
        var sequenceAllocator = new DbOutboxSequenceAllocator();
        DateTime now = _clock.GetUtcNow().UtcDateTime;
        Guid printerId = Guid.NewGuid();
        Guid jobId = Guid.NewGuid();
        Guid attemptId = Guid.NewGuid();
        Guid startOutboxId = Guid.NewGuid();
        Guid controlOutboxId = Guid.NewGuid();
        Guid bedClearRecordId = Guid.NewGuid();
        Guid manufacturerId = Guid.NewGuid();
        Guid modelId = Guid.NewGuid();

        db.Manufacturers.Add(new Manufacturer { Id = manufacturerId, Name = $"Test Manufacturer {manufacturerId:N}" });
        db.PrinterModels.Add(new PrinterModel { Id = modelId, ManufacturerId = manufacturerId, Name = $"Test Model {modelId:N}" });
        db.Printers.Add(new Printer
        {
            Id = printerId,
            Name = "Recovery Printer",
            ServerUrl = $"http://printer-{printerId:N}.local",
            ManufacturerId = manufacturerId,
            ModelId = modelId,
            IsEnabled = true,
            IsAvailable = true,
            BackendPort = 7125,
            ConfigurationRevision = 1,
        });
        db.PrinterDispatchStates.Add(new PrinterDispatchState
        {
            PrinterId = printerId,
            ActiveJobId = jobId,
            ActiveDispatchAttemptId = attemptId,
            PhysicalControlCommandId = attemptId,
            PhysicalControlAttemptId = attemptId,
            PhysicalControlOperation = "start",
            PhysicalControlActorSubject = "operator-1",
            PhysicalControlStartedAtUtc = now,
            PhysicalControlRequiresReconciliation = true,
            QueueRevision = 1,
        });
        db.PrintJobs.Add(new PrintJob
        {
            Id = jobId,
            Name = "Recovery job",
            AssignedPrinterId = printerId,
            Status = PrintJobStatus.Starting,
            QueuePosition = 1,
            Priority = (int)PrintJobPriority.Normal,
            CreatedAt = now,
            UpdatedAt = now,
            QueuedAt = now,
            ActualStartTime = now,
        });
        db.QueueDispatchAttempts.Add(new QueueDispatchAttempt
        {
            Id = attemptId,
            PrintJobId = jobId,
            PrinterId = printerId,
            PrinterConfigRevision = 1,
            AttemptNumber = 1,
            ActorSubject = "operator-1",
            StartPathKind = "Manual",
            ClaimedAtUtc = claimedAtUtc ?? now.AddMinutes(-30),
            Outcome = DispatchAttemptOutcome.Unknown,
            IsRetryable = true,
            RequiresReconciliation = true,
            BackendCallPhase = DispatchBackendCallPhase.AwaitingReconciliation,
            BackendCallStartedAtUtc = now.AddMinutes(-31),
            BackendResponseAtUtc = now.AddMinutes(-30),
            BackendSenderSettledAtUtc = senderSettled ? now.AddMinutes(-30) : null,
            ErrorCode = "backend_outcome_unknown",
            ErrorDetail = "response lost",
            UpdatedAtUtc = now,
        });
        db.QueueDispatchOutbox.Add(new QueueDispatchOutbox
        {
            Id = startOutboxId,
            Sequence = await sequenceAllocator.AllocateAsync(db),
            AggregateType = nameof(PrintJob),
            AggregateId = jobId,
            PrinterId = printerId,
            AttemptId = attemptId,
            EventType = BedClearAcknowledgementService.BackendStartCommandEventType,
            PayloadJson = "{}",
            Status = liveSenderKind == LiveSenderKind.StartProcessing ? QueueOutboxEventStatus.Processing : QueueOutboxEventStatus.Pending,
            FailureCode = liveSenderKind == LiveSenderKind.StartProcessing ? null : "backend_outcome_unknown",
            CreatedAtUtc = now,
        });
        db.QueueDispatchOutbox.Add(new QueueDispatchOutbox
        {
            Id = controlOutboxId,
            Sequence = await sequenceAllocator.AllocateAsync(db),
            AggregateType = nameof(PrintJob),
            AggregateId = jobId,
            PrinterId = printerId,
            AttemptId = attemptId,
            EventType = BackendControlCommandConsumerService.EventType,
            PayloadJson = "{}",
            Status = liveSenderKind == LiveSenderKind.ControlProcessing ? QueueOutboxEventStatus.Processing : QueueOutboxEventStatus.Pending,
            CreatedAtUtc = now,
        });
        db.BedClearCommandRecords.Add(new BedClearCommandRecord
        {
            Id = bedClearRecordId,
            PrinterId = printerId,
            JobId = jobId,
            IdempotencyKey = $"ack-{Guid.NewGuid():N}",
            RequestSha256 = new string('a', 64),
            ActorSubject = "operator-1",
            Status = BedClearCommandStatus.Pending,
            OutboxEventId = startOutboxId,
            DispatchAttemptId = attemptId,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            ExpiresAtUtc = now.AddMinutes(15),
        });

        await db.SaveChangesAsync();
        await transaction.CommitAsync();

        PrinterDispatchState state = await db.PrinterDispatchStates.AsNoTracking().SingleAsync(s => s.PrinterId == printerId);
        QueueDispatchAttempt attempt = await db.QueueDispatchAttempts.AsNoTracking().SingleAsync(a => a.Id == attemptId);
        return new RecoveryFixture(
            printerId,
            jobId,
            attemptId,
            state.Revision,
            attempt.Revision,
            startOutboxId,
            controlOutboxId,
            bedClearRecordId);
    }

    private AppDbContext CreateContext()
    {
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connectionString, sqlite => sqlite.MigrationsAssembly("Farm.Migrations.Sqlite"))
            .Options;
        var ctx = new AppDbContext(options);
        ctx.Database.ExecuteSqlRaw("PRAGMA foreign_keys = OFF;");
        return ctx;
    }

    private ServiceProvider CreateServiceProvider() =>
        new ServiceCollection()
            .AddDbContext<AppDbContext>(builder => builder.UseSqlite(
                _connectionString,
                sqlite => sqlite.MigrationsAssembly("Farm.Migrations.Sqlite")))
            .AddScoped<IDbOutboxSequenceAllocator, DbOutboxSequenceAllocator>()
            .BuildServiceProvider();

    private DispatchRecoveryService CreateRecoveryService(AppDbContext db) =>
        new(
            db,
            new DbOutboxSequenceAllocator(),
            Options.Create(new DispatchEscalationOptions()),
            _clock,
            NullLogger<DispatchRecoveryService>.Instance);

    private static DispatchClaimService CreateClaim(AppDbContext db) =>
        new(
            db,
            DispatchTestDoubles.OnlineIdleReader(Guid.Empty),
            new DbOutboxSequenceAllocator(),
            NullLogger<DispatchClaimService>.Instance,
            DispatchTestDoubles.TelemetryFreshnessPolicy(),
            DispatchTestDoubles.ValidByteIntegrityVerifier());

    private static string JsonError(DispatchRecoveryResult result)
    {
        using JsonDocument body = JsonDocument.Parse(result.BodyJson);
        return body.RootElement.GetProperty("error").GetString()!;
    }

    private sealed record RecoveryFixture(
        Guid PrinterId,
        Guid JobId,
        Guid AttemptId,
        long StateRevision,
        long AttemptRevision,
        Guid StartOutboxId,
        Guid ControlOutboxId,
        Guid BedClearRecordId);

    public enum LiveSenderKind
    {
        None,
        StartProcessing,
        ControlProcessing,
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

/// <summary>Header and authorization guard coverage for dispatch recovery endpoints.</summary>
public sealed class DispatchRecoveryControllerTests
{
    [Fact]
    public async Task RecoverAsync_MissingIfMatch_ReturnsPreconditionRequiredBeforeServiceCall()
    {
        Guid printerId = Guid.NewGuid();
        var recovery = new Mock<IDispatchRecoveryService>(MockBehavior.Strict);
        var authorization = AuthorizedPrinter(printerId, canAccess: true);
        DispatchRecoveryController controller = CreateController(recovery.Object, authorization.Object);
        controller.Request.Headers["Idempotency-Key"] = "recover";

        IActionResult action = await controller.RecoverAsync(
            printerId,
            new DispatchRecoveryRequest(),
            "recover",
            CancellationToken.None);

        ObjectResult result = action.Should().BeOfType<ObjectResult>().Subject;
        result.StatusCode.Should().Be(StatusCodes.Status428PreconditionRequired);
        ErrorCode(result).Should().Be("precondition_required");
        recovery.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RecoverAsync_InvalidIfMatch_ReturnsBadRequestBeforeServiceCall()
    {
        Guid printerId = Guid.NewGuid();
        var recovery = new Mock<IDispatchRecoveryService>(MockBehavior.Strict);
        var authorization = AuthorizedPrinter(printerId, canAccess: true);
        DispatchRecoveryController controller = CreateController(recovery.Object, authorization.Object);
        controller.Request.Headers.IfMatch = "not-base64";
        controller.Request.Headers["Idempotency-Key"] = "recover";

        IActionResult action = await controller.RecoverAsync(
            printerId,
            new DispatchRecoveryRequest(),
            "recover",
            CancellationToken.None);

        BadRequestObjectResult result = action.Should().BeOfType<BadRequestObjectResult>().Subject;
        ErrorCode(result).Should().Be("invalid_if_match");
        recovery.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RecoverAsync_MissingIdempotencyKey_ReturnsPreconditionRequiredBeforeServiceCall()
    {
        Guid printerId = Guid.NewGuid();
        var recovery = new Mock<IDispatchRecoveryService>(MockBehavior.Strict);
        var authorization = AuthorizedPrinter(printerId, canAccess: true);
        DispatchRecoveryController controller = CreateController(recovery.Object, authorization.Object);
        controller.Request.Headers.IfMatch = RevisionETag.EncodeQuoted(1);

        IActionResult action = await controller.RecoverAsync(
            printerId,
            new DispatchRecoveryRequest(),
            null,
            CancellationToken.None);

        ObjectResult result = action.Should().BeOfType<ObjectResult>().Subject;
        result.StatusCode.Should().Be(StatusCodes.Status428PreconditionRequired);
        ErrorCode(result).Should().Be("idempotency_key_required");
        recovery.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RecoverAsync_OutOfScopePrinter_ReturnsNotFoundBeforeHeaderValidation()
    {
        Guid printerId = Guid.NewGuid();
        var recovery = new Mock<IDispatchRecoveryService>(MockBehavior.Strict);
        var authorization = AuthorizedPrinter(printerId, canAccess: false);
        DispatchRecoveryController controller = CreateController(recovery.Object, authorization.Object);

        IActionResult action = await controller.RecoverAsync(
            printerId,
            new DispatchRecoveryRequest(),
            null,
            CancellationToken.None);

        NotFoundObjectResult result = action.Should().BeOfType<NotFoundObjectResult>().Subject;
        ErrorCode(result).Should().Be("printer_not_found");
        recovery.VerifyNoOtherCalls();
    }

    private static Mock<IQueueResourceAuthorizationService> AuthorizedPrinter(Guid printerId, bool canAccess)
    {
        var authorization = new Mock<IQueueResourceAuthorizationService>(MockBehavior.Strict);
        authorization.Setup(service => service.CanAccessPrinterAsync(
                It.IsAny<ClaimsPrincipal>(),
                printerId,
                PrinterGroupAccessLevel.Manage,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(canAccess);
        return authorization;
    }

    private static DispatchRecoveryController CreateController(
        IDispatchRecoveryService recovery,
        IQueueResourceAuthorizationService authorization)
    {
        ClaimsIdentity identity = new(
        [
            new Claim(ClaimTypes.NameIdentifier, "operator-1"),
            new Claim("sub", "operator-1"),
        ], "test");
        return new DispatchRecoveryController(recovery, authorization)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) },
            },
        };
    }

    private static string ErrorCode(ObjectResult result) =>
        result.Value!.GetType().GetProperty("error")!.GetValue(result.Value) as string ?? string.Empty;
}

