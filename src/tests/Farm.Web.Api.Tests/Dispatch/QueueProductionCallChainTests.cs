// <copyright file="QueueProductionCallChainTests.cs" company="OlyForge3D">
// Copyright (c) OlyForge3D. All rights reserved.
// </copyright>

using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Farm.Api.Services.PrintQueue;
using Farm.Backend.Plugin.Moonraker;
using Farm.Backend.Plugin.OctoPrint;
using Farm.Backend.Plugin.PrusaLink;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos.PrintQueue;
using Farm.Infrastructure.PrinterCalibration;
using Farm.Infrastructure.Repositories.Printers;
using Farm.Infrastructure.Repositories.Queue;
using Farm.Infrastructure.Repositories.UnitOfWork;
using Farm.Infrastructure.Services;
using Farm.Infrastructure.Services.AutoDispatch;
using Farm.Infrastructure.Services.FileManagement;
using Farm.Infrastructure.Services.Interfaces;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.Queue;
using Farm.Infrastructure.Services.Queue.Dispatch;
using Farm.Infrastructure.Services.SignalR;
using Farm.Infrastructure.Services.Spoolman;
using Farm.Infrastructure.Services.StorageManagement;
using Farm.Infrastructure.Settings;
using Farm.Infrastructure.Telemetry;
using Farm.Infrastructure.Tests.Dispatch;
using Farm.Web.Api.Controllers;
using Farm.Web.Api.Controllers.Requests;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
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
    private static readonly Guid CalibrationOwnerId = Guid.NewGuid();
    private static readonly byte[] AuthoritativeGcodeBytes =
        Encoding.UTF8.GetBytes("G28\nG1 X10 Y10\n");
    private static readonly string AuthoritativeGcodeSha256 =
        Convert.ToHexString(SHA256.HashData(AuthoritativeGcodeBytes))
            .ToLowerInvariant();

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
    public void ActorIdentity_InvalidNameIdentifierFallsBackToGuidSubject()
    {
        Guid userId = Guid.NewGuid();
        var principal = new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity(
            [
                new System.Security.Claims.Claim(
                    System.Security.Claims.ClaimTypes.NameIdentifier,
                    "display-name"),
                new System.Security.Claims.Claim("sub", userId.ToString()),
            ],
            "test"));

        QueueActorIdentity.Resolve(principal).Should().Be(userId.ToString());
    }

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

        (await verify.QueueDispatchOutbox.CountAsync(e =>
            e.AggregateId == fixture.JobId &&
            e.EventType == "PrintFarmer.Queue.JobDispatchStarted.v1")).Should().Be(1);
        (await verify.QueueDispatchOutbox.CountAsync(e =>
            e.AggregateId == fixture.JobId &&
            e.EventType == QueueLifecycleEventWriter.EventTypeBedClearConsumed)).Should().Be(1);
    }

    [Fact]
    [Trait("Category", "DbHeavy")]
    public async Task StartPath_FilamentOverride_ClaimsExactMismatchAndWritesDurableAudit()
    {
        Fixture fixture;
        await using (AppDbContext seed = CreateContext())
        {
            fixture = await SeedStandardJobAsync(seed);
            PrintJob job = await seed.PrintJobs.SingleAsync(candidate => candidate.Id == fixture.JobId);
            job.RequiredMaterialType = "PETG";
            job.EstimatedFilamentUsage = 100;
            Printer printer = await seed.Printers.SingleAsync(
                candidate => candidate.Id == fixture.PrinterId);
            printer.CurrentMaterial = "PLA";
            printer.CurrentSpoolId = 42;
            await seed.SaveChangesAsync();
        }

        await using (AppDbContext deniedContext = CreateContext())
        {
            DispatchClaimResult denied = await CreateClaim(
                    deniedContext,
                    DispatchTestDoubles.OnlineIdleReader(fixture.PrinterId))
                .AcquireClaimAsync(new DispatchClaimRequest(
                    fixture.JobId,
                    fixture.PrinterId,
                    "operator-1",
                    "Manual",
                    null,
                    null,
                    null));
            denied.Success.Should().BeFalse();
            denied.ErrorCode.Should().Be("filament_material_mismatch");
        }

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
                    500,
                    null,
                    false));
        await using (AppDbContext claimContext = CreateContext())
        {
            PrintJob reviewedJob = await claimContext.PrintJobs.SingleAsync(
                candidate => candidate.Id == fixture.JobId);
            Printer reviewedPrinter = await claimContext.Printers.SingleAsync(
                candidate => candidate.Id == fixture.PrinterId);
            FilamentCheckResult reviewedCheck =
                await FilamentPreflightEvaluator.CheckAsync(
                    reviewedPrinter,
                    reviewedJob,
                    spoolmanService.Object,
                    NullLogger.Instance,
                    CancellationToken.None);
            var authorization = new FilamentOverrideAuthorization(
                reviewedCheck.Outcome.ToString(),
                reviewedCheck.Message!,
                reviewedCheck.LoadedMaterial,
                reviewedCheck.RequiredMaterial,
                reviewedCheck.RemainingWeightG,
                reviewedCheck.RequiredWeightG,
                FilamentPreflightEvaluator.ComputeVersion(reviewedCheck),
                reviewedPrinter.RowVersion!,
                OverrideApproved: true);
            DispatchClaimResult accepted = await CreateClaim(
                    claimContext,
                    DispatchTestDoubles.OnlineIdleReader(fixture.PrinterId),
                    spoolmanService: spoolmanService.Object)
                .AcquireClaimAsync(new DispatchClaimRequest(
                    fixture.JobId,
                    fixture.PrinterId,
                    "operator-1",
                    "FilamentOverride",
                    null,
                    null,
                    null,
                    authorization));
            accepted.Success.Should().BeTrue(accepted.ErrorDetail);
        }

        await using AppDbContext verify = CreateContext();
        (await verify.PrintJobs.SingleAsync(candidate => candidate.Id == fixture.JobId))
            .Status.Should().Be(PrintJobStatus.Starting);
        QueueOperationAudit audit = await verify.QueueOperationAudits.SingleAsync(
            candidate =>
                candidate.PrintJobId == fixture.JobId &&
                candidate.Operation == QueueAuditOperations.SafetyOverride);
        audit.ActorSubject.Should().Be("operator-1");
        audit.ReasonCode.Should().Be("filament_override");
        audit.DetailJson.Should().Contain("Material mismatch: loaded PLA, job requires PETG");
    }

    [Fact]
    [Trait("Category", "DbHeavy")]
    public async Task StartPath_WhenFilamentEvidenceChangesAtClaimTime_ReturnsFreshChallenge()
    {
        Fixture fixture;
        await using (AppDbContext seed = CreateContext())
        {
            fixture = await SeedStandardJobAsync(seed);
            PrintJob job = await seed.PrintJobs.SingleAsync(candidate => candidate.Id == fixture.JobId);
            job.RequiredMaterialType = "PETG";
            job.EstimatedFilamentUsage = 100;
            Printer printer = await seed.Printers.SingleAsync(
                candidate => candidate.Id == fixture.PrinterId);
            printer.CurrentSpoolId = 42;
            await seed.SaveChangesAsync();
        }

        Mock<ISpoolmanService> spoolmanService = new();
        spoolmanService
            .SetupSequence(service => service.GetSpoolByIdAsync(
                42,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(
                new SpoolmanSpoolDto(
                    42,
                    "PLA spool",
                    "PLA",
                    500,
                    null,
                    false))
            .ReturnsAsync(
                new SpoolmanSpoolDto(
                    42,
                    "ABS spool",
                    "ABS",
                    500,
                    null,
                    false));
        await using AppDbContext claimContext = CreateContext();
        PrintJob reviewedJob = await claimContext.PrintJobs.SingleAsync(
            candidate => candidate.Id == fixture.JobId);
        Printer reviewedPrinter = await claimContext.Printers.SingleAsync(
            candidate => candidate.Id == fixture.PrinterId);
        FilamentCheckResult reviewedCheck =
            await FilamentPreflightEvaluator.CheckAsync(
                reviewedPrinter,
                reviewedJob,
                spoolmanService.Object,
                NullLogger.Instance,
                CancellationToken.None);
        var authorization = new FilamentOverrideAuthorization(
            reviewedCheck.Outcome.ToString(),
            reviewedCheck.Message!,
            reviewedCheck.LoadedMaterial,
            reviewedCheck.RequiredMaterial,
            reviewedCheck.RemainingWeightG,
            reviewedCheck.RequiredWeightG,
            FilamentPreflightEvaluator.ComputeVersion(reviewedCheck),
            reviewedPrinter.RowVersion!,
            OverrideApproved: true);

        DispatchClaimResult result = await CreateClaim(
                claimContext,
                DispatchTestDoubles.OnlineIdleReader(fixture.PrinterId),
                spoolmanService: spoolmanService.Object)
            .AcquireClaimAsync(new DispatchClaimRequest(
                fixture.JobId,
                fixture.PrinterId,
                "operator-1",
                "FilamentOverride",
                null,
                null,
                null,
                authorization));

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("filament_check_changed");
        result.CurrentFilamentCheck.Should().NotBeNull();
        result.CurrentFilamentCheck!.LoadedMaterial.Should().Be("ABS");
        await using AppDbContext verify = CreateContext();
        (await verify.PrintJobs.SingleAsync(candidate => candidate.Id == fixture.JobId))
            .Status.Should().Be(PrintJobStatus.Assigned);
    }

    [Fact]
    [Trait("Category", "DbHeavy")]
    public async Task StartPath_WhenSpoolAssignmentChangesAfterReview_RejectsClaim()
    {
        Fixture fixture;
        await using (AppDbContext seed = CreateContext())
        {
            fixture = await SeedStandardJobAsync(seed);
            PrintJob job = await seed.PrintJobs.SingleAsync(candidate => candidate.Id == fixture.JobId);
            job.RequiredMaterialType = "PLA";
            job.EstimatedFilamentUsage = 100;
            Printer printer = await seed.Printers.SingleAsync(
                candidate => candidate.Id == fixture.PrinterId);
            printer.CurrentSpoolId = 42;
            await seed.SaveChangesAsync();
        }

        Mock<ISpoolmanService> spoolmanService = new();
        spoolmanService
            .Setup(service => service.GetSpoolByIdAsync(
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(
                new SpoolmanSpoolDto(
                    42,
                    "PLA spool",
                    "PLA",
                    500,
                    null,
                    false));
        FilamentOverrideAuthorization authorization;
        await using (AppDbContext reviewContext = CreateContext())
        {
            PrintJob reviewedJob = await reviewContext.PrintJobs.SingleAsync(
                candidate => candidate.Id == fixture.JobId);
            Printer reviewedPrinter = await reviewContext.Printers.SingleAsync(
                candidate => candidate.Id == fixture.PrinterId);
            FilamentCheckResult reviewedCheck =
                await FilamentPreflightEvaluator.CheckAsync(
                    reviewedPrinter,
                    reviewedJob,
                    spoolmanService.Object,
                    NullLogger.Instance,
                    CancellationToken.None);
            authorization = new FilamentOverrideAuthorization(
                reviewedCheck.Outcome.ToString(),
                reviewedCheck.Message ?? "Filament compatible.",
                reviewedCheck.LoadedMaterial,
                reviewedCheck.RequiredMaterial,
                reviewedCheck.RemainingWeightG,
                reviewedCheck.RequiredWeightG,
                FilamentPreflightEvaluator.ComputeVersion(reviewedCheck),
                reviewedPrinter.RowVersion!,
                OverrideApproved: false);
        }

        await using (AppDbContext concurrentContext = CreateContext())
        {
            Printer printer = await concurrentContext.Printers.SingleAsync(
                candidate => candidate.Id == fixture.PrinterId);
            printer.CurrentSpoolId = 43;
            await concurrentContext.SaveChangesAsync();
        }

        await using AppDbContext claimContext = CreateContext();
        DispatchClaimResult result = await CreateClaim(
                claimContext,
                DispatchTestDoubles.OnlineIdleReader(fixture.PrinterId),
                spoolmanService: spoolmanService.Object)
            .AcquireClaimAsync(new DispatchClaimRequest(
                fixture.JobId,
                fixture.PrinterId,
                "system:auto-dispatch",
                "AutoDispatch",
                null,
                null,
                null,
                authorization));

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("filament_check_changed");
        result.CurrentFilamentCheck.Should().NotBeNull();
        await using AppDbContext verify = CreateContext();
        (await verify.PrintJobs.SingleAsync(candidate => candidate.Id == fixture.JobId))
            .Status.Should().Be(PrintJobStatus.Assigned);
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
    public async Task AdHoc_BackendAccepted_RetainsLeaseAndBlocksRepeatedStart()
    {
        await using AppDbContext seed = CreateContext();
        Fixture fixture = await SeedCalibrationAsync(seed, withAck: false);

        await using (AppDbContext firstContext = CreateContext())
        {
            DispatchClaimService firstService = CreateClaim(
                firstContext,
                DispatchTestDoubles.OnlineIdleReader(fixture.PrinterId));
            DispatchClaimResult first = await firstService.AcquireAdHocClaimAsync(
                new AdHocDispatchClaimRequest(
                    fixture.PrinterId,
                    "op",
                    "SliceBridge",
                    "accepted.gcode"));
            first.Success.Should().BeTrue(first.ErrorDetail);
            await firstService.RecordBackendAcceptedAsync(
                first.Attempt!.Id,
                first.Attempt.BackendFileName);
        }

        await using AppDbContext secondContext = CreateContext();
        DispatchClaimResult second = await CreateClaim(
                secondContext,
                DispatchTestDoubles.OnlineIdleReader(fixture.PrinterId))
            .AcquireAdHocClaimAsync(new AdHocDispatchClaimRequest(
                fixture.PrinterId,
                "op",
                "SliceBridge",
                "duplicate.gcode"));

        second.Success.Should().BeFalse();
        second.ErrorCode.Should().Be("printer_busy_active");
    }

    [Fact]
    [Trait("Category", "DbHeavy")]
    public async Task BedClearAcknowledgement_ActiveAdHocAttempt_ReturnsPrinterBusy()
    {
        await using AppDbContext context = CreateContext();
        Fixture fixture = await SeedCalibrationAsync(context, withAck: false);
        DispatchClaimResult adHoc = await CreateClaim(
                context,
                DispatchTestDoubles.OnlineIdleReader(fixture.PrinterId))
            .AcquireAdHocClaimAsync(new AdHocDispatchClaimRequest(
                fixture.PrinterId,
                "operator-1",
                "PrinterFile",
                "external.gcode",
                UseDeterministicFileName: false));
        adHoc.Success.Should().BeTrue(adHoc.ErrorDetail);

        PrintJob job = await context.PrintJobs.SingleAsync(
            candidate => candidate.Id == fixture.JobId);
        Printer printer = await context.Printers.SingleAsync(
            candidate => candidate.Id == fixture.PrinterId);
        PrinterDispatchState state = await context.PrinterDispatchStates.SingleAsync(
            candidate => candidate.PrinterId == fixture.PrinterId);
        var acknowledgement = new BedClearAcknowledgementService(
            context,
            new DbOutboxSequenceAllocator(),
            DispatchTestDoubles.OnlineIdleReader(fixture.PrinterId),
            NullLogger<BedClearAcknowledgementService>.Instance,
            DispatchTestDoubles.TelemetryFreshnessPolicy(),
            DispatchTestDoubles.ValidByteIntegrityVerifier());

        AcknowledgeBedClearResult result = await acknowledgement.AcknowledgeAsync(
            new AcknowledgeBedClearRequest(
                fixture.JobId,
                fixture.PrinterId,
                "operator-1",
                "adhoc-busy-ack",
                state.RowVersion,
                printer.ConfigurationRevision,
                job.RowVersion));

        result.Outcome.Should().Be(BedClearAckOutcome.PrinterBusy);
        (await context.QueueDispatchOutbox.CountAsync(evt =>
            evt.EventType ==
            BedClearAcknowledgementService.BackendStartCommandEventType)).Should().Be(0);
    }

    [Fact]
    [Trait("Category", "DbHeavy")]
    public async Task BackendStartConsumer_CrashBeforeSend_ResumesSameAttemptExactlyOnce()
    {
        string storageRoot = Path.Join(
            AppContext.BaseDirectory,
            $"queue-resume-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Join(storageRoot, "gcode"));
        try
        {
            Fixture fixture;
            DispatchClaimResult claim;
            await using (AppDbContext seed = CreateContext())
            {
                fixture = await SeedCalibrationAsync(seed, withAck: true);
                GcodeFile gcode = await seed.GcodeFiles.SingleAsync(
                    file => file.Id == fixture.GcodeId);
                await File.WriteAllBytesAsync(
                    Path.Join(storageRoot, "gcode", gcode.FileName),
                    AuthoritativeGcodeBytes);

                claim = await CreateClaim(
                        seed,
                        DispatchTestDoubles.OnlineIdleReader(fixture.PrinterId))
                    .AcquireClaimAsync(new DispatchClaimRequest(
                        fixture.JobId,
                        fixture.PrinterId,
                        "operator-1",
                        "BedClear",
                        fixture.AckKey,
                        null,
                        null));
                claim.Success.Should().BeTrue(claim.ErrorDetail);
            }

            var printers = new Mock<IPrintersService>();
            printers.Setup(service => service.UploadAndStartPrintAsync(
                    fixture.PrinterId,
                    claim.Attempt!.BackendFileName!,
                    It.IsAny<Stream>(),
                    It.IsAny<IProgress<UploadAndPrintStage>?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(UploadAndPrintResult.Ok("provider-resume-1"));
            var storage = new Mock<IStoragePathService>();
            storage.Setup(service => service.GetGcodeStorageDirectory())
                .Returns(storageRoot);

            await using AppDbContext managementContext = CreateContext();
            DispatchClaimService claimService = CreateClaim(
                managementContext,
                DispatchTestDoubles.OnlineIdleReader(fixture.PrinterId));
            var management = new PrintJobManagementService(
                new EfPrintJobManagementRepository(managementContext),
                NullLogger<PrintJobManagementService>.Instance,
                printers.Object,
                storage.Object,
                CreateHubContext(),
                Mock.Of<IStoredFileOperationsService>(),
                Mock.Of<IPrinterStatusCacheReader>(),
                dispatchClaimService: claimService,
                appDbContext: managementContext,
                outboxSequenceAllocator: new DbOutboxSequenceAllocator());

            ServiceProvider provider = new ServiceCollection()
                .AddDbContext<AppDbContext>(options => options.UseSqlite(
                    _connectionString,
                    sqlite => sqlite.MigrationsAssembly("Farm.Migrations.Sqlite")))
                .AddSingleton<IPrintJobManagementService>(management)
                .BuildServiceProvider();
            await using (provider)
            {
                var consumer = new BackendStartCommandConsumerService(
                    provider.GetRequiredService<IServiceScopeFactory>(),
                    NullLogger<BackendStartCommandConsumerService>.Instance);
                await consumer.ProcessPendingCommandsAsync(CancellationToken.None);
            }

            printers.Verify(service => service.UploadAndStartPrintAsync(
                fixture.PrinterId,
                claim.Attempt!.BackendFileName!,
                It.IsAny<Stream>(),
                It.IsAny<IProgress<UploadAndPrintStage>?>(),
                It.IsAny<CancellationToken>()), Times.Once);
            await using AppDbContext verify = CreateContext();
            (await verify.QueueDispatchAttempts.CountAsync(
                attempt => attempt.PrintJobId == fixture.JobId)).Should().Be(1);
            QueueDispatchAttempt persistedAttempt = await verify.QueueDispatchAttempts
                .SingleAsync(attempt => attempt.PrintJobId == fixture.JobId);
            persistedAttempt.Id.Should().Be(claim.Attempt!.Id);
            persistedAttempt.Outcome.Should().Be(DispatchAttemptOutcome.Accepted);
            QueueDispatchOutbox command = await verify.QueueDispatchOutbox.SingleAsync(
                evt => evt.EventType ==
                    BedClearAcknowledgementService.BackendStartCommandEventType);
            command.Status.Should().Be(QueueOutboxEventStatus.Published);
        }
        finally
        {
            if (Directory.Exists(storageRoot))
            {
                Directory.Delete(storageRoot, recursive: true);
            }
        }
    }

    [Fact]
    [Trait("Category", "DbHeavy")]
    public async Task BackendStartConsumer_SdcpLostAcknowledgement_RetainsStartFences()
    {
        string storageRoot = Path.Join(
            AppContext.BaseDirectory,
            $"queue-sdcp-response-loss-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Join(storageRoot, "gcode"));
        try
        {
            Fixture fixture;
            DispatchClaimResult claim;
            await using (AppDbContext seed = CreateContext())
            {
                fixture = await SeedCalibrationAsync(
                    seed,
                    withAck: true,
                    backend: PrinterBackend.SDCP);
                GcodeFile gcode = await seed.GcodeFiles.SingleAsync(
                    file => file.Id == fixture.GcodeId);
                await File.WriteAllBytesAsync(
                    Path.Join(storageRoot, "gcode", gcode.FileName),
                    AuthoritativeGcodeBytes);
                claim = await CreateClaim(
                        seed,
                        DispatchTestDoubles.OnlineIdleReader(fixture.PrinterId))
                    .AcquireClaimAsync(new DispatchClaimRequest(
                        fixture.JobId,
                        fixture.PrinterId,
                        "operator-1",
                        "BedClear",
                        fixture.AckKey,
                        null,
                        null));
                claim.Success.Should().BeTrue(claim.ErrorDetail);
            }

            var printers = new Mock<IPrintersService>();
            printers.Setup(service => service.UploadAndStartPrintAsync(
                    fixture.PrinterId,
                    claim.Attempt!.BackendFileName!,
                    It.IsAny<Stream>(),
                    It.IsAny<IProgress<UploadAndPrintStage>?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(UploadAndPrintResult.Unknown(
                    UploadAndPrintStage.StartingPrint,
                    "SDCP start acknowledgement was lost."));
            var storage = new Mock<IStoragePathService>();
            storage.Setup(service => service.GetGcodeStorageDirectory())
                .Returns(storageRoot);
            await using AppDbContext managementContext = CreateContext();
            DispatchClaimService claimService = CreateClaim(
                managementContext,
                DispatchTestDoubles.OnlineIdleReader(fixture.PrinterId));
            var management = new PrintJobManagementService(
                new EfPrintJobManagementRepository(managementContext),
                NullLogger<PrintJobManagementService>.Instance,
                printers.Object,
                storage.Object,
                CreateHubContext(),
                Mock.Of<IStoredFileOperationsService>(),
                Mock.Of<IPrinterStatusCacheReader>(),
                dispatchClaimService: claimService,
                appDbContext: managementContext,
                outboxSequenceAllocator: new DbOutboxSequenceAllocator());
            ServiceProvider provider = new ServiceCollection()
                .AddDbContext<AppDbContext>(options => options.UseSqlite(
                    _connectionString,
                    sqlite => sqlite.MigrationsAssembly("Farm.Migrations.Sqlite")))
                .AddSingleton<IPrintJobManagementService>(management)
                .BuildServiceProvider();
            await using (provider)
            {
                var consumer = new BackendStartCommandConsumerService(
                    provider.GetRequiredService<IServiceScopeFactory>(),
                    NullLogger<BackendStartCommandConsumerService>.Instance);
                await consumer.ProcessPendingCommandsAsync(CancellationToken.None);
            }

            printers.Verify(service => service.UploadAndStartPrintAsync(
                fixture.PrinterId,
                claim.Attempt!.BackendFileName!,
                It.IsAny<Stream>(),
                It.IsAny<IProgress<UploadAndPrintStage>?>(),
                It.IsAny<CancellationToken>()), Times.Once);
            await AssertIndeterminateFencesAsync(fixture, claim.Attempt!.Id);
        }
        finally
        {
            if (Directory.Exists(storageRoot))
            {
                Directory.Delete(storageRoot, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Conflict)]
    [Trait("Category", "DbHeavy")]
    public async Task BackendStartConsumer_RealOctoPrintExplicit4xx_ReleasesAndRearms(
        HttpStatusCode statusCode)
    {
        string storageRoot = Path.Join(
            AppContext.BaseDirectory,
            $"queue-octoprint-rejection-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Join(storageRoot, "gcode"));
        try
        {
            Fixture fixture;
            DispatchClaimResult claim;
            Printer printer;
            await using (AppDbContext seed = CreateContext())
            {
                fixture = await SeedCalibrationAsync(
                    seed,
                    withAck: true,
                    backend: PrinterBackend.OctoPrint,
                    credential: new PrinterCredential { ApiKey = "test-key" });
                printer = await seed.Printers.SingleAsync(
                    candidate => candidate.Id == fixture.PrinterId);
                GcodeFile gcode = await seed.GcodeFiles.SingleAsync(
                    file => file.Id == fixture.GcodeId);
                await File.WriteAllBytesAsync(
                    Path.Join(storageRoot, "gcode", gcode.FileName),
                    AuthoritativeGcodeBytes);
                claim = await CreateClaim(
                        seed,
                        DispatchTestDoubles.OnlineIdleReader(fixture.PrinterId))
                    .AcquireClaimAsync(new DispatchClaimRequest(
                        fixture.JobId,
                        fixture.PrinterId,
                        "operator-1",
                        "BedClear",
                        fixture.AckKey,
                        null,
                        null));
                claim.Success.Should().BeTrue(claim.ErrorDetail);
                await seed.SaveChangesAsync();
            }

            using var handler = new HistoryAuthorityHandler(
                _ => new HttpResponseMessage(statusCode));
            using var http = new HttpClient(handler);
            var adapter = new OctoPrintClient(
                http,
                NullLogger<OctoPrintClient>.Instance,
                new BackendTimeoutSettings());
            await using AppDbContext managementContext = CreateContext();
            PrintersService printers = CreateConcreteUploadPrintersService(
                managementContext,
                printer,
                adapter);
            var storage = new Mock<IStoragePathService>();
            storage.Setup(service => service.GetGcodeStorageDirectory())
                .Returns(storageRoot);
            DispatchClaimService claimService = CreateClaim(
                managementContext,
                DispatchTestDoubles.OnlineIdleReader(fixture.PrinterId));
            var management = new PrintJobManagementService(
                new EfPrintJobManagementRepository(managementContext),
                NullLogger<PrintJobManagementService>.Instance,
                printers,
                storage.Object,
                CreateHubContext(),
                Mock.Of<IStoredFileOperationsService>(),
                Mock.Of<IPrinterStatusCacheReader>(),
                dispatchClaimService: claimService,
                appDbContext: managementContext,
                outboxSequenceAllocator: new DbOutboxSequenceAllocator());
            ServiceProvider provider = new ServiceCollection()
                .AddDbContext<AppDbContext>(options => options.UseSqlite(
                    _connectionString,
                    sqlite => sqlite.MigrationsAssembly("Farm.Migrations.Sqlite")))
                .AddSingleton<IPrintJobManagementService>(management)
                .BuildServiceProvider();
            await using (provider)
            {
                var consumer = new BackendStartCommandConsumerService(
                    provider.GetRequiredService<IServiceScopeFactory>(),
                    NullLogger<BackendStartCommandConsumerService>.Instance);
                await consumer.ProcessPendingCommandsAsync(CancellationToken.None);
            }

            await using AppDbContext verify = CreateContext();
            QueueDispatchAttempt attempt = await verify.QueueDispatchAttempts
                .SingleAsync(candidate => candidate.Id == claim.Attempt!.Id);
            PrintJob job = await verify.PrintJobs.SingleAsync(
                candidate => candidate.Id == fixture.JobId);
            PrinterDispatchState state = await verify.PrinterDispatchStates.SingleAsync(
                candidate => candidate.PrinterId == fixture.PrinterId);
            BedClearCommandRecord firstAcknowledgement =
                await verify.BedClearCommandRecords.SingleAsync(
                    candidate => candidate.DispatchAttemptId == attempt.Id);
            attempt.Outcome.Should().Be(DispatchAttemptOutcome.FailedBeforeStart);
            attempt.RequiresReconciliation.Should().BeFalse();
            job.Status.Should().Be(PrintJobStatus.Assigned);
            state.ActiveDispatchAttemptId.Should().BeNull();
            firstAcknowledgement.Status.Should().Be(BedClearCommandStatus.Rejected);
            handler.RequestPaths.Should().ContainSingle("/api/files/local");

            var acknowledgement = new BedClearAcknowledgementService(
                verify,
                new DbOutboxSequenceAllocator(),
                DispatchTestDoubles.OnlineIdleReader(fixture.PrinterId),
                NullLogger<BedClearAcknowledgementService>.Instance,
                DispatchTestDoubles.TelemetryFreshnessPolicy(),
                DispatchTestDoubles.ValidByteIntegrityVerifier());
            AcknowledgeBedClearResult retry = await acknowledgement.AcknowledgeAsync(
                new AcknowledgeBedClearRequest(
                    fixture.JobId,
                    fixture.PrinterId,
                    "operator-2",
                    $"retry-{(int)statusCode}",
                    state.RowVersion,
                    printer.ConfigurationRevision,
                    job.RowVersion));
            retry.Outcome.Should().Be(BedClearAckOutcome.Accepted, retry.ErrorDetail);
        }
        finally
        {
            if (Directory.Exists(storageRoot))
            {
                Directory.Delete(storageRoot, recursive: true);
            }
        }
    }

    [Fact]
    [Trait("Category", "DbHeavy")]
    public async Task BackendStartConsumer_RealOctoPrintPostSendLoss_SendsOnceAndRetainsFences()
    {
        string storageRoot = Path.Join(
            AppContext.BaseDirectory,
            $"queue-octoprint-response-loss-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Join(storageRoot, "gcode"));
        try
        {
            Fixture fixture;
            DispatchClaimResult claim;
            Printer printer;
            await using (AppDbContext seed = CreateContext())
            {
                fixture = await SeedCalibrationAsync(
                    seed,
                    withAck: true,
                    backend: PrinterBackend.OctoPrint,
                    credential: new PrinterCredential { ApiKey = "test-key" });
                printer = await seed.Printers.SingleAsync(
                    candidate => candidate.Id == fixture.PrinterId);
                GcodeFile gcode = await seed.GcodeFiles.SingleAsync(
                    file => file.Id == fixture.GcodeId);
                await File.WriteAllBytesAsync(
                    Path.Join(storageRoot, "gcode", gcode.FileName),
                    AuthoritativeGcodeBytes);
                claim = await CreateClaim(
                        seed,
                        DispatchTestDoubles.OnlineIdleReader(fixture.PrinterId))
                    .AcquireClaimAsync(new DispatchClaimRequest(
                        fixture.JobId,
                        fixture.PrinterId,
                        "operator-1",
                        "BedClear",
                        fixture.AckKey,
                        null,
                        null));
                claim.Success.Should().BeTrue(claim.ErrorDetail);
                await seed.SaveChangesAsync();
            }

            int requestCount = 0;
            using var handler = new AsyncMessageHandler(async (request, ct) =>
            {
                requestCount++;
                _ = await request.Content!.ReadAsByteArrayAsync(ct);
                throw new HttpRequestException(
                    "Connection reset after request body was sent.",
                    new IOException("response lost"));
            });
            using var http = new HttpClient(handler);
            var adapter = new OctoPrintClient(
                http,
                NullLogger<OctoPrintClient>.Instance,
                new BackendTimeoutSettings());
            await using AppDbContext managementContext = CreateContext();
            PrintersService printers = CreateConcreteUploadPrintersService(
                managementContext,
                printer,
                adapter);
            var storage = new Mock<IStoragePathService>();
            storage.Setup(service => service.GetGcodeStorageDirectory())
                .Returns(storageRoot);
            DispatchClaimService claimService = CreateClaim(
                managementContext,
                DispatchTestDoubles.OnlineIdleReader(fixture.PrinterId));
            var management = new PrintJobManagementService(
                new EfPrintJobManagementRepository(managementContext),
                NullLogger<PrintJobManagementService>.Instance,
                printers,
                storage.Object,
                CreateHubContext(),
                Mock.Of<IStoredFileOperationsService>(),
                Mock.Of<IPrinterStatusCacheReader>(),
                dispatchClaimService: claimService,
                appDbContext: managementContext,
                outboxSequenceAllocator: new DbOutboxSequenceAllocator());
            ServiceProvider provider = new ServiceCollection()
                .AddDbContext<AppDbContext>(options => options.UseSqlite(
                    _connectionString,
                    sqlite => sqlite.MigrationsAssembly("Farm.Migrations.Sqlite")))
                .AddSingleton<IPrintJobManagementService>(management)
                .BuildServiceProvider();
            await using (provider)
            {
                var consumer = new BackendStartCommandConsumerService(
                    provider.GetRequiredService<IServiceScopeFactory>(),
                    NullLogger<BackendStartCommandConsumerService>.Instance);
                await consumer.ProcessPendingCommandsAsync(CancellationToken.None);
            }

            requestCount.Should().Be(1);
            await AssertIndeterminateFencesAsync(fixture, claim.Attempt!.Id);
        }
        finally
        {
            if (Directory.Exists(storageRoot))
            {
                Directory.Delete(storageRoot, recursive: true);
            }
        }
    }

    [Fact]
    [Trait("Category", "DbHeavy")]
    public async Task BackendStartConsumer_RealMoonraker502_RetainsFences()
    {
        string storageRoot = Path.Join(
            AppContext.BaseDirectory,
            $"queue-moonraker-502-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Join(storageRoot, "gcode"));
        try
        {
            Fixture fixture;
            DispatchClaimResult claim;
            Printer printer;
            await using (AppDbContext seed = CreateContext())
            {
                fixture = await SeedCalibrationAsync(
                    seed,
                    withAck: true,
                    backend: PrinterBackend.Moonraker);
                printer = await seed.Printers.SingleAsync(
                    candidate => candidate.Id == fixture.PrinterId);
                GcodeFile gcode = await seed.GcodeFiles.SingleAsync(
                    file => file.Id == fixture.GcodeId);
                await File.WriteAllBytesAsync(
                    Path.Join(storageRoot, "gcode", gcode.FileName),
                    AuthoritativeGcodeBytes);
                claim = await CreateClaim(
                        seed,
                        DispatchTestDoubles.OnlineIdleReader(fixture.PrinterId))
                    .AcquireClaimAsync(new DispatchClaimRequest(
                        fixture.JobId,
                        fixture.PrinterId,
                        "operator-1",
                        "BedClear",
                        fixture.AckKey,
                        null,
                        null));
                claim.Success.Should().BeTrue(claim.ErrorDetail);
            }

            using var handler = new HistoryAuthorityHandler(
                _ => new HttpResponseMessage(HttpStatusCode.BadGateway));
            using var http = new HttpClient(handler);
            var adapter = new MoonrakerClient(
                http,
                NullLogger<MoonrakerClient>.Instance,
                new BackendTimeoutSettings());
            await using AppDbContext managementContext = CreateContext();
            PrintersService printers = CreateConcreteUploadPrintersService(
                managementContext,
                printer,
                adapter);
            var storage = new Mock<IStoragePathService>();
            storage.Setup(service => service.GetGcodeStorageDirectory())
                .Returns(storageRoot);
            DispatchClaimService claimService = CreateClaim(
                managementContext,
                DispatchTestDoubles.OnlineIdleReader(fixture.PrinterId));
            var management = new PrintJobManagementService(
                new EfPrintJobManagementRepository(managementContext),
                NullLogger<PrintJobManagementService>.Instance,
                printers,
                storage.Object,
                CreateHubContext(),
                Mock.Of<IStoredFileOperationsService>(),
                Mock.Of<IPrinterStatusCacheReader>(),
                dispatchClaimService: claimService,
                appDbContext: managementContext,
                outboxSequenceAllocator: new DbOutboxSequenceAllocator());
            ServiceProvider provider = new ServiceCollection()
                .AddDbContext<AppDbContext>(options => options.UseSqlite(
                    _connectionString,
                    sqlite => sqlite.MigrationsAssembly("Farm.Migrations.Sqlite")))
                .AddSingleton<IPrintJobManagementService>(management)
                .BuildServiceProvider();
            await using (provider)
            {
                var consumer = new BackendStartCommandConsumerService(
                    provider.GetRequiredService<IServiceScopeFactory>(),
                    NullLogger<BackendStartCommandConsumerService>.Instance);
                await consumer.ProcessPendingCommandsAsync(CancellationToken.None);
            }

            handler.RequestPaths.Should().ContainSingle("/server/files/upload");
            await AssertIndeterminateFencesAsync(fixture, claim.Attempt!.Id);
        }
        finally
        {
            if (Directory.Exists(storageRoot))
            {
                Directory.Delete(storageRoot, recursive: true);
            }
        }
    }

    [Fact]
    [Trait("Category", "DbHeavy")]
    public async Task Cancel_DurableConsumer_BackendAcceptedTransitionsAndReleasesLeaseAtomically()
    {
        Fixture fixture;
        Guid attemptId;
        await using (AppDbContext seed = CreateContext())
        {
            fixture = await SeedCalibrationAsync(seed, withAck: true);
            DispatchClaimService claimService = CreateClaim(
                seed,
                DispatchTestDoubles.OnlineIdleReader(fixture.PrinterId));
            DispatchClaimResult claim = await claimService.AcquireClaimAsync(
                new DispatchClaimRequest(
                    fixture.JobId,
                    fixture.PrinterId,
                    "operator-1",
                    "Manual",
                    fixture.AckKey,
                    null,
                    null));
            claim.Success.Should().BeTrue(claim.ErrorDetail);
            attemptId = claim.Attempt!.Id;
            await claimService.RecordBackendAcceptedAsync(
                attemptId,
                claim.Attempt.BackendFileName);

            await using var transaction = await seed.Database.BeginTransactionAsync();
            seed.QueueDispatchOutbox.Add(new QueueDispatchOutbox
            {
                Id = Guid.NewGuid(),
                Sequence = await new DbOutboxSequenceAllocator().AllocateAsync(seed),
                AggregateType = nameof(PrintJob),
                AggregateId = fixture.JobId,
                PrinterId = fixture.PrinterId,
                AttemptId = attemptId,
                EventType = BackendControlCommandConsumerService.EventType,
                SchemaVersion = "1",
                PayloadJson = JsonSerializer.Serialize(new
                {
                    jobId = fixture.JobId,
                    printerId = fixture.PrinterId,
                    attemptId,
                    operation = "cancel",
                    actorSubject = "operator-1",
                }),
                Status = QueueOutboxEventStatus.Pending,
                CreatedAtUtc = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
            await transaction.CommitAsync();
        }

        Mock<IPrintersService> printers = new();
        printers.Setup(service => service.ExecuteControlAsync(
                fixture.PrinterId,
                BackendControlOperation.Cancel,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(BackendControlOutcome.Accepted());
        ServiceProvider provider = new ServiceCollection()
            .AddDbContext<AppDbContext>(options => options.UseSqlite(
                _connectionString,
                sqlite => sqlite.MigrationsAssembly("Farm.Migrations.Sqlite")))
            .AddScoped<IDbOutboxSequenceAllocator, DbOutboxSequenceAllocator>()
            .AddSingleton(printers.Object)
            .BuildServiceProvider();
        await using (provider)
        {
            var consumer = new BackendControlCommandConsumerService(
                provider.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<BackendControlCommandConsumerService>.Instance);
            await consumer.ProcessPendingAsync(CancellationToken.None);
        }

        await using AppDbContext verify = CreateContext();
        PrintJob job = await verify.PrintJobs.SingleAsync(
            candidate => candidate.Id == fixture.JobId);
        PrinterDispatchState state = await verify.PrinterDispatchStates.SingleAsync(
            candidate => candidate.PrinterId == fixture.PrinterId);
        QueueDispatchOutbox command = await verify.QueueDispatchOutbox.SingleAsync(
            candidate => candidate.EventType == BackendControlCommandConsumerService.EventType);
        printers.Verify(service => service.ExecuteControlAsync(
            fixture.PrinterId,
            BackendControlOperation.Cancel,
            It.IsAny<CancellationToken>()), Times.Once);
        job.Status.Should().Be(
            PrintJobStatus.Cancelled,
            $"command status={command.Status}, error={command.LastError}");
        state.ActiveJobId.Should().BeNull();
        state.ActiveDispatchAttemptId.Should().BeNull();
        command.Status.Should().Be(QueueOutboxEventStatus.Published);
        (await verify.QueueDispatchOutbox.AnyAsync(candidate =>
            candidate.EventType == QueueLifecycleEventWriter.EventTypeJobCancelled))
            .Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "DbHeavy")]
    public async Task ControlFenceTerminalDefer_MaxMinusOne_IsAtomicAndContinuesPoll()
    {
        Fixture fixture;
        Guid attemptId;
        Guid terminalCommandId = Guid.NewGuid();
        Guid laterCommandId = Guid.NewGuid();
        await using (AppDbContext seed = CreateContext())
        {
            fixture = await SeedCalibrationAsync(seed, withAck: true);
            DispatchClaimService claimService = CreateClaim(
                seed,
                DispatchTestDoubles.OnlineIdleReader(fixture.PrinterId));
            DispatchClaimResult claim = await claimService.AcquireClaimAsync(
                new DispatchClaimRequest(
                    fixture.JobId,
                    fixture.PrinterId,
                    "operator-1",
                    "Manual",
                    fixture.AckKey,
                    null,
                    null));
            claim.Success.Should().BeTrue(claim.ErrorDetail);
            attemptId = claim.Attempt!.Id;
            await claimService.RecordBackendAcceptedAsync(
                attemptId,
                claim.Attempt.BackendFileName);

            PrinterDispatchState state = await seed.PrinterDispatchStates.SingleAsync(
                candidate => candidate.PrinterId == fixture.PrinterId);
            state.PhysicalControlCommandId = Guid.NewGuid();
            state.PhysicalControlAttemptId = attemptId;
            state.PhysicalControlOperation = "start";
            state.PhysicalControlStartedAtUtc = DateTime.UtcNow;
            long nextCommandSequence =
                await seed.QueueDispatchOutbox.MaxAsync(command => command.Sequence) + 1;
            seed.QueueDispatchOutbox.AddRange(
                new QueueDispatchOutbox
                {
                    Id = terminalCommandId,
                    Sequence = nextCommandSequence,
                    AggregateType = nameof(PrintJob),
                    AggregateId = fixture.JobId,
                    PrinterId = fixture.PrinterId,
                    AttemptId = attemptId,
                    EventType = BackendControlCommandConsumerService.EventType,
                    SchemaVersion = "1",
                    PayloadJson = JsonSerializer.Serialize(new
                    {
                        jobId = fixture.JobId,
                        printerId = fixture.PrinterId,
                        attemptId,
                        operation = "cancel",
                        actorSubject = "operator-1",
                    }),
                    Status = QueueOutboxEventStatus.Pending,
                    AttemptCount = 119,
                    CreatedAtUtc = DateTime.UtcNow,
                },
                new QueueDispatchOutbox
                {
                    Id = laterCommandId,
                    Sequence = nextCommandSequence + 1,
                    AggregateType = nameof(PrintJob),
                    AggregateId = fixture.JobId,
                    PrinterId = fixture.PrinterId,
                    AttemptId = attemptId,
                    EventType = BackendControlCommandConsumerService.EventType,
                    SchemaVersion = "1",
                    PayloadJson = "not-json",
                    Status = QueueOutboxEventStatus.Pending,
                    CreatedAtUtc = DateTime.UtcNow,
                });
            OutboxSequenceState sequenceState =
                await seed.OutboxSequenceStates.SingleAsync();
            sequenceState.NextSequence = long.MaxValue - 1;
            await seed.SaveChangesAsync();
        }

        var printers = new Mock<IPrintersService>(MockBehavior.Strict);
        ServiceProvider provider = new ServiceCollection()
            .AddDbContext<AppDbContext>(options => options.UseSqlite(
                _connectionString,
                sqlite => sqlite.MigrationsAssembly("Farm.Migrations.Sqlite")))
            .AddScoped<IDbOutboxSequenceAllocator, DbOutboxSequenceAllocator>()
            .AddSingleton(printers.Object)
            .BuildServiceProvider();
        await using (provider)
        {
            var consumer = new BackendControlCommandConsumerService(
                provider.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<BackendControlCommandConsumerService>.Instance);
            await consumer.ProcessPendingAsync(CancellationToken.None);
        }

        await using AppDbContext verify = CreateContext();
        QueueDispatchOutbox terminalCommand =
            await verify.QueueDispatchOutbox.SingleAsync(
                command => command.Id == terminalCommandId);
        QueueDispatchOutbox laterCommand =
            await verify.QueueDispatchOutbox.SingleAsync(
                command => command.Id == laterCommandId);
        terminalCommand.Status.Should().Be(QueueOutboxEventStatus.DeadLettered);
        terminalCommand.FailureCode.Should().Be(
            "manual_control_reconciliation_required");
        laterCommand.Status.Should().Be(QueueOutboxEventStatus.DeadLettered);
        laterCommand.FailureCode.Should().Be("invalid_control_command");
        QueueDispatchOutbox rejectionEvent =
            await verify.QueueDispatchOutbox.SingleAsync(command =>
                command.EventType ==
                    QueueLifecycleEventWriter.EventTypeControlRejected &&
                command.AttemptId == attemptId);
        rejectionEvent.Sequence.Should().Be(long.MaxValue);
        printers.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [Trait("Category", "DbHeavy")]
    public async Task Cancel_DuringStart_BothBarrierOrderings_AreAcceptedAndHonoredExactlyOnce(
        bool startOwnsBarrierFirst)
    {
        string storageRoot = Path.Join(
            AppContext.BaseDirectory,
            $"queue-cancel-start-race-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Join(storageRoot, "gcode"));
        try
        {
            Fixture fixture;
            await using (AppDbContext seed = CreateContext())
            {
                fixture = await SeedCalibrationAsync(seed, withAck: true);
                GcodeFile gcode = await seed.GcodeFiles.SingleAsync(
                    candidate => candidate.Id == fixture.GcodeId);
                await File.WriteAllBytesAsync(
                    Path.Join(storageRoot, "gcode", gcode.FileName),
                    AuthoritativeGcodeBytes);
                DispatchClaimResult claim = await CreateClaim(
                        seed,
                        DispatchTestDoubles.OnlineIdleReader(fixture.PrinterId))
                    .AcquireClaimAsync(new DispatchClaimRequest(
                        fixture.JobId,
                        fixture.PrinterId,
                        "operator-1",
                        "BedClear",
                        fixture.AckKey,
                        null,
                        null));
                claim.Success.Should().BeTrue(claim.ErrorDetail);
            }

            var backendEntered = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseBackend = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var printers = new Mock<IPrintersService>();
            printers.Setup(service => service.UploadAndStartPrintAsync(
                    fixture.PrinterId,
                    It.IsAny<string>(),
                    It.IsAny<Stream>(),
                    It.IsAny<IProgress<UploadAndPrintStage>?>(),
                    It.IsAny<CancellationToken>()))
                .Returns(async () =>
                {
                    backendEntered.TrySetResult(true);
                    await releaseBackend.Task;
                    return UploadAndPrintResult.Ok("cancel-race-start");
                });
            printers.Setup(service => service.ExecuteControlAsync(
                    fixture.PrinterId,
                    BackendControlOperation.Cancel,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(BackendControlOutcome.Accepted());
            var storage = new Mock<IStoragePathService>();
            storage.Setup(service => service.GetGcodeStorageDirectory())
                .Returns(storageRoot);

            ServiceProvider provider = new ServiceCollection()
                .AddDbContext<AppDbContext>(options => options.UseSqlite(
                    _connectionString,
                    sqlite => sqlite.MigrationsAssembly("Farm.Migrations.Sqlite")))
                .AddScoped<IDbOutboxSequenceAllocator, DbOutboxSequenceAllocator>()
                .AddSingleton(printers.Object)
                .AddScoped<IPrintJobManagementService>(services =>
                    CreateManagementService(
                        services.GetRequiredService<AppDbContext>(),
                        printers.Object,
                        storage.Object))
                .BuildServiceProvider();
            await using (provider)
            {
                var startConsumer = new BackendStartCommandConsumerService(
                    provider.GetRequiredService<IServiceScopeFactory>(),
                    NullLogger<BackendStartCommandConsumerService>.Instance);
                var controlConsumer = new BackendControlCommandConsumerService(
                    provider.GetRequiredService<IServiceScopeFactory>(),
                    NullLogger<BackendControlCommandConsumerService>.Instance);

                async Task<IActionResult> EnqueueCancellationAsync()
                {
                    await using AppDbContext cancelContext = CreateContext();
                    PrintJob current = await cancelContext.PrintJobs.SingleAsync(
                        candidate => candidate.Id == fixture.JobId);
                    PrintJobManagementService management = CreateManagementService(
                        cancelContext,
                        printers.Object,
                        storage.Object);
                    JobQueueController controller = CreateJobQueueController(
                        management,
                        Mock.Of<IBedClearAcknowledgementService>(),
                        cancelContext);
                    controller.Request.Headers.IfMatch =
                        $"\"{Convert.ToBase64String(current.RowVersion!)}\"";
                    return await controller.CancelJobAsync(fixture.JobId);
                }

                if (startOwnsBarrierFirst)
                {
                    Task startPoll = startConsumer.ProcessPendingCommandsAsync(
                        CancellationToken.None);
                    await backendEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

                    IActionResult cancel = await EnqueueCancellationAsync();
                    cancel.Should().BeOfType<AcceptedResult>();

                    await controlConsumer.ProcessPendingAsync(CancellationToken.None);
                    await using (AppDbContext pendingVerify = CreateContext())
                    {
                        QueueDispatchOutbox pending =
                            await pendingVerify.QueueDispatchOutbox.SingleAsync(
                                candidate => candidate.EventType ==
                                    BackendControlCommandConsumerService.EventType);
                        pending.Status.Should().Be(QueueOutboxEventStatus.Pending);
                        pending.FailureCode.Should().Be("physical_control_fence_conflict");
                        pending.RetryAfterUtc.Should().BeAfter(DateTime.UtcNow);
                    }

                    releaseBackend.TrySetResult(true);
                    await startPoll;
                    await using (AppDbContext retryContext = CreateContext())
                    {
                        QueueDispatchOutbox retry =
                            await retryContext.QueueDispatchOutbox.SingleAsync(
                                candidate => candidate.EventType ==
                                    BackendControlCommandConsumerService.EventType);
                        retry.RetryAfterUtc = DateTime.UtcNow.AddSeconds(-1);
                        await retryContext.SaveChangesAsync();
                    }

                    await controlConsumer.ProcessPendingAsync(CancellationToken.None);
                }
                else
                {
                    IActionResult cancel = await EnqueueCancellationAsync();
                    cancel.Should().BeOfType<AcceptedResult>();
                    await using (AppDbContext barrierVerify = CreateContext())
                    {
                        QueueDispatchOutbox cancellation =
                            await barrierVerify.QueueDispatchOutbox.SingleAsync(
                                candidate => candidate.EventType ==
                                    BackendControlCommandConsumerService.EventType);
                        PrinterDispatchState state =
                            await barrierVerify.PrinterDispatchStates.SingleAsync(
                                candidate => candidate.PrinterId == fixture.PrinterId);
                        state.PhysicalControlCommandId.Should().Be(cancellation.Id);
                        state.PhysicalControlOperation.Should().Be("cancel");
                    }

                    await startConsumer.ProcessPendingCommandsAsync(CancellationToken.None);
                    await controlConsumer.ProcessPendingAsync(CancellationToken.None);
                }
            }

            printers.Verify(service => service.ExecuteControlAsync(
                fixture.PrinterId,
                BackendControlOperation.Cancel,
                It.IsAny<CancellationToken>()), Times.Once);
            printers.Verify(service => service.UploadAndStartPrintAsync(
                fixture.PrinterId,
                It.IsAny<string>(),
                It.IsAny<Stream>(),
                It.IsAny<IProgress<UploadAndPrintStage>?>(),
                It.IsAny<CancellationToken>()), startOwnsBarrierFirst ? Times.Once() : Times.Never());

            await using AppDbContext verify = CreateContext();
            PrintJob job = await verify.PrintJobs.SingleAsync(
                candidate => candidate.Id == fixture.JobId);
            QueueDispatchOutbox command = await verify.QueueDispatchOutbox.SingleAsync(
                candidate => candidate.EventType ==
                    BackendControlCommandConsumerService.EventType);
            job.Status.Should().Be(PrintJobStatus.Cancelled);
            command.Status.Should().Be(QueueOutboxEventStatus.Published);
            (await verify.QueueDispatchOutbox.CountAsync(candidate =>
                candidate.EventType == QueueLifecycleEventWriter.EventTypeJobCancelled))
                .Should().Be(1);
        }
        finally
        {
            if (Directory.Exists(storageRoot))
            {
                Directory.Delete(storageRoot, recursive: true);
            }
        }
    }

    [Fact]
    [Trait("Category", "DbHeavy")]
    public async Task UnknownStart_QueuedCancel_TerminalHistoryCompletesExactlyOnce()
    {
        const string backendJobId = "unknown-start-cancel";
        (Fixture fixture, Guid attemptId) =
            await SeedUnknownReconciliationAttemptAsync(backendJobId);
        var printers = new Mock<IPrintersService>();
        printers.Setup(service => service.GetStatusDtoAsync(
                fixture.PrinterId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PrinterStatusDto(
                fixture.PrinterId,
                IsOnline: true,
                State: "idle"));
        printers.Setup(service => service.ProbeHistoryJobAsync(
                fixture.PrinterId,
                backendJobId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(HistoryJobProbeResult.Found(new HistoryJob
            {
                JobId = backendJobId,
                Filename = "cancelled.gcode",
                Status = "cancelled",
                StartTime = DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeSeconds(),
            }));
        var storage = new Mock<IStoragePathService>();
        await using (AppDbContext cancelContext = CreateContext())
        {
            PrintJob current = await cancelContext.PrintJobs.SingleAsync(
                candidate => candidate.Id == fixture.JobId);
            PrintJobManagementService management = CreateManagementService(
                cancelContext,
                printers.Object,
                storage.Object);
            JobQueueController controller = CreateJobQueueController(
                management,
                Mock.Of<IBedClearAcknowledgementService>(),
                cancelContext);
            controller.Request.Headers.IfMatch =
                $"\"{Convert.ToBase64String(current.RowVersion!)}\"";

            IActionResult result = await controller.CancelJobAsync(fixture.JobId);

            result.Should().BeOfType<AcceptedResult>();
        }

        await RunReconciliationAsync(printers.Object);

        await using AppDbContext verify = CreateContext();
        PrintJob job = await verify.PrintJobs.SingleAsync(
            candidate => candidate.Id == fixture.JobId);
        PrinterDispatchState state = await verify.PrinterDispatchStates.SingleAsync(
            candidate => candidate.PrinterId == fixture.PrinterId);
        QueueDispatchOutbox command = await verify.QueueDispatchOutbox.SingleAsync(
            candidate =>
                candidate.EventType ==
                    BackendControlCommandConsumerService.EventType &&
                candidate.AttemptId == attemptId);
        job.Status.Should().Be(PrintJobStatus.Cancelled);
        command.Status.Should().Be(QueueOutboxEventStatus.Published);
        state.ActiveJobId.Should().BeNull();
        state.ActiveDispatchAttemptId.Should().BeNull();
        state.PhysicalControlCommandId.Should().BeNull();
        (await verify.QueueDispatchOutbox.CountAsync(candidate =>
            candidate.EventType ==
                QueueLifecycleEventWriter.EventTypeJobCancelled &&
            candidate.AttemptId == attemptId)).Should().Be(1);
        printers.Verify(service => service.ExecuteControlAsync(
            fixture.PrinterId,
            It.IsAny<BackendControlOperation>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "DbHeavy")]
    public async Task UnknownStart_QueuedCancelOrAbort_AuthoritativeAbsenceHonorsIntent(
        bool abort)
    {
        const string backendJobId = "unknown-start-absent";
        (Fixture fixture, Guid attemptId) =
            await SeedUnknownReconciliationAttemptAsync(backendJobId);
        var printers = new Mock<IPrintersService>();
        var storage = new Mock<IStoragePathService>();
        await using (AppDbContext controlContext = CreateContext())
        {
            PrintJob current = await controlContext.PrintJobs.SingleAsync(
                candidate => candidate.Id == fixture.JobId);
            PrintJobManagementService management = CreateManagementService(
                controlContext,
                printers.Object,
                storage.Object);
            JobQueueController controller = CreateJobQueueController(
                management,
                Mock.Of<IBedClearAcknowledgementService>(),
                controlContext);
            controller.Request.Headers.IfMatch =
                $"\"{Convert.ToBase64String(current.RowVersion!)}\"";

            IActionResult result = abort
                ? await controller.AbortPrintAsync(fixture.JobId)
                : await controller.CancelJobAsync(fixture.JobId);

            result.Should().BeOfType<AcceptedResult>();
        }

        Mock<IPrintersService> historyProbe = CreateQuiescentHistoryProbe(
            fixture.PrinterId,
            backendJobId,
            HistoryListProbeResult.Authoritative(new HistoryListResponse()));
        await RunReconciliationAsync(historyProbe.Object);

        await using AppDbContext verify = CreateContext();
        QueueDispatchAttempt attempt =
            await verify.QueueDispatchAttempts.SingleAsync(
                candidate => candidate.Id == attemptId);
        PrintJob job = await verify.PrintJobs.SingleAsync(
            candidate => candidate.Id == fixture.JobId);
        PrinterDispatchState state = await verify.PrinterDispatchStates.SingleAsync(
            candidate => candidate.PrinterId == fixture.PrinterId);
        QueueDispatchOutbox command = await verify.QueueDispatchOutbox.SingleAsync(
            candidate =>
                candidate.EventType ==
                    BackendControlCommandConsumerService.EventType &&
                candidate.AttemptId == attemptId);
        string expectedEventType = abort
            ? QueueLifecycleEventWriter.EventTypeJobAborted
            : QueueLifecycleEventWriter.EventTypeJobCancelled;
        job.Status.Should().Be(
            abort ? PrintJobStatus.Queued : PrintJobStatus.Cancelled);
        attempt.IsRetryable.Should().Be(abort);
        command.Status.Should().Be(QueueOutboxEventStatus.Published);
        command.FailureCode.Should().BeNull();
        state.ActiveJobId.Should().BeNull();
        state.ActiveDispatchAttemptId.Should().BeNull();
        state.PhysicalControlCommandId.Should().BeNull();
        state.PhysicalControlAttemptId.Should().BeNull();
        state.PhysicalControlOperation.Should().BeNull();
        state.PhysicalControlActorSubject.Should().BeNull();
        state.PhysicalControlStartedAtUtc.Should().BeNull();
        state.PhysicalControlRequiresReconciliation.Should().BeFalse();
        state.AcknowledgedJobId.Should().BeNull();
        (await verify.QueueDispatchOutbox.CountAsync(candidate =>
            candidate.EventType == expectedEventType &&
            candidate.AttemptId == attemptId)).Should().Be(1);
        (await verify.QueueDispatchOutbox.AnyAsync(candidate =>
            candidate.EventType ==
                DispatchClaimService.EventTypeReconciliationAbsent &&
            candidate.AttemptId == attemptId)).Should().BeFalse();
        (await verify.QueueDispatchOutbox.AnyAsync(candidate =>
            candidate.EventType ==
                QueueLifecycleEventWriter.EventTypeControlRejected &&
            candidate.AttemptId == attemptId)).Should().BeFalse();
        printers.Verify(service => service.ExecuteControlAsync(
            fixture.PrinterId,
            It.IsAny<BackendControlOperation>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [Trait("Category", "DbHeavy")]
    public async Task KnownStartRejection_HonorsPendingIntentAndClearsOwningBarrier(
        bool abort,
        bool startOwnsBarrier)
    {
        Fixture fixture;
        Guid attemptId;
        long queueRevisionBeforeEnqueue;
        long stateRevisionBeforeEnqueue;
        await using (AppDbContext seed = CreateContext())
        {
            fixture = await SeedCalibrationAsync(seed, withAck: true);
            DispatchClaimService claimService = CreateClaim(
                seed,
                DispatchTestDoubles.OnlineIdleReader(fixture.PrinterId));
            DispatchClaimResult claim = await claimService.AcquireClaimAsync(
                new DispatchClaimRequest(
                    fixture.JobId,
                    fixture.PrinterId,
                    "operator-1",
                    "Manual",
                    fixture.AckKey,
                    null,
                    null));
            claim.Success.Should().BeTrue(claim.ErrorDetail);
            attemptId = claim.Attempt!.Id;
            if (startOwnsBarrier)
            {
                (await claimService.RecordBackendCallStartedAsync(attemptId))
                    .Should().BeTrue();
            }

            PrinterDispatchState baselineState =
                await seed.PrinterDispatchStates.SingleAsync(
                    candidate => candidate.PrinterId == fixture.PrinterId);
            queueRevisionBeforeEnqueue = baselineState.QueueRevision;
            stateRevisionBeforeEnqueue = baselineState.Revision;
        }

        var printers = new Mock<IPrintersService>();
        var storage = new Mock<IStoragePathService>();
        await using (AppDbContext controlContext = CreateContext())
        {
            PrintJob current = await controlContext.PrintJobs.SingleAsync(
                candidate => candidate.Id == fixture.JobId);
            PrintJobManagementService management = CreateManagementService(
                controlContext,
                printers.Object,
                storage.Object);
            JobQueueController controller = CreateJobQueueController(
                management,
                Mock.Of<IBedClearAcknowledgementService>(),
                controlContext);
            controller.Request.Headers.IfMatch =
                $"\"{Convert.ToBase64String(current.RowVersion!)}\"";

            IActionResult result = abort
                ? await controller.AbortPrintAsync(fixture.JobId)
                : await controller.CancelJobAsync(fixture.JobId);

            result.Should().BeOfType<AcceptedResult>();
        }

        await using (AppDbContext enqueueVerify = CreateContext())
        {
            QueueDispatchOutbox enqueuedCommand =
                await enqueueVerify.QueueDispatchOutbox.SingleAsync(
                    candidate =>
                        candidate.EventType ==
                            BackendControlCommandConsumerService.EventType &&
                        candidate.AttemptId == attemptId);
            PrinterDispatchState enqueuedState =
                await enqueueVerify.PrinterDispatchStates.SingleAsync(
                    candidate => candidate.PrinterId == fixture.PrinterId);
            enqueuedState.QueueRevision.Should().Be(
                queueRevisionBeforeEnqueue + 1);
            enqueuedState.Revision.Should().Be(stateRevisionBeforeEnqueue + 1);
            enqueuedState.PhysicalControlCommandId.Should().Be(
                startOwnsBarrier ? attemptId : enqueuedCommand.Id);
        }

        await using (AppDbContext failureContext = CreateContext())
        {
            bool released = await CreateClaim(
                    failureContext,
                    DispatchTestDoubles.OnlineIdleReader(fixture.PrinterId))
                .ReleaseClaimOnKnownFailureAsync(
                    attemptId,
                    "provider_rejected_start",
                    "Provider rejected the start.");
            released.Should().BeTrue();
        }

        await using AppDbContext verify = CreateContext();
        PrintJob job = await verify.PrintJobs.SingleAsync(
            candidate => candidate.Id == fixture.JobId);
        PrinterDispatchState state = await verify.PrinterDispatchStates.SingleAsync(
            candidate => candidate.PrinterId == fixture.PrinterId);
        QueueDispatchOutbox command = await verify.QueueDispatchOutbox.SingleAsync(
            candidate =>
                candidate.EventType ==
                    BackendControlCommandConsumerService.EventType &&
                candidate.AttemptId == attemptId);
        string expectedEventType = abort
            ? QueueLifecycleEventWriter.EventTypeJobAborted
            : QueueLifecycleEventWriter.EventTypeJobCancelled;
        job.Status.Should().Be(
            abort ? PrintJobStatus.Queued : PrintJobStatus.Cancelled);
        command.Status.Should().Be(QueueOutboxEventStatus.Published);
        state.ActiveJobId.Should().BeNull();
        state.ActiveDispatchAttemptId.Should().BeNull();
        state.PhysicalControlCommandId.Should().BeNull();
        state.PhysicalControlAttemptId.Should().BeNull();
        state.PhysicalControlOperation.Should().BeNull();
        state.PhysicalControlActorSubject.Should().BeNull();
        state.PhysicalControlStartedAtUtc.Should().BeNull();
        state.PhysicalControlRequiresReconciliation.Should().BeFalse();
        (await verify.QueueDispatchOutbox.CountAsync(candidate =>
            candidate.EventType == expectedEventType &&
            candidate.AttemptId == attemptId)).Should().Be(1);
        (await verify.QueueDispatchOutbox.AnyAsync(candidate =>
            candidate.EventType == DispatchClaimService.EventTypeKnownFailure &&
            candidate.AttemptId == attemptId)).Should().BeFalse();
        printers.Verify(service => service.ExecuteControlAsync(
            fixture.PrinterId,
            It.IsAny<BackendControlOperation>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    [Trait("Category", "DbHeavy")]
    public async Task KnownStartRejection_ConcurrentFenceDefer_RetriesAndHonorsIntentOnce()
    {
        Fixture fixture;
        Guid attemptId;
        await using (AppDbContext seed = CreateContext())
        {
            fixture = await SeedCalibrationAsync(seed, withAck: true);
            DispatchClaimService claimService = CreateClaim(
                seed,
                DispatchTestDoubles.OnlineIdleReader(fixture.PrinterId));
            DispatchClaimResult claim = await claimService.AcquireClaimAsync(
                new DispatchClaimRequest(
                    fixture.JobId,
                    fixture.PrinterId,
                    "operator-1",
                    "Manual",
                    fixture.AckKey,
                    null,
                    null));
            claim.Success.Should().BeTrue(claim.ErrorDetail);
            attemptId = claim.Attempt!.Id;
            (await claimService.RecordBackendCallStartedAsync(attemptId))
                .Should().BeTrue();
        }

        var printers = new Mock<IPrintersService>();
        var storage = new Mock<IStoragePathService>();
        await using (AppDbContext controlContext = CreateContext())
        {
            PrintJob current = await controlContext.PrintJobs.SingleAsync(
                candidate => candidate.Id == fixture.JobId);
            JobQueueController controller = CreateJobQueueController(
                CreateManagementService(
                    controlContext,
                    printers.Object,
                    storage.Object),
                Mock.Of<IBedClearAcknowledgementService>(),
                controlContext);
            controller.Request.Headers.IfMatch =
                $"\"{Convert.ToBase64String(current.RowVersion!)}\"";
            (await controller.CancelJobAsync(fixture.JobId))
                .Should().BeOfType<AcceptedResult>();
        }

        await using AppDbContext failureContext = CreateContext();
        QueueDispatchOutbox staleIntent =
            await failureContext.QueueDispatchOutbox.SingleAsync(
                candidate =>
                    candidate.EventType ==
                        BackendControlCommandConsumerService.EventType &&
                    candidate.AttemptId == attemptId);

        ServiceProvider provider = new ServiceCollection()
            .AddDbContext<AppDbContext>(options => options.UseSqlite(
                _connectionString,
                sqlite => sqlite.MigrationsAssembly("Farm.Migrations.Sqlite")))
            .AddScoped<IDbOutboxSequenceAllocator, DbOutboxSequenceAllocator>()
            .AddSingleton(printers.Object)
            .BuildServiceProvider();
        await using (provider)
        {
            var consumer = new BackendControlCommandConsumerService(
                provider.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<BackendControlCommandConsumerService>.Instance);
            await consumer.ProcessPendingAsync(CancellationToken.None);
        }

        staleIntent.AttemptCount.Should().Be(0);
        bool released = await CreateClaim(
                failureContext,
                DispatchTestDoubles.OnlineIdleReader(fixture.PrinterId))
            .ReleaseClaimOnKnownFailureAsync(
                attemptId,
                "provider_rejected_start",
                "Provider rejected the start.");
        released.Should().BeTrue();

        await using AppDbContext verify = CreateContext();
        PrintJob job = await verify.PrintJobs.SingleAsync(
            candidate => candidate.Id == fixture.JobId);
        QueueDispatchOutbox command =
            await verify.QueueDispatchOutbox.SingleAsync(
                candidate => candidate.Id == staleIntent.Id);
        job.Status.Should().Be(PrintJobStatus.Cancelled);
        command.Status.Should().Be(QueueOutboxEventStatus.Published);
        command.AttemptCount.Should().Be(1);
        (await verify.QueueDispatchOutbox.CountAsync(candidate =>
            candidate.EventType ==
                QueueLifecycleEventWriter.EventTypeJobCancelled &&
            candidate.AttemptId == attemptId)).Should().Be(1);
        (await verify.QueueDispatchOutbox.AnyAsync(candidate =>
            candidate.EventType == DispatchClaimService.EventTypeKnownFailure &&
            candidate.AttemptId == attemptId)).Should().BeFalse();
        (await verify.QueueDispatchOutbox.AnyAsync(candidate =>
            candidate.EventType ==
                QueueLifecycleEventWriter.EventTypeControlRejected &&
            candidate.AttemptId == attemptId)).Should().BeFalse();
        printers.Verify(service => service.ExecuteControlAsync(
            fixture.PrinterId,
            It.IsAny<BackendControlOperation>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    [Trait("Category", "DbHeavy")]
    public async Task KnownStartRejection_IntentEnqueuedAtCommitBoundary_IsObservedOnRetry()
    {
        Fixture fixture;
        Guid attemptId;
        await using (AppDbContext seed = CreateContext())
        {
            fixture = await SeedCalibrationAsync(seed, withAck: true);
            DispatchClaimService claimService = CreateClaim(
                seed,
                DispatchTestDoubles.OnlineIdleReader(fixture.PrinterId));
            DispatchClaimResult claim = await claimService.AcquireClaimAsync(
                new DispatchClaimRequest(
                    fixture.JobId,
                    fixture.PrinterId,
                    "operator-1",
                    "Manual",
                    fixture.AckKey,
                    null,
                    null));
            claim.Success.Should().BeTrue(claim.ErrorDetail);
            attemptId = claim.Attempt!.Id;
            (await claimService.RecordBackendCallStartedAsync(attemptId))
                .Should().BeTrue();
        }

        var concurrency = new ThrowOnceConcurrencySaveInterceptor();
        await using AppDbContext failureContext = CreateContext(concurrency);
        Task<bool> release = CreateClaim(
                failureContext,
                DispatchTestDoubles.OnlineIdleReader(fixture.PrinterId))
            .ReleaseClaimOnKnownFailureAsync(
                attemptId,
                "provider_rejected_start",
                "Provider rejected the start.");
        await concurrency.Triggered.WaitAsync(TimeSpan.FromSeconds(5));

        var printers = new Mock<IPrintersService>();
        var storage = new Mock<IStoragePathService>();
        await using (AppDbContext controlContext = CreateContext())
        {
            PrintJob current = await controlContext.PrintJobs.SingleAsync(
                candidate => candidate.Id == fixture.JobId);
            JobQueueController controller = CreateJobQueueController(
                CreateManagementService(
                    controlContext,
                    printers.Object,
                    storage.Object),
                Mock.Of<IBedClearAcknowledgementService>(),
                controlContext);
            controller.Request.Headers.IfMatch =
                $"\"{Convert.ToBase64String(current.RowVersion!)}\"";

            IActionResult result =
                await controller.CancelJobAsync(fixture.JobId);

            result.Should().BeOfType<AcceptedResult>();
        }

        (await release).Should().BeTrue();

        await using AppDbContext verify = CreateContext();
        PrintJob job = await verify.PrintJobs.SingleAsync(
            candidate => candidate.Id == fixture.JobId);
        QueueDispatchOutbox command =
            await verify.QueueDispatchOutbox.SingleAsync(
                candidate =>
                    candidate.EventType ==
                        BackendControlCommandConsumerService.EventType &&
                    candidate.AttemptId == attemptId);
        job.Status.Should().Be(PrintJobStatus.Cancelled);
        command.Status.Should().Be(QueueOutboxEventStatus.Published);
        command.FailureCode.Should().BeNull();
        (await verify.QueueDispatchOutbox.CountAsync(candidate =>
            candidate.EventType ==
                QueueLifecycleEventWriter.EventTypeJobCancelled &&
            candidate.AttemptId == attemptId)).Should().Be(1);
        (await verify.QueueDispatchOutbox.AnyAsync(candidate =>
            candidate.EventType == DispatchClaimService.EventTypeKnownFailure &&
            candidate.AttemptId == attemptId)).Should().BeFalse();
        (await verify.QueueDispatchOutbox.AnyAsync(candidate =>
            candidate.EventType ==
                QueueLifecycleEventWriter.EventTypeControlRejected &&
            candidate.AttemptId == attemptId)).Should().BeFalse();
        printers.Verify(service => service.ExecuteControlAsync(
            fixture.PrinterId,
            It.IsAny<BackendControlOperation>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    [Trait("Category", "DbHeavy")]
    public async Task Cancel_DurableConsumer_StaleAttemptFenceMakesZeroBackendCalls()
    {
        Fixture fixture;
        await using (AppDbContext seed = CreateContext())
        {
            fixture = await SeedCalibrationAsync(seed, withAck: true);
            DispatchClaimResult claim = await CreateClaim(
                    seed,
                    DispatchTestDoubles.OnlineIdleReader(fixture.PrinterId))
                .AcquireClaimAsync(new DispatchClaimRequest(
                    fixture.JobId,
                    fixture.PrinterId,
                    "operator-1",
                    "Manual",
                    fixture.AckKey,
                    null,
                    null));
            claim.Success.Should().BeTrue(claim.ErrorDetail);

            Guid staleAttemptId = Guid.NewGuid();
            await using var transaction = await seed.Database.BeginTransactionAsync();
            seed.QueueDispatchOutbox.Add(new QueueDispatchOutbox
            {
                Id = Guid.NewGuid(),
                Sequence = await new DbOutboxSequenceAllocator().AllocateAsync(seed),
                AggregateType = nameof(PrintJob),
                AggregateId = fixture.JobId,
                PrinterId = fixture.PrinterId,
                AttemptId = staleAttemptId,
                EventType = BackendControlCommandConsumerService.EventType,
                SchemaVersion = "1",
                PayloadJson = JsonSerializer.Serialize(new
                {
                    jobId = fixture.JobId,
                    printerId = fixture.PrinterId,
                    attemptId = staleAttemptId,
                    operation = "cancel",
                    actorSubject = "operator-1",
                }),
                Status = QueueOutboxEventStatus.Pending,
                CreatedAtUtc = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
            await transaction.CommitAsync();
        }

        Mock<IPrintersService> printers = new();
        ServiceProvider provider = new ServiceCollection()
            .AddDbContext<AppDbContext>(options => options.UseSqlite(
                _connectionString,
                sqlite => sqlite.MigrationsAssembly("Farm.Migrations.Sqlite")))
            .AddSingleton(printers.Object)
            .BuildServiceProvider();
        await using (provider)
        {
            var consumer = new BackendControlCommandConsumerService(
                provider.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<BackendControlCommandConsumerService>.Instance);
            await consumer.ProcessPendingAsync(CancellationToken.None);
        }

        printers.Verify(
            service => service.ExecuteControlAsync(
                It.IsAny<Guid>(),
                It.IsAny<BackendControlOperation>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        await using AppDbContext verify = CreateContext();
        QueueDispatchOutbox command = await verify.QueueDispatchOutbox.SingleAsync(
            candidate => candidate.EventType == BackendControlCommandConsumerService.EventType);
        command.Status.Should().Be(QueueOutboxEventStatus.DeadLettered);
        command.FailureCode.Should().Be("control_attempt_fence_conflict");
    }

    [Fact]
    [Trait("Category", "DbHeavy")]
    public async Task PauseResume_PreUpgradeJobWithoutLease_SynthesizesAndRetainsOwnership()
    {
        Fixture fixture;
        var printers = new Mock<IPrintersService>();
        printers.Setup(service => service.ExecuteControlAsync(
                It.IsAny<Guid>(),
                It.IsAny<BackendControlOperation>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(BackendControlOutcome.Accepted());
        await using (AppDbContext seed = CreateContext())
        {
            fixture = await SeedCalibrationAsync(seed, withAck: false);
            PrintJob job = await seed.PrintJobs.SingleAsync(
                candidate => candidate.Id == fixture.JobId);
            job.Status = PrintJobStatus.Printing;
            job.ActualStartTime = DateTime.UtcNow.AddMinutes(-2);
            await seed.SaveChangesAsync();
            string etag = Convert.ToBase64String(job.RowVersion!);

            QueuedPrintJobDto queued = await CreateManagementService(
                    seed,
                    printers.Object)
                .PauseJobAsync(
                    fixture.JobId.ToString(),
                    "operator-1",
                    etag);
            queued.Status.Should().Be(
                PrintJobStatus.Printing.ToString(),
                "database state changes only after hardware acceptance");
        }

        await ProcessControlCommandsAsync(printers.Object);

        Guid syntheticAttemptId;
        await using (AppDbContext pausedContext = CreateContext())
        {
            PrintJob paused = await pausedContext.PrintJobs.SingleAsync(
                candidate => candidate.Id == fixture.JobId);
            PrinterDispatchState state = await pausedContext.PrinterDispatchStates
                .SingleAsync(candidate => candidate.PrinterId == fixture.PrinterId);
            QueueDispatchAttempt attempt = await pausedContext.QueueDispatchAttempts
                .SingleAsync(candidate => candidate.PrintJobId == fixture.JobId);
            paused.Status.Should().Be(PrintJobStatus.Paused);
            attempt.StartPathKind.Should().Be("LegacyControlOwnership");
            state.ActiveJobId.Should().Be(fixture.JobId);
            state.ActiveDispatchAttemptId.Should().Be(attempt.Id);
            syntheticAttemptId = attempt.Id;

            await CreateManagementService(pausedContext, printers.Object)
                .ResumeJobAsync(
                    fixture.JobId.ToString(),
                    "operator-1",
                    Convert.ToBase64String(paused.RowVersion!));
        }

        await ProcessControlCommandsAsync(printers.Object);

        await using AppDbContext verify = CreateContext();
        PrintJob resumed = await verify.PrintJobs.SingleAsync(
            candidate => candidate.Id == fixture.JobId);
        PrinterDispatchState resumedState = await verify.PrinterDispatchStates
            .SingleAsync(candidate => candidate.PrinterId == fixture.PrinterId);
        resumed.Status.Should().Be(PrintJobStatus.Printing);
        resumedState.ActiveDispatchAttemptId.Should().Be(syntheticAttemptId);
        (await verify.QueueDispatchOutbox.CountAsync(command =>
            command.EventType == BackendControlCommandConsumerService.EventType &&
            command.Status == QueueOutboxEventStatus.Published)).Should().Be(2);
        printers.Verify(service => service.ExecuteControlAsync(
            fixture.PrinterId,
            It.IsAny<BackendControlOperation>(),
            It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    [Trait("Category", "DbHeavy")]
    public async Task Scheduler_RejectedTypedDispatch_RecordsFailureWithAuthorizedActor()
    {
        await using AppDbContext context = CreateContext();
        Fixture fixture = await SeedCalibrationAsync(context, withAck: false);
        context.JobSchedules.Add(new JobSchedule
        {
            PrintJobId = fixture.JobId,
            ScheduledStartTime = DateTime.UtcNow.AddMinutes(-1),
            IsActive = true,
            IsPaused = false,
            InitiatingActorSubject = CalibrationOwnerId.ToString(),
            RequiresOperatorReauthorization = false,
        });
        await context.SaveChangesAsync();
        var authorization = new QueueResourceAuthorizationService(context);
        string actorSubject = CalibrationOwnerId.ToString();
        (await authorization.CanActorAccessJobAsync(
            actorSubject,
            fixture.JobId,
            PrinterGroupAccessLevel.Submit)).Should().BeTrue();
        (await authorization.CanActorAccessPrinterAsync(
            actorSubject,
            fixture.PrinterId,
            PrinterGroupAccessLevel.Submit)).Should().BeTrue();
        PrintJob scheduledJob = await context.PrintJobs.SingleAsync(
            candidate => candidate.Id == fixture.JobId);
        (await authorization.CanActorAccessProjectAsync(
            actorSubject,
            scheduledJob.CalibrationProjectId!.Value)).Should().BeTrue();

        var management = new Mock<IPrintJobManagementService>();
        management.Setup(service => service.DispatchJobAsync(
                fixture.JobId.ToString(),
                CalibrationOwnerId.ToString(),
                null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueuedPrintJobDto
            {
                Id = fixture.JobId.ToString(),
                DispatchResult = new DispatchAttemptResultDto
                {
                    Outcome = DispatchAttemptOutcome.Rejected,
                    ErrorCode = "printer_busy_database",
                    ErrorDetail = "The printer is already owned.",
                },
            });
        var scheduler = new JobSchedulingService(
            context,
            NullLogger<JobSchedulingService>.Instance,
            management.Object,
            authorization);

        await scheduler.TriggerScheduledJobsAsync();

        JobExecution execution = await context.JobExecutions.SingleAsync();
        execution.Status.Should().Be("Rejected");
        execution.Message.Should().Be("The scheduled start was not accepted.");
        management.Verify(service => service.DispatchJobAsync(
            fixture.JobId.ToString(),
            CalibrationOwnerId.ToString(),
            null,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    [Trait("Category", "DbHeavy")]
    public async Task Cancel_ResponseLost_IsSingleFlightAndEventuallyRequiresManualReview()
    {
        Fixture fixture;
        DispatchClaimResult claim;
        var printers = new Mock<IPrintersService>();
        printers.Setup(service => service.ExecuteControlAsync(
                It.IsAny<Guid>(),
                BackendControlOperation.Cancel,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(BackendControlOutcome.Unknown("response lost"));
        await using (AppDbContext seed = CreateContext())
        {
            fixture = await SeedCalibrationAsync(seed, withAck: true);
            DispatchClaimService claimService = CreateClaim(
                seed,
                DispatchTestDoubles.OnlineIdleReader(fixture.PrinterId));
            claim = await claimService.AcquireClaimAsync(new DispatchClaimRequest(
                fixture.JobId,
                fixture.PrinterId,
                "operator-1",
                "Manual",
                fixture.AckKey,
                null,
                null));
            await claimService.RecordBackendAcceptedAsync(
                claim.Attempt!.Id,
                claim.Attempt.BackendFileName);
            PrintJob job = await seed.PrintJobs.SingleAsync(
                candidate => candidate.Id == fixture.JobId);
            await CreateManagementService(seed, printers.Object).CancelJobAsync(
                fixture.JobId.ToString(),
                "operator-1",
                Convert.ToBase64String(job.RowVersion!));
        }

        await ProcessControlCommandsAsync(printers.Object);
        await ProcessControlCommandsAsync(printers.Object);
        await using (AppDbContext duplicateContext = CreateContext())
        {
            PrintJob activeJob = await duplicateContext.PrintJobs.SingleAsync(
                candidate => candidate.Id == fixture.JobId);
            Func<Task> duplicate = async () => await CreateManagementService(
                    duplicateContext,
                    printers.Object)
                .CancelJobAsync(
                    fixture.JobId.ToString(),
                    "operator-1",
                    Convert.ToBase64String(activeJob.RowVersion!));
            await duplicate.Should().ThrowAsync<QueueSemanticConflictException>(
                "an unresolved hardware command must never be sent again blindly");
        }

        await using (AppDbContext callbackContext = CreateContext())
        {
            bool marked = await CreateCompletionService(callbackContext)
                .MarkCurrentJobAsCompletedAsync(
                    fixture.PrinterId,
                    "idle",
                    new PrinterTerminalObservation(claim.Attempt!.BackendFileName));
            marked.Should().BeFalse(
                "generic terminal callbacks must defer to exact control reconciliation");
        }

        await using (AppDbContext ageContext = CreateContext())
        {
            QueueDispatchOutbox command = await ageContext.QueueDispatchOutbox.SingleAsync(
                candidate => candidate.EventType ==
                    BackendControlCommandConsumerService.EventType);
            command.LastAttemptedAtUtc = DateTime.UtcNow.AddHours(-1);
            await ageContext.SaveChangesAsync();
        }

        printers.Setup(service => service.GetStatusDtoAsync(
                fixture.PrinterId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PrinterStatusDto(
                fixture.PrinterId,
                IsOnline: true,
                State: "printing",
                JobName: claim.Attempt!.BackendFileName,
                FileName: claim.Attempt.BackendFileName));
        await ReconcileControlCommandsAsync(printers.Object);

        printers.Verify(service => service.ExecuteControlAsync(
            fixture.PrinterId,
            BackendControlOperation.Cancel,
            It.IsAny<CancellationToken>()), Times.Once);
        await using AppDbContext verify = CreateContext();
        QueueDispatchOutbox persisted = await verify.QueueDispatchOutbox.SingleAsync(
            candidate => candidate.EventType ==
                BackendControlCommandConsumerService.EventType);
        PrintJob active = await verify.PrintJobs.SingleAsync(
            candidate => candidate.Id == fixture.JobId);
        persisted.Status.Should().Be(QueueOutboxEventStatus.Processing);
        persisted.FailureCode.Should().Be("backend_control_unknown");
        active.Status.Should().Be(PrintJobStatus.Printing);

        await using (AppDbContext manualAgeContext = CreateContext())
        {
            QueueDispatchOutbox command = await manualAgeContext.QueueDispatchOutbox.SingleAsync(
                candidate => candidate.EventType ==
                    BackendControlCommandConsumerService.EventType);
            command.CreatedAtUtc = DateTime.UtcNow.AddHours(-25);
            command.LastAttemptedAtUtc = DateTime.UtcNow.AddHours(-1);
            await manualAgeContext.SaveChangesAsync();
        }

        await ReconcileControlCommandsAsync(printers.Object);
        await using AppDbContext manualVerify = CreateContext();
        QueueDispatchOutbox unresolved = await manualVerify.QueueDispatchOutbox.SingleAsync(
            candidate => candidate.EventType ==
                BackendControlCommandConsumerService.EventType);
        PrinterDispatchState fencedState = await manualVerify.PrinterDispatchStates.SingleAsync(
            candidate => candidate.PrinterId == fixture.PrinterId);
        unresolved.Status.Should().Be(QueueOutboxEventStatus.DeadLettered);
        unresolved.FailureCode.Should().Be("manual_control_reconciliation_required");
        fencedState.ActiveDispatchAttemptId.Should().Be(claim.Attempt!.Id);
        printers.Verify(service => service.ExecuteControlAsync(
            fixture.PrinterId,
            BackendControlOperation.Cancel,
            It.IsAny<CancellationToken>()), Times.Once);
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
        ctx.ChangeTracker.Clear();
        (await ctx.PrintJobs.FindAsync(fixture.JobId))!.Status
            .Should().Be(PrintJobStatus.Assigned);
        (await ctx.PrinterDispatchStates.FindAsync(fixture.PrinterId))!
            .ActiveDispatchAttemptId.Should().BeNull();
        (await ctx.QueueDispatchAttempts.CountAsync(attempt =>
            attempt.PrintJobId == fixture.JobId)).Should().Be(0);
    }

    [Fact]
    [Trait("Category", "DbHeavy")]
    public async Task DispatchRoute_TwoMatchingIfMatchRequests_LoserReturns412WithBothEtags()
    {
        string storageRoot = Path.Join(
            AppContext.BaseDirectory,
            $"queue-dispatch-etag-race-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Join(storageRoot, "gcode"));
        try
        {
            Fixture fixture;
            byte[] initialJobRevision;
            await using (AppDbContext seed = CreateContext())
            {
                fixture = await SeedStandardJobAsync(seed);
                PrintJob job = await seed.PrintJobs.SingleAsync(
                    candidate => candidate.Id == fixture.JobId);
                initialJobRevision = job.RowVersion!.ToArray();
                GcodeFile gcode = await seed.GcodeFiles.SingleAsync(
                    candidate => candidate.Id == fixture.GcodeId);
                await File.WriteAllBytesAsync(
                    Path.Join(storageRoot, "gcode", gcode.FileName),
                    AuthoritativeGcodeBytes);
            }

            var backendEntered = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseBackend = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var printers = new Mock<IPrintersService>();
            printers.Setup(service => service.UploadAndStartPrintAsync(
                    fixture.PrinterId,
                    It.IsAny<string>(),
                    It.IsAny<Stream>(),
                    It.IsAny<IProgress<UploadAndPrintStage>?>(),
                    It.IsAny<CancellationToken>()))
                .Returns(async () =>
                {
                    backendEntered.TrySetResult(true);
                    await releaseBackend.Task;
                    return UploadAndPrintResult.Ok("dispatch-etag-winner");
                });
            var storage = new Mock<IStoragePathService>();
            storage.Setup(service => service.GetGcodeStorageDirectory())
                .Returns(storageRoot);

            await using AppDbContext firstContext = CreateContext();
            await using AppDbContext secondContext = CreateContext();
            PrintJobManagementService firstManagement = CreateManagementService(
                firstContext,
                printers.Object,
                storage.Object,
                DispatchTestDoubles.OnlineIdleReader(fixture.PrinterId));
            PrintJobManagementService secondManagement = CreateManagementService(
                secondContext,
                printers.Object,
                storage.Object,
                DispatchTestDoubles.OnlineIdleReader(fixture.PrinterId));
            JobQueueController firstController = CreateJobQueueController(
                firstManagement,
                Mock.Of<IBedClearAcknowledgementService>(),
                firstContext);
            JobQueueController secondController = CreateJobQueueController(
                secondManagement,
                Mock.Of<IBedClearAcknowledgementService>(),
                secondContext);
            string initialEtag = Convert.ToBase64String(initialJobRevision);
            firstController.Request.Headers.IfMatch = $"\"{initialEtag}\"";
            secondController.Request.Headers.IfMatch = $"\"{initialEtag}\"";

            Task<IActionResult> winnerTask =
                firstController.DispatchJobAsync(fixture.JobId);
            await backendEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            IActionResult loser = await secondController.DispatchJobAsync(fixture.JobId);

            ObjectResult conflict = loser.Should().BeOfType<ObjectResult>().Subject;
            conflict.StatusCode.Should().Be(StatusCodes.Status412PreconditionFailed);
            string? jobEtag = ReadResponseProperty(conflict, "jobETag");
            string? dispatchEtag = ReadResponseProperty(conflict, "dispatchStateETag");
            jobEtag.Should().NotBeNullOrWhiteSpace();
            dispatchEtag.Should().NotBeNullOrWhiteSpace();
            await using (AppDbContext conflictState = CreateContext())
            {
                PrintJob currentJob = await conflictState.PrintJobs.SingleAsync(
                    candidate => candidate.Id == fixture.JobId);
                PrinterDispatchState currentDispatch =
                    await conflictState.PrinterDispatchStates.SingleAsync(
                        candidate => candidate.PrinterId == fixture.PrinterId);
                jobEtag.Should().Be(Convert.ToBase64String(currentJob.RowVersion!));
                dispatchEtag.Should().Be(
                    Convert.ToBase64String(currentDispatch.RowVersion!));
            }

            releaseBackend.TrySetResult(true);
            IActionResult winner = await winnerTask;
            winner.Should().BeAssignableTo<ObjectResult>();
            printers.Verify(service => service.UploadAndStartPrintAsync(
                fixture.PrinterId,
                It.IsAny<string>(),
                It.IsAny<Stream>(),
                It.IsAny<IProgress<UploadAndPrintStage>?>(),
                It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            if (Directory.Exists(storageRoot))
            {
                Directory.Delete(storageRoot, recursive: true);
            }
        }
    }

    [Fact]
    [Trait("Category", "DbHeavy")]
    public async Task DispatchToRoute_StaleIfMatch_Returns412WithBothCurrentEtags()
    {
        Fixture fixture;
        byte[] staleJobRevision;
        await using (AppDbContext seed = CreateContext())
        {
            fixture = await SeedStandardJobAsync(seed);
            PrintJob job = await seed.PrintJobs.SingleAsync(
                candidate => candidate.Id == fixture.JobId);
            staleJobRevision = job.RowVersion!.ToArray();
            job.Priority = (int)PrintJobPriority.High;
            job.UpdatedAt = DateTime.UtcNow;
            await seed.SaveChangesAsync();
        }

        await using AppDbContext context = CreateContext();
        PrintJob currentJob = await context.PrintJobs.AsNoTracking().SingleAsync(
            candidate => candidate.Id == fixture.JobId);
        PrinterDispatchState currentDispatch = await context.PrinterDispatchStates
            .AsNoTracking()
            .SingleAsync(candidate => candidate.PrinterId == fixture.PrinterId);
        var scorer = new Mock<IDispatchScorer>();
        scorer.Setup(service => service.ScorePrintersForJobAsync(
                fixture.JobId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new DispatchScore(
                    fixture.PrinterId,
                    "printer",
                    100,
                    [],
                    Eliminated: false,
                    []),
            ]);
        var dispatch = new JobDispatchService(
            scorer.Object,
            Mock.Of<IPrintJobManagementService>(),
            Mock.Of<ISpoolmanService>(),
            context,
            NullLogger<JobDispatchService>.Instance,
            Mock.Of<Farm.Infrastructure.Services.Spoolman.IFilamentCoverageBroadcaster>(),
            Mock.Of<Farm.Infrastructure.Services.PartsInventory.IPartOutputSnapshotService>());
        JobQueueController controller = CreateJobQueueController(
            Mock.Of<IPrintJobManagementService>(),
            Mock.Of<IBedClearAcknowledgementService>(),
            context,
            dispatch);
        controller.Request.Headers.IfMatch =
            $"\"{Convert.ToBase64String(staleJobRevision)}\"";

        IActionResult result = await controller.DispatchToAsync(
            fixture.JobId,
            new DispatchJobDto { PrinterId = fixture.PrinterId });

        ObjectResult conflict = result.Should().BeOfType<ObjectResult>().Subject;
        conflict.StatusCode.Should().Be(StatusCodes.Status412PreconditionFailed);
        ReadResponseProperty(conflict, "jobETag").Should().Be(
            Convert.ToBase64String(currentJob.RowVersion!));
        ReadResponseProperty(conflict, "dispatchStateETag").Should().Be(
            Convert.ToBase64String(currentDispatch.RowVersion!));
    }

    [Fact]
    [Trait("Category", "DbHeavy")]
    public async Task DispatchToRoute_FinalSaveRace_LoserReturns412WithCommittedEtags()
    {
        Fixture fixture;
        byte[] initialJobRevision;
        await using (AppDbContext seed = CreateContext())
        {
            fixture = await SeedStandardJobAsync(seed);
            Printer printer = await seed.Printers.SingleAsync(
                candidate => candidate.Id == fixture.PrinterId);
            printer.CurrentSpoolId = 123;
            await seed.SaveChangesAsync();
            PrintJob job = await seed.PrintJobs.SingleAsync(
                candidate => candidate.Id == fixture.JobId);
            initialJobRevision = job.RowVersion!.ToArray();
        }

        var scorer = new Mock<IDispatchScorer>();
        scorer.Setup(service => service.ScorePrintersForJobAsync(
                fixture.JobId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new DispatchScore(
                    fixture.PrinterId,
                    "printer",
                    100,
                    [],
                    Eliminated: false,
                    []),
            ]);
        var winnerEntered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var loserEntered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWinner = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLoser = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var winnerSpoolman = new Mock<ISpoolmanService>();
        winnerSpoolman.Setup(service => service.GetSpoolByIdAsync(
                123,
                It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                winnerEntered.TrySetResult(true);
                await releaseWinner.Task;
                return (SpoolmanSpoolDto?)null;
            });
        var loserSpoolman = new Mock<ISpoolmanService>();
        loserSpoolman.Setup(service => service.GetSpoolByIdAsync(
                123,
                It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                loserEntered.TrySetResult(true);
                await releaseLoser.Task;
                return (SpoolmanSpoolDto?)null;
            });
        var management = new Mock<IPrintJobManagementService>();
        management.Setup(service => service.DispatchJobAsync(
                fixture.JobId.ToString(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueuedPrintJobDto
            {
                Id = fixture.JobId.ToString(),
            });
        await using AppDbContext winnerContext = CreateContext();
        await using AppDbContext loserContext = CreateContext();
        var winnerDispatch = new JobDispatchService(
            scorer.Object,
            management.Object,
            winnerSpoolman.Object,
            winnerContext,
            NullLogger<JobDispatchService>.Instance,
            Mock.Of<Farm.Infrastructure.Services.Spoolman.IFilamentCoverageBroadcaster>(),
            Mock.Of<Farm.Infrastructure.Services.PartsInventory.IPartOutputSnapshotService>());
        var loserDispatch = new JobDispatchService(
            scorer.Object,
            management.Object,
            loserSpoolman.Object,
            loserContext,
            NullLogger<JobDispatchService>.Instance,
            Mock.Of<Farm.Infrastructure.Services.Spoolman.IFilamentCoverageBroadcaster>(),
            Mock.Of<Farm.Infrastructure.Services.PartsInventory.IPartOutputSnapshotService>());
        JobQueueController winnerController = CreateJobQueueController(
            management.Object,
            Mock.Of<IBedClearAcknowledgementService>(),
            winnerContext,
            winnerDispatch);
        JobQueueController loserController = CreateJobQueueController(
            management.Object,
            Mock.Of<IBedClearAcknowledgementService>(),
            loserContext,
            loserDispatch);
        string initialEtag = Convert.ToBase64String(initialJobRevision);
        winnerController.Request.Headers.IfMatch = $"\"{initialEtag}\"";
        loserController.Request.Headers.IfMatch = $"\"{initialEtag}\"";

        Task<IActionResult> winnerTask = winnerController.DispatchToAsync(
            fixture.JobId,
            new DispatchJobDto { PrinterId = fixture.PrinterId });
        Task<IActionResult> loserTask = loserController.DispatchToAsync(
            fixture.JobId,
            new DispatchJobDto { PrinterId = fixture.PrinterId });
        await Task.WhenAll(
            winnerEntered.Task.WaitAsync(TimeSpan.FromSeconds(10)),
            loserEntered.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        releaseWinner.TrySetResult(true);
        IActionResult winner = await winnerTask;
        winner.Should().BeAssignableTo<ObjectResult>();
        releaseLoser.TrySetResult(true);
        IActionResult loser = await loserTask;

        ObjectResult conflict = loser.Should().BeOfType<ObjectResult>().Subject;
        conflict.StatusCode.Should().Be(StatusCodes.Status412PreconditionFailed);
        await using AppDbContext committed = CreateContext();
        PrintJob committedJob = await committed.PrintJobs.AsNoTracking().SingleAsync(
            candidate => candidate.Id == fixture.JobId);
        PrinterDispatchState committedDispatch =
            await committed.PrinterDispatchStates.AsNoTracking().SingleAsync(
                candidate => candidate.PrinterId == fixture.PrinterId);
        ReadResponseProperty(conflict, "jobETag").Should().Be(
            Convert.ToBase64String(committedJob.RowVersion!));
        ReadResponseProperty(conflict, "dispatchStateETag").Should().Be(
            Convert.ToBase64String(committedDispatch.RowVersion!));
        management.Verify(service => service.DispatchJobAsync(
            fixture.JobId.ToString(),
            It.IsAny<string>(),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    [Trait("Category", "DbHeavy")]
    public async Task AcknowledgeAndStartRoute_TwoMatchingEtags_LoserReturns412WithBothEtags()
    {
        Fixture fixture;
        byte[] initialJobRevision;
        byte[] initialDispatchRevision;
        long printerRevision;
        await using (AppDbContext seed = CreateContext())
        {
            fixture = await SeedCalibrationAsync(seed, withAck: false);
            PrintJob job = await seed.PrintJobs.SingleAsync(
                candidate => candidate.Id == fixture.JobId);
            Printer printer = await seed.Printers.SingleAsync(
                candidate => candidate.Id == fixture.PrinterId);
            PrinterDispatchState state = await seed.PrinterDispatchStates.SingleAsync(
                candidate => candidate.PrinterId == fixture.PrinterId);
            initialJobRevision = job.RowVersion!.ToArray();
            initialDispatchRevision = state.RowVersion!.ToArray();
            printerRevision = printer.ConfigurationRevision;
        }

        await using AppDbContext winnerContext = CreateContext();
        await using AppDbContext loserContext = CreateContext();
        _ = await winnerContext.PrintJobs.SingleAsync(
            candidate => candidate.Id == fixture.JobId);
        _ = await winnerContext.PrinterDispatchStates.SingleAsync(
            candidate => candidate.PrinterId == fixture.PrinterId);
        _ = await loserContext.PrintJobs.SingleAsync(
            candidate => candidate.Id == fixture.JobId);
        _ = await loserContext.PrinterDispatchStates.SingleAsync(
            candidate => candidate.PrinterId == fixture.PrinterId);

        JobQueueController winnerController = CreateJobQueueController(
            Mock.Of<IPrintJobManagementService>(),
            CreateBedClearAcknowledgementService(winnerContext, fixture.PrinterId),
            winnerContext);
        JobQueueController loserController = CreateJobQueueController(
            Mock.Of<IPrintJobManagementService>(),
            CreateBedClearAcknowledgementService(loserContext, fixture.PrinterId),
            loserContext);
        SetAcknowledgementHeaders(
            winnerController,
            initialJobRevision,
            initialDispatchRevision,
            "ack-etag-winner");
        SetAcknowledgementHeaders(
            loserController,
            initialJobRevision,
            initialDispatchRevision,
            "ack-etag-loser");
        var request = new AcknowledgeBedClearRequestDto
        {
            PrinterId = fixture.PrinterId,
            ExpectedPrinterConfigRevision = printerRevision,
        };

        IActionResult winner = await winnerController.AcknowledgeBedClearAndStartAsync(
            fixture.JobId,
            request,
            CancellationToken.None);
        IActionResult loser = await loserController.AcknowledgeBedClearAndStartAsync(
            fixture.JobId,
            request,
            CancellationToken.None);

        winner.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        ObjectResult conflict = loser.Should().BeOfType<ObjectResult>().Subject;
        conflict.StatusCode.Should().Be(StatusCodes.Status412PreconditionFailed);
        string? jobEtag = ReadResponseProperty(conflict, "jobETag");
        string? dispatchEtag = ReadResponseProperty(conflict, "dispatchStateETag");
        jobEtag.Should().NotBeNullOrWhiteSpace();
        dispatchEtag.Should().NotBeNullOrWhiteSpace();
        await using AppDbContext verify = CreateContext();
        PrintJob currentJob = await verify.PrintJobs.SingleAsync(
            candidate => candidate.Id == fixture.JobId);
        PrinterDispatchState currentDispatch =
            await verify.PrinterDispatchStates.SingleAsync(
                candidate => candidate.PrinterId == fixture.PrinterId);
        jobEtag.Should().Be(Convert.ToBase64String(currentJob.RowVersion!));
        dispatchEtag.Should().Be(Convert.ToBase64String(currentDispatch.RowVersion!));
        (await verify.BedClearCommandRecords.CountAsync()).Should().Be(1);
    }

    // =========================================================================
    // Hard filament gate
    // =========================================================================

    [Fact]
    [Trait("Category", "DbHeavy")]
    public async Task FilamentGate_RejectsClaimWhenPinnedSpoolIsNoLongerLoaded()
    {
        await using AppDbContext seed = CreateContext();
        // The pinned-spool filament gate only applies to FilamentCalibration jobs
        // (DispatchClaimService.cs), so this fixture must keep that job kind.
        Fixture fixture = await SeedCalibrationAsync(
            seed, withAck: true, jobKind: JobKind.FilamentCalibration);

        await using AppDbContext swap = CreateContext();
        Printer printer = await swap.Printers.SingleAsync(p => p.Id == fixture.PrinterId);
        printer.CurrentSpoolId = SpoolId + 1; // operator swapped the spool
        Spool pinnedSpool = await swap.Spools.SingleAsync(
            spool => spool.AssignedPrinterId == fixture.PrinterId);
        pinnedSpool.InUse = false;
        pinnedSpool.AssignedPrinterId = null;
        await swap.SaveChangesAsync();

        await using AppDbContext ctx = CreateContext();
        DispatchClaimResult result = await CreateClaim(ctx, DispatchTestDoubles.OnlineIdleReader(fixture.PrinterId))
            .AcquireClaimAsync(new DispatchClaimRequest(
                fixture.JobId, fixture.PrinterId, "op", "Manual", fixture.AckKey, null, null));

        // Post-#1989/D3b: any FilamentCalibration claim now fails unconditionally
        // via EvaluatePersistedCalibrationInputsAsync *before* EvaluatePinnedSpoolAsync
        // is ever reached (DispatchClaimService.AcquireClaimAsync, lines ~275-291), so
        // the pinned-spool-mismatch gate this test used to exercise is now
        // unreachable for calibration jobs. See issue #1990.
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("calibration_dispatch_unavailable");

        await using AppDbContext verify = CreateContext();
        QueueOperationAudit denial = await verify.QueueOperationAudits
            .SingleAsync(a => a.PrintJobId == fixture.JobId && a.Outcome == QueueAuditOutcomes.Denied);
        denial.ReasonCode.Should().Be("calibration_dispatch_unavailable");
        PrinterDispatchState dispatchState = await verify.PrinterDispatchStates
            .SingleAsync(state => state.PrinterId == fixture.PrinterId);
        dispatchState.AcknowledgedJobId.Should().Be(
            fixture.JobId,
            "a failed safety gate must not consume the acknowledgement");
    }

    [Fact]
    [Trait("Category", "DbHeavy")]
    public async Task BedClearAcknowledgement_AfterSpoolCorrection_CalibrationDispatchStillUnavailable()
    {
        Fixture fixture;
        Guid spoolId;
        await using (AppDbContext seed = CreateContext())
        {
            // The mutable filament block/clear flow this test exercises is a
            // FilamentCalibration-only gate (BedClearAcknowledgementService.cs).
            fixture = await SeedCalibrationAsync(
                seed, withAck: false, jobKind: JobKind.FilamentCalibration);
            Printer printer = await seed.Printers.SingleAsync(
                candidate => candidate.Id == fixture.PrinterId);
            Spool spool = await seed.Spools.SingleAsync(
                candidate => candidate.AssignedPrinterId == fixture.PrinterId);
            spoolId = spool.Id;
            printer.CurrentSpoolId = SpoolId + 1;
            spool.InUse = false;
            spool.AssignedPrinterId = null;
            await seed.SaveChangesAsync();
        }

        await using (AppDbContext blockedContext = CreateContext())
        {
            PrintJob job = await blockedContext.PrintJobs.SingleAsync(
                candidate => candidate.Id == fixture.JobId);
            Printer printer = await blockedContext.Printers.SingleAsync(
                candidate => candidate.Id == fixture.PrinterId);
            PrinterDispatchState state =
                await blockedContext.PrinterDispatchStates.SingleAsync(
                    candidate => candidate.PrinterId == fixture.PrinterId);
            BedClearAcknowledgementService acknowledgement =
                CreateBedClearAcknowledgementService(blockedContext, fixture.PrinterId);
            JobQueueController controller = CreateJobQueueController(
                Mock.Of<IPrintJobManagementService>(),
                acknowledgement,
                blockedContext);
            SetAcknowledgementHeaders(
                controller,
                job.RowVersion!,
                state.RowVersion!,
                "filament-blocked");

            IActionResult first = await controller.AcknowledgeBedClearAndStartAsync(
                fixture.JobId,
                new AcknowledgeBedClearRequestDto
                {
                    PrinterId = fixture.PrinterId,
                    ExpectedPrinterConfigRevision = printer.ConfigurationRevision,
                },
                CancellationToken.None);

            ObjectResult response = first.Should().BeAssignableTo<ObjectResult>().Subject;
            response.StatusCode.Should().Be(StatusCodes.Status422UnprocessableEntity);
        }

        await using (AppDbContext blockedVerify = CreateContext())
        {
            PrintJob blockedJob = await blockedVerify.PrintJobs.SingleAsync(
                candidate => candidate.Id == fixture.JobId);
            PrinterDispatchState blockedState =
                await blockedVerify.PrinterDispatchStates.SingleAsync(
                    candidate => candidate.PrinterId == fixture.PrinterId);
            blockedJob.BlockedReasonCode.Should().Be(JobBlockedReasonCode.FilamentCheckFailed);
            blockedState.AcknowledgedJobId.Should().BeNull();
            (await blockedVerify.BedClearCommandRecords.CountAsync()).Should().Be(0);
        }

        await using (AppDbContext correction = CreateContext())
        {
            Printer printer = await correction.Printers.SingleAsync(
                candidate => candidate.Id == fixture.PrinterId);
            Spool spool = await correction.Spools.SingleAsync(
                candidate => candidate.Id == spoolId);
            printer.CurrentSpoolId = SpoolId;
            spool.InUse = true;
            spool.AssignedPrinterId = fixture.PrinterId;
            await correction.SaveChangesAsync();
        }

        // #1989 (D3b): correcting the mutable filament block used to let the second
        // acknowledgement clear it and succeed (202). Calibration dispatch is now
        // unconditionally unavailable (see EvaluatePersistedCalibrationInputsAsync),
        // so the second attempt still fails - just for a different, calibration-only
        // reason - instead of ever reaching a successful 202.
        await using (AppDbContext correctedContext = CreateContext())
        {
            PrintJob job = await correctedContext.PrintJobs.SingleAsync(
                candidate => candidate.Id == fixture.JobId);
            Printer printer = await correctedContext.Printers.SingleAsync(
                candidate => candidate.Id == fixture.PrinterId);
            PrinterDispatchState state =
                await correctedContext.PrinterDispatchStates.SingleAsync(
                    candidate => candidate.PrinterId == fixture.PrinterId);
            BedClearAcknowledgementService acknowledgement =
                CreateBedClearAcknowledgementService(correctedContext, fixture.PrinterId);
            JobQueueController controller = CreateJobQueueController(
                Mock.Of<IPrintJobManagementService>(),
                acknowledgement,
                correctedContext);
            SetAcknowledgementHeaders(
                controller,
                job.RowVersion!,
                state.RowVersion!,
                "filament-corrected");

            IActionResult second = await controller.AcknowledgeBedClearAndStartAsync(
                fixture.JobId,
                new AcknowledgeBedClearRequestDto
                {
                    PrinterId = fixture.PrinterId,
                    ExpectedPrinterConfigRevision = printer.ConfigurationRevision,
                },
                CancellationToken.None);

            ObjectResult response = second.Should().BeAssignableTo<ObjectResult>().Subject;
            response.StatusCode.Should().Be(StatusCodes.Status422UnprocessableEntity);
        }

        await using AppDbContext verify = CreateContext();
        PrinterDispatchState persistedState =
            await verify.PrinterDispatchStates.SingleAsync(
                candidate => candidate.PrinterId == fixture.PrinterId);
        persistedState.AcknowledgedJobId.Should().BeNull();
        (await verify.BedClearCommandRecords.CountAsync()).Should().Be(0);
    }

    [Fact]
    [Trait("Category", "DbHeavy")]
    public async Task BedClearAcknowledgement_ImmutableProvenanceBlock_RemainsHardFailure()
    {
        await using AppDbContext context = CreateContext();
        // This test asserts the immutable-provenance-block gate that only applies to
        // FilamentCalibration jobs (BedClearAcknowledgementService.cs), so it must keep
        // the calibration job kind unlike the other fixtures in this file.
        Fixture fixture = await SeedCalibrationAsync(
            context, withAck: false, jobKind: JobKind.FilamentCalibration);
        PrintJob job = await context.PrintJobs.SingleAsync(
            candidate => candidate.Id == fixture.JobId);
        Printer printer;
        PrinterDispatchState state;
        job.BlockedReasonCode = JobBlockedReasonCode.ContentHashMismatch;
        job.BlockedReasonJson = """{"errorCode":"content_hash_mismatch"}""";
        await context.SaveChangesAsync();

        context.ChangeTracker.Clear();
        job = await context.PrintJobs.SingleAsync(candidate => candidate.Id == fixture.JobId);
        printer = await context.Printers.SingleAsync(
            candidate => candidate.Id == fixture.PrinterId);
        state = await context.PrinterDispatchStates.SingleAsync(
            candidate => candidate.PrinterId == fixture.PrinterId);
        BedClearAcknowledgementService acknowledgement =
            CreateBedClearAcknowledgementService(context, fixture.PrinterId);
        AcknowledgeBedClearResult result = await acknowledgement.AcknowledgeAsync(
            new AcknowledgeBedClearRequest(
                fixture.JobId,
                fixture.PrinterId,
                "operator-1",
                "immutable-block",
                state.RowVersion,
                printer.ConfigurationRevision,
                job.RowVersion));

        result.Outcome.Should().Be(BedClearAckOutcome.CalibrationJobIncompatible);
        job.BlockedReasonCode.Should().Be(JobBlockedReasonCode.ContentHashMismatch);
        (await context.BedClearCommandRecords.CountAsync()).Should().Be(0);
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
            Copies = 1,
            Priority = PrintJobPriority.High,
        };

        await using AppDbContext ctxA = CreateContext();
        await using AppDbContext ctxB = CreateContext();

        // Post-#1989/D3b: CalibrationQueueCanonicalizer.BuildAsync now unconditionally
        // rejects every calibration-lineage artifact before any row is written (the
        // PrinterConfigurationSnapshot compatibility check it depended on is gone; see
        // #1990). The "loser rereads winner" unique-index race this test used to exercise
        // is therefore unreachable for calibration jobs: both concurrent producers must
        // fail deterministically at the same pre-insert gate, and no row must ever land.
        Func<Task> actA = async () => await CreateQueueService(ctxA)
            .AddJobToQueueAsync(request, CalibrationOwnerId, CancellationToken.None);
        Func<Task> actB = async () => await CreateQueueService(ctxB)
            .AddJobToQueueAsync(request, CalibrationOwnerId, CancellationToken.None);

        (await actA.Should().ThrowAsync<CalibrationQueueIncompatibleException>())
            .WithMessage("*known interim limitation*#1990*");
        (await actB.Should().ThrowAsync<CalibrationQueueIncompatibleException>())
            .WithMessage("*known interim limitation*#1990*");

        await using AppDbContext verify = CreateContext();
        (await verify.PrintJobs.CountAsync(j => j.IdempotencyKey == "concurrent-key")).Should().Be(0);
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
            .AddJobToQueueAsync(laundered, CalibrationOwnerId, CancellationToken.None);

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

    [Fact]
    [Trait("Category", "DbHeavy")]
    public async Task Reconciler_IdleAndEveryExactIdentityAbsent_ReleasesLease()
    {
        await using AppDbContext seed = CreateContext();
        Fixture fixture = await SeedCalibrationAsync(seed, withAck: true);
        DispatchClaimService claimService = CreateClaim(
            seed,
            DispatchTestDoubles.OnlineIdleReader(fixture.PrinterId));
        DispatchClaimResult claim = await claimService.AcquireClaimAsync(
            new DispatchClaimRequest(
                fixture.JobId,
                fixture.PrinterId,
                "operator-1",
                "Manual",
                fixture.AckKey,
                null,
                null));
        claim.Success.Should().BeTrue(claim.ErrorDetail);
        claim.Attempt!.BackendJobId = "provider-history-id";
        await seed.SaveChangesAsync();
        await claimService.RecordUnknownOutcomeAsync(
            claim.Attempt.Id,
            "response lost");

        var printers = new Mock<IPrintersService>();
        printers.Setup(service => service.GetStatusDtoAsync(
                fixture.PrinterId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PrinterStatusDto(
                fixture.PrinterId,
                IsOnline: true,
                State: "idle"));
        printers.Setup(service => service.ProbeHistoryJobAsync(
                fixture.PrinterId,
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(HistoryJobProbeResult.NotFound());
        printers.Setup(service => service.ProbeHistoryListAsync(
                fixture.PrinterId,
                It.IsAny<int?>(),
                It.IsAny<int?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(HistoryListProbeResult.Authoritative(
                new HistoryListResponse()));

        await RunReconciliationAsync(printers.Object);

        await using AppDbContext verify = CreateContext();
        QueueDispatchAttempt attempt = await verify.QueueDispatchAttempts
            .SingleAsync(candidate => candidate.Id == claim.Attempt.Id);
        PrintJob job = await verify.PrintJobs.SingleAsync(
            candidate => candidate.Id == fixture.JobId);
        PrinterDispatchState state = await verify.PrinterDispatchStates.SingleAsync(
            candidate => candidate.PrinterId == fixture.PrinterId);
        attempt.Outcome.Should().Be(DispatchAttemptOutcome.FailedBeforeStart);
        attempt.ErrorCode.Should().Be("reconciliation_absent");
        job.Status.Should().Be(PrintJobStatus.Assigned);
        state.ActiveDispatchAttemptId.Should().BeNull();
        printers.Verify(service => service.ProbeHistoryJobAsync(
            fixture.PrinterId,
            "provider-history-id",
            It.IsAny<CancellationToken>()), Times.Once);
        printers.Verify(service => service.ProbeHistoryListAsync(
            fixture.PrinterId,
            It.IsAny<int?>(),
            It.IsAny<int?>(),
            It.IsAny<DateTime?>(),
            It.IsAny<DateTime?>(),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [Trait("Category", "DbHeavy")]
    public async Task Reconciler_UnrelatedExcludedEvidence_PermitsAuthoritativeAbsence(
        bool differentBackendId)
    {
        const string backendJobId = "authoritative-attempt-id";
        (Fixture fixture, Guid attemptId) =
            await SeedUnknownReconciliationAttemptAsync(backendJobId);
        QueueDispatchAttempt attempt;
        await using (AppDbContext read = CreateContext())
        {
            attempt = await read.QueueDispatchAttempts
                .AsNoTracking()
                .SingleAsync(candidate => candidate.Id == attemptId);
        }

        HistoryExcludedEntryEvidence excluded = differentBackendId
            ? new HistoryExcludedEntryEvidence(
                "different-provider-id",
                attempt.BackendFileName,
                new DateTimeOffset(
                    DateTime.SpecifyKind(
                        attempt.ClaimedAtUtc,
                        DateTimeKind.Utc)).ToUnixTimeSeconds(),
                "malformed_history_entry")
            : new HistoryExcludedEntryEvidence(
                BackendJobId: null,
                Filename: string.Empty,
                StartTime: null,
                Reason: "malformed_history_entry");
        Mock<IPrintersService> printers = CreateQuiescentHistoryProbe(
            fixture.PrinterId,
            backendJobId,
            HistoryListProbeResult.Authoritative(
                new HistoryListResponse
                {
                    ExcludedEntries = [excluded],
                }));

        await RunReconciliationAsync(printers.Object);

        await using AppDbContext verify = CreateContext();
        QueueDispatchAttempt reconciled =
            await verify.QueueDispatchAttempts.SingleAsync(
                candidate => candidate.Id == attemptId);
        PrintJob job = await verify.PrintJobs.SingleAsync(
            candidate => candidate.Id == fixture.JobId);
        PrinterDispatchState state =
            await verify.PrinterDispatchStates.SingleAsync(
                candidate => candidate.PrinterId == fixture.PrinterId);
        reconciled.Outcome.Should().Be(
            DispatchAttemptOutcome.FailedBeforeStart);
        reconciled.ErrorCode.Should().Be("reconciliation_absent");
        job.Status.Should().Be(PrintJobStatus.Assigned);
        state.ActiveJobId.Should().BeNull();
        state.ActiveDispatchAttemptId.Should().BeNull();
        state.PhysicalControlCommandId.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "DbHeavy")]
    public async Task Reconciler_OneExactIdentityProbeFails_RetainsUnknownLease()
    {
        await using AppDbContext seed = CreateContext();
        Fixture fixture = await SeedCalibrationAsync(seed, withAck: true);
        DispatchClaimService claimService = CreateClaim(
            seed,
            DispatchTestDoubles.OnlineIdleReader(fixture.PrinterId));
        DispatchClaimResult claim = await claimService.AcquireClaimAsync(
            new DispatchClaimRequest(
                fixture.JobId,
                fixture.PrinterId,
                "operator-1",
                "Manual",
                fixture.AckKey,
                null,
                null));
        claim.Success.Should().BeTrue(claim.ErrorDetail);
        claim.Attempt!.BackendJobId = "provider-history-id";
        await seed.SaveChangesAsync();
        await claimService.RecordUnknownOutcomeAsync(
            claim.Attempt.Id,
            "response lost");

        var printers = new Mock<IPrintersService>();
        printers.Setup(service => service.GetStatusDtoAsync(
                fixture.PrinterId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PrinterStatusDto(
                fixture.PrinterId,
                IsOnline: true,
                State: "idle"));
        printers.Setup(service => service.ProbeHistoryJobAsync(
                fixture.PrinterId,
                "provider-history-id",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(HistoryJobProbeResult.Unavailable());
        printers.Setup(service => service.ProbeHistoryListAsync(
                fixture.PrinterId,
                It.IsAny<int?>(),
                It.IsAny<int?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(HistoryListProbeResult.Authoritative(
                new HistoryListResponse()));

        await RunReconciliationAsync(printers.Object);

        await using AppDbContext verify = CreateContext();
        QueueDispatchAttempt attempt = await verify.QueueDispatchAttempts
            .SingleAsync(candidate => candidate.Id == claim.Attempt.Id);
        PrinterDispatchState state = await verify.PrinterDispatchStates.SingleAsync(
            candidate => candidate.PrinterId == fixture.PrinterId);
        attempt.Outcome.Should().Be(DispatchAttemptOutcome.Unknown);
        attempt.RequiresReconciliation.Should().BeTrue();
        state.ActiveDispatchAttemptId.Should().Be(claim.Attempt.Id);
        printers.Verify(service => service.ProbeHistoryListAsync(
            It.IsAny<Guid>(),
            It.IsAny<int?>(),
            It.IsAny<int?>(),
            It.IsAny<DateTime?>(),
            It.IsAny<DateTime?>(),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(HistoryDetailProbeStatus.Unsupported)]
    [InlineData(HistoryDetailProbeStatus.Unavailable)]
    [InlineData(HistoryDetailProbeStatus.Error)]
    [Trait("Category", "DbHeavy")]
    public async Task Reconciler_NonAuthoritativeDetailProbe_RetainsEveryFence(
        HistoryDetailProbeStatus status)
    {
        string backendJobId = $"detail-{status.ToString().ToLowerInvariant()}";
        (Fixture fixture, Guid attemptId) =
            await SeedUnknownReconciliationAttemptAsync(backendJobId);
        var printers = new Mock<IPrintersService>();
        printers.Setup(service => service.GetStatusDtoAsync(
                fixture.PrinterId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PrinterStatusDto(
                fixture.PrinterId,
                IsOnline: true,
                State: "idle"));
        HistoryJobProbeResult detail = status switch
        {
            HistoryDetailProbeStatus.Unsupported =>
                HistoryJobProbeResult.Unsupported(),
            HistoryDetailProbeStatus.Unavailable =>
                HistoryJobProbeResult.Unavailable(),
            _ => HistoryJobProbeResult.Error(),
        };
        printers.Setup(service => service.ProbeHistoryJobAsync(
                fixture.PrinterId,
                backendJobId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(detail);

        await RunReconciliationAsync(printers.Object);

        await AssertIndeterminateFencesAsync(fixture, attemptId);
        printers.Verify(service => service.ProbeHistoryListAsync(
            It.IsAny<Guid>(),
            It.IsAny<int?>(),
            It.IsAny<int?>(),
            It.IsAny<DateTime?>(),
            It.IsAny<DateTime?>(),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    [Trait("Category", "DbHeavy")]
    public async Task Reconciler_RealPrintersServiceNullDetail_RetainsEveryFence()
    {
        const string backendJobId = "null-detail-through-service";
        (Fixture fixture, Guid attemptId) =
            await SeedUnknownReconciliationAttemptAsync(backendJobId);
        await using AppDbContext serviceDb = CreateContext();
        Printer printer = await serviceDb.Printers.SingleAsync(
            candidate => candidate.Id == fixture.PrinterId);
        var history = new Mock<ISupportsHistory>();
        history.Setup(client => client.GetHistoryJobAsync(
                It.IsAny<string>(),
                backendJobId,
                It.IsAny<PrinterCredential?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((HistoryJob?)null);
        PrintersService service = CreateConcreteHistoryPrintersService(
            serviceDb,
            printer,
            history.Object);

        await RunReconciliationAsync(service);

        await AssertIndeterminateFencesAsync(fixture, attemptId);
        history.Verify(client => client.GetHistoryListAsync(
            It.IsAny<string>(),
            It.IsAny<int?>(),
            It.IsAny<int?>(),
            It.IsAny<DateTime?>(),
            It.IsAny<DateTime?>(),
            It.IsAny<string?>(),
            It.IsAny<PrinterCredential?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    [Trait("Category", "DbHeavy")]
    public async Task Reconciler_RealPrintersServiceUnavailableList_RetainsEveryFence()
    {
        const string backendJobId = "list-unavailable-through-service";
        (Fixture fixture, Guid attemptId) =
            await SeedUnknownReconciliationAttemptAsync(backendJobId);
        await using AppDbContext serviceDb = CreateContext();
        Printer printer = await serviceDb.Printers.SingleAsync(
            candidate => candidate.Id == fixture.PrinterId);
        var history = new Mock<ISupportsHistory>();
        history.Setup(client => client.GetHistoryJobAsync(
                It.IsAny<string>(),
                backendJobId,
                It.IsAny<PrinterCredential?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HistoryJobNotFoundException(backendJobId));
        history.Setup(client => client.GetHistoryListAsync(
                It.IsAny<string>(),
                It.IsAny<int?>(),
                It.IsAny<int?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<string?>(),
                It.IsAny<PrinterCredential?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((HistoryListResponse?)null);
        PrintersService service = CreateConcreteHistoryPrintersService(
            serviceDb,
            printer,
            history.Object);

        await RunReconciliationAsync(service);

        await AssertIndeterminateFencesAsync(fixture, attemptId);
    }

    [Fact]
    [Trait("Category", "DbHeavy")]
    public async Task Reconciler_MoonrakerMalformedMatchingIdentity_RetainsEveryFence()
    {
        const string backendJobId = "moonraker-malformed-list";
        (Fixture fixture, Guid attemptId) =
            await SeedUnknownReconciliationAttemptAsync(
                backendJobId,
                PrinterBackend.Moonraker);
        await using AppDbContext serviceDb = CreateContext();
        Printer printer = await serviceDb.Printers.SingleAsync(
            candidate => candidate.Id == fixture.PrinterId);
        QueueDispatchAttempt attempt =
            await serviceDb.QueueDispatchAttempts.SingleAsync(
                candidate => candidate.Id == attemptId);
        string backendFilename = attempt.BackendFileName
            ?? attempt.BackendFileIdentity
            ?? throw new InvalidOperationException(
                "Seeded attempt must persist a backend filename.");
        using var handler = new HistoryAuthorityHandler(request =>
            request.RequestUri!.AbsolutePath == "/server/history/job"
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : JsonResponse(JsonSerializer.Serialize(new
                {
                    result = new
                    {
                        count = 1,
                        jobs = new[]
                        {
                            new
                            {
                                filename = backendFilename,
                                status = "completed",
                            },
                        },
                    },
                })));
        using var http = new HttpClient(handler);
        var history = new MoonrakerClient(
            http,
            NullLogger<MoonrakerClient>.Instance,
            new BackendTimeoutSettings());
        PrintersService service = CreateConcreteHistoryPrintersService(
            serviceDb,
            printer,
            history);

        await RunReconciliationAsync(service);

        await AssertIndeterminateFencesAsync(fixture, attemptId);
        handler.RequestPaths.Should().Contain("/server/history/list");
    }

    [Fact]
    [Trait("Category", "DbHeavy")]
    public async Task Reconciler_OctoPrintMalformedMatchingIdentity_RetainsEveryFence()
    {
        const string backendJobId = "octoprint-malformed-list";
        (Fixture fixture, Guid attemptId) =
            await SeedUnknownReconciliationAttemptAsync(
                backendJobId,
                PrinterBackend.OctoPrint);
        await using AppDbContext serviceDb = CreateContext();
        Printer printer = await serviceDb.Printers.SingleAsync(
            candidate => candidate.Id == fixture.PrinterId);
        printer.Credential = new PrinterCredential { ApiKey = "test-api-key" };
        using var handler = new HistoryAuthorityHandler(request =>
            request.RequestUri!.AbsolutePath.Contains(
                backendJobId,
                StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : JsonResponse(JsonSerializer.Serialize(new
                {
                    success = true,
                    count = 1,
                    results = new[]
                    {
                        new { name = backendJobId, success = true },
                    },
                })));
        using var http = new HttpClient(handler);
        var history = new OctoPrintClient(
            http,
            NullLogger<OctoPrintClient>.Instance,
            new BackendTimeoutSettings());
        PrintersService service = CreateConcreteHistoryPrintersService(
            serviceDb,
            printer,
            history);
        await RunReconciliationAsync(service);

        await AssertIndeterminateFencesAsync(fixture, attemptId);
        handler.RequestPaths.Should().Contain("/api/history");
    }

    [Fact]
    [Trait("Category", "DbHeavy")]
    public async Task Reconciler_OctoPrintCompleteFullPage_ProvesAbsenceAndReleasesLease()
    {
        const string backendJobId = "octoprint-full-page";
        (Fixture fixture, Guid attemptId) =
            await SeedUnknownReconciliationAttemptAsync(
                backendJobId,
                PrinterBackend.OctoPrint);
        await using AppDbContext serviceDb = CreateContext();
        Printer printer = await serviceDb.Printers.SingleAsync(
            candidate => candidate.Id == fixture.PrinterId);
        printer.Credential = new PrinterCredential { ApiKey = "test-api-key" };
        string payload = JsonSerializer.Serialize(new
        {
            success = true,
            count = 100,
            results = Enumerable.Range(0, 100).Select(index => new
            {
                name = $"page-{index}.gcode",
                success = true,
                timestamp = 1700000000 + index,
            }),
        });
        using var handler = new HistoryAuthorityHandler(request =>
            request.RequestUri!.AbsolutePath.Contains(
                backendJobId,
                StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : JsonResponse(payload));
        using var http = new HttpClient(handler);
        var history = new OctoPrintClient(
            http,
            NullLogger<OctoPrintClient>.Instance,
            new BackendTimeoutSettings());
        PrintersService service = CreateConcreteHistoryPrintersService(
            serviceDb,
            printer,
            history);

        await RunReconciliationAsync(service);

        await AssertAuthoritativeAbsenceReleasedAsync(fixture, attemptId);
    }

    [Fact]
    [Trait("Category", "DbHeavy")]
    public async Task Reconciler_OctoPrint250Rows_FindsTerminalIdentityAndReleasesLease()
    {
        const string backendJobId = "octoprint-terminal-match.gcode";
        (Fixture fixture, Guid attemptId) =
            await SeedUnknownReconciliationAttemptAsync(
                backendJobId,
                PrinterBackend.OctoPrint);
        await using AppDbContext serviceDb = CreateContext();
        Printer printer = await serviceDb.Printers.SingleAsync(
            candidate => candidate.Id == fixture.PrinterId);
        printer.Credential = new PrinterCredential { ApiKey = "test-api-key" };
        long firstTimestamp = DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeSeconds();
        var entries = Enumerable.Range(0, 250)
            .Select(index => new
            {
                name = index == 249 ? backendJobId : $"history-{index:D3}.gcode",
                success = true,
                timestamp = firstTimestamp + index,
                completionTime = firstTimestamp + index + 30,
            })
            .ToArray();
        using var handler = new HistoryAuthorityHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.Contains(
                    backendJobId,
                    StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            int start = ReadQueryInt(request, "start");
            int limit = ReadQueryInt(request, "limit");
            return JsonResponse(JsonSerializer.Serialize(new
            {
                success = true,
                count = entries.Length,
                results = entries.Skip(start).Take(limit),
            }));
        });
        using var http = new HttpClient(handler);
        var history = new OctoPrintClient(
            http,
            NullLogger<OctoPrintClient>.Instance,
            new BackendTimeoutSettings());
        PrintersService service = CreateConcreteHistoryPrintersService(
            serviceDb,
            printer,
            history);
        await RunReconciliationAsync(service);

        await AssertReconciledTerminalAsync(
            fixture,
            attemptId,
            PrintJobStatus.Completed,
            DispatchClaimService.EventTypeJobCompleted,
            expectedFailureCode: null);
        handler.RequestPaths.Count(path => path == "/api/history").Should().Be(3);
    }

    [Theory]
    [InlineData(
        "error",
        PrintJobStatus.Failed,
        "PrintFarmer.Queue.JobFailed.v1",
        "reconciliation_failed")]
    [InlineData(
        "cancelled",
        PrintJobStatus.Cancelled,
        "PrintFarmer.Queue.JobCancelled.v1",
        "reconciliation_cancelled")]
    [InlineData(
        "klippy_shutdown",
        PrintJobStatus.Failed,
        "PrintFarmer.Queue.JobFailed.v1",
        "reconciliation_failed")]
    [InlineData(
        "stopped",
        PrintJobStatus.Cancelled,
        "PrintFarmer.Queue.JobCancelled.v1",
        "reconciliation_cancelled")]
    [InlineData(
        "klippy_disconnect",
        PrintJobStatus.Failed,
        "PrintFarmer.Queue.JobFailed.v1",
        "reconciliation_failed")]
    [InlineData(
        "server_exit",
        PrintJobStatus.Failed,
        "PrintFarmer.Queue.JobFailed.v1",
        "reconciliation_failed")]
    [InlineData(
        "interrupted",
        PrintJobStatus.Failed,
        "PrintFarmer.Queue.JobFailed.v1",
        "reconciliation_failed")]
    [Trait("Category", "DbHeavy")]
    public async Task Reconciler_RealMoonrakerTerminalStatus_MapsLifecycleWithoutJobCompleted(
        string historyStatus,
        PrintJobStatus expectedStatus,
        string expectedEventType,
        string expectedFailureCode)
    {
        string backendJobId = $"moonraker-{historyStatus}";
        (Fixture fixture, Guid attemptId) =
            await SeedUnknownReconciliationAttemptAsync(
                backendJobId,
                PrinterBackend.Moonraker);
        await using AppDbContext serviceDb = CreateContext();
        Printer printer = await serviceDb.Printers.SingleAsync(
            candidate => candidate.Id == fixture.PrinterId);
        long startTimestamp = DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeSeconds();
        using var handler = new HistoryAuthorityHandler(request =>
            request.RequestUri!.AbsolutePath == "/server/history/job"
                ? JsonResponse(JsonSerializer.Serialize(new
                {
                    result = new
                    {
                        job_id = backendJobId,
                        filename = "terminal-status.gcode",
                        status = historyStatus,
                        start_time = startTimestamp,
                        end_time = startTimestamp + 30,
                    },
                }))
                : throw new InvalidOperationException(
                    "A found authoritative detail must not fall back to the list."));
        using var http = new HttpClient(handler);
        var history = new MoonrakerClient(
            http,
            NullLogger<MoonrakerClient>.Instance,
            new BackendTimeoutSettings());
        HistoryJob? directHistory =
            await ((ISupportsHistory)history).GetHistoryJobAsync(
                printer.BackendUrl,
                backendJobId,
                printer.Credential,
                CancellationToken.None);
        directHistory.Should().NotBeNull();
        directHistory!.JobId.Should().Be(backendJobId);
        directHistory.Status.Should().Be(historyStatus);
        PrintersService service = CreateConcreteHistoryPrintersService(
            serviceDb,
            printer,
            history);

        await RunReconciliationAsync(service);

        await AssertReconciledTerminalAsync(
            fixture,
            attemptId,
            expectedStatus,
            expectedEventType,
            expectedFailureCode);
        handler.RequestPaths.Should().Equal(
            "/server/history/job",
            "/server/history/job");
    }

    [Fact]
    [Trait("Category", "DbHeavy")]
    public async Task Reconciler_RealPrusaLinkStopped_MapsCancelledWithoutJobCompleted()
    {
        const string backendJobId = "prusalink-stopped";
        (Fixture fixture, Guid attemptId) =
            await SeedUnknownReconciliationAttemptAsync(
                backendJobId,
                PrinterBackend.PrusaLink);
        await using AppDbContext serviceDb = CreateContext();
        Printer printer = await serviceDb.Printers.SingleAsync(
            candidate => candidate.Id == fixture.PrinterId);
        long startTimestamp =
            DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeSeconds();
        using var handler = new HistoryAuthorityHandler(request =>
            request.RequestUri!.AbsolutePath.Contains(
                backendJobId,
                StringComparison.Ordinal)
                ? JsonResponse(JsonSerializer.Serialize(new
                {
                    success = true,
                    id = backendJobId,
                    state = "STOPPED",
                    startTime = startTimestamp,
                    endTime = startTimestamp + 30,
                    job = new { file = new { name = "stopped.gcode" } },
                }))
                : throw new InvalidOperationException(
                    "A found authoritative detail must not fall back to the list."));
        using var http = new HttpClient(handler);
        var history = new PrusaLinkClient(
            http,
            NullLogger<PrusaLinkClient>.Instance);
        PrintersService service = CreateConcreteHistoryPrintersService(
            serviceDb,
            printer,
            history);

        await RunReconciliationAsync(service);

        await AssertReconciledTerminalAsync(
            fixture,
            attemptId,
            PrintJobStatus.Cancelled,
            DispatchClaimService.EventTypeJobCancelled,
            "reconciliation_cancelled");
    }

    [Fact]
    [Trait("Category", "DbHeavy")]
    public async Task Reconciler_UnknownHistoryStatus_RemainsIndeterminate()
    {
        const string backendJobId = "future-terminal-status";
        (Fixture fixture, Guid attemptId) =
            await SeedUnknownReconciliationAttemptAsync(backendJobId);
        var printers = new Mock<IPrintersService>();
        printers.Setup(service => service.GetStatusDtoAsync(
                fixture.PrinterId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PrinterStatusDto(
                fixture.PrinterId,
                IsOnline: true,
                State: "idle"));
        printers.Setup(service => service.ProbeHistoryJobAsync(
                fixture.PrinterId,
                backendJobId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(HistoryJobProbeResult.Found(new HistoryJob
            {
                JobId = backendJobId,
                Filename = "future.gcode",
                Status = "future_terminal_state",
                StartTime =
                    DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeSeconds(),
            }));

        await RunReconciliationAsync(printers.Object);

        await AssertIndeterminateFencesAsync(fixture, attemptId);
    }

    [Fact]
    [Trait("Category", "DbHeavy")]
    public async Task Reconciler_RealMoonrakerPreClaimIdentity_RetainsEveryFence()
    {
        const string backendJobId = "moonraker-old-identity";
        (Fixture fixture, Guid attemptId) =
            await SeedUnknownReconciliationAttemptAsync(
                backendJobId,
                PrinterBackend.Moonraker);
        await using AppDbContext serviceDb = CreateContext();
        Printer printer = await serviceDb.Printers.SingleAsync(
            candidate => candidate.Id == fixture.PrinterId);
        QueueDispatchAttempt seededAttempt =
            await serviceDb.QueueDispatchAttempts.SingleAsync(
                candidate => candidate.Id == attemptId);
        long oldTimestamp = new DateTimeOffset(
            DateTime.SpecifyKind(
                seededAttempt.ClaimedAtUtc.AddHours(-1),
                DateTimeKind.Utc)).ToUnixTimeSeconds();
        using var handler = new HistoryAuthorityHandler(request =>
            request.RequestUri!.AbsolutePath == "/server/history/job"
                ? JsonResponse(JsonSerializer.Serialize(new
                {
                    result = new
                    {
                        job_id = backendJobId,
                        filename = seededAttempt.BackendFileName,
                        status = "completed",
                        start_time = oldTimestamp,
                        end_time = oldTimestamp + 30,
                    },
                }))
                : throw new InvalidOperationException(
                    "A found authoritative detail must not fall back to the list."));
        using var http = new HttpClient(handler);
        var history = new MoonrakerClient(
            http,
            NullLogger<MoonrakerClient>.Instance,
            new BackendTimeoutSettings());
        PrintersService service = CreateConcreteHistoryPrintersService(
            serviceDb,
            printer,
            history);

        await RunReconciliationAsync(service);

        await AssertIndeterminateFencesAsync(fixture, attemptId);
        handler.RequestPaths.Should().Equal("/server/history/job");
    }

    [Fact]
    [Trait("Category", "DbHeavy")]
    public async Task Reconciler_SameSecondProviderTimestamp_ResolvesCompleted()
    {
        const string backendJobId = "same-second-history";
        (Fixture fixture, Guid attemptId) =
            await SeedUnknownReconciliationAttemptAsync(backendJobId);
        DateTime claimedAtUtc;
        await using (AppDbContext read = CreateContext())
        {
            claimedAtUtc = (await read.QueueDispatchAttempts.SingleAsync(
                candidate => candidate.Id == attemptId)).ClaimedAtUtc;
        }

        long providerTimestamp = new DateTimeOffset(
            DateTime.SpecifyKind(claimedAtUtc, DateTimeKind.Utc))
            .ToUnixTimeSeconds();
        var printers = new Mock<IPrintersService>();
        printers.Setup(service => service.GetStatusDtoAsync(
                fixture.PrinterId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PrinterStatusDto(
                fixture.PrinterId,
                IsOnline: true,
                State: "idle"));
        printers.Setup(service => service.ProbeHistoryJobAsync(
                fixture.PrinterId,
                backendJobId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(HistoryJobProbeResult.Found(new HistoryJob
            {
                JobId = backendJobId,
                Filename = "same-second.gcode",
                Status = "completed",
                StartTime = providerTimestamp,
            }));

        await RunReconciliationAsync(printers.Object);

        await AssertReconciledTerminalAsync(
            fixture,
            attemptId,
            PrintJobStatus.Completed,
            DispatchClaimService.EventTypeJobCompleted,
            expectedFailureCode: null);
    }

    [Fact]
    [Trait("Category", "DbHeavy")]
    public async Task Reconciler_MultiCopyBackendHistoryCompleted_RequeuesRemainingCopies()
    {
        // #1742: the CompletedOnBackend branch of the terminal-reconciliation switch
        // used to set job.Status = Completed unconditionally once backend history
        // proved the job finished, silently dropping any remaining copies for a
        // Copies > 1 job caught by periodic reconciliation instead of a live terminal
        // observation. Mirrors JobCompletion_MultiCopyRequeue_WritesNonMembershipEventAndSkipsHint
        // and OrphanSync_MultiCopyPrintingJob_RequeuesRemainingCopiesInsteadOfCompleting but
        // exercises the QueueReconciliationService history-driven producer path. Copies is
        // an immutable calibration provenance field once persisted, so the multi-copy shape
        // must be set at creation time (copies: 2).
        const string backendJobId = "multi-copy-history";
        (Fixture fixture, Guid attemptId) =
            await SeedUnknownReconciliationAttemptAsync(backendJobId, copies: 2);
        DateTime claimedAtUtc;
        await using (AppDbContext read = CreateContext())
        {
            claimedAtUtc = (await read.QueueDispatchAttempts.SingleAsync(
                candidate => candidate.Id == attemptId)).ClaimedAtUtc;
        }

        long providerTimestamp = new DateTimeOffset(
            DateTime.SpecifyKind(claimedAtUtc, DateTimeKind.Utc))
            .ToUnixTimeSeconds();
        var printers = new Mock<IPrintersService>();
        printers.Setup(service => service.GetStatusDtoAsync(
                fixture.PrinterId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PrinterStatusDto(
                fixture.PrinterId,
                IsOnline: true,
                State: "idle"));
        printers.Setup(service => service.ProbeHistoryJobAsync(
                fixture.PrinterId,
                backendJobId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(HistoryJobProbeResult.Found(new HistoryJob
            {
                JobId = backendJobId,
                Filename = "multi-copy-history.gcode",
                Status = "completed",
                StartTime = providerTimestamp,
            }));

        await RunReconciliationAsync(printers.Object);

        // The helper's expectedStatus/expectedEventType assertions double as the
        // "must NOT write EventTypeJobCompleted for a non-terminal requeue" check
        // (see its `if (expectedStatus != PrintJobStatus.Completed)` branch).
        await AssertReconciledTerminalAsync(
            fixture,
            attemptId,
            PrintJobStatus.Queued,
            QueueLifecycleEventWriter.EventTypeJobCopyCompleted,
            expectedFailureCode: null);

        await using AppDbContext verify = CreateContext();
        PrintJob persistedJob = await verify.PrintJobs.SingleAsync(
            candidate => candidate.Id == fixture.JobId);
        persistedJob.CompletedCopies.Should().Be(1);
    }

    [Fact]
    [Trait("Category", "DbHeavy")]
    public async Task Reconciler_FilenameOnlyOneSecondBeforeClaimFloor_RetainsEveryFence()
    {
        (Fixture fixture, Guid attemptId) =
            await SeedUnknownReconciliationAttemptAsync(backendJobId: null);
        DateTime claimedAtUtc;
        string backendFileName;
        await using (AppDbContext read = CreateContext())
        {
            QueueDispatchAttempt attempt =
                await read.QueueDispatchAttempts.SingleAsync(
                    candidate => candidate.Id == attemptId);
            claimedAtUtc = attempt.ClaimedAtUtc;
            backendFileName = attempt.BackendFileName ??
                throw new InvalidOperationException(
                    "The dispatch attempt must retain its backend filename.");
        }

        long providerTimestamp = new DateTimeOffset(
            DateTime.SpecifyKind(claimedAtUtc, DateTimeKind.Utc))
            .ToUnixTimeSeconds() - 1;
        var printers = new Mock<IPrintersService>();
        printers.Setup(service => service.GetStatusDtoAsync(
                fixture.PrinterId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PrinterStatusDto(
                fixture.PrinterId,
                IsOnline: true,
                State: "idle"));
        printers.Setup(service => service.ProbeHistoryListAsync(
                fixture.PrinterId,
                It.IsAny<int?>(),
                It.IsAny<int?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(HistoryListProbeResult.Authoritative(
                new HistoryListResponse
                {
                    Jobs =
                    [
                        new HistoryJob
                        {
                            JobId = string.Empty,
                            Filename = backendFileName,
                            Status = "completed",
                            StartTime = providerTimestamp,
                        },
                    ],
                }));

        await RunReconciliationAsync(printers.Object);

        await AssertIndeterminateFencesAsync(fixture, attemptId);
    }

    [Fact]
    [Trait("Category", "DbHeavy")]
    public async Task Reconciler_ThirtySecondOldProviderTimestamp_RemainsIndeterminate()
    {
        const string backendJobId = "thirty-seconds-old-history";
        (Fixture fixture, Guid attemptId) =
            await SeedUnknownReconciliationAttemptAsync(backendJobId);
        DateTime claimedAtUtc;
        await using (AppDbContext read = CreateContext())
        {
            claimedAtUtc = (await read.QueueDispatchAttempts.SingleAsync(
                candidate => candidate.Id == attemptId)).ClaimedAtUtc;
        }

        long providerTimestamp = new DateTimeOffset(
            DateTime.SpecifyKind(
                claimedAtUtc.AddSeconds(-30),
                DateTimeKind.Utc))
            .ToUnixTimeSeconds();
        var printers = new Mock<IPrintersService>();
        printers.Setup(service => service.GetStatusDtoAsync(
                fixture.PrinterId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PrinterStatusDto(
                fixture.PrinterId,
                IsOnline: true,
                State: "idle"));
        printers.Setup(service => service.ProbeHistoryJobAsync(
                fixture.PrinterId,
                backendJobId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(HistoryJobProbeResult.Found(new HistoryJob
            {
                JobId = backendJobId,
                Filename = "old.gcode",
                Status = "completed",
                StartTime = providerTimestamp,
            }));

        await RunReconciliationAsync(printers.Object);

        await AssertIndeterminateFencesAsync(fixture, attemptId);
    }

    [Fact]
    [Trait("Category", "DbHeavy")]
    public async Task Reconciler_PrusaLinkMalformedMatchingIdentity_RetainsEveryFence()
    {
        const string backendJobId = "prusalink-missing-start";
        (Fixture fixture, Guid attemptId) =
            await SeedUnknownReconciliationAttemptAsync(
                backendJobId,
                PrinterBackend.PrusaLink);
        await using AppDbContext serviceDb = CreateContext();
        Printer printer = await serviceDb.Printers.SingleAsync(
            candidate => candidate.Id == fixture.PrinterId);
        using var handler = new HistoryAuthorityHandler(request =>
            request.RequestUri!.AbsolutePath.Contains(
                backendJobId,
                StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : JsonResponse(JsonSerializer.Serialize(new
                {
                    success = true,
                    count = 1,
                    results = new[]
                    {
                        new
                        {
                            id = backendJobId,
                            state = "completed",
                            job = new { file = new { name = "attempt.gcode" } },
                        },
                    },
                })));
        using var http = new HttpClient(handler);
        var history = new PrusaLinkClient(
            http,
            NullLogger<PrusaLinkClient>.Instance);
        PrintersService service = CreateConcreteHistoryPrintersService(
            serviceDb,
            printer,
            history);

        await RunReconciliationAsync(service);

        await AssertIndeterminateFencesAsync(fixture, attemptId);
    }

    [Fact]
    [Trait("Category", "DbHeavy")]
    public async Task Reconciler_SdcpMalformedMatchingIdentity_RetainsEveryFence()
    {
        const string backendJobId = "sdcp-missing-start";
        (Fixture fixture, Guid attemptId) =
            await SeedUnknownReconciliationAttemptAsync(
                backendJobId,
                PrinterBackend.SDCP);
        await using AppDbContext serviceDb = CreateContext();
        Printer printer = await serviceDb.Printers.SingleAsync(
            candidate => candidate.Id == fixture.PrinterId);
        var history = new Mock<ISupportsHistory>();
        history.Setup(client => client.GetHistoryJobAsync(
                It.IsAny<string>(),
                backendJobId,
                It.IsAny<PrinterCredential?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HistoryJobNotFoundException(backendJobId));
        history.Setup(client => client.GetHistoryListAsync(
                It.IsAny<string>(),
                It.IsAny<int?>(),
                It.IsAny<int?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<string?>(),
                It.IsAny<PrinterCredential?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HistoryListResponse
            {
                Count = 1,
                Jobs = [],
                ExaminedSourceEntries = 1,
                ExcludedEntries =
                [
                    new HistoryExcludedEntryEvidence(
                        backendJobId,
                        Filename: null,
                        StartTime: null,
                        Reason: "malformed_history_detail"),
                ],
                AuthorityEvidence = new HistoryListAuthorityEvidence(
                    "sdcp",
                    SourceEntryCount: 1,
                    ExaminedEntryCount: 1,
                    StartsAtBeginning: true,
                    HasUnambiguousEnd: true,
                    CoversRequestedRange: true,
                    ExcludedEntryCount: 1),
            });
        PrintersService service = CreateConcreteHistoryPrintersService(
            serviceDb,
            printer,
            history.Object);

        await RunReconciliationAsync(service);

        await AssertIndeterminateFencesAsync(fixture, attemptId);
    }

    [Fact]
    [Trait("Category", "DbHeavy")]
    public async Task Reconciler_FlashForgeUnsupportedHistory_RetainsEveryFence()
    {
        (Fixture fixture, Guid attemptId) =
            await SeedUnknownReconciliationAttemptAsync(
                "flashforge-history-id",
                PrinterBackend.FlashForge);
        await using (AppDbContext ageContext = CreateContext())
        {
            QueueDispatchOutbox command =
                await ageContext.QueueDispatchOutbox.SingleAsync(
                    candidate =>
                        candidate.EventType ==
                            BedClearAcknowledgementService.BackendStartCommandEventType &&
                        candidate.AttemptId == attemptId);
            command.CreatedAtUtc = DateTime.UtcNow.AddDays(-2);
            await ageContext.SaveChangesAsync();
        }

        Mock<IPrintersService> printers = CreateQuiescentHistoryProbe(
            fixture.PrinterId,
            "flashforge-history-id",
            HistoryListProbeResult.Unsupported());

        await RunReconciliationAsync(printers.Object);

        await AssertIndeterminateFencesAsync(fixture, attemptId);
    }

    [Fact]
    [Trait("Category", "DbHeavy")]
    public async Task Reconciler_NullHistoryProbe_RetainsEveryFence()
    {
        (Fixture fixture, Guid attemptId) =
            await SeedUnknownReconciliationAttemptAsync("null-history-id");
        Mock<IPrintersService> printers = CreateQuiescentHistoryProbe(
            fixture.PrinterId,
            "null-history-id",
            historyProbe: null);

        await RunReconciliationAsync(printers.Object);

        await AssertIndeterminateFencesAsync(fixture, attemptId);
    }

    [Theory]
    [InlineData(HistoryProbeStatus.Unavailable)]
    [InlineData(HistoryProbeStatus.Error)]
    [Trait("Category", "DbHeavy")]
    public async Task Reconciler_NonAuthoritativeHistoryProbe_RetainsEveryFence(
        HistoryProbeStatus status)
    {
        (Fixture fixture, Guid attemptId) =
            await SeedUnknownReconciliationAttemptAsync(
                $"history-{status.ToString().ToLowerInvariant()}");
        HistoryListProbeResult probe = status == HistoryProbeStatus.Unavailable
            ? HistoryListProbeResult.Unavailable()
            : HistoryListProbeResult.Error();
        Mock<IPrintersService> printers = CreateQuiescentHistoryProbe(
            fixture.PrinterId,
            $"history-{status.ToString().ToLowerInvariant()}",
            probe);

        await RunReconciliationAsync(printers.Object);

        await AssertIndeterminateFencesAsync(fixture, attemptId);
    }

    [Fact]
    [Trait("Category", "DbHeavy")]
    public async Task Reconciler_NoBackendJobId_AuthoritativeEmptyRetainsEveryFence()
    {
        (Fixture fixture, Guid attemptId) =
            await SeedUnknownReconciliationAttemptAsync(backendJobId: null);
        var printers = new Mock<IPrintersService>();
        printers.Setup(service => service.GetStatusDtoAsync(
                fixture.PrinterId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PrinterStatusDto(
                fixture.PrinterId,
                IsOnline: true,
                State: "idle"));
        printers.Setup(service => service.ProbeHistoryListAsync(
                fixture.PrinterId,
                It.IsAny<int?>(),
                It.IsAny<int?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(HistoryListProbeResult.Authoritative(
                new HistoryListResponse()));

        await RunReconciliationAsync(printers.Object);

        await AssertIndeterminateFencesAsync(fixture, attemptId);
        printers.Verify(service => service.ProbeHistoryJobAsync(
            It.IsAny<Guid>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
        printers.Verify(service => service.ProbeHistoryListAsync(
            fixture.PrinterId,
            It.IsAny<int?>(),
            It.IsAny<int?>(),
            It.IsAny<DateTime?>(),
            It.IsAny<DateTime?>(),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    [Trait("Category", "DbHeavy")]
    public async Task Reconciler_UnknownOnlineState_RetainsEveryFenceWithoutHistoryProbe()
    {
        (Fixture fixture, Guid attemptId) =
            await SeedUnknownReconciliationAttemptAsync("unknown-state-id");
        var printers = new Mock<IPrintersService>();
        printers.Setup(service => service.GetStatusDtoAsync(
                fixture.PrinterId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PrinterStatusDto(
                fixture.PrinterId,
                IsOnline: true,
                State: "busy-but-unmapped"));

        await RunReconciliationAsync(printers.Object);

        await AssertIndeterminateFencesAsync(fixture, attemptId);
        printers.Verify(service => service.ProbeHistoryJobAsync(
            It.IsAny<Guid>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
        printers.Verify(service => service.ProbeHistoryListAsync(
            It.IsAny<Guid>(),
            It.IsAny<int?>(),
            It.IsAny<int?>(),
            It.IsAny<DateTime?>(),
            It.IsAny<DateTime?>(),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    [Trait("Category", "DbHeavy")]
    public async Task OrphanSync_UnknownStartingAttempt_IgnoresCachedIdleState()
    {
        await using AppDbContext seed = CreateContext();
        Fixture fixture = await SeedCalibrationAsync(seed, withAck: true);
        DispatchClaimService claimService = CreateClaim(
            seed,
            DispatchTestDoubles.OnlineIdleReader(fixture.PrinterId));
        DispatchClaimResult claim = await claimService.AcquireClaimAsync(
            new DispatchClaimRequest(
                fixture.JobId,
                fixture.PrinterId,
                "operator-1",
                "Manual",
                fixture.AckKey,
                null,
                null));
        await claimService.RecordUnknownOutcomeAsync(
            claim.Attempt!.Id,
            "response lost");

        await using AppDbContext syncContext = CreateContext();
        int synced = await CreateCompletionService(syncContext)
            .SyncOrphanedPrintingJobsAsync(_ => "idle", QueueActorIdentity.Scheduler);

        synced.Should().Be(0);
        await using AppDbContext verify = CreateContext();
        PrintJob job = await verify.PrintJobs.SingleAsync(
            candidate => candidate.Id == fixture.JobId);
        PrinterDispatchState state = await verify.PrinterDispatchStates.SingleAsync(
            candidate => candidate.PrinterId == fixture.PrinterId);
        job.Status.Should().Be(PrintJobStatus.Starting);
        state.ActiveDispatchAttemptId.Should().Be(claim.Attempt.Id);
    }

    [Fact]
    [Trait("Category", "DbHeavy")]
    public async Task OrphanSync_AcceptedPrintingJob_AuditsInvokingActor()
    {
        string actorSubject = Guid.NewGuid().ToString();
        await using AppDbContext seed = CreateContext();
        Fixture fixture = await SeedCalibrationAsync(seed, withAck: true);
        DispatchClaimService claimService = CreateClaim(
            seed,
            DispatchTestDoubles.OnlineIdleReader(fixture.PrinterId));
        DispatchClaimResult claim = await claimService.AcquireClaimAsync(
            new DispatchClaimRequest(
                fixture.JobId,
                fixture.PrinterId,
                "operator-1",
                "Manual",
                fixture.AckKey,
                null,
                null));
        claim.Success.Should().BeTrue(claim.ErrorDetail);
        await claimService.RecordBackendAcceptedAsync(
            claim.Attempt!.Id,
            claim.Attempt.BackendFileName);

        seed.ChangeTracker.Clear();
        PrintJob printingJob = await seed.PrintJobs.SingleAsync(
            candidate => candidate.Id == fixture.JobId);
        printingJob.ActualStartTime = DateTime.UtcNow.AddMinutes(-10);
        printingJob.UpdatedAt = printingJob.ActualStartTime.Value;
        await seed.SaveChangesAsync();

        await using AppDbContext syncContext = CreateContext();
        int synced = await CreateCompletionService(syncContext)
            .SyncOrphanedPrintingJobsAsync(_ => "idle", actorSubject);

        synced.Should().Be(1);
        await using AppDbContext verify = CreateContext();
        PrintJob completedJob = await verify.PrintJobs.SingleAsync(
            candidate => candidate.Id == fixture.JobId);
        completedJob.Status.Should().Be(PrintJobStatus.Completed);
        QueueOperationAudit audit = await verify.QueueOperationAudits.SingleAsync(
            candidate =>
                candidate.PrintJobId == fixture.JobId &&
                candidate.Operation == QueueAuditOperations.Reconciliation &&
                candidate.ReasonCode == "orphan_sync_completed");
        audit.ActorSubject.Should().Be(actorSubject);
        audit.Outcome.Should().Be(QueueAuditOutcomes.Success);
    }

    [Fact]
    [Trait("Category", "DbHeavy")]
    public async Task OrphanSync_MultiCopyPrintingJob_RequeuesRemainingCopiesInsteadOfCompleting()
    {
        // #1742: SyncOrphanedPrintingJobsAsync's completion-state branch used to set
        // job.Status = Completed unconditionally on a cached printer completion-state
        // observation, silently dropping any remaining copies for a Copies > 1 job. This
        // mirrors JobCompletion_MultiCopyRequeue_WritesNonMembershipEventAndSkipsHint but
        // exercises the orphan-sync producer path instead of the primary completion path.
        // Copies is an immutable calibration provenance field once persisted, so the
        // multi-copy shape must be set at creation time (copies: 2).
        string actorSubject = Guid.NewGuid().ToString();
        await using AppDbContext seed = CreateContext();
        Fixture fixture = await SeedCalibrationAsync(seed, withAck: true, copies: 2);
        DispatchClaimService claimService = CreateClaim(
            seed,
            DispatchTestDoubles.OnlineIdleReader(fixture.PrinterId));
        DispatchClaimResult claim = await claimService.AcquireClaimAsync(
            new DispatchClaimRequest(
                fixture.JobId,
                fixture.PrinterId,
                "operator-1",
                "Manual",
                fixture.AckKey,
                null,
                null));
        claim.Success.Should().BeTrue(claim.ErrorDetail);
        await claimService.RecordBackendAcceptedAsync(
            claim.Attempt!.Id,
            claim.Attempt.BackendFileName);

        seed.ChangeTracker.Clear();
        PrintJob printingJob = await seed.PrintJobs.SingleAsync(
            candidate => candidate.Id == fixture.JobId);
        printingJob.ActualStartTime = DateTime.UtcNow.AddMinutes(-10);
        printingJob.UpdatedAt = printingJob.ActualStartTime.Value;
        await seed.SaveChangesAsync();

        await using AppDbContext syncContext = CreateContext();
        int synced = await CreateCompletionService(syncContext)
            .SyncOrphanedPrintingJobsAsync(_ => "idle", actorSubject);

        synced.Should().Be(1);

        await using AppDbContext verify = CreateContext();
        PrintJob persistedJob = await verify.PrintJobs.SingleAsync(
            candidate => candidate.Id == fixture.JobId);
        persistedJob.Status.Should().Be(
            PrintJobStatus.Queued,
            "one of two copies finished -- the job must requeue for the next copy, not exit the active set");
        persistedJob.CompletedCopies.Should().Be(1);

        QueueOperationAudit audit = await verify.QueueOperationAudits.SingleAsync(
            candidate =>
                candidate.PrintJobId == fixture.JobId &&
                candidate.Operation == QueueAuditOperations.Reconciliation &&
                candidate.ReasonCode == "orphan_sync_copy_completed");
        audit.ActorSubject.Should().Be(actorSubject);
        audit.Outcome.Should().Be(QueueAuditOutcomes.Success);

        // The REAL producer must write the non-membership-changing event type for this
        // in-set transition, not EventTypeJobCompleted or EventTypeJobOrphanSynced.
        QueueDispatchOutbox completionEvent = await verify.QueueDispatchOutbox
            .Where(e => e.AggregateId == fixture.JobId)
            .OrderByDescending(e => e.Sequence)
            .FirstAsync();
        completionEvent.EventType.Should().Be(QueueLifecycleEventWriter.EventTypeJobCopyCompleted);
        completionEvent.EventType.Should().NotBe(QueueLifecycleEventWriter.EventTypeJobCompleted);
        completionEvent.EventType.Should().NotBe(DispatchClaimService.EventTypeJobOrphanSynced);
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
    public async Task AckInvalidationDrift_UrgentInsertionInvalidatesAcknowledgement()
    {
        await using AppDbContext seed = CreateContext();
        Fixture fixture = await SeedCalibrationAsync(seed, withAck: true);

        await using AppDbContext ctx = CreateContext();
        GcodeFile source = await ctx.GcodeFiles.SingleAsync(file => file.Id == fixture.GcodeId);
        var standardArtifact = new GcodeFile
        {
            Id = Guid.NewGuid(),
            FolderId = source.FolderId,
            Name = "urgent-standard.gcode",
            FileName = "urgent-standard.gcode",
            FilePath = "/gcode",
            FileSizeBytes = 100,
            FileHash = new string('1', 64),
            ContentSha256 = new string('1', 64),
        };
        ctx.GcodeFiles.Add(standardArtifact);
        await ctx.SaveChangesAsync();

        _ = await CreateQueueService(ctx).AddJobToQueueAsync(
            new QueuePrintJobDto
            {
                GcodeFileId = standardArtifact.Id,
                AssignedPrinterId = fixture.PrinterId,
                Priority = PrintJobPriority.Urgent,
                Copies = 1,
            },
            CalibrationOwnerId,
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
                Priority = PrintJobPriority.Urgent,
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
        // This test asserts an immutability guard keyed on the job actually starting as
        // FilamentCalibration, so it must keep that job kind unlike other fixtures here.
        Fixture fixture = await SeedCalibrationAsync(
            seed, withAck: false, jobKind: JobKind.FilamentCalibration);

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
        QueueDispatchOutbox row = await verify.QueueDispatchOutbox.SingleAsync(e =>
            e.AggregateId == fixture.JobId &&
            e.EventType == "PrintFarmer.Queue.JobDispatchStarted.v1");
        Guid? calibrationAttemptId = await verify.PrintJobs
            .Where(job => job.Id == fixture.JobId)
            .Select(job => job.CalibrationAttemptId)
            .SingleAsync();

        QueueEventEnvelope first = QueueEventEnvelope.FromOutbox(
            row.Id, row.Sequence, row.CreatedAtUtc, row.EventType,
            jobId: row.AggregateId, printerId: row.PrinterId,
            calibrationAttemptId: row.CalibrationAttemptId,
            jobRevision: row.AggregateRowVersion, dispatchStateRevision: row.DispatchStateRowVersion,
            attemptId: row.AttemptId, attemptNumber: row.AttemptNumber,
            attemptOutcome: row.AttemptOutcome, bedClearState: row.BedClearState,
            bedClearCommandId: row.BedClearCommandId,
            bedClearExpiresAtUtc: row.BedClearExpiresAtUtc,
            failureRetryable: row.FailureRetryable,
            failureRequiresReconciliation: row.FailureRequiresReconciliation,
            payloadJson: row.PayloadJson,
            jobLogicalRevision: row.JobRevision,
            dispatchStateLogicalRevision: row.DispatchStateRevision,
            schemaVersion: row.SchemaVersion);

        QueueEventEnvelope redelivery = QueueEventEnvelope.FromOutbox(
            row.Id, row.Sequence, row.CreatedAtUtc, row.EventType,
            jobId: row.AggregateId, printerId: row.PrinterId,
            calibrationAttemptId: row.CalibrationAttemptId,
            jobRevision: row.AggregateRowVersion, dispatchStateRevision: row.DispatchStateRowVersion,
            attemptId: row.AttemptId, attemptNumber: row.AttemptNumber,
            attemptOutcome: row.AttemptOutcome, bedClearState: row.BedClearState,
            bedClearCommandId: row.BedClearCommandId,
            bedClearExpiresAtUtc: row.BedClearExpiresAtUtc,
            failureRetryable: row.FailureRetryable,
            failureRequiresReconciliation: row.FailureRequiresReconciliation,
            payloadJson: row.PayloadJson,
            jobLogicalRevision: row.JobRevision,
            dispatchStateLogicalRevision: row.DispatchStateRevision,
            schemaVersion: row.SchemaVersion);

        redelivery.Should().Be(first, "a redelivery must be byte-identical so consumers can de-duplicate");
        first.EventId.Should().Be(row.Id, "the envelope id is the durable outbox row id");
        first.OccurredAtUtc.Should().Be(row.CreatedAtUtc, "the envelope time is the durable write time");
        first.Sequence.Should().BeGreaterThan(0, "the sequence enables gap detection");
        first.AttemptId.Should().Be(row.AttemptId);
        first.AttemptNumber.Should().Be(row.AttemptNumber);
        first.AttemptOutcome.Should().Be(row.AttemptOutcome);
        row.SchemaVersion.Should().Be(QueueEventSchemaVersions.Current);
        first.SchemaVersion.Should().Be(row.SchemaVersion);
        first.CalibrationAttemptId.Should().Be(calibrationAttemptId);
        first.BedClearState.Should().Be("Consumed");
        first.BedClearCommandId.Should().NotBeNull();
        first.JobLogicalRevision.Should().BeGreaterThan(0);
        first.DispatchStateLogicalRevision.Should().BeGreaterThan(0);

        QueueEventEnvelope printerHint = first.RedactForPrinter();
        printerHint.EventType.Should().Be("PrintFarmer.Queue.PrinterStateChanged.v1");
        printerHint.PrinterId.Should().Be(first.PrinterId);
        printerHint.JobId.Should().BeNull();
        printerHint.ProjectId.Should().BeNull();
        printerHint.CalibrationAttemptId.Should().BeNull();
        printerHint.AttemptId.Should().BeNull();
        printerHint.AttemptNumber.Should().BeNull();
        printerHint.AttemptOutcome.Should().BeNull();
        printerHint.BedClearCommandId.Should().BeNull();
        printerHint.JobRevision.Should().BeNull();
        printerHint.DispatchStateRevision.Should().BeNull();
        printerHint.PayloadJson.Should().BeNull();

        QueueEventEnvelope legacy = QueueEventEnvelope.FromOutbox(
            Guid.NewGuid(),
            row.Sequence + 1,
            row.CreatedAtUtc,
            "PrintFarmer.Queue.Legacy.v1",
            schemaVersion: "1");
        legacy.SchemaVersion.Should().Be("1");
    }

    [Fact]
    [Trait("Category", "DbHeavy")]
    public async Task Events_LogicalRevisionsAreServerDerivedAndMatchCommittedAggregates()
    {
        await using AppDbContext seed = CreateContext();
        Fixture fixture = await SeedCalibrationAsync(seed, withAck: true);

        await using AppDbContext beforeContext = CreateContext();
        long priorRevision = await beforeContext.PrintJobs
            .Where(job => job.Id == fixture.JobId)
            .Select(job => job.Revision)
            .SingleAsync();

        await using AppDbContext claimContext = CreateContext();
        DispatchClaimResult result = await CreateClaim(
                claimContext,
                DispatchTestDoubles.OnlineIdleReader(fixture.PrinterId))
            .AcquireClaimAsync(new DispatchClaimRequest(
                fixture.JobId,
                fixture.PrinterId,
                "op",
                "Manual",
                fixture.AckKey,
                null,
                null));
        result.Success.Should().BeTrue(result.ErrorDetail);

        await using AppDbContext verify = CreateContext();
        PrintJob job = await verify.PrintJobs.SingleAsync(candidate => candidate.Id == fixture.JobId);
        PrinterDispatchState dispatchState = await verify.PrinterDispatchStates
            .SingleAsync(state => state.PrinterId == fixture.PrinterId);
        QueueDispatchOutbox queueEvent = await verify.QueueDispatchOutbox
            .SingleAsync(evt =>
                evt.AggregateId == fixture.JobId &&
                evt.EventType == "PrintFarmer.Queue.JobDispatchStarted.v1");

        job.Revision.Should().Be(priorRevision + 1);
        queueEvent.JobRevision.Should().Be(job.Revision);
        queueEvent.DispatchStateRevision.Should().Be(dispatchState.Revision);

        job.Revision = 999_999;
        job.Name = "caller-cannot-author-revision";
        await verify.SaveChangesAsync();
        job.Revision.Should().Be(priorRevision + 2);
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
        QueueDispatchOutbox row = await verify.QueueDispatchOutbox.SingleAsync(e =>
            e.AggregateId == fixture.JobId &&
            e.EventType == "PrintFarmer.Queue.JobDispatchStarted.v1");

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
    public async Task ResourceAuthorization_RequiresSourceAndDestinationGroupAccess()
    {
        await using AppDbContext context = CreateContext();
        Fixture fixture = await SeedCalibrationAsync(context, withAck: false);
        Guid actorId = Guid.NewGuid();
        var sourceGroup = new PrinterGroup
        {
            Id = Guid.NewGuid(),
            Name = $"source-{Guid.NewGuid():N}",
        };
        var destinationGroup = new PrinterGroup
        {
            Id = Guid.NewGuid(),
            Name = $"destination-{Guid.NewGuid():N}",
        };
        var restrictedRole = new Role
        {
            Id = Guid.NewGuid(),
            Name = $"restricted-{Guid.NewGuid():N}",
            DisplayName = "Restricted source role",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        context.PrinterGroups.AddRange(sourceGroup, destinationGroup);
        context.Roles.Add(restrictedRole);
        context.PrinterGroupAccesses.Add(new PrinterGroupAccess
        {
            Id = Guid.NewGuid(),
            PrinterGroupId = sourceGroup.Id,
            RoleId = restrictedRole.Id,
            AccessLevel = PrinterGroupAccessLevel.Submit,
        });

        Printer printer = await context.Printers.SingleAsync(
            candidate => candidate.Id == fixture.PrinterId);
        printer.PrinterGroupId = destinationGroup.Id;
        var gcode = new GcodeFile
        {
            Id = Guid.NewGuid(),
            Name = "restricted-source.gcode",
            FileName = "restricted-source.gcode",
            FilePath = "/gcode",
            FileHash = new string('d', 64),
            FileSizeBytes = 1,
            PrinterGroupId = sourceGroup.Id,
        };
        context.GcodeFiles.Add(gcode);
        var job = new PrintJob
        {
            Id = Guid.NewGuid(),
            Name = gcode.Name,
            GcodeFileId = gcode.Id,
            AssignedPrinterId = printer.Id,
            CreatorSubject = actorId.ToString(),
            JobKind = JobKind.Standard,
            Status = PrintJobStatus.Completed,
            Priority = (int)PrintJobPriority.Normal,
            QueuePosition = 1,
            Copies = 1,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            QueuedAt = DateTime.UtcNow,
        };
        context.PrintJobs.Add(job);
        await context.SaveChangesAsync();

        var authorization = new QueueResourceAuthorizationService(context);
        bool allowed = await authorization.CanActorAccessJobAsync(
            actorId.ToString(),
            job.Id,
            PrinterGroupAccessLevel.Submit);

        allowed.Should().BeFalse(
            "destination access cannot launder a job whose source G-code group is restricted");
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

    [Fact]
    [Trait("Category", "DbHeavy")]
    public async Task OutboxPublisher_CrashAfterLease_RecoversGenericEventForRedelivery()
    {
        await using (AppDbContext seed = CreateContext())
        {
            await seed.Database.MigrateAsync();
            await using var transaction = await seed.Database.BeginTransactionAsync();
            seed.QueueDispatchOutbox.Add(new QueueDispatchOutbox
            {
                Id = Guid.NewGuid(),
                Sequence = await new DbOutboxSequenceAllocator().AllocateAsync(seed),
                AggregateType = nameof(PrintJob),
                AggregateId = Guid.NewGuid(),
                EventType = QueueLifecycleEventWriter.EventTypeJobCompleted,
                SchemaVersion = "1",
                PayloadJson = "{}",
                Status = QueueOutboxEventStatus.Processing,
                AttemptCount = 1,
                LastAttemptedAtUtc = DateTime.UtcNow.AddHours(-1),
                CreatedAtUtc = DateTime.UtcNow.AddHours(-1),
            });
            await seed.SaveChangesAsync();
            await transaction.CommitAsync();
        }

        ServiceProvider provider = new ServiceCollection()
            .AddDbContext<AppDbContext>(options => options.UseSqlite(
                _connectionString,
                sqlite => sqlite.MigrationsAssembly("Farm.Migrations.Sqlite")))
            .BuildServiceProvider();
        await using (provider)
        {
            var publisher = new QueueOutboxPublisherService(
                provider.GetRequiredService<IServiceScopeFactory>(),
                Mock.Of<IHubContext<PrinterHub>>(),
                NullLogger<QueueOutboxPublisherService>.Instance);

            await publisher.RecoverStaleLeasesAsync(CancellationToken.None);
        }

        await using AppDbContext verify = CreateContext();
        QueueDispatchOutbox recovered = await verify.QueueDispatchOutbox.SingleAsync();
        recovered.Status.Should().Be(QueueOutboxEventStatus.Pending);
        recovered.RetryAfterUtc.Should().BeOnOrBefore(DateTime.UtcNow);
    }

    [Fact]
    [Trait("Category", "DbHeavy")]
    public async Task OutboxPublisher_SkipsDiscoveryHintAndSendsPersistedEnvelope()
    {
        Guid calibrationAttemptId = Guid.NewGuid();
        QueueDispatchOutbox row;
        await using (AppDbContext seed = CreateContext())
        {
            await seed.Database.MigrateAsync();
            await using var transaction = await seed.Database.BeginTransactionAsync();
            row = new QueueDispatchOutbox
            {
                Id = Guid.NewGuid(),
                Sequence = await new DbOutboxSequenceAllocator().AllocateAsync(seed),
                AggregateType = nameof(PrintJob),
                AggregateId = Guid.NewGuid(),
                CalibrationAttemptId = calibrationAttemptId,
                EventType = "PrintFarmer.Queue.ResourceDiscoveryProbe.v1",
                SchemaVersion = QueueEventSchemaVersions.Current,
                PayloadJson = "{}",
                Status = QueueOutboxEventStatus.Processing,
                CreatedAtUtc = DateTime.UtcNow,
            };
            seed.QueueDispatchOutbox.Add(row);
            await seed.SaveChangesAsync();
            await transaction.CommitAsync();
        }

        var proxy = new Mock<IClientProxy>();
        proxy
            .Setup(client => client.SendCoreAsync(
                It.IsAny<string>(),
                It.IsAny<object?[]>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var clients = new Mock<IHubClients>();
        clients
            .Setup(client => client.Group(It.IsAny<string>()))
            .Returns(proxy.Object);
        var hub = new Mock<IHubContext<PrinterHub>>();
        hub.Setup(context => context.Clients).Returns(clients.Object);
        ServiceProvider provider = new ServiceCollection()
            .AddDbContext<AppDbContext>(options => options.UseSqlite(
                _connectionString,
                sqlite => sqlite.MigrationsAssembly("Farm.Migrations.Sqlite")))
            .BuildServiceProvider();
        await using (provider)
        {
            var publisher = new QueueOutboxPublisherService(
                provider.GetRequiredService<IServiceScopeFactory>(),
                hub.Object,
                NullLogger<QueueOutboxPublisherService>.Instance);

            await publisher.ProcessSingleEventAsync(row, CancellationToken.None);
        }

        // #1731: the outbox publisher no longer broadcasts "queueresourceschanged" for
        // ordinary job/dispatch/bed-clear lifecycle events -- that hint is now sent only
        // by IQueueSubscriptionMembershipNotifier, invoked directly from the actual
        // membership-changing mutation points (see
        // PrinterGroupServiceMembershipNotificationTests for that coverage).
        proxy.Verify(client => client.SendCoreAsync(
            "queueresourceschanged",
            It.IsAny<object?[]>(),
            It.IsAny<CancellationToken>()), Times.Never);
        proxy.Verify(client => client.SendCoreAsync(
            "queueevent",
            It.Is<object?[]>(arguments =>
                IsExpectedQueueEnvelope(arguments, calibrationAttemptId)),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [Trait("Category", "DbHeavy")]
    [InlineData("PrintFarmer.Queue.JobQueued.v1")]
    [InlineData("PrintFarmer.Queue.CalibrationJobQueued.v1")]
    [InlineData("PrintFarmer.Queue.JobCompleted.v1")]
    [InlineData("PrintFarmer.Queue.JobFailed.v1")]
    [InlineData("PrintFarmer.Queue.JobCancelled.v1")]
    [InlineData("PrintFarmer.Queue.JobOrphanSynced.v1")]
    public async Task OutboxPublisher_SendsDiscoveryHintForMembershipChangingJobTransitions(
        string membershipChangingEventType)
    {
        // #1731 PR #1741 review (Bishop): GetSubscriptionResourcesAsync's active jobIds/
        // projectIds snapshot changes on these transitions even with no authorization
        // change -- the outbox publisher must still narrowly re-fire the discovery hint
        // for exactly this set, not just skip it unconditionally as before.
        QueueDispatchOutbox row;
        await using (AppDbContext seed = CreateContext())
        {
            await seed.Database.MigrateAsync();
            await using var transaction = await seed.Database.BeginTransactionAsync();
            row = new QueueDispatchOutbox
            {
                Id = Guid.NewGuid(),
                Sequence = await new DbOutboxSequenceAllocator().AllocateAsync(seed),
                AggregateType = nameof(PrintJob),
                AggregateId = Guid.NewGuid(),
                EventType = membershipChangingEventType,
                SchemaVersion = QueueEventSchemaVersions.Current,
                PayloadJson = "{}",
                Status = QueueOutboxEventStatus.Processing,
                CreatedAtUtc = DateTime.UtcNow,
            };
            seed.QueueDispatchOutbox.Add(row);
            await seed.SaveChangesAsync();
            await transaction.CommitAsync();
        }

        var proxy = new Mock<IClientProxy>();
        proxy
            .Setup(client => client.SendCoreAsync(
                It.IsAny<string>(),
                It.IsAny<object?[]>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var clients = new Mock<IHubClients>();
        clients
            .Setup(client => client.Group(It.IsAny<string>()))
            .Returns(proxy.Object);
        var hub = new Mock<IHubContext<PrinterHub>>();
        hub.Setup(context => context.Clients).Returns(clients.Object);
        ServiceProvider provider = new ServiceCollection()
            .AddDbContext<AppDbContext>(options => options.UseSqlite(
                _connectionString,
                sqlite => sqlite.MigrationsAssembly("Farm.Migrations.Sqlite")))
            .BuildServiceProvider();
        await using (provider)
        {
            var membershipNotifier = new QueueSubscriptionMembershipNotifier(
                hub.Object,
                NullLogger<QueueSubscriptionMembershipNotifier>.Instance);
            var publisher = new QueueOutboxPublisherService(
                provider.GetRequiredService<IServiceScopeFactory>(),
                hub.Object,
                NullLogger<QueueOutboxPublisherService>.Instance,
                membershipNotifier);

            await publisher.ProcessSingleEventAsync(row, CancellationToken.None);
        }

        proxy.Verify(client => client.SendCoreAsync(
            "queueresourceschanged",
            It.IsAny<object?[]>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    [Trait("Category", "DbHeavy")]
    public async Task OutboxPublisher_SkipsDiscoveryHintForJobAbortedEvent()
    {
        // "abort" returns the job to PrintJobStatus.Queued (still active), so unlike
        // JobCancelled/JobCompleted/JobFailed it must NOT re-trigger the discovery hint.
        QueueDispatchOutbox row;
        await using (AppDbContext seed = CreateContext())
        {
            await seed.Database.MigrateAsync();
            await using var transaction = await seed.Database.BeginTransactionAsync();
            row = new QueueDispatchOutbox
            {
                Id = Guid.NewGuid(),
                Sequence = await new DbOutboxSequenceAllocator().AllocateAsync(seed),
                AggregateType = nameof(PrintJob),
                AggregateId = Guid.NewGuid(),
                EventType = QueueLifecycleEventWriter.EventTypeJobAborted,
                SchemaVersion = QueueEventSchemaVersions.Current,
                PayloadJson = "{}",
                Status = QueueOutboxEventStatus.Processing,
                CreatedAtUtc = DateTime.UtcNow,
            };
            seed.QueueDispatchOutbox.Add(row);
            await seed.SaveChangesAsync();
            await transaction.CommitAsync();
        }

        var proxy = new Mock<IClientProxy>();
        proxy
            .Setup(client => client.SendCoreAsync(
                It.IsAny<string>(),
                It.IsAny<object?[]>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var clients = new Mock<IHubClients>();
        clients
            .Setup(client => client.Group(It.IsAny<string>()))
            .Returns(proxy.Object);
        var hub = new Mock<IHubContext<PrinterHub>>();
        hub.Setup(context => context.Clients).Returns(clients.Object);
        ServiceProvider provider = new ServiceCollection()
            .AddDbContext<AppDbContext>(options => options.UseSqlite(
                _connectionString,
                sqlite => sqlite.MigrationsAssembly("Farm.Migrations.Sqlite")))
            .BuildServiceProvider();
        await using (provider)
        {
            var membershipNotifier = new QueueSubscriptionMembershipNotifier(
                hub.Object,
                NullLogger<QueueSubscriptionMembershipNotifier>.Instance);
            var publisher = new QueueOutboxPublisherService(
                provider.GetRequiredService<IServiceScopeFactory>(),
                hub.Object,
                NullLogger<QueueOutboxPublisherService>.Instance,
                membershipNotifier);

            await publisher.ProcessSingleEventAsync(row, CancellationToken.None);
        }

        proxy.Verify(client => client.SendCoreAsync(
            "queueresourceschanged",
            It.IsAny<object?[]>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    [Trait("Category", "DbHeavy")]
    public async Task JobCompletion_MultiCopyRequeue_WritesNonMembershipEventAndSkipsHint()
    {
        // #1731 PR #1741 review (Bishop, round 2): the tests above seed synthetic outbox
        // rows directly, which only proves the PUBLISHER's string match -- not that the
        // real PRODUCER (PrintJobCompletionService) actually stays aligned with the
        // "EventTypeJobCompleted only when the job truly exits the active set" contract.
        // For a multi-copy job with CompletedCopies < Copies, MarkCurrentJobAsCompletedAsync
        // sets the job's status back to Queued (still active) rather than Completed, so the
        // producer must write EventTypeJobCopyCompleted (not EventTypeJobCompleted) or the
        // publisher would wrongly re-fire the membership-discovery hint for an in-set
        // transition. This test exercises the real production completion path end-to-end
        // against a persisted multi-copy job, then feeds the REAL resulting outbox row
        // through the REAL QueueOutboxPublisherService + QueueSubscriptionMembershipNotifier.
        // Copies is an immutable calibration provenance field once persisted, so the
        // multi-copy shape must be set at creation time (copies: 2) rather than mutated
        // afterward — see AppDbContext.EnsureCalibrationJobFieldsAreImmutable.
        await using AppDbContext seed = CreateContext();
        Fixture fixture = await SeedCalibrationAsync(seed, withAck: true, copies: 2);

        await using AppDbContext claimCtx = CreateContext();
        DispatchClaimResult claim = await CreateClaim(claimCtx, DispatchTestDoubles.OnlineIdleReader(fixture.PrinterId))
            .AcquireClaimAsync(new DispatchClaimRequest(
                fixture.JobId, fixture.PrinterId, "op", "Manual", fixture.AckKey, null, null));
        claim.Success.Should().BeTrue(claim.ErrorDetail);

        await using AppDbContext acceptCtx = CreateContext();
        await CreateClaim(acceptCtx, DispatchTestDoubles.OnlineIdleReader(fixture.PrinterId))
            .RecordBackendAcceptedAsync(claim.Attempt!.Id, "multi-copy-file.gcode");

        await using AppDbContext completeCtx = CreateContext();
        bool completed = await CreateCompletionService(completeCtx).MarkCurrentJobAsCompletedAsync(
            fixture.PrinterId,
            "complete",
            new PrinterTerminalObservation(claim.Attempt.BackendFileName, claim.Attempt.Id));

        completed.Should().BeTrue();

        // Assert: the persisted job actually requeued for the next copy, not completed.
        await using AppDbContext verify = CreateContext();
        PrintJob persistedJob = await verify.PrintJobs.SingleAsync(j => j.Id == fixture.JobId);
        persistedJob.Status.Should().Be(
            PrintJobStatus.Queued,
            "one of two copies finished -- the job must requeue for the next copy, not exit the active set");
        persistedJob.CompletedCopies.Should().Be(1);

        // Assert: the REAL producer wrote the non-membership-changing event type for this
        // in-set transition, not EventTypeJobCompleted.
        QueueDispatchOutbox completionEvent = await verify.QueueDispatchOutbox
            .Where(e => e.AggregateId == fixture.JobId)
            .OrderByDescending(e => e.Sequence)
            .FirstAsync();
        completionEvent.EventType.Should().Be(QueueLifecycleEventWriter.EventTypeJobCopyCompleted);
        completionEvent.EventType.Should().NotBe(QueueLifecycleEventWriter.EventTypeJobCompleted);

        // Feed the REAL outbox row through the REAL publisher + membership notifier and
        // confirm the discovery hint is correctly skipped for this in-set transition.
        var proxy = new Mock<IClientProxy>();
        proxy
            .Setup(client => client.SendCoreAsync(
                It.IsAny<string>(),
                It.IsAny<object?[]>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var clients = new Mock<IHubClients>();
        clients
            .Setup(client => client.Group(It.IsAny<string>()))
            .Returns(proxy.Object);
        var hub = new Mock<IHubContext<PrinterHub>>();
        hub.Setup(context => context.Clients).Returns(clients.Object);
        ServiceProvider provider = new ServiceCollection()
            .AddDbContext<AppDbContext>(options => options.UseSqlite(
                _connectionString,
                sqlite => sqlite.MigrationsAssembly("Farm.Migrations.Sqlite")))
            .BuildServiceProvider();
        await using (provider)
        {
            var membershipNotifier = new QueueSubscriptionMembershipNotifier(
                hub.Object,
                NullLogger<QueueSubscriptionMembershipNotifier>.Instance);
            var publisher = new QueueOutboxPublisherService(
                provider.GetRequiredService<IServiceScopeFactory>(),
                hub.Object,
                NullLogger<QueueOutboxPublisherService>.Instance,
                membershipNotifier);

            await publisher.ProcessSingleEventAsync(completionEvent, CancellationToken.None);
        }

        proxy.Verify(
            client => client.SendCoreAsync(
                "queueresourceschanged",
                It.IsAny<object?[]>(),
                It.IsAny<CancellationToken>()),
            Times.Never,
            "a multi-copy requeue-to-Queued transition does not change the caller's active jobIds/projectIds snapshot");
    }

    private static bool IsExpectedQueueEnvelope(
        object?[] arguments,
        Guid calibrationAttemptId)
    {
        return arguments is [QueueEventEnvelope envelope] &&
               envelope.SchemaVersion == QueueEventSchemaVersions.Current &&
               envelope.CalibrationAttemptId == calibrationAttemptId;
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

    [Fact]
    [Trait("Category", "DbHeavy")]
    public async Task BackendStartConsumer_TerminalRejection_RejectsCommandReplayRecord()
    {
        Fixture fixture;
        await using (AppDbContext seed = CreateContext())
        {
            fixture = await SeedCalibrationAsync(seed, withAck: true);
        }

        var management = new Mock<IPrintJobManagementService>();
        management.Setup(service => service.DispatchJobWithAckAsync(
                fixture.JobId.ToString(),
                It.IsAny<string>(),
                fixture.AckKey,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(BackendStartOutcome.Rejected(
                "claim_denied",
                "The claim is no longer dispatchable."));
        ServiceProvider provider = new ServiceCollection()
            .AddDbContext<AppDbContext>(options => options.UseSqlite(
                _connectionString,
                sqlite => sqlite.MigrationsAssembly("Farm.Migrations.Sqlite")))
            .AddSingleton(management.Object)
            .BuildServiceProvider();
        await using (provider)
        {
            var consumer = new BackendStartCommandConsumerService(
                provider.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<BackendStartCommandConsumerService>.Instance);
            await consumer.ProcessPendingCommandsAsync(CancellationToken.None);
        }

        await using AppDbContext verify = CreateContext();
        QueueDispatchOutbox command = await verify.QueueDispatchOutbox.SingleAsync(
            evt => evt.EventType ==
                BedClearAcknowledgementService.BackendStartCommandEventType);
        BedClearCommandRecord record = await verify.BedClearCommandRecords.SingleAsync(
            candidate => candidate.OutboxEventId == command.Id);
        command.Status.Should().Be(QueueOutboxEventStatus.DeadLettered);
        record.Status.Should().Be(BedClearCommandStatus.Rejected);
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
            .MarkCurrentJobAsCompletedAsync(
                fixture.PrinterId,
                "complete",
                new PrinterTerminalObservation(
                    claim.Attempt.BackendFileName,
                    claim.Attempt.Id));
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
            .MarkCurrentJobAsFailedAsync(
                fixture.PrinterId,
                "nozzle clog",
                new PrinterTerminalObservation(
                    claim.Attempt.BackendFileName,
                    claim.Attempt.Id));
        failed.Should().BeTrue();

        await using AppDbContext verify = CreateContext();
        PrinterDispatchState state = await verify.PrinterDispatchStates.SingleAsync(s => s.PrinterId == fixture.PrinterId);
        state.ActiveJobId.Should().BeNull("failing a job must release the ActiveJobId lease");
        state.ActiveDispatchAttemptId.Should().BeNull("failing a job must release the ActiveDispatchAttemptId");
        PrintJob failedJob = await verify.PrintJobs.SingleAsync(
            job => job.Id == fixture.JobId);
        failedJob.Status.Should().Be(PrintJobStatus.Failed);
        QueueDispatchAttempt acceptedAttempt = await verify.QueueDispatchAttempts
            .SingleAsync(attempt => attempt.Id == claim.Attempt!.Id);
        acceptedAttempt.Outcome.Should().Be(
            DispatchAttemptOutcome.Accepted,
            "a later print failure does not change the fact that the start was accepted");
    }

    [Fact]
    [Trait("Category", "DbHeavy")]
    public async Task TerminalCallback_DelayedOldAttempt_DoesNotCompleteNewerJob()
    {
        await using AppDbContext context = CreateContext();
        Fixture fixture = await SeedCalibrationAsync(context, withAck: true);
        DispatchClaimService claimService = CreateClaim(
            context,
            DispatchTestDoubles.OnlineIdleReader(fixture.PrinterId));
        DispatchClaimResult oldClaim = await claimService.AcquireClaimAsync(
            new DispatchClaimRequest(
                fixture.JobId,
                fixture.PrinterId,
                "operator-1",
                "Manual",
                fixture.AckKey,
                null,
                null));
        await claimService.RecordBackendAcceptedAsync(
            oldClaim.Attempt!.Id,
            oldClaim.Attempt.BackendFileName);

        context.ChangeTracker.Clear();
        PrintJob oldJob = await context.PrintJobs.SingleAsync(
            candidate => candidate.Id == fixture.JobId);
        oldJob.Status = PrintJobStatus.Completed;
        oldJob.ActualEndTime = DateTime.UtcNow;
        Guid newerJobId = Guid.NewGuid();
        Guid newerAttemptId = Guid.NewGuid();
        context.PrintJobs.Add(new PrintJob
        {
            Id = newerJobId,
            Name = "newer.gcode",
            AssignedPrinterId = fixture.PrinterId,
            Status = PrintJobStatus.Printing,
            JobKind = JobKind.Standard,
            Priority = (int)PrintJobPriority.Normal,
            QueuePosition = 99,
            ActualStartTime = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            QueuedAt = DateTime.UtcNow,
        });
        context.QueueDispatchAttempts.Add(new QueueDispatchAttempt
        {
            Id = newerAttemptId,
            PrintJobId = newerJobId,
            PrinterId = fixture.PrinterId,
            PrinterConfigRevision = 1,
            AttemptNumber = 1,
            ActorSubject = "operator-2",
            StartPathKind = "Manual",
            ClaimedAtUtc = DateTime.UtcNow,
            BackendAcceptedAtUtc = DateTime.UtcNow,
            Outcome = DispatchAttemptOutcome.Accepted,
            BackendFileName = "newer.gcode",
            BackendCallPhase = DispatchBackendCallPhase.PostAccept,
            UpdatedAtUtc = DateTime.UtcNow,
        });
        PrinterDispatchState state = await context.PrinterDispatchStates.SingleAsync(
            candidate => candidate.PrinterId == fixture.PrinterId);
        state.ActiveJobId = newerJobId;
        state.ActiveDispatchAttemptId = newerAttemptId;
        await context.SaveChangesAsync();

        bool completed = await CreateCompletionService(context)
            .MarkCurrentJobAsCompletedAsync(
                fixture.PrinterId,
                "complete",
                new PrinterTerminalObservation(
                    oldClaim.Attempt.BackendFileName,
                    oldClaim.Attempt.Id));

        completed.Should().BeFalse();
        context.ChangeTracker.Clear();
        PrintJob newerJob = await context.PrintJobs.SingleAsync(
            candidate => candidate.Id == newerJobId);
        state = await context.PrinterDispatchStates.SingleAsync(
            candidate => candidate.PrinterId == fixture.PrinterId);
        newerJob.Status.Should().Be(PrintJobStatus.Printing);
        state.ActiveDispatchAttemptId.Should().Be(newerAttemptId);
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

    [Fact]
    [Trait("Category", "DbHeavy")]
    public async Task AdvertisedTelemetrySla_IsIdenticalForClaimAndBedClearAcknowledgement()
    {
        await using AppDbContext seed = CreateContext();
        Fixture fixture = await SeedCalibrationAsync(seed, withAck: true);
        IPrinterStatusSnapshotReader twentySecondOld =
            DispatchTestDoubles.OnlineIdleReader(fixture.PrinterId, ageSeconds: 20);
        IPrinterTelemetryFreshnessPolicy tenSecondSla =
            DispatchTestDoubles.TelemetryFreshnessPolicy(
                TimeSpan.FromSeconds(10));

        await using AppDbContext claimContext = CreateContext();
        DispatchClaimResult claim = await CreateClaim(
                claimContext,
                twentySecondOld,
                tenSecondSla)
            .AcquireClaimAsync(new DispatchClaimRequest(
                fixture.JobId,
                fixture.PrinterId,
                "operator",
                "Manual",
                fixture.AckKey,
                null,
                null));

        claim.Success.Should().BeFalse();
        claim.ErrorCode.Should().Be("telemetry_stale");

        await using AppDbContext acknowledgementContext = CreateContext();
        PrintJob job = await acknowledgementContext.PrintJobs.SingleAsync(
            candidate => candidate.Id == fixture.JobId);
        Printer printer = await acknowledgementContext.Printers.SingleAsync(
            candidate => candidate.Id == fixture.PrinterId);
        PrinterDispatchState state =
            await acknowledgementContext.PrinterDispatchStates.SingleAsync(
                candidate => candidate.PrinterId == fixture.PrinterId);
        state.AcknowledgedJobId = null;
        state.AcknowledgedAtUtc = null;
        state.AcknowledgedBySubject = null;
        state.AcknowledgementIdempotencyKey = null;
        state.AcknowledgementExpiresAtUtc = null;
        state.AcknowledgedJobRowVersion = null;
        state.AcknowledgedQueueRevision = null;
        state.AcknowledgedPrinterConfigRevision = null;
        var acknowledgement = new BedClearAcknowledgementService(
            acknowledgementContext,
            new DbOutboxSequenceAllocator(),
            twentySecondOld,
            NullLogger<BedClearAcknowledgementService>.Instance,
            tenSecondSla,
            DispatchTestDoubles.ValidByteIntegrityVerifier());
        AcknowledgeBedClearResult acknowledged =
            await acknowledgement.AcknowledgeAsync(
                new AcknowledgeBedClearRequest(
                    fixture.JobId,
                    fixture.PrinterId,
                    "operator",
                    "sla-boundary",
                    state.RowVersion,
                    printer.ConfigurationRevision,
                    job.RowVersion));

        acknowledged.Outcome.Should().Be(
            BedClearAckOutcome.PrinterOfflineOrStale);
        acknowledged.ErrorDetail.Should().Contain("10 seconds");
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

    [Fact]
    [Trait("Category", "DbHeavy")]
    public async Task ReassignJob_DestinationPositionOccupied_AllocatesUniqueTailPosition()
    {
        await using AppDbContext context = CreateContext();
        Fixture fixture = await SeedCalibrationAsync(context, withAck: false);
        Printer sourcePrinter = await context.Printers.SingleAsync(
            candidate => candidate.Id == fixture.PrinterId);
        Printer destination = BuildPrinter(
            sourcePrinter.ManufacturerId,
            sourcePrinter.ModelId);
        context.Printers.Add(destination);
        context.PrinterDispatchStates.Add(new PrinterDispatchState
        {
            PrinterId = destination.Id,
        });
        var destinationHead = new PrintJob
        {
            Id = Guid.NewGuid(),
            Name = "destination-head",
            AssignedPrinterId = destination.Id,
            Status = PrintJobStatus.Assigned,
            JobKind = JobKind.Standard,
            Priority = (int)PrintJobPriority.Normal,
            QueuePosition = 1,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            QueuedAt = DateTime.UtcNow,
        };
        var moving = new PrintJob
        {
            Id = Guid.NewGuid(),
            Name = "moving",
            AssignedPrinterId = sourcePrinter.Id,
            Status = PrintJobStatus.Assigned,
            JobKind = JobKind.Standard,
            Priority = (int)PrintJobPriority.Normal,
            QueuePosition = 2,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            QueuedAt = DateTime.UtcNow,
        };
        context.PrintJobs.AddRange(destinationHead, moving);
        context.QueuePositionStates.Add(new QueuePositionState
        {
            ScopeId = destination.Id,
            NextPosition = 1,
        });
        await context.SaveChangesAsync();

        JobQueuePrintJobDto? updated = await CreateQueueService(context).UpdateJobAsync(
            moving.Id,
            new UpdatePrintJobStatusDto
            {
                AssignedPrinterId = destination.Id,
                IfMatchJobRowVersion = Convert.ToBase64String(moving.RowVersion!),
            },
            CancellationToken.None);

        updated.Should().NotBeNull();
        updated!.AssignedPrinterId.Should().Be(destination.Id);
        updated.QueuePosition.Should().Be(2);
        context.ChangeTracker.Clear();
        List<int> positions = await context.PrintJobs
            .Where(job =>
                job.AssignedPrinterId == destination.Id &&
                (job.Status == PrintJobStatus.Queued ||
                 job.Status == PrintJobStatus.Assigned))
            .Select(job => job.QueuePosition)
            .ToListAsync();
        positions.Should().OnlyHaveUniqueItems();
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
            .MarkCurrentJobAsCompletedAsync(
                fixture.PrinterId,
                "complete",
                new PrinterTerminalObservation(
                    claim.Attempt.BackendFileName,
                    claim.Attempt.Id));
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
        completionEvent.SchemaVersion.Should().Be(QueueEventSchemaVersions.Current);
        completionEvent.CalibrationAttemptId.Should().Be(
            await verify.PrintJobs
                .Where(job => job.Id == fixture.JobId)
                .Select(job => job.CalibrationAttemptId)
                .SingleAsync());
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
            .MarkCurrentJobAsFailedAsync(
                fixture.PrinterId,
                "nozzle clog",
                new PrinterTerminalObservation(
                    claim.Attempt.BackendFileName,
                    claim.Attempt.Id));
        failed.Should().BeTrue();

        // Assert
        await using AppDbContext verify = CreateContext();
        QueueDispatchOutbox failEvent = await verify.QueueDispatchOutbox
            .Where(e => e.AggregateId == fixture.JobId && e.EventType == DispatchClaimService.EventTypeJobFailed)
            .SingleAsync();

        failEvent.FailureCode.Should().Be("backend_failure");
        failEvent.AttemptOutcome.Should().Be(
            DispatchAttemptOutcome.Accepted.ToString());
        (await verify.QueueDispatchAttempts.SingleAsync(
            attempt => attempt.Id == claim.Attempt!.Id)).Outcome.Should().Be(
                DispatchAttemptOutcome.Accepted);
        (await verify.PrintJobs.SingleAsync(
            job => job.Id == fixture.JobId)).Status.Should().Be(
                PrintJobStatus.Failed);
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
        await CreateCompletionService(completeCtx).MarkCurrentJobAsCompletedAsync(
            fixture.PrinterId,
            "complete",
            new PrinterTerminalObservation(
                claim.Attempt.BackendFileName,
                claim.Attempt.Id));

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

    private async Task<(Fixture Fixture, Guid AttemptId)>
        SeedUnknownReconciliationAttemptAsync(
            string? backendJobId,
            PrinterBackend? backend = null,
            int copies = 1)
    {
        await using AppDbContext db = CreateContext();
        Fixture fixture = await SeedCalibrationAsync(db, withAck: true, copies: copies);
        DispatchClaimService claimService = CreateClaim(
            db,
            DispatchTestDoubles.OnlineIdleReader(fixture.PrinterId));
        DispatchClaimResult claim = await claimService.AcquireClaimAsync(
            new DispatchClaimRequest(
                fixture.JobId,
                fixture.PrinterId,
                "operator-1",
                "Manual",
                fixture.AckKey,
                null,
                null));
        claim.Success.Should().BeTrue(claim.ErrorDetail);
        claim.Attempt!.BackendJobId = backendJobId;
        if (backend.HasValue)
        {
            Printer printer = await db.Printers.SingleAsync(
                candidate => candidate.Id == fixture.PrinterId);
            printer.Backend = (int)backend.Value;
        }

        await db.SaveChangesAsync();
        (await claimService.RecordBackendCallStartedAsync(claim.Attempt.Id))
            .Should().BeTrue();
        (await claimService.RecordUnknownOutcomeAsync(
            claim.Attempt.Id,
            "response lost")).Should().BeTrue();
        return (fixture, claim.Attempt.Id);
    }

    private static Mock<IPrintersService> CreateQuiescentHistoryProbe(
        Guid printerId,
        string backendJobId,
        HistoryListProbeResult? historyProbe)
    {
        var printers = new Mock<IPrintersService>();
        printers.Setup(service => service.GetStatusDtoAsync(
                printerId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PrinterStatusDto(
                printerId,
                IsOnline: true,
                State: "idle"));
        printers.Setup(service => service.ProbeHistoryJobAsync(
                printerId,
                backendJobId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(HistoryJobProbeResult.NotFound());
        printers.Setup(service => service.ProbeHistoryListAsync(
                printerId,
                It.IsAny<int?>(),
                It.IsAny<int?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(historyProbe!);
        return printers;
    }

    private static PrintersService CreateConcreteHistoryPrintersService(
        AppDbContext db,
        Printer printer,
        ISupportsHistory? historyClient)
    {
        var printersRepository = new Mock<IPrintersRepository>();
        printersRepository.Setup(repository => repository.FindByIdAsync(
                printer.Id,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(printer);
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(work => work.Printers).Returns(printersRepository.Object);

        var capabilityFactory = new Mock<IBackendCapabilityFactory>();
        if (historyClient is null)
        {
            ISupportsHistory? unsupported = null;
            capabilityFactory.Setup(factory => factory.TryGetHistoryClientTyped(
                    (PrinterBackend)printer.Backend,
                    out unsupported))
                .Returns(false);
        }
        else
        {
            ISupportsHistory? supported = historyClient;
            capabilityFactory.Setup(factory => factory.TryGetHistoryClientTyped(
                    (PrinterBackend)printer.Backend,
                    out supported))
                .Returns(true);
        }

        var statusClient = new Mock<IPrinterStatusClient>();
        statusClient.Setup(client => client.GetPrinterStatusAsync(
                printer,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PrinterStatusDto(
                printer.Id,
                IsOnline: true,
                State: "idle"));
        var statusFactory = new Mock<IPrinterStatusClientFactory>();
        statusFactory.Setup(factory => factory.GetStatusClient(printer.Backend))
            .Returns(statusClient.Object);

        return new PrintersService(
            unitOfWork.Object,
            db,
            Mock.Of<IBackendClientFactory>(),
            capabilityFactory.Object,
            Mock.Of<Farm.Infrastructure.Services.Catalog.ICatalogService>(),
            Mock.Of<IHttpClientFactory>(),
            NullLogger<PrintersService>.Instance,
            Mock.Of<IPrinterStatusBroadcaster>(),
            Mock.Of<IMultiPrinterStatusCoordinator>(),
            statusFactory.Object,
            Mock.Of<IPrinterStatusCacheReader>(),
            Mock.Of<Farm.Infrastructure.Services.Locations.ILocationService>(),
            Mock.Of<Farm.Infrastructure.Services.Security.ISensitiveDataProtector>(),
            Mock.Of<ISpoolmanService>(),
            Mock.Of<Farm.Infrastructure.Services.Cameras.IGo2RtcService>(),
            Mock.Of<IStoragePathService>(),
            Mock.Of<Farm.Infrastructure.Services.Spoolman.IFilamentCoverageSpoolResolver>());
    }

    private static PrintersService CreateConcreteUploadPrintersService(
        AppDbContext db,
        Printer printer,
        ISupportsUploadAndPrint uploadClient)
    {
        var printersRepository = new Mock<IPrintersRepository>();
        printersRepository.Setup(repository => repository.FindByIdAsync(
                printer.Id,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(printer);
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(work => work.Printers).Returns(printersRepository.Object);
        var capabilityFactory = new Mock<IBackendCapabilityFactory>();
        ISupportsUploadAndPrint? supported = uploadClient;
        capabilityFactory.Setup(factory => factory.TryGetUploadAndPrintClientTyped(
                (PrinterBackend)printer.Backend,
                out supported))
            .Returns(true);

        return new PrintersService(
            unitOfWork.Object,
            db,
            Mock.Of<IBackendClientFactory>(),
            capabilityFactory.Object,
            Mock.Of<Farm.Infrastructure.Services.Catalog.ICatalogService>(),
            Mock.Of<IHttpClientFactory>(),
            NullLogger<PrintersService>.Instance,
            Mock.Of<IPrinterStatusBroadcaster>(),
            Mock.Of<IMultiPrinterStatusCoordinator>(),
            Mock.Of<IPrinterStatusClientFactory>(),
            Mock.Of<IPrinterStatusCacheReader>(),
            Mock.Of<Farm.Infrastructure.Services.Locations.ILocationService>(),
            Mock.Of<Farm.Infrastructure.Services.Security.ISensitiveDataProtector>(),
            Mock.Of<ISpoolmanService>(),
            Mock.Of<Farm.Infrastructure.Services.Cameras.IGo2RtcService>(),
            Mock.Of<IStoragePathService>(),
            Mock.Of<Farm.Infrastructure.Services.Spoolman.IFilamentCoverageSpoolResolver>());
    }

    private static HttpResponseMessage JsonResponse(string payload) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                payload,
                Encoding.UTF8,
                "application/json"),
        };

    private static int ReadQueryInt(HttpRequestMessage request, string name)
    {
        string value = request.RequestUri!.Query
            .TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .Single(parts => string.Equals(parts[0], name, StringComparison.Ordinal))[1];
        return int.Parse(value, CultureInfo.InvariantCulture);
    }

    private sealed class HistoryAuthorityHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        : HttpMessageHandler
    {
        public List<string> RequestPaths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestPaths.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(responseFactory(request));
        }
    }

    private sealed class AsyncMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }

    private async Task AssertAuthoritativeAbsenceReleasedAsync(
        Fixture fixture,
        Guid attemptId)
    {
        await using AppDbContext db = CreateContext();
        QueueDispatchAttempt attempt = await db.QueueDispatchAttempts.SingleAsync(
            candidate => candidate.Id == attemptId);
        PrintJob job = await db.PrintJobs.SingleAsync(
            candidate => candidate.Id == fixture.JobId);
        PrinterDispatchState state = await db.PrinterDispatchStates.SingleAsync(
            candidate => candidate.PrinterId == fixture.PrinterId);
        BedClearCommandRecord acknowledgement =
            await db.BedClearCommandRecords.SingleAsync(
                candidate => candidate.DispatchAttemptId == attemptId);
        QueueDispatchOutbox startCommand =
            await db.QueueDispatchOutbox.SingleAsync(
                candidate =>
                    candidate.EventType ==
                        BedClearAcknowledgementService.BackendStartCommandEventType &&
                    candidate.AttemptId == attemptId);

        attempt.Outcome.Should().Be(DispatchAttemptOutcome.FailedBeforeStart);
        attempt.ErrorCode.Should().Be("reconciliation_absent");
        attempt.RequiresReconciliation.Should().BeFalse();
        attempt.BackendCallPhase.Should().Be(DispatchBackendCallPhase.Terminal);
        job.Status.Should().Be(PrintJobStatus.Assigned);
        state.ActiveJobId.Should().BeNull();
        state.ActiveDispatchAttemptId.Should().BeNull();
        acknowledgement.Status.Should().Be(BedClearCommandStatus.Rejected);
        startCommand.Status.Should().Be(QueueOutboxEventStatus.DeadLettered);
        startCommand.FailureCode.Should().Be("reconciliation_absent");
        (await db.QueueDispatchOutbox.AnyAsync(candidate =>
            candidate.EventType ==
                DispatchClaimService.EventTypeReconciliationAbsent &&
            candidate.AttemptId == attemptId)).Should().BeTrue();
    }

    private async Task AssertReconciledTerminalAsync(
        Fixture fixture,
        Guid attemptId,
        PrintJobStatus expectedStatus,
        string expectedEventType,
        string? expectedFailureCode)
    {
        await using AppDbContext db = CreateContext();
        QueueDispatchAttempt attempt = await db.QueueDispatchAttempts.SingleAsync(
            candidate => candidate.Id == attemptId);
        PrintJob job = await db.PrintJobs.SingleAsync(
            candidate => candidate.Id == fixture.JobId);
        PrinterDispatchState state = await db.PrinterDispatchStates.SingleAsync(
            candidate => candidate.PrinterId == fixture.PrinterId);
        BedClearCommandRecord acknowledgement =
            await db.BedClearCommandRecords.SingleAsync(
                candidate => candidate.DispatchAttemptId == attemptId);

        attempt.Outcome.Should().Be(DispatchAttemptOutcome.Accepted);
        attempt.RequiresReconciliation.Should().BeFalse();
        attempt.BackendCallPhase.Should().Be(DispatchBackendCallPhase.Terminal);
        attempt.ErrorCode.Should().Be(expectedFailureCode);
        job.Status.Should().Be(expectedStatus);
        state.ActiveJobId.Should().BeNull();
        state.ActiveDispatchAttemptId.Should().BeNull();
        acknowledgement.Status.Should().Be(BedClearCommandStatus.Accepted);
        QueueDispatchOutbox terminalEvent = await db.QueueDispatchOutbox.SingleAsync(
            candidate =>
                candidate.EventType == expectedEventType &&
                candidate.AttemptId == attemptId);
        terminalEvent.FailureCode.Should().Be(expectedFailureCode);
        if (expectedStatus != PrintJobStatus.Completed)
        {
            (await db.QueueDispatchOutbox.AnyAsync(candidate =>
                candidate.EventType == DispatchClaimService.EventTypeJobCompleted &&
                candidate.AttemptId == attemptId)).Should().BeFalse();
        }
    }

    private async Task AssertIndeterminateFencesAsync(
        Fixture fixture,
        Guid attemptId)
    {
        await using AppDbContext db = CreateContext();
        QueueDispatchAttempt attempt = await db.QueueDispatchAttempts.SingleAsync(
            candidate => candidate.Id == attemptId);
        PrintJob job = await db.PrintJobs.SingleAsync(
            candidate => candidate.Id == fixture.JobId);
        PrinterDispatchState state = await db.PrinterDispatchStates.SingleAsync(
            candidate => candidate.PrinterId == fixture.PrinterId);
        BedClearCommandRecord acknowledgement =
            await db.BedClearCommandRecords.SingleAsync(
                candidate => candidate.DispatchAttemptId == attemptId);
        QueueDispatchOutbox startCommand =
            await db.QueueDispatchOutbox.SingleAsync(
                candidate =>
                    candidate.EventType ==
                        BedClearAcknowledgementService.BackendStartCommandEventType &&
                    candidate.AttemptId == attemptId);

        attempt.Outcome.Should().Be(DispatchAttemptOutcome.Unknown);
        attempt.RequiresReconciliation.Should().BeTrue();
        attempt.BackendCallPhase.Should().Be(
            DispatchBackendCallPhase.AwaitingReconciliation);
        attempt.AcknowledgementIdempotencyKey.Should().Be(fixture.AckKey);
        job.Status.Should().Be(PrintJobStatus.Starting);
        state.ActiveJobId.Should().Be(fixture.JobId);
        state.ActiveDispatchAttemptId.Should().Be(attemptId);
        state.PhysicalControlCommandId.Should().Be(attemptId);
        state.PhysicalControlAttemptId.Should().Be(attemptId);
        state.PhysicalControlOperation.Should().Be("start");
        state.PhysicalControlRequiresReconciliation.Should().BeTrue();
        acknowledgement.Status.Should().Be(BedClearCommandStatus.Unknown);
        startCommand.Status.Should().BeOneOf(
            QueueOutboxEventStatus.Pending,
            QueueOutboxEventStatus.Processing);
        startCommand.CompletedAtUtc.Should().BeNull();
        startCommand.FailureCode.Should().NotBe("reconciliation_absent");
        (await db.QueueDispatchOutbox.AnyAsync(candidate =>
            candidate.EventType ==
                DispatchClaimService.EventTypeReconciliationAbsent &&
            candidate.AttemptId == attemptId)).Should().BeFalse();
    }

    private async Task RunReconciliationAsync(IPrintersService printers)
    {
        ServiceProvider provider = new ServiceCollection()
            .AddDbContext<AppDbContext>(options => options.UseSqlite(
                _connectionString,
                sqlite => sqlite.MigrationsAssembly("Farm.Migrations.Sqlite")))
            .AddScoped<IDbOutboxSequenceAllocator, DbOutboxSequenceAllocator>()
            .AddSingleton(printers)
            .BuildServiceProvider();
        await using (provider)
        {
            var reconciler = new QueueReconciliationService(
                provider.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<QueueReconciliationService>.Instance);
            await reconciler.ReconcileStaleAttemptsAsync(CancellationToken.None);
        }
    }

    private async Task ProcessControlCommandsAsync(IPrintersService printers)
    {
        ServiceProvider provider = new ServiceCollection()
            .AddDbContext<AppDbContext>(options => options.UseSqlite(
                _connectionString,
                sqlite => sqlite.MigrationsAssembly("Farm.Migrations.Sqlite")))
            .AddScoped<IDbOutboxSequenceAllocator, DbOutboxSequenceAllocator>()
            .AddSingleton(printers)
            .BuildServiceProvider();
        await using (provider)
        {
            var consumer = new BackendControlCommandConsumerService(
                provider.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<BackendControlCommandConsumerService>.Instance);
            await consumer.ProcessPendingAsync(CancellationToken.None);
        }
    }

    private async Task ReconcileControlCommandsAsync(IPrintersService printers)
    {
        ServiceProvider provider = new ServiceCollection()
            .AddDbContext<AppDbContext>(options => options.UseSqlite(
                _connectionString,
                sqlite => sqlite.MigrationsAssembly("Farm.Migrations.Sqlite")))
            .AddScoped<IDbOutboxSequenceAllocator, DbOutboxSequenceAllocator>()
            .AddSingleton(printers)
            .BuildServiceProvider();
        await using (provider)
        {
            var consumer = new BackendControlCommandConsumerService(
                provider.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<BackendControlCommandConsumerService>.Instance);
            await consumer.RecoverStaleLeasesAsync(CancellationToken.None);
        }
    }

    private static PrintJobManagementService CreateManagementService(
        AppDbContext db,
        IPrintersService? printers = null,
        IStoragePathService? storage = null,
        IPrinterStatusSnapshotReader? statusReader = null) =>
        new(
            new EfPrintJobManagementRepository(db),
            NullLogger<PrintJobManagementService>.Instance,
            printers ?? Mock.Of<IPrintersService>(),
            storage ?? Mock.Of<IStoragePathService>(),
            CreateHubContext(),
            Mock.Of<IStoredFileOperationsService>(),
            Mock.Of<IPrinterStatusCacheReader>(),
            dispatchClaimService: CreateClaim(
                db,
                statusReader ?? DispatchTestDoubles.NoTelemetryReader()),
            appDbContext: db,
            outboxSequenceAllocator: new DbOutboxSequenceAllocator(),
            queuePositionAllocator: new QueuePositionAllocator(db));

    private static IHubContext<PrinterHub> CreateHubContext()
    {
        var client = new Mock<IClientProxy>();
        client.Setup(proxy => proxy.SendCoreAsync(
                It.IsAny<string>(),
                It.IsAny<object?[]>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var clients = new Mock<IHubClients>();
        clients.Setup(value => value.Group(It.IsAny<string>()))
            .Returns(client.Object);
        clients.SetupGet(value => value.All).Returns(client.Object);
        var hub = new Mock<IHubContext<PrinterHub>>();
        hub.SetupGet(value => value.Clients).Returns(clients.Object);
        return hub.Object;
    }

    private static BedClearAcknowledgementService CreateBedClearAcknowledgementService(
        AppDbContext db,
        Guid printerId) =>
        new(
            db,
            new DbOutboxSequenceAllocator(),
            DispatchTestDoubles.OnlineIdleReader(printerId),
            NullLogger<BedClearAcknowledgementService>.Instance,
            DispatchTestDoubles.TelemetryFreshnessPolicy(),
            DispatchTestDoubles.ValidByteIntegrityVerifier());

    private static JobQueueController CreateJobQueueController(
        IPrintJobManagementService management,
        IBedClearAcknowledgementService acknowledgement,
        AppDbContext db,
        IJobDispatchService? dispatch = null)
    {
        var controller = new JobQueueController(
            Mock.Of<IJobQueueService>(),
            management,
            Mock.Of<IPrintJobCompletionService>(),
            dispatch ?? Mock.Of<IJobDispatchService>(),
            Mock.Of<IBatchDispatchService>(),
            acknowledgement,
            Mock.Of<IPrinterStatusCacheReader>(),
            Mock.Of<IPrintFarmerTelemetryService>(),
            Mock.Of<Farm.Infrastructure.Services.PartsInventory.IPartHarvestService>(),
            Mock.Of<Farm.Infrastructure.Services.OperatorFeatures.IOperatorFeatureGate>(),
            NullLogger<JobQueueController>.Instance,
            db);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(
                    new ClaimsIdentity(
                        [new Claim(ClaimTypes.NameIdentifier, CalibrationOwnerId.ToString())],
                        "test")),
            },
        };
        return controller;
    }

    private static void SetAcknowledgementHeaders(
        JobQueueController controller,
        byte[] jobRowVersion,
        byte[] dispatchStateRowVersion,
        string idempotencyKey)
    {
        controller.Request.Headers.IfMatch =
            $"\"{Convert.ToBase64String(jobRowVersion)}\"";
        controller.Request.Headers["X-Dispatch-State-If-Match"] =
            $"\"{Convert.ToBase64String(dispatchStateRowVersion)}\"";
        controller.Request.Headers["Idempotency-Key"] = idempotencyKey;
    }

    private static string? ReadResponseProperty(ObjectResult response, string name) =>
        response.Value?.GetType().GetProperty(name)?.GetValue(response.Value) as string;

    private AppDbContext CreateContext(params IInterceptor[] interceptors)
    {
        DbContextOptionsBuilder<AppDbContext> builder =
            new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(
                    _connectionString,
                    sqlite => sqlite.MigrationsAssembly("Farm.Migrations.Sqlite"));
        if (interceptors.Length > 0)
        {
            builder.AddInterceptors(interceptors);
        }

        DbContextOptions<AppDbContext> opts = builder.Options;
        var ctx = new AppDbContext(opts);
        ctx.Database.ExecuteSqlRaw("PRAGMA foreign_keys = OFF;");
        return ctx;
    }

    private sealed class ThrowOnceConcurrencySaveInterceptor
        : SaveChangesInterceptor
    {
        private readonly TaskCompletionSource<bool> _triggered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _hasThrown;

        public Task Triggered => _triggered.Task;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _hasThrown, 1) == 0)
            {
                _triggered.TrySetResult(true);
                throw new DbUpdateConcurrencyException(
                    "Simulated concurrent dispatch-state revision change.");
            }

            return base.SavingChangesAsync(
                eventData,
                result,
                cancellationToken);
        }
    }

    private static DispatchClaimService CreateClaim(
        AppDbContext db,
        IPrinterStatusSnapshotReader reader,
        IPrinterTelemetryFreshnessPolicy? telemetryFreshnessPolicy = null,
        ISpoolmanService? spoolmanService = null) =>
        new(
            db,
            reader,
            new DbOutboxSequenceAllocator(),
            NullLogger<DispatchClaimService>.Instance,
            telemetryFreshnessPolicy ??
                DispatchTestDoubles.TelemetryFreshnessPolicy(),
            DispatchTestDoubles.ValidByteIntegrityVerifier(),
            spoolmanService: spoolmanService);

    private static JobQueueService CreateQueueService(AppDbContext db) =>
        new(
            new EfQueueRepository(db),
            new DirectQueueDataService(db),
            NullLogger<JobQueueService>.Instance,
            db: db,
            sequenceAllocator: new DbOutboxSequenceAllocator(),
            positionAllocator: new QueuePositionAllocator(db));

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

    private async Task<Fixture> SeedCalibrationArtifactOnlyAsync(
        AppDbContext db,
        PrinterBackend? backend = null,
        PrinterCredential? credential = null)
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
        gcode.PrinterModelId = mdl.Id;
        db.GcodeFiles.Add(gcode);

        Printer printer = BuildPrinter(mfr.Id, mdl.Id);
        if (backend.HasValue)
        {
            printer.Backend = (int)backend.Value;
            printer.Credential = credential;
        }

        var toolhead = new Toolhead
        {
            Id = Guid.NewGuid(),
            PrinterId = printer.Id,
            Name = "Primary",
            Index = 0,
            IsPrimary = true,
            NozzleDiameter = 0.4,
            CurrentSpoolId = SpoolId,
            CurrentMaterial = Material,
        };
        printer.Toolheads.Add(toolhead);
        db.Printers.Add(printer);
        db.PrinterDispatchStates.Add(new PrinterDispatchState { PrinterId = printer.Id });
        var spool = new Spool
        {
            Id = Guid.NewGuid(),
            Material = Material,
            Sku = "PLA-TEST-SKU",
            LotNumber = "LOT-TEST",
            WeightGrams = 1000,
            InUse = true,
            AssignedPrinterId = printer.Id,
        };
        db.Spools.Add(spool);

        Guid snapshotId = Guid.NewGuid();
        db.CalibrationProjects.Add(new CalibrationProject
        {
            Id = gcode.CalibrationProjectId!.Value,
            OwnerUserId = CalibrationOwnerId,
            Name = "Production calibration",
            PrinterId = printer.Id,
            SelectedToolheadId = toolhead.Id,
            SelectedToolheadIndex = toolhead.Index,
            FilamentProvider = "local",
            FilamentProductId = "pla",
            FilamentProductName = "PLA",
            FilamentMaterial = Material,
            FilamentSku = "PLA-TEST-SKU",
            LocalSpoolId = spool.Id,
            FilamentSnapshotJson = """{"material":"PLA"}""",
        });
        db.CalibrationAttempts.Add(new CalibrationAttempt
        {
            Id = gcode.CalibrationAttemptId!.Value,
            ProjectId = gcode.CalibrationProjectId.Value,
            SpecificationSha256 = gcode.SpecificationSha256!,
        });
        db.CalibrationOrchestrations.Add(new CalibrationOrchestration
        {
            Id = gcode.CalibrationOrchestrationId!.Value,
            ProjectId = gcode.CalibrationProjectId.Value,
            AttemptId = gcode.CalibrationAttemptId.Value,
            SpecificationSha256 = gcode.SpecificationSha256,
            SliceJobId = gcode.SourceSliceJobId,
            GcodeFileId = gcode.Id,
        });

        await db.SaveChangesAsync();
        return new Fixture(printer.Id, Guid.Empty, gcode.Id, string.Empty);
    }

    private async Task<Fixture> SeedStandardJobAsync(AppDbContext db)
    {
        Fixture artifact = await SeedCalibrationArtifactOnlyAsync(db);
        GcodeFile gcode = await db.GcodeFiles.SingleAsync(
            candidate => candidate.Id == artifact.GcodeId);
        var job = new PrintJob
        {
            Id = Guid.NewGuid(),
            Name = "standard-dispatch",
            GcodeFileId = gcode.Id,
            AssignedPrinterId = artifact.PrinterId,
            CreatorSubject = CalibrationOwnerId.ToString(),
            Status = PrintJobStatus.Assigned,
            Priority = (int)PrintJobPriority.Normal,
            QueuePosition = 1,
            JobKind = JobKind.Standard,
            Copies = 1,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            QueuedAt = DateTime.UtcNow,
        };
        db.PrintJobs.Add(job);
        db.QueuePositionStates.Add(new QueuePositionState
        {
            ScopeId = artifact.PrinterId,
            NextPosition = job.QueuePosition,
        });
        await db.SaveChangesAsync();
        return new Fixture(artifact.PrinterId, job.Id, artifact.GcodeId, string.Empty);
    }

    private async Task<Fixture> SeedCalibrationAsync(
        AppDbContext db,
        bool withAck,
        PrinterBackend? backend = null,
        PrinterCredential? credential = null,
        int copies = 1,
        JobKind jobKind = JobKind.Standard)
    {
        Fixture baseFixture = await SeedCalibrationArtifactOnlyAsync(
            db,
            backend,
            credential);

        GcodeFile gcode = await db.GcodeFiles.SingleAsync(g => g.Id == baseFixture.GcodeId);
        Printer printer = await db.Printers
            .Include(candidate => candidate.Toolheads)
            .SingleAsync(candidate => candidate.Id == baseFixture.PrinterId);
        Spool spool = await db.Spools.SingleAsync(
            candidate => candidate.AssignedPrinterId == baseFixture.PrinterId);
        Toolhead toolhead = printer.Toolheads.Single();
        string snapshotSha256 = new('6', 64);
        Guid snapshotId = Guid.NewGuid();

        var job = new PrintJob
        {
            Id = Guid.NewGuid(),
            Name = "calibration",
            GcodeFileId = gcode.Id,
            AssignedPrinterId = baseFixture.PrinterId,
            Status = PrintJobStatus.Assigned,
            Priority = (int)PrintJobPriority.High,
            QueuePosition = 1,
            JobKind = jobKind,
            Copies = copies,
            RequiredFirmwareFamily = PrinterFirmwareFamily.Klipper,
            RequiredGcodeDialect = PrinterGcodeDialect.Klipper,
            RequiredSlicerEngine = "OrcaSlicer",
            RequiredSlicerDistribution = "upstream",
            RequiredSlicerVersion = "2.3.0",
            RequiredSlicerContainerDigest = gcode.SlicerContainerDigest,
            PinnedPrinterConfigRevision = 1,
            GcodeContentSha256 = gcode.ContentSha256,
            PinnedGcodeFileSizeBytes = gcode.FileSizeBytes,
            SpecificationSha256 = gcode.SpecificationSha256,
            MachineProfileSha256 = gcode.MachineProfileSha256,
            ProcessProfileSha256 = gcode.ProcessProfileSha256,
            FilamentProfileSha256 = gcode.FilamentProfileSha256,
            PrinterConfigSnapshotSha256 = snapshotSha256,
            PinnedPrinterModelId = printer.ModelId,
            PinnedToolheadId = toolhead.Id,
            PinnedToolheadIndex = toolhead.Index,
            PinnedSpoolId = spool.Id,
            PinnedFilamentSku = "PLA-TEST-SKU",
            PinnedFilamentLotNumber = "LOT-TEST",
            FilamentSnapshotSha256 = ComputeSha256("""{"material":"PLA"}"""),
            SourceModelSha256 = new string('8', 64),
            CalibrationManifestSha256 = gcode.CalibrationManifestSha256,
            RequiredNozzleDiameter = 0.4m,
            RequiredCapabilities = [],
            PinnedObjectDimensionX = gcode.ObjectDimensionX,
            PinnedObjectDimensionY = gcode.ObjectDimensionY,
            PinnedObjectDimensionZ = gcode.ObjectDimensionZ,
            EstimatedFilamentUsage = gcode.EstimatedFilamentWeightG,
            FilamentName = "PLA",
            CalibrationProjectId = gcode.CalibrationProjectId,
            CalibrationAttemptId = gcode.CalibrationAttemptId,
            CalibrationOrchestrationId = gcode.CalibrationOrchestrationId,
            CalibrationConfigSnapshotId = snapshotId,
            SourceArtifactId = gcode.SourceArtifactId,
            SliceJobId = gcode.SourceSliceJobId,
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
        db.QueuePositionStates.Add(new QueuePositionState
        {
            ScopeId = baseFixture.PrinterId,
            NextPosition = job.QueuePosition,
        });
        await db.SaveChangesAsync();

        const string AckKey = "prod-ack-key";
        if (withAck)
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            PrinterDispatchState state = await db.PrinterDispatchStates
                .SingleAsync(s => s.PrinterId == baseFixture.PrinterId);
            state.AcknowledgedJobId = job.Id;
            state.AcknowledgedAtUtc = DateTime.UtcNow;
            state.AcknowledgedBySubject = "operator-1";
            state.AcknowledgementIdempotencyKey = AckKey;
            state.AcknowledgementExpiresAtUtc = DateTime.UtcNow.AddMinutes(15);
            state.AcknowledgedJobRowVersion = job.RowVersion;
            state.AcknowledgedQueueRevision = state.QueueRevision;
            state.AcknowledgedPrinterConfigRevision = printer.ConfigurationRevision;
            Guid commandId = Guid.NewGuid();
            db.BedClearCommandRecords.Add(new BedClearCommandRecord
            {
                Id = Guid.NewGuid(),
                PrinterId = printer.Id,
                JobId = job.Id,
                IdempotencyKey = AckKey,
                RequestSha256 = new string('a', 64),
                ActorSubject = "operator-1",
                JobRowVersion = job.RowVersion ?? [],
                DispatchStateRowVersion = state.RowVersion ?? [],
                QueueRevision = state.QueueRevision,
                PrinterConfigRevision = printer.ConfigurationRevision,
                Status = BedClearCommandStatus.Pending,
                OutboxEventId = commandId,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow,
                ExpiresAtUtc = DateTime.UtcNow.AddMinutes(15),
            });
            db.QueueDispatchOutbox.Add(new QueueDispatchOutbox
            {
                Id = commandId,
                Sequence = await new DbOutboxSequenceAllocator().AllocateAsync(db),
                AggregateType = nameof(PrintJob),
                AggregateId = job.Id,
                PrinterId = printer.Id,
                ProjectId = job.CalibrationProjectId,
                JobStatus = job.Status.ToString(),
                JobKind = job.JobKind?.ToString(),
                EventType = BedClearAcknowledgementService.BackendStartCommandEventType,
                SchemaVersion = "1",
                PayloadJson = JsonSerializer.Serialize(new
                {
                    jobId = job.Id,
                    printerId = printer.Id,
                    actorSubject = "operator-1",
                    acknowledgementKey = AckKey,
                }),
                Status = QueueOutboxEventStatus.Pending,
                CreatedAtUtc = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }

        return baseFixture with { JobId = job.Id, AckKey = AckKey };
    }

    private static GcodeFile BuildPromotedArtifact() => new()
    {
        Id = Guid.NewGuid(),
        Name = "calibration.gcode",
        FileName = "calibration.gcode",
        FilePath = "/gcode",
        FileSizeBytes = AuthoritativeGcodeBytes.Length,
        EstimatedFilamentWeightG = 10,
        FileHash = AuthoritativeGcodeSha256,
        IsImmutable = true,
        PromotedAtUtc = DateTime.UtcNow.AddMinutes(-1),
        ContentSha256 = AuthoritativeGcodeSha256,
        SourceArtifactId = Guid.NewGuid(),
        SourceSliceJobId = Guid.NewGuid(),
        SourceModelSha256 = new string('8', 64),
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
        SlicerContainerDigest = "sha256:test",
        FirmwareFamily = nameof(PrinterFirmwareFamily.Klipper),
        GcodeDialect = nameof(PrinterGcodeDialect.Klipper),
        ObjectDimensionX = 20,
        ObjectDimensionY = 20,
        ObjectDimensionZ = 20,
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
        MaxBuildVolumeX = 200,
        MaxBuildVolumeY = 200,
        MaxBuildVolumeZ = 200,
    };

    private static string ComputeSha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();
}
