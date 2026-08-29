using System.Text.Json;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Modules.Calibration.Contracts;
using Farm.Modules.Calibration.Services.Calibration;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Farm.Modules.Calibration.Tests.Services.Calibration;

/// <summary>
/// Proves the AC #5 (issue #2188) cross-process claim-fencing contract: two racing attempts to
/// run the same external-side-effect step for the same <see cref="CalibrationOrchestration"/>
/// result in EXACTLY ONE gateway invocation, mirroring the
/// <c>CalibrationQueueConcurrencyTests.TwoContexts_ClaimSameStandardJob_OnlyOneSucceeds</c> pattern
/// already used for print-queue dispatch (<c>DispatchClaimService</c>).
/// </summary>
/// <remarks>
/// <para>
/// This calls <see cref="CalibrationOrchestrationSagaService.RunSendingToPrinterStepAsync"/>
/// directly - via two independent <see cref="AppDbContext"/> instances backed by the SAME
/// SQLite database, each racing through <c>Task.WhenAll</c> - rather
/// than through the public <c>AdvanceAsync</c> entry point. <c>AdvanceAsync</c> serializes every
/// call for one orchestration id with an in-process <c>SemaphoreSlim</c> (see
/// <c>CalibrationOrchestrationSagaService.GetAdvanceLock</c>), which defends the common
/// single-process case but would make two calls from the same test process impossible to race at
/// the database layer through that entry point - exactly the gap that motivated making
/// <see cref="CalibrationOrchestrationSagaService.RunSendingToPrinterStepAsync"/> and
/// <see cref="CalibrationOrchestrationSagaService.TryAcquireStepClaimAsync"/> internal rather than
/// private. Racing the step method directly is therefore the only way to exercise the residual
/// multi-instance scenario (two horizontally-scaled API processes, each with its own independent
/// in-process lock table) this issue actually closes.
/// </para>
/// <para>
/// Uses a real, file-backed-in-memory SQLite database shared across connections via
/// <c>cache=shared</c> (mirroring <c>CalibrationQueueConcurrencyTests</c>), so both racing calls
/// hit genuine SQLite-enforced row-version fencing rather than an in-memory provider that cannot
/// model real concurrent writers. Deliberately does NOT carry the <c>DbHeavy</c> trait: the
/// separate <c>dotnet-test-providers</c> CI job that runs <c>DbHeavy</c>-tagged tests only executes
/// <c>Farm.Web.Api.Tests.dll</c>, so a <c>DbHeavy</c>-tagged test in this project would silently
/// never run in CI at all (excluded from the default job's <c>Category!=DbHeavy</c> filter, and
/// never picked up by the DbHeavy job because it targets a different assembly). This mirrors the
/// existing, untagged <c>CalibrationOrchestrationTakeOverConcurrencyTests</c> in this same project.
/// </para>
/// </remarks>
public sealed class CalibrationOrchestrationClaimFencingConcurrencyTests : IAsyncDisposable
{
    private static int _dbCounter;

    private readonly SqliteConnection _keepAlive;
    private readonly string _connectionString;

    public CalibrationOrchestrationClaimFencingConcurrencyTests()
    {
        int id = Interlocked.Increment(ref _dbCounter);
        _connectionString = $"Data Source=file:calib_claim_fencing_{id}?mode=memory&cache=shared;Foreign Keys=False";
        _keepAlive = new SqliteConnection(_connectionString);
        _keepAlive.Open();
    }

    public async ValueTask DisposeAsync() => await _keepAlive.DisposeAsync();

    [Fact]
    public async Task TwoContexts_RaceSendingToPrinterStep_OnlyOneGatewayInvocation()
    {
        // Arrange: seed a project/attempt/orchestration, then fast-forward the orchestration to
        // "sending-to-printer" with a slice job already recorded - this test is only about the
        // claim race at that one step, not about driving the whole saga there first.
        Guid ownerId = Guid.NewGuid();
        Guid printerId = Guid.NewGuid();
        Guid orchestrationId;
        Guid projectId;
        await using (AppDbContext seedContext = CreateContext())
        {
            _ = await seedContext.Database.EnsureCreatedAsync();
            SeedPrinter(seedContext, printerId);

            CalibrationActor actor = new(ownerId, "owner", false);
            CalibrationApiResult<CalibrationProjectDto> project = await CreateProjectService(seedContext)
                .CreateProjectAsync(CreateProjectRequest(printerId), actor, CancellationToken.None);
            _ = project.StatusCode.Should().Be(StatusCodes.Status201Created);
            projectId = project.Value!.Id;

            CalibrationApiResult<CalibrationAttemptDto> attempt = await CreateProjectService(seedContext)
                .CreateAttemptAsync(projectId, CreateAttemptRequest(), actor, CancellationToken.None);
            _ = attempt.StatusCode.Should().Be(StatusCodes.Status201Created);

            CalibrationOrchestration orchestration = await seedContext.CalibrationOrchestrations
                .SingleAsync(o => o.AttemptId == attempt.Value!.Id);
            orchestrationId = orchestration.Id;
            orchestration.CurrentStep = CalibrationSagaSteps.SendingToPrinter;
            orchestration.SliceJobId = Guid.NewGuid();
            orchestration.Revision++;
            _ = await seedContext.SaveChangesAsync();
        }

        CountingPrintDispatchGateway sharedGateway = new();

        await using AppDbContext ctx1 = CreateContext();
        await using AppDbContext ctx2 = CreateContext();

        CalibrationOrchestration view1 = await ctx1.CalibrationOrchestrations.SingleAsync(o => o.Id == orchestrationId);
        CalibrationOrchestration view2 = await ctx2.CalibrationOrchestrations.SingleAsync(o => o.Id == orchestrationId);
        CalibrationProject project1 = await ctx1.CalibrationProjects.SingleAsync(p => p.Id == projectId);
        CalibrationProject project2 = await ctx2.CalibrationProjects.SingleAsync(p => p.Id == projectId);
        _ = view1.Revision.Should().Be(view2.Revision, "both racers must start from the same committed snapshot");
        _ = view1.LeaseOwner.Should().BeNull("no claim has been taken yet");
        long revisionBeforeRace = view1.Revision;

        CalibrationOrchestrationSagaService saga1 = CreateSaga(ctx1, sharedGateway);
        CalibrationOrchestrationSagaService saga2 = CreateSaga(ctx2, sharedGateway);
        DateTime nowUtc = DateTime.UtcNow;

        // Act - fire both concurrently, racing the claim-then-dispatch step directly at the
        // database layer (see remarks above for why AdvanceAsync cannot be used to prove this).
        Task<CalibrationOrchestrationSagaService.StepOutcome> t1 =
            saga1.RunSendingToPrinterStepAsync(view1, project1, nowUtc, CancellationToken.None);
        Task<CalibrationOrchestrationSagaService.StepOutcome> t2 =
            saga2.RunSendingToPrinterStepAsync(view2, project2, nowUtc, CancellationToken.None);
        CalibrationOrchestrationSagaService.StepOutcome[] outcomes = await Task.WhenAll(t1, t2);

        // Assert - exactly one caller ever won the claim and so ever called the gateway; the
        // other must have observed ClaimLost() WITHOUT the gateway having been invoked on its
        // behalf (AC #2). This is the crux of the AC #5 proof: a losing racer never reaches the
        // gateway at all, rather than reaching it and merely losing an after-the-fact save.
        int wonClaimCount = outcomes.Count(o => !o.ClaimConflict);
        int lostClaimCount = outcomes.Count(o => o.ClaimConflict);
        _ = wonClaimCount.Should().Be(1, "exactly one racer must win the claim");
        _ = lostClaimCount.Should().Be(1, "exactly one racer must observe ClaimLost() without calling the gateway");
        _ = sharedGateway.CallCount.Should().Be(1, "exactly one gateway invocation must occur for the whole race");

        // The winner's outcome must reflect a genuine step advance (not a no-op) and the
        // orchestration's persisted lease must have been claimed (then cleared by ApplyOutcome in
        // the real AdvanceLockedAsync flow - not exercised directly here since this test targets
        // the step method, not the outer Advance orchestration).
        CalibrationOrchestrationSagaService.StepOutcome winnerOutcome = outcomes.Single(o => !o.ClaimConflict);
        _ = winnerOutcome.Changed.Should().BeTrue();
        _ = winnerOutcome.NextStep.Should().Be(CalibrationSagaSteps.AwaitingPrint);

        await using AppDbContext verifyContext = CreateContext();
        CalibrationOrchestration persisted = await verifyContext.CalibrationOrchestrations
            .SingleAsync(o => o.Id == orchestrationId);
        _ = persisted.LeaseOwner.Should().NotBeNull("the winning claim's lease is still recorded on the row");
        _ = persisted.Revision.Should().Be(revisionBeforeRace + 1, "exactly one claim-acquire save committed");
    }

    private AppDbContext CreateContext()
    {
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connectionString)
            .Options;
        AppDbContext context = new(options);
        context.Database.ExecuteSqlRaw("PRAGMA foreign_keys = OFF;");
        return context;
    }

    private static void SeedPrinter(AppDbContext context, Guid printerId)
    {
        Guid manufacturerId = Guid.NewGuid();
        Guid modelId = Guid.NewGuid();
        _ = context.Manufacturers.Add(new Manufacturer { Id = manufacturerId, Name = "Claim fencing test manufacturer" });
        _ = context.PrinterModels.Add(new PrinterModel { Id = modelId, ManufacturerId = manufacturerId, Name = "Claim fencing test model" });
        _ = context.Printers.Add(new Printer
        {
            Id = printerId,
            Name = "Claim fencing test printer",
            ServerUrl = $"http://{printerId:N}.test",
            BackendPort = 7125,
            ManufacturerId = manufacturerId,
            ModelId = modelId,
        });
        context.SaveChanges();
    }

    private static CalibrationOrchestrationSagaService CreateSaga(
        AppDbContext context,
        IPrintDispatchGateway printDispatchGateway) =>
        new(
            context,
            CreateProjectService(context),
            new StubSliceSubmissionGateway(),
            printDispatchGateway,
            TimeProvider.System,
            NullLogger<CalibrationOrchestrationSagaService>.Instance);

    private static CalibrationProjectService CreateProjectService(AppDbContext context) =>
        new(
            context,
            new TestCalibrationBlobStore(),
            TimeProvider.System,
            NullLogger<CalibrationProjectService>.Instance);

    private static CalibrationProjectCreateRequest CreateProjectRequest(Guid printerId) =>
        new()
        {
            ClientId = "test-client",
            RequestId = $"project-{Guid.NewGuid():N}",
            Name = "Claim fencing race",
            PrinterId = printerId,
            PrinterConfigurationRevision = 1,
            FilamentProvider = "catalog",
            FilamentProductId = "sku-pla-blue",
            FilamentProductName = "PLA Blue",
            FilamentMaterial = "PLA",
            FilamentSnapshot = JsonSerializer.SerializeToElement(new { vendor = "OlyForge" }),
            OrderedSteps = JsonSerializer.SerializeToElement(new[] { "temperature" }),
            CurrentSelections = JsonSerializer.SerializeToElement(new { }),
            ExperienceMode = "Coach",
        };

    private static CalibrationAttemptCreateRequest CreateAttemptRequest() =>
        new()
        {
            ClientId = "test-client",
            RequestId = $"attempt-{Guid.NewGuid():N}",
            CalibrationKind = "temperature",
            Method = "temperature_tower",
            DefinitionVersion = "1",
            Input = JsonSerializer.SerializeToElement(new { modelUrl = "https://example.test/model.3mf" }),
            Specification = JsonSerializer.SerializeToElement(new { targetTemperatureC = 210 }),
            ProfileSnapshotIds = JsonSerializer.SerializeToElement(Array.Empty<Guid>()),
            PrinterConfigurationRevision = 1,
        };

    private sealed class StubSliceSubmissionGateway : ISliceSubmissionGateway
    {
        public Task<SliceSubmissionResult> SubmitAsync(CalibrationSliceSubmission submission, CancellationToken ct) =>
            Task.FromResult(SliceSubmissionResult.Ok(Guid.NewGuid()));

        public Task<SliceStatusResult> GetStatusAsync(Guid sliceJobId, CancellationToken ct) =>
            Task.FromResult(SliceStatusResult.Ok("Completed"));
    }

    /// <summary>Counts real invocations so the test can assert EXACTLY ONE occurred (AC #5).</summary>
    private sealed class CountingPrintDispatchGateway : IPrintDispatchGateway
    {
        private int _callCount;

        public int CallCount => _callCount;

        public Task<PrintDispatchResult> SendToPrinterAsync(Guid sliceJobId, Guid printerId, CancellationToken ct)
        {
            _ = Interlocked.Increment(ref _callCount);
            return Task.FromResult(PrintDispatchResult.Ok());
        }
    }

    private sealed class TestCalibrationBlobStore : ICalibrationBlobStore
    {
        public Task DeleteAsync(string opaqueStorageKey, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<bool> ExistsAsync(string opaqueStorageKey, CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task<CalibrationBlobMetadata?> GetMetadataAsync(
            string opaqueStorageKey,
            CancellationToken cancellationToken) =>
            Task.FromResult<CalibrationBlobMetadata?>(null);

        public Task<Stream> OpenReadAsync(string opaqueStorageKey, CancellationToken cancellationToken) =>
            Task.FromResult<Stream>(new MemoryStream());

        public Task<CalibrationBlobMetadata> PutAsync(
            CalibrationBlobWriteRequest request,
            Stream content,
            CancellationToken cancellationToken) =>
            Task.FromResult(new CalibrationBlobMetadata(
                $"calibration/{request.PhotoId:N}.png",
                "image/png",
                1,
                new string('a', 64),
                1,
                1));
    }
}
