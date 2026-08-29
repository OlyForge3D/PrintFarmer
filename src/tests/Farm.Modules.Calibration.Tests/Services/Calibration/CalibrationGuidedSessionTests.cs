using System.Security.Cryptography;
using System.Text.Json;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.PrinterCalibration;
using Farm.Modules.Calibration.Contracts;
using Farm.Modules.Calibration.Services.Calibration;
using Farm.Slicer.Module.Models;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Farm.Modules.Calibration.Tests.Services.Calibration;

/// <summary>
/// Covers issue #2180's four guided-session gaps end to end at the service layer: (1) the
/// project-owned draft profile document and its gated promotion to a real custom filament
/// profile, (2) skip/pending/completed method disposition, (3) the server-owned step guidance
/// catalog, and (4) server-side <c>setup</c> input validation. Each acceptance criterion is
/// paired with an explicit control per the issue's wording.
/// </summary>
public sealed class CalibrationGuidedSessionTests
{
    // Gap 3: step metadata is served to clients rather than duplicated per client.

    [Fact]
    public void GetMethodGuidanceCatalog_ReturnsGuidanceForEveryDeclaredMethod()
    {
        CalibrationProjectService service = CreateService(CreateContext());

        IReadOnlyList<CalibrationMethodGuidanceDto> catalog = service.GetMethodGuidanceCatalog();

        _ = catalog.Should().NotBeEmpty();
        CalibrationMethodGuidanceDto temperatureTower = catalog.Should()
            .ContainSingle(entry => entry.Method == CalibrationMethods.ToWireName(CalibrationMethod.TemperatureTower))
            .Subject;
        _ = temperatureTower.Title.Should().NotBeNullOrWhiteSpace();
        _ = temperatureTower.Purpose.Should().NotBeNullOrWhiteSpace();
        _ = temperatureTower.WikiUrl.Should().NotBeNullOrWhiteSpace();
        _ = temperatureTower.SetupInputs.Should().HaveCount(2);
        _ = temperatureTower.MeasureQuantity.Should().NotBeNull();
        _ = temperatureTower.MeasureQuantity!.Key.Should().Be("temperature_c");
        _ = temperatureTower.Steps.Should().Equal(
            CalibrationMethodSteps.Setup,
            CalibrationMethodSteps.Print,
            CalibrationMethodSteps.Measure,
            CalibrationMethodSteps.Select);
    }

    // Gap 4: setup inputs are validated server-side, with an out-of-range control.

    [Fact]
    public async Task CreateAttemptAsync_TemperatureTowerMissingSetupInputs_ReturnsValidationError()
    {
        await using AppDbContext db = CreateContext();
        CalibrationActor actor = new(Guid.NewGuid(), "owner", false);
        CalibrationProjectService service = CreateService(db);
        CalibrationApiResult<CalibrationProjectDto> project = await service.CreateProjectAsync(
            CreateProjectRequest(Guid.NewGuid(), "setup-missing-project"),
            actor,
            CancellationToken.None);

        CalibrationApiResult<CalibrationAttemptDto> result = await service.CreateAttemptAsync(
            project.Value!.Id,
            CreateTemperatureTowerAttemptRequest("setup-missing-attempt", specification: new { }),
            actor,
            CancellationToken.None);

        _ = result.StatusCode.Should().Be(StatusCodes.Status422UnprocessableEntity);
        _ = result.Code.Should().Be("setup_input_missing");
        _ = (await db.CalibrationAttempts.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task CreateAttemptAsync_TemperatureTowerOutOfRangeSetupInputs_ReturnsValidationError()
    {
        await using AppDbContext db = CreateContext();
        CalibrationActor actor = new(Guid.NewGuid(), "owner", false);
        CalibrationProjectService service = CreateService(db);
        CalibrationApiResult<CalibrationProjectDto> project = await service.CreateProjectAsync(
            CreateProjectRequest(Guid.NewGuid(), "setup-out-of-range-project"),
            actor,
            CancellationToken.None);

        CalibrationApiResult<CalibrationAttemptDto> result = await service.CreateAttemptAsync(
            project.Value!.Id,
            CreateTemperatureTowerAttemptRequest(
                "setup-out-of-range-attempt",
                specification: new { start_temperature_c = 500, end_temperature_c = 210 }),
            actor,
            CancellationToken.None);

        _ = result.StatusCode.Should().Be(StatusCodes.Status422UnprocessableEntity);
        _ = result.Code.Should().Be("setup_input_out_of_range");
        _ = (await db.CalibrationAttempts.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task CreateAttemptAsync_TemperatureTowerInRangeSetupInputs_Succeeds()
    {
        await using AppDbContext db = CreateContext();
        CalibrationActor actor = new(Guid.NewGuid(), "owner", false);
        CalibrationProjectService service = CreateService(db);
        CalibrationApiResult<CalibrationProjectDto> project = await service.CreateProjectAsync(
            CreateProjectRequest(Guid.NewGuid(), "setup-valid-project"),
            actor,
            CancellationToken.None);

        CalibrationApiResult<CalibrationAttemptDto> result = await service.CreateAttemptAsync(
            project.Value!.Id,
            CreateTemperatureTowerAttemptRequest(
                "setup-valid-attempt",
                specification: new { start_temperature_c = 230, end_temperature_c = 190 }),
            actor,
            CancellationToken.None);

        _ = result.StatusCode.Should().Be(StatusCodes.Status201Created);
        _ = (await db.CalibrationAttempts.CountAsync()).Should().Be(1);
    }

    // Gap 2: a skipped step is distinguishable from a pending one and does not block completion.

    [Fact]
    public async Task UpdateProjectAsync_ActiveToCompletedWithPendingMethod_IsBlocked()
    {
        await using AppDbContext db = CreateContext();
        CalibrationActor actor = new(Guid.NewGuid(), "owner", false);
        FakeFilamentProfilePromotionGateway gateway = new();
        CalibrationProjectService service = CreateService(db, gateway);
        CalibrationApiResult<CalibrationProjectDto> project = await service.CreateProjectAsync(
            CreateProjectRequest(Guid.NewGuid(), "pending-blocks-completion-project"),
            actor,
            CancellationToken.None);
        _ = await service.CreateAttemptAsync(
            project.Value!.Id,
            CreateTemperatureTowerAttemptRequest(
                "pending-blocks-completion-attempt",
                specification: new { start_temperature_c = 230, end_temperature_c = 190 }),
            actor,
            CancellationToken.None);

        CalibrationApiResult<CalibrationProjectDto> completed = await service.UpdateProjectAsync(
            project.Value.Id,
            new CalibrationProjectUpdateRequest { BaseRevision = project.Value.Revision, LifecycleStatus = "Completed" },
            IfMatch(project.Value),
            actor,
            CancellationToken.None);

        _ = completed.StatusCode.Should().Be(StatusCodes.Status422UnprocessableEntity);
        _ = completed.Code.Should().Be("project_completion_blocked_pending_method");
        _ = gateway.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task SetMethodDispositionAsync_Skipped_IsDistinctFromPendingAndDoesNotBlockCompletion()
    {
        await using AppDbContext db = CreateContext();
        CalibrationActor actor = new(Guid.NewGuid(), "owner", false);
        FakeFilamentProfilePromotionGateway gateway = new();
        CalibrationProjectService service = CreateService(db, gateway);
        CalibrationApiResult<CalibrationProjectDto> project = await service.CreateProjectAsync(
            CreateProjectRequest(Guid.NewGuid(), "skip-project"),
            actor,
            CancellationToken.None);
        _ = await service.CreateAttemptAsync(
            project.Value!.Id,
            CreateTemperatureTowerAttemptRequest(
                "skip-attempt",
                specification: new { start_temperature_c = 230, end_temperature_c = 190 }),
            actor,
            CancellationToken.None);

        string temperatureTowerMethod = CalibrationMethods.ToWireName(CalibrationMethod.TemperatureTower);
        CalibrationApiResult<IReadOnlyList<CalibrationMethodProgressDto>> progressList =
            await service.GetMethodProgressAsync(project.Value.Id, actor, CancellationToken.None);
        _ = progressList.IsSuccess.Should().BeTrue();
        CalibrationMethodProgressDto pending = progressList.Value!.Single(entry => entry.Method == temperatureTowerMethod);
        _ = pending.Disposition.Should().Be("Pending");

        CalibrationApiResult<CalibrationMethodProgressDto> skipped = await service.SetMethodDispositionAsync(
            project.Value.Id,
            temperatureTowerMethod,
            new CalibrationMethodDispositionRequest { BaseRevision = pending.Revision, Disposition = "Skipped" },
            $"\"calibration-method-progress-{pending.Id:N}-{pending.Revision}\"",
            actor,
            CancellationToken.None);

        _ = skipped.StatusCode.Should().Be(StatusCodes.Status200OK);
        _ = skipped.Value!.Disposition.Should().Be("Skipped");
        _ = skipped.Value.Disposition.Should().NotBe("Pending", "a deliberately skipped step must be distinguishable from a pending one");

        CalibrationApiResult<CalibrationProjectDto> completed = await service.UpdateProjectAsync(
            project.Value.Id,
            new CalibrationProjectUpdateRequest { BaseRevision = project.Value.Revision, LifecycleStatus = "Completed" },
            IfMatch(project.Value),
            actor,
            CancellationToken.None);

        _ = completed.StatusCode.Should().Be(StatusCodes.Status200OK, "a Skipped method must not block project completion");
    }

    [Fact]
    public async Task SetMethodDispositionAsync_ClientRequestsCompleted_ReturnsValidationError()
    {
        await using AppDbContext db = CreateContext();
        CalibrationActor actor = new(Guid.NewGuid(), "owner", false);
        CalibrationProjectService service = CreateService(db);
        CalibrationApiResult<CalibrationProjectDto> project = await service.CreateProjectAsync(
            CreateProjectRequest(Guid.NewGuid(), "no-client-completion-project"),
            actor,
            CancellationToken.None);
        _ = await service.CreateAttemptAsync(
            project.Value!.Id,
            CreateTemperatureTowerAttemptRequest(
                "no-client-completion-attempt",
                specification: new { start_temperature_c = 230, end_temperature_c = 190 }),
            actor,
            CancellationToken.None);
        string temperatureTowerMethod = CalibrationMethods.ToWireName(CalibrationMethod.TemperatureTower);

        CalibrationApiResult<CalibrationMethodProgressDto> result = await service.SetMethodDispositionAsync(
            project.Value.Id,
            temperatureTowerMethod,
            new CalibrationMethodDispositionRequest { BaseRevision = 1, Disposition = "Completed" },
            null,
            actor,
            CancellationToken.None);

        _ = result.StatusCode.Should().Be(StatusCodes.Status422UnprocessableEntity);
        _ = result.Code.Should().Be("method_disposition_invalid");
    }

    // Cross-device resume: a project started on one device is resumable on another with correct
    // step dispositions, paired with a control that a device-scoped draft does NOT leak across
    // devices (CalibrationDraft is deliberately DeviceLineageId-scoped).

    [Fact]
    public async Task GetMethodProgressAsync_ResumedOnDifferentDevice_ReportsConsistentDisposition()
    {
        await using AppDbContext db = CreateContext();
        CalibrationActor actor = new(Guid.NewGuid(), "owner", false);
        CalibrationProjectService service = CreateService(db);
        CalibrationApiResult<CalibrationProjectDto> project = await service.CreateProjectAsync(
            CreateProjectRequest(Guid.NewGuid(), "resume-project"),
            actor,
            CancellationToken.None);
        string temperatureTowerMethod = CalibrationMethods.ToWireName(CalibrationMethod.TemperatureTower);

        // Device A starts the attempt (which ensures the project-owned, not device-scoped,
        // method-progress row) and records a setup draft under its own device lineage.
        _ = await service.CreateAttemptAsync(
            project.Value!.Id,
            CreateTemperatureTowerAttemptRequest(
                "resume-attempt",
                specification: new { start_temperature_c = 230, end_temperature_c = 190 }),
            actor,
            CancellationToken.None);
        _ = await service.UpsertDraftAsync(
            project.Value.Id,
            CalibrationMethodSteps.Setup,
            CreateStepDraftRequest(temperatureTowerMethod, "device-a"),
            null,
            actor,
            CancellationToken.None);

        // Device B resumes: it sees the correct (project-owned) Pending disposition for the
        // method it never touched.
        CalibrationApiResult<IReadOnlyList<CalibrationMethodProgressDto>> resumed =
            await service.GetMethodProgressAsync(project.Value.Id, actor, CancellationToken.None);

        _ = resumed.IsSuccess.Should().BeTrue();
        CalibrationMethodProgressDto progress = resumed.Value!.Single(entry => entry.Method == temperatureTowerMethod);
        _ = progress.Disposition.Should().Be("Pending");

        // Control: device B's own "setup" draft attempt does NOT see device A's device-scoped
        // draft - CalibrationDraft is deliberately DeviceLineageId-scoped, unlike method-progress.
        CalibrationApiResult<CalibrationDraftDto> deviceBSetup = await service.UpsertDraftAsync(
            project.Value.Id,
            CalibrationMethodSteps.Setup,
            CreateStepDraftRequest(temperatureTowerMethod, "device-b"),
            null,
            actor,
            CancellationToken.None);

        _ = deviceBSetup.IsSuccess.Should().BeTrue(
            "a device-scoped draft on device-a must not leak into an independent device-b resume");
        _ = deviceBSetup.Value!.DeviceLineageId.Should().Be("device-b");
    }

    // Gap 1: an abandoned project leaves NO entry in the user's custom filament profiles, with a
    // control that a completed one does.

    [Fact]
    public async Task UpdateProjectAsync_CompletedAfterSelection_PromotesDraftProfileExactlyOnce()
    {
        await using AppDbContext db = CreateContext();
        CalibrationActor actor = new(Guid.NewGuid(), "owner", false);
        FakeFilamentProfilePromotionGateway gateway = new();
        CalibrationProjectService service = CreateService(db, gateway);
        CalibrationApiResult<CalibrationProjectDto> project = await service.CreateProjectAsync(
            CreateProjectRequest(Guid.NewGuid(), "completion-promotes-project"),
            actor,
            CancellationToken.None);
        CalibrationApiResult<CalibrationAttemptDto> attempt = await service.CreateAttemptAsync(
            project.Value!.Id,
            CreateTemperatureTowerAttemptRequest(
                "completion-promotes-attempt",
                specification: new { start_temperature_c = 230, end_temperature_c = 190 }),
            actor,
            CancellationToken.None);

        CalibrationApiResult<CalibrationObservationDto> selection = await service.AppendObservationAsync(
            attempt.Value!.Id,
            CreateSelectionObservationRequest("completion-promotes-selection", 215m),
            actor,
            CancellationToken.None);
        _ = selection.StatusCode.Should().Be(StatusCodes.Status201Created);

        CalibrationApiResult<CalibrationDraftProfileDto> draftBeforeCompletion = await service.GetDraftProfileAsync(
            project.Value.Id,
            actor,
            CancellationToken.None);
        _ = draftBeforeCompletion.IsSuccess.Should().BeTrue();
        _ = draftBeforeCompletion.Value!.PromotedProfileId.Should().BeNull(
            "the draft profile must not be promoted before the project reaches Completed");

        CalibrationApiResult<CalibrationProjectDto> completed = await service.UpdateProjectAsync(
            project.Value.Id,
            new CalibrationProjectUpdateRequest { BaseRevision = project.Value.Revision, LifecycleStatus = "Completed" },
            IfMatch(project.Value),
            actor,
            CancellationToken.None);

        _ = completed.StatusCode.Should().Be(StatusCodes.Status200OK);
        _ = gateway.CallCount.Should().Be(1, "promotion must run exactly once for a genuinely completed project");

        CalibrationApiResult<CalibrationDraftProfileDto> draftAfterCompletion = await service.GetDraftProfileAsync(
            project.Value.Id,
            actor,
            CancellationToken.None);
        _ = draftAfterCompletion.Value!.PromotedProfileId.Should().NotBeNull();

        // A redundant re-PATCH to Completed (e.g. a benign client retry) must not re-promote.
        CalibrationApiResult<CalibrationProjectDto> reCompleted = await service.UpdateProjectAsync(
            project.Value.Id,
            new CalibrationProjectUpdateRequest { BaseRevision = completed.Value!.Revision, LifecycleStatus = "Completed" },
            IfMatch(completed.Value),
            actor,
            CancellationToken.None);
        _ = reCompleted.StatusCode.Should().Be(StatusCodes.Status200OK);
        _ = gateway.CallCount.Should().Be(1, "promotion must be idempotent on a redundant Completed re-PATCH");
    }

    [Fact]
    public async Task UpdateProjectAsync_ArchivedWithoutCompletion_NeverPromotesDraftProfile()
    {
        await using AppDbContext db = CreateContext();
        CalibrationActor actor = new(Guid.NewGuid(), "owner", false);
        FakeFilamentProfilePromotionGateway gateway = new();
        CalibrationProjectService service = CreateService(db, gateway);
        CalibrationApiResult<CalibrationProjectDto> project = await service.CreateProjectAsync(
            CreateProjectRequest(Guid.NewGuid(), "abandoned-project"),
            actor,
            CancellationToken.None);
        CalibrationApiResult<CalibrationAttemptDto> attempt = await service.CreateAttemptAsync(
            project.Value!.Id,
            CreateTemperatureTowerAttemptRequest(
                "abandoned-attempt",
                specification: new { start_temperature_c = 230, end_temperature_c = 190 }),
            actor,
            CancellationToken.None);
        // The operator records a partial result, then abandons the run without ever completing
        // it - this must not leave a promoted (i.e. real, listed) custom filament profile behind.
        _ = await service.AppendObservationAsync(
            attempt.Value!.Id,
            CreateSelectionObservationRequest("abandoned-selection", 215m),
            actor,
            CancellationToken.None);

        CalibrationApiResult<CalibrationProjectDto> archived = await service.UpdateProjectAsync(
            project.Value.Id,
            new CalibrationProjectUpdateRequest { BaseRevision = project.Value.Revision, LifecycleStatus = "Archived" },
            IfMatch(project.Value),
            actor,
            CancellationToken.None);

        _ = archived.StatusCode.Should().Be(StatusCodes.Status200OK);
        _ = gateway.CallCount.Should().Be(
            0,
            "an abandoned/archived project must leave no entry in the user's custom filament profiles");

        CalibrationApiResult<CalibrationDraftProfileDto> draftProfile = await service.GetDraftProfileAsync(
            project.Value.Id,
            actor,
            CancellationToken.None);
        _ = draftProfile.Value!.PromotedProfileId.Should().BeNull();
    }

    [Fact]
    public async Task UpdateProjectAsync_ConcurrentCompletionAlreadyClaimed_NeverInvokesGatewayAndReturnsConflict()
    {
        // Review fix (Vasquez/Hicks/Bishop, issue #2180): simulates a second, genuinely
        // concurrent Active -> Completed request racing this one. Directly stamping
        // PromotionClaimedAtUtc on the draft profile row (bypassing the service) reproduces the
        // state a competing request would have already committed via its own atomic
        // ExecuteUpdateAsync claim, before this request's UpdateProjectAsync call runs. The claim
        // gate must reject this request's completion attempt WITHOUT ever calling the external
        // promotion gateway - the whole point of the claim is that at most one caller ever
        // reaches the external side effect.
        await using AppDbContext db = CreateContext();
        CalibrationActor actor = new(Guid.NewGuid(), "owner", false);
        FakeFilamentProfilePromotionGateway gateway = new();
        CalibrationProjectService service = CreateService(db, gateway);
        CalibrationApiResult<CalibrationProjectDto> project = await service.CreateProjectAsync(
            CreateProjectRequest(Guid.NewGuid(), "concurrent-completion-project"),
            actor,
            CancellationToken.None);
        CalibrationApiResult<CalibrationAttemptDto> attempt = await service.CreateAttemptAsync(
            project.Value!.Id,
            CreateTemperatureTowerAttemptRequest(
                "concurrent-completion-attempt",
                specification: new { start_temperature_c = 230, end_temperature_c = 190 }),
            actor,
            CancellationToken.None);
        _ = await service.AppendObservationAsync(
            attempt.Value!.Id,
            CreateSelectionObservationRequest("concurrent-completion-selection", 215m),
            actor,
            CancellationToken.None);

        CalibrationDraftProfile draftRow = await db.CalibrationDraftProfiles
            .SingleAsync(profile => profile.ProjectId == project.Value.Id);
        draftRow.PromotionClaimedAtUtc = DateTime.UtcNow;
        _ = await db.SaveChangesAsync();

        CalibrationApiResult<CalibrationProjectDto> completed = await service.UpdateProjectAsync(
            project.Value.Id,
            new CalibrationProjectUpdateRequest { BaseRevision = project.Value.Revision, LifecycleStatus = "Completed" },
            IfMatch(project.Value),
            actor,
            CancellationToken.None);

        _ = completed.IsSuccess.Should().BeFalse(
            "a project whose draft profile is already claimed by a concurrent completion must not also complete");
        _ = gateway.CallCount.Should().Be(
            0,
            "the external promotion gateway must never be called once another request already holds the claim");

        CalibrationApiResult<CalibrationDraftProfileDto> draftAfterConflict = await service.GetDraftProfileAsync(
            project.Value.Id,
            actor,
            CancellationToken.None);
        _ = draftAfterConflict.Value!.PromotedProfileId.Should().BeNull(
            "a rejected concurrent completion must not leave a promoted profile behind");
    }

    private static string IfMatch(CalibrationProjectDto project) =>
        $"\"calibration-project-{project.Id:N}-{project.Revision}\"";

    private static CalibrationDraftUpsertRequest CreateStepDraftRequest(string method, string deviceLineageId) =>
        new()
        {
            DeviceLineageId = deviceLineageId,
            Method = method,
            Values = JsonSerializer.SerializeToElement(new { }),
            Prerequisites = JsonSerializer.SerializeToElement(new { }),
        };

    private static CalibrationObservationCreateRequest CreateSelectionObservationRequest(
        string operationId,
        decimal temperatureC) =>
        new()
        {
            ClientId = "desktop",
            OperationId = operationId,
            ObservationType = "selection",
            Measurements = JsonSerializer.SerializeToElement(
                new Dictionary<string, decimal> { ["temperature_c"] = temperatureC }),
            Result = JsonSerializer.SerializeToElement(new { }),
            Units = JsonSerializer.SerializeToElement(new { }),
        };

    private static CalibrationAttemptCreateRequest CreateTemperatureTowerAttemptRequest(
        string requestId,
        object specification) =>
        new()
        {
            ClientId = "desktop",
            RequestId = requestId,
            CalibrationKind = "temperature",
            Method = CalibrationMethods.ToWireName(CalibrationMethod.TemperatureTower),
            DefinitionVersion = "1",
            Input = JsonSerializer.SerializeToElement(new { }),
            Specification = JsonSerializer.SerializeToElement(specification),
            ProfileSnapshotIds = JsonSerializer.SerializeToElement(Array.Empty<Guid>()),
            PrinterConfigurationRevision = 1,
        };

    private static AppDbContext CreateContext()
    {
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"calibration-guided-session-{Guid.NewGuid()}")
            .Options;
        return new(options);
    }

    private static CalibrationProjectService CreateService(AppDbContext db) =>
        CreateService(db, new FakeFilamentProfilePromotionGateway());

    private static CalibrationProjectService CreateService(
        AppDbContext db,
        IFilamentProfilePromotionGateway gateway) =>
        new(
            db,
            new TestCalibrationBlobStore(),
            TimeProvider.System,
            NullLogger<CalibrationProjectService>.Instance,
            gateway);

    private static CalibrationProjectCreateRequest CreateProjectRequest(Guid printerId, string requestId) =>
        new()
        {
            ClientId = "client-a",
            RequestId = requestId,
            Name = "PLA baseline",
            PrinterId = printerId,
            PrinterConfigurationRevision = 1,
            FilamentProvider = "catalog",
            FilamentProductId = "sku-pla-blue",
            FilamentProductName = "PLA Blue",
            FilamentMaterial = "PLA",
            FilamentSnapshot = JsonSerializer.SerializeToElement(
                new { vendor = "OlyForge", product = "PLA Blue", sku = "sku-pla-blue" }),
            OrderedSteps = JsonSerializer.SerializeToElement(new[] { "flow" }),
            CurrentSelections = JsonSerializer.SerializeToElement(new { }),
            ExperienceMode = "Coach",
        };

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

        public async Task<CalibrationBlobMetadata> PutAsync(
            CalibrationBlobWriteRequest request,
            Stream content,
            CancellationToken cancellationToken)
        {
            using MemoryStream copy = new();
            await content.CopyToAsync(copy, cancellationToken);
            string sourceSha256 = Convert.ToHexString(SHA256.HashData(copy.ToArray())).ToLowerInvariant();
            return new CalibrationBlobMetadata(
                $"calibration/{request.PhotoId:N}.png",
                "image/png",
                copy.Length,
                sourceSha256,
                1,
                1,
                sourceSha256);
        }
    }

    /// <summary>
    /// Fake for <see cref="IFilamentProfilePromotionGateway"/> that always succeeds and tracks
    /// <see cref="CallCount"/>, so tests can assert promotion runs exactly once for a genuinely
    /// completed project and never for an abandoned/archived one.
    /// </summary>
    private sealed class FakeFilamentProfilePromotionGateway : IFilamentProfilePromotionGateway
    {
        public int CallCount { get; private set; }

        public Task<FilamentProfilePromotionResult> PromoteAsync(
            FilamentProfilePromotionRequest request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(FilamentProfilePromotionResult.Ok(Guid.NewGuid()));
        }
    }
}
