using System.Security.Cryptography;
using System.Text.Json;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.PrinterCalibration;
using Farm.Web.Api.Contracts;
using Farm.Web.Api.Services.Calibration;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Farm.Web.Api.Tests.Services.Calibration;

public sealed class CalibrationProjectServiceTests
{
    [Fact]
    public async Task CreateProjectAsync_EquivalentRequest_ReturnsExactIdempotentReplay()
    {
        await using AppDbContext db = CreateContext();
        Guid ownerId = Guid.NewGuid();
        Guid printerId = Guid.NewGuid();
        CalibrationProjectService service = CreateService(db, printerId);
        CalibrationActor actor = new(ownerId, ownerId.ToString(), false);
        CalibrationProjectCreateRequest request = CreateProjectRequest(printerId, "request-1");

        CalibrationApiResult<CalibrationProjectDto> first = await service.CreateProjectAsync(
            request,
            actor,
            CancellationToken.None);
        CalibrationApiResult<CalibrationProjectDto> replay = await service.CreateProjectAsync(
            request,
            actor,
            CancellationToken.None);

        _ = first.StatusCode.Should().Be(StatusCodes.Status201Created);
        _ = replay.Replayed.Should().BeTrue();
        _ = replay.Value!.Id.Should().Be(first.Value!.Id);
        _ = (await db.CalibrationProjects.CountAsync()).Should().Be(1);
        _ = (await db.CalibrationChanges.CountAsync()).Should().Be(1);
        string snapshot = (await db.PrinterConfigurationSnapshots.SingleAsync()).SanitizedSnapshotJson;
        _ = snapshot.Should().NotContain("serverUrl");
    }

    [Fact]
    public async Task CreateProjectAsync_DifferentOwner_CannotReadProject()
    {
        await using AppDbContext db = CreateContext();
        Guid printerId = Guid.NewGuid();
        Guid ownerId = Guid.NewGuid();
        CalibrationProjectService service = CreateService(db, printerId);
        CalibrationApiResult<CalibrationProjectDto> created = await service.CreateProjectAsync(
            CreateProjectRequest(printerId, "request-2"),
            new(ownerId, ownerId.ToString(), false),
            CancellationToken.None);

        CalibrationApiResult<CalibrationProjectDto> read = await service.GetProjectAsync(
            created.Value!.Id,
            new(Guid.NewGuid(), "other-user", false),
            false,
            CancellationToken.None);

        _ = read.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        _ = read.Code.Should().Be("calibration_resource_not_found");
    }

    [Fact]
    public async Task UpdateProjectAsync_StaleRevision_ReturnsSafeConflict()
    {
        await using AppDbContext db = CreateContext();
        Guid printerId = Guid.NewGuid();
        Guid ownerId = Guid.NewGuid();
        CalibrationActor actor = new(ownerId, ownerId.ToString(), false);
        CalibrationProjectService service = CreateService(db, printerId);
        CalibrationApiResult<CalibrationProjectDto> created = await service.CreateProjectAsync(
            CreateProjectRequest(printerId, "request-3"),
            actor,
            CancellationToken.None);
        Guid projectId = created.Value!.Id;

        CalibrationApiResult<CalibrationProjectDto> updated = await service.UpdateProjectAsync(
            projectId,
            new CalibrationProjectUpdateRequest { BaseRevision = 1, Name = "Updated name" },
            $"\"calibration-project-{projectId:N}-1\"",
            actor,
            CancellationToken.None);
        CalibrationApiResult<CalibrationProjectDto> conflict = await service.UpdateProjectAsync(
            projectId,
            new CalibrationProjectUpdateRequest { BaseRevision = 1, Name = "Stale name" },
            $"\"calibration-project-{projectId:N}-1\"",
            actor,
            CancellationToken.None);

        _ = updated.Value!.Revision.Should().Be(2);
        _ = conflict.StatusCode.Should().Be(StatusCodes.Status412PreconditionFailed);
        _ = conflict.Conflict!.CurrentRevision.Should().Be(2);
        _ = conflict.Conflict.ResolutionOptions.Should().Contain("refresh");
    }

    [Fact]
    public async Task UpdateProjectAsync_MissingPrecondition_ReturnsPreconditionRequired()
    {
        await using AppDbContext db = CreateContext();
        Guid printerId = Guid.NewGuid();
        Guid ownerId = Guid.NewGuid();
        CalibrationActor actor = new(ownerId, ownerId.ToString(), false);
        CalibrationProjectService service = CreateService(db, printerId);
        CalibrationApiResult<CalibrationProjectDto> created = await service.CreateProjectAsync(
            CreateProjectRequest(printerId, "request-4"),
            actor,
            CancellationToken.None);

        CalibrationApiResult<CalibrationProjectDto> result = await service.UpdateProjectAsync(
            created.Value!.Id,
            new CalibrationProjectUpdateRequest { Name = "No precondition" },
            null,
            actor,
            CancellationToken.None);

        _ = result.StatusCode.Should().Be(StatusCodes.Status428PreconditionRequired);
        _ = result.Code.Should().Be("precondition_required");
    }

    [Fact]
    public async Task UpdateProjectAsync_UnsafeOrderedSteps_ReturnsValidationWithoutMutation()
    {
        await using AppDbContext db = CreateContext();
        Guid printerId = Guid.NewGuid();
        CalibrationActor actor = new(Guid.NewGuid(), "owner", false);
        CalibrationProjectService service = CreateService(db, printerId);
        CalibrationApiResult<CalibrationProjectDto> created = await service.CreateProjectAsync(
            CreateProjectRequest(printerId, "unsafe-ordered-steps"),
            actor,
            CancellationToken.None);

        CalibrationApiResult<CalibrationProjectDto> result = await service.UpdateProjectAsync(
            created.Value!.Id,
            new CalibrationProjectUpdateRequest
            {
                BaseRevision = 1,
                Name = "Must not leak",
                OrderedSteps = JsonSerializer.SerializeToElement(new { api_key = "secret" }),
            },
            $"\"calibration-project-{created.Value.Id:N}-1\"",
            actor,
            CancellationToken.None);

        _ = result.StatusCode.Should().Be(StatusCodes.Status422UnprocessableEntity);
        _ = result.Code.Should().Be("profile_contains_credential");
        db.ChangeTracker.Clear();
        CalibrationProject persisted = await db.CalibrationProjects.SingleAsync();
        _ = persisted.Name.Should().Be("PLA baseline");
        _ = persisted.Revision.Should().Be(1);
    }

    [Fact]
    public async Task UpdateProjectAsync_UnsafeCurrentSelections_ReturnsValidationWithoutMutation()
    {
        await using AppDbContext db = CreateContext();
        Guid printerId = Guid.NewGuid();
        CalibrationActor actor = new(Guid.NewGuid(), "owner", false);
        CalibrationProjectService service = CreateService(db, printerId);
        CalibrationApiResult<CalibrationProjectDto> created = await service.CreateProjectAsync(
            CreateProjectRequest(printerId, "unsafe-current-selections"),
            actor,
            CancellationToken.None);

        CalibrationApiResult<CalibrationProjectDto> result = await service.UpdateProjectAsync(
            created.Value!.Id,
            new CalibrationProjectUpdateRequest
            {
                BaseRevision = 1,
                CurrentSelections = JsonSerializer.SerializeToElement(new { path = @"C:\private\profile.json" }),
            },
            $"\"calibration-project-{created.Value.Id:N}-1\"",
            actor,
            CancellationToken.None);

        _ = result.StatusCode.Should().Be(StatusCodes.Status422UnprocessableEntity);
        _ = result.Code.Should().Be("profile_contains_filesystem_path");
        db.ChangeTracker.Clear();
        _ = (await db.CalibrationProjects.SingleAsync()).Revision.Should().Be(1);
    }

    [Fact]
    public async Task ApplyChangesAsync_FailedMutationBeforeSuccess_DoesNotPersistFailedTrackedState()
    {
        await using AppDbContext db = CreateContext();
        Guid printerId = Guid.NewGuid();
        CalibrationActor actor = new(Guid.NewGuid(), "owner", false);
        CalibrationProjectService service = CreateService(db, printerId);
        CalibrationApiResult<CalibrationProjectDto> created = await service.CreateProjectAsync(
            CreateProjectRequest(printerId, "batch-state"),
            actor,
            CancellationToken.None);
        Guid projectId = created.Value!.Id;
        CalibrationSyncApplyRequest request = new(
        [
            CreateSyncMutation(
                projectId,
                "failed",
                new
                {
                    name = "Leaked name",
                    orderedSteps = new { password = "secret" },
                }),
            CreateSyncMutation(projectId, "successful", new { currentStep = "flow" }),
        ]);

        IReadOnlyList<CalibrationSyncMutationResultDto> results = await service.ApplyChangesAsync(
            request,
            actor,
            CancellationToken.None);

        _ = results.Select(result => result.Status).Should().Equal("invalid", "applied");
        db.ChangeTracker.Clear();
        CalibrationProject persisted = await db.CalibrationProjects.SingleAsync();
        _ = persisted.Name.Should().Be("PLA baseline");
        _ = persisted.CurrentStep.Should().Be("flow");
        _ = persisted.Revision.Should().Be(2);
    }

    [Fact]
    public async Task ApplyChangesAsync_NormalizedExactRetry_ReplaysAndPayloadMismatchConflicts()
    {
        await using AppDbContext db = CreateContext();
        Guid printerId = Guid.NewGuid();
        CalibrationActor actor = new(Guid.NewGuid(), "owner", false);
        CalibrationProjectService service = CreateService(db, printerId);
        CalibrationApiResult<CalibrationProjectDto> created = await service.CreateProjectAsync(
            CreateProjectRequest(printerId, "sync-replay"),
            actor,
            CancellationToken.None);
        Guid projectId = created.Value!.Id;

        CalibrationSyncMutationResultDto first = (await service.ApplyChangesAsync(
            new([CreateSyncMutation(projectId, " operation-1 ", new { name = "Updated" }, " desktop ")]),
            actor,
            CancellationToken.None)).Single();
        CalibrationSyncMutationResultDto replay = (await service.ApplyChangesAsync(
            new([CreateSyncMutation(projectId, "operation-1", new { name = "Updated" }, "desktop")]),
            actor,
            CancellationToken.None)).Single();
        CalibrationSyncMutationResultDto mismatch = (await service.ApplyChangesAsync(
            new([CreateSyncMutation(projectId, "operation-1", new { name = "Different" }, "desktop")]),
            actor,
            CancellationToken.None)).Single();

        _ = first.Status.Should().Be("applied");
        _ = replay.Status.Should().Be("replayed");
        _ = mismatch.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        _ = mismatch.Code.Should().Be("semantic_conflict");
        _ = (await db.CalibrationChanges.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task ApplyChangesAsync_ExactDeleteRetry_ReplaysDeletedRepresentation()
    {
        await using AppDbContext db = CreateContext();
        Guid printerId = Guid.NewGuid();
        CalibrationActor actor = new(Guid.NewGuid(), "owner", false);
        CalibrationProjectService service = CreateService(db, printerId);
        CalibrationApiResult<CalibrationProjectDto> created = await service.CreateProjectAsync(
            CreateProjectRequest(printerId, "sync-delete"),
            actor,
            CancellationToken.None);
        CalibrationSyncMutationRequest mutation = CreateSyncMutation(
            created.Value!.Id,
            "delete-operation",
            new { },
            operationType: "project.delete");

        CalibrationSyncMutationResultDto first = (await service.ApplyChangesAsync(
            new([mutation]),
            actor,
            CancellationToken.None)).Single();
        CalibrationSyncMutationResultDto replay = (await service.ApplyChangesAsync(
            new([mutation]),
            actor,
            CancellationToken.None)).Single();

        _ = first.Status.Should().Be("applied");
        _ = replay.Status.Should().Be("replayed");
        _ = replay.Result!.Value.GetRawText().Should().Be(first.Result!.Value.GetRawText());
    }

    [Fact]
    public async Task UploadPhotoAsync_RetryValidatesBytesAndMetadata()
    {
        await using AppDbContext db = CreateContext();
        Guid printerId = Guid.NewGuid();
        CalibrationActor actor = new(Guid.NewGuid(), "owner", false);
        CalibrationProjectService service = CreateService(db, printerId);
        CalibrationApiResult<CalibrationProjectDto> created = await service.CreateProjectAsync(
            CreateProjectRequest(printerId, "photo-replay"),
            actor,
            CancellationToken.None);
        Guid attemptId = await AddAttemptAsync(db, created.Value!.Id, actor.Subject);

        using MemoryStream photoStream1 = new([1, 2, 3]);
        CalibrationApiResult<CalibrationPhotoDto> first = await service.UploadPhotoAsync(
            attemptId,
            " upload-1 ",
            "photo.png",
            "image/png",
            null,
            "caption",
            1,
            photoStream1,
            actor,
            CancellationToken.None);
        using MemoryStream photoStream2 = new([1, 2, 3]);
        CalibrationApiResult<CalibrationPhotoDto> replay = await service.UploadPhotoAsync(
            attemptId,
            "upload-1",
            "photo.png",
            "image/png",
            null,
            "caption",
            1,
            photoStream2,
            actor,
            CancellationToken.None);
        using MemoryStream photoStream3 = new([1, 2, 3]);
        CalibrationApiResult<CalibrationPhotoDto> mismatch = await service.UploadPhotoAsync(
            attemptId,
            "upload-1",
            "photo.png",
            "image/png",
            null,
            "different caption",
            1,
            photoStream3,
            actor,
            CancellationToken.None);

        _ = first.StatusCode.Should().Be(StatusCodes.Status201Created);
        _ = replay.Replayed.Should().BeTrue();
        _ = replay.Value!.Id.Should().Be(first.Value!.Id);
        _ = mismatch.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        _ = mismatch.Code.Should().Be("idempotency_payload_mismatch");
        _ = (await db.CalibrationPhotos.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task ReconcilePendingPhotoDeletesAsync_PurgeKeepsPublicRevisionUnchanged()
    {
        await using AppDbContext db = CreateContext();
        Guid printerId = Guid.NewGuid();
        CalibrationActor actor = new(Guid.NewGuid(), "owner", false);
        CalibrationProjectService service = CreateService(db, printerId);
        CalibrationApiResult<CalibrationProjectDto> created = await service.CreateProjectAsync(
            CreateProjectRequest(printerId, "photo-purge"),
            actor,
            CancellationToken.None);
        Guid attemptId = await AddAttemptAsync(db, created.Value!.Id, actor.Subject);
        CalibrationPhoto photo = new()
        {
            Id = Guid.NewGuid(),
            ProjectId = created.Value.Id,
            AttemptId = attemptId,
            ClientUploadId = "purge-1",
            OpaqueStorageKey = "calibration/private.png",
            OriginalFileName = "private.png",
            ContentType = "image/png",
            SizeBytes = 3,
            Sha256 = new string('a', 64),
            Width = 1,
            Height = 1,
            Revision = 2,
            CreatedAtUtc = DateTime.UtcNow,
            CreatedBySubject = actor.Subject,
            DeletedAtUtc = DateTime.UtcNow,
            DeletedBySubject = actor.Subject,
            DeleteRequestedAtUtc = DateTime.UtcNow,
        };
        _ = db.CalibrationPhotos.Add(photo);
        _ = await db.SaveChangesAsync();

        int reconciled = await service.ReconcilePendingPhotoDeletesAsync(CancellationToken.None);

        _ = reconciled.Should().Be(1);
        db.ChangeTracker.Clear();
        CalibrationPhoto persisted = await db.CalibrationPhotos.SingleAsync();
        _ = persisted.PurgedAtUtc.Should().NotBeNull();
        _ = persisted.Revision.Should().Be(2);
    }

    [Fact]
    public async Task DeletePhotoAsync_ImmediatePurgeKeepsJournaledRevision()
    {
        await using AppDbContext db = CreateContext();
        Guid printerId = Guid.NewGuid();
        CalibrationActor actor = new(Guid.NewGuid(), "owner", false);
        CalibrationProjectService service = CreateService(db, printerId);
        CalibrationApiResult<CalibrationProjectDto> created = await service.CreateProjectAsync(
            CreateProjectRequest(printerId, "photo-delete"),
            actor,
            CancellationToken.None);
        Guid attemptId = await AddAttemptAsync(db, created.Value!.Id, actor.Subject);
        CalibrationPhoto photo = new()
        {
            Id = Guid.NewGuid(),
            ProjectId = created.Value.Id,
            AttemptId = attemptId,
            ClientUploadId = "delete-1",
            OpaqueStorageKey = "calibration/private.png",
            OriginalFileName = "private.png",
            ContentType = "image/png",
            SizeBytes = 3,
            Sha256 = new string('a', 64),
            Width = 1,
            Height = 1,
            Revision = 1,
            CreatedAtUtc = DateTime.UtcNow,
            CreatedBySubject = actor.Subject,
        };
        _ = db.CalibrationPhotos.Add(photo);
        _ = await db.SaveChangesAsync();

        CalibrationApiResult<CalibrationPhotoDto> result = await service.DeletePhotoAsync(
            photo.Id,
            1,
            $"\"calibration-photo-{photo.Id:N}-1\"",
            actor,
            CancellationToken.None);

        _ = result.Value!.Revision.Should().Be(2);
        db.ChangeTracker.Clear();
        CalibrationPhoto persisted = await db.CalibrationPhotos.SingleAsync();
        _ = persisted.PurgedAtUtc.Should().NotBeNull();
        _ = persisted.Revision.Should().Be(2);
        _ = (await db.CalibrationChanges
                .Where(change => change.EntityType == "photo" && change.EntityId == photo.Id)
                .Select(change => change.EntityRevision)
                .SingleAsync())
            .Should().Be(2);
    }

    [Fact]
    public async Task CreateGeneratedProfileAsync_UnsafeJson_ReturnsValidationWithoutPersistence()
    {
        await using AppDbContext db = CreateContext();
        Guid printerId = Guid.NewGuid();
        Guid ownerId = Guid.NewGuid();
        CalibrationActor actor = new(ownerId, ownerId.ToString(), false);
        CalibrationProjectService service = CreateService(db, printerId);
        CalibrationApiResult<CalibrationProjectDto> created = await service.CreateProjectAsync(
            CreateProjectRequest(printerId, "request-5"),
            actor,
            CancellationToken.None);
        Guid attemptId = Guid.NewGuid();
        _ = db.CalibrationAttempts.Add(new()
        {
            Id = attemptId,
            ProjectId = created.Value!.Id,
            Sequence = 1,
            CalibrationKind = "flow",
            Method = "manual",
            DefinitionVersion = "1",
            InputJson = "{}",
            SpecificationJson = "{}",
            SpecificationSha256 = "0".PadLeft(64, '0'),
            PrinterConfigurationSnapshotId = (await db.PrinterConfigurationSnapshots.SingleAsync()).Id,
            ProfileSnapshotIdsJson = "[]",
            AttemptRequestId = "attempt-1",
            CreatedAtUtc = DateTime.UtcNow,
            CreatedBySubject = actor.Subject,
        });
        _ = await db.SaveChangesAsync();

        CalibrationApiResult<GeneratedProfileRevisionDto> result =
            await service.CreateGeneratedProfileAsync(
                created.Value.Id,
                new GeneratedProfileRevisionCreateRequest
                {
                    ClientId = "client",
                    GenerationRequestId = "generation-1",
                    SourceAttemptId = attemptId,
                    ProfileType = "filament",
                    SchemaVersion = "1.0",
                    SlicerEngine = CalibrationContractConstants.SlicerEngine,
                    SlicerDistribution = CalibrationContractConstants.SlicerDistribution,
                    Name = "Unsafe",
                    NormalizedSettings = JsonSerializer.SerializeToElement(new { flow_ratio = 1.0 }),
                    ExactProfileJson = """{"api_key":"do-not-store"}""",
                    SourceProfileFingerprint = "a".PadLeft(64, 'a'),
                    GeneratorVersion = "test",
                },
                actor,
                CancellationToken.None);

        _ = result.StatusCode.Should().Be(StatusCodes.Status422UnprocessableEntity);
        _ = result.Code.Should().Be("profile_contains_credential");
        _ = (await db.GeneratedProfileRevisions.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task GetChangesAsync_AfterReturnedCursor_DoesNotReplayPriorChanges()
    {
        await using AppDbContext db = CreateContext();
        Guid printerId = Guid.NewGuid();
        Guid ownerId = Guid.NewGuid();
        CalibrationActor actor = new(ownerId, ownerId.ToString(), false);
        CalibrationProjectService service = CreateService(db, printerId);
        _ = await service.CreateProjectAsync(
            CreateProjectRequest(printerId, "request-6"),
            actor,
            CancellationToken.None);

        CalibrationApiResult<CalibrationChangesResponse> first = await service.GetChangesAsync(
            null,
            10,
            actor,
            CancellationToken.None);
        CalibrationApiResult<CalibrationChangesResponse> second = await service.GetChangesAsync(
            first.Value!.NextCursor,
            10,
            actor,
            CancellationToken.None);

        _ = first.Value.Changes.Should().ContainSingle();
        _ = second.Value!.Changes.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateAttemptAsync_ReusedOperationIdAgainstDifferentProject_ReturnsIdempotencyMismatch()
    {
        await using AppDbContext db = CreateContext();
        Guid printerId = Guid.NewGuid();
        CalibrationActor actor = new(Guid.NewGuid(), "owner", false);
        CalibrationProjectService service = CreateService(db, printerId);
        CalibrationApiResult<CalibrationProjectDto> projectA = await service.CreateProjectAsync(
            CreateProjectRequest(printerId, "project-a"),
            actor,
            CancellationToken.None);
        CalibrationApiResult<CalibrationProjectDto> projectB = await service.CreateProjectAsync(
            CreateProjectRequest(printerId, "project-b"),
            actor,
            CancellationToken.None);
        CalibrationAttemptCreateRequest sharedRequest = CreateAttemptRequest("shared-attempt");

        CalibrationApiResult<CalibrationAttemptDto> onA = await service.CreateAttemptAsync(
            projectA.Value!.Id,
            sharedRequest,
            actor,
            CancellationToken.None);
        CalibrationApiResult<CalibrationAttemptDto> crossProjectReplay = await service.CreateAttemptAsync(
            projectB.Value!.Id,
            sharedRequest,
            actor,
            CancellationToken.None);
        CalibrationApiResult<CalibrationAttemptDto> sameRouteReplay = await service.CreateAttemptAsync(
            projectA.Value.Id,
            sharedRequest,
            actor,
            CancellationToken.None);

        _ = onA.StatusCode.Should().Be(StatusCodes.Status201Created);
        _ = crossProjectReplay.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        _ = crossProjectReplay.Code.Should().Be("idempotency_payload_mismatch");
        _ = sameRouteReplay.Replayed.Should().BeTrue();
        _ = sameRouteReplay.Value!.Id.Should().Be(onA.Value!.Id);
        _ = (await db.CalibrationAttempts.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task AppendAttemptEventAsync_ReusedOperationIdAgainstDifferentAttempt_ReturnsIdempotencyMismatch()
    {
        await using AppDbContext db = CreateContext();
        Guid printerId = Guid.NewGuid();
        CalibrationActor actor = new(Guid.NewGuid(), "owner", false);
        CalibrationProjectService service = CreateService(db, printerId);
        CalibrationApiResult<CalibrationProjectDto> project = await service.CreateProjectAsync(
            CreateProjectRequest(printerId, "event-project"),
            actor,
            CancellationToken.None);
        Guid attemptA = await AddAttemptAsync(db, project.Value!.Id, actor.Subject);
        Guid attemptB = await AddAttemptAsync(db, project.Value.Id, actor.Subject, sequence: 2);
        CalibrationAttemptEventCreateRequest sharedRequest = new()
        {
            ClientId = "desktop",
            OperationId = "shared-event",
            EventType = "started",
        };

        CalibrationApiResult<CalibrationAttemptEventDto> onA = await service.AppendAttemptEventAsync(
            attemptA,
            sharedRequest,
            actor,
            CancellationToken.None);
        CalibrationApiResult<CalibrationAttemptEventDto> crossAttemptReplay =
            await service.AppendAttemptEventAsync(attemptB, sharedRequest, actor, CancellationToken.None);
        CalibrationApiResult<CalibrationAttemptEventDto> sameRouteReplay =
            await service.AppendAttemptEventAsync(attemptA, sharedRequest, actor, CancellationToken.None);

        _ = onA.StatusCode.Should().Be(StatusCodes.Status201Created);
        _ = crossAttemptReplay.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        _ = crossAttemptReplay.Code.Should().Be("idempotency_payload_mismatch");
        _ = sameRouteReplay.Replayed.Should().BeTrue();
        _ = sameRouteReplay.Value!.Id.Should().Be(onA.Value!.Id);
        _ = (await db.CalibrationAttemptEvents.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task AppendObservationAsync_ReusedOperationIdAgainstDifferentAttempt_ReturnsIdempotencyMismatch()
    {
        await using AppDbContext db = CreateContext();
        Guid printerId = Guid.NewGuid();
        CalibrationActor actor = new(Guid.NewGuid(), "owner", false);
        CalibrationProjectService service = CreateService(db, printerId);
        CalibrationApiResult<CalibrationProjectDto> project = await service.CreateProjectAsync(
            CreateProjectRequest(printerId, "observation-project"),
            actor,
            CancellationToken.None);
        Guid attemptA = await AddAttemptAsync(db, project.Value!.Id, actor.Subject);
        Guid attemptB = await AddAttemptAsync(db, project.Value.Id, actor.Subject, sequence: 2);
        CalibrationObservationCreateRequest sharedRequest = new()
        {
            ClientId = "desktop",
            OperationId = "shared-observation",
            ObservationType = "measurement",
            Measurements = JsonSerializer.SerializeToElement(new { flow = 0.98 }),
            Result = JsonSerializer.SerializeToElement(new { verdict = "pass" }),
            Units = JsonSerializer.SerializeToElement(new { flow = "ratio" }),
            Confidence = 0.9m,
        };

        CalibrationApiResult<CalibrationObservationDto> onA = await service.AppendObservationAsync(
            attemptA,
            sharedRequest,
            actor,
            CancellationToken.None);
        CalibrationApiResult<CalibrationObservationDto> crossAttemptReplay =
            await service.AppendObservationAsync(attemptB, sharedRequest, actor, CancellationToken.None);
        CalibrationApiResult<CalibrationObservationDto> sameRouteReplay =
            await service.AppendObservationAsync(attemptA, sharedRequest, actor, CancellationToken.None);

        _ = onA.StatusCode.Should().Be(StatusCodes.Status201Created);
        _ = crossAttemptReplay.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        _ = crossAttemptReplay.Code.Should().Be("idempotency_payload_mismatch");
        _ = sameRouteReplay.Replayed.Should().BeTrue();
        _ = sameRouteReplay.Value!.Id.Should().Be(onA.Value!.Id);
        _ = (await db.CalibrationObservations.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task CreateGeneratedProfileAsync_ReusedOperationIdAgainstDifferentProject_ReturnsIdempotencyMismatch()
    {
        await using AppDbContext db = CreateContext();
        Guid printerId = Guid.NewGuid();
        CalibrationActor actor = new(Guid.NewGuid(), "owner", false);
        CalibrationProjectService service = CreateService(db, printerId);
        CalibrationApiResult<CalibrationProjectDto> projectA = await service.CreateProjectAsync(
            CreateProjectRequest(printerId, "gp-project-a"),
            actor,
            CancellationToken.None);
        CalibrationApiResult<CalibrationProjectDto> projectB = await service.CreateProjectAsync(
            CreateProjectRequest(printerId, "gp-project-b"),
            actor,
            CancellationToken.None);
        Guid attemptA = await AddAttemptAsync(db, projectA.Value!.Id, actor.Subject);
        Guid attemptB = await AddAttemptAsync(db, projectB.Value!.Id, actor.Subject, sequence: 2);
        GeneratedProfileRevisionCreateRequest requestA = CreateGeneratedProfileRequest(attemptA, "shared-generation");
        GeneratedProfileRevisionCreateRequest requestB = CreateGeneratedProfileRequest(attemptB, "shared-generation");

        CalibrationApiResult<GeneratedProfileRevisionDto> onA = await service.CreateGeneratedProfileAsync(
            projectA.Value.Id,
            requestA,
            actor,
            CancellationToken.None);
        CalibrationApiResult<GeneratedProfileRevisionDto> crossProjectReplay =
            await service.CreateGeneratedProfileAsync(projectB.Value.Id, requestB, actor, CancellationToken.None);

        _ = onA.StatusCode.Should().Be(StatusCodes.Status201Created);
        _ = crossProjectReplay.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        _ = crossProjectReplay.Code.Should().Be("idempotency_payload_mismatch");
        _ = (await db.GeneratedProfileRevisions.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task RecordGeneratedProfileOperationAsync_ReusedOperationIdAgainstDifferentRevision_ReturnsIdempotencyMismatch()
    {
        await using AppDbContext db = CreateContext();
        Guid printerId = Guid.NewGuid();
        CalibrationActor actor = new(Guid.NewGuid(), "owner", false);
        CalibrationProjectService service = CreateService(db, printerId);
        CalibrationApiResult<CalibrationProjectDto> project = await service.CreateProjectAsync(
            CreateProjectRequest(printerId, "profile-op-project"),
            actor,
            CancellationToken.None);
        Guid attempt = await AddAttemptAsync(db, project.Value!.Id, actor.Subject);
        CalibrationApiResult<GeneratedProfileRevisionDto> revisionA = await service.CreateGeneratedProfileAsync(
            project.Value.Id,
            CreateGeneratedProfileRequest(attempt, "revision-a"),
            actor,
            CancellationToken.None);
        CalibrationApiResult<GeneratedProfileRevisionDto> revisionB = await service.CreateGeneratedProfileAsync(
            project.Value.Id,
            CreateGeneratedProfileRequest(attempt, "revision-b"),
            actor,
            CancellationToken.None);
        GeneratedProfileRevisionOperationRequest sharedRequest = new()
        {
            ClientId = "desktop",
            OperationId = "shared-export",
            ExportFormat = "orca-json",
        };

        CalibrationApiResult<GeneratedProfileRevisionDto> onA =
            await service.RecordGeneratedProfileOperationAsync(
                revisionA.Value!.Id,
                sharedRequest,
                "export",
                actor,
                CancellationToken.None);
        CalibrationApiResult<GeneratedProfileRevisionDto> crossRevisionReplay =
            await service.RecordGeneratedProfileOperationAsync(
                revisionB.Value!.Id,
                sharedRequest,
                "export",
                actor,
                CancellationToken.None);
        CalibrationApiResult<GeneratedProfileRevisionDto> sameRouteReplay =
            await service.RecordGeneratedProfileOperationAsync(
                revisionA.Value.Id,
                sharedRequest,
                "export",
                actor,
                CancellationToken.None);

        _ = onA.StatusCode.Should().Be(StatusCodes.Status201Created);
        _ = crossRevisionReplay.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        _ = crossRevisionReplay.Code.Should().Be("idempotency_payload_mismatch");
        _ = sameRouteReplay.Replayed.Should().BeTrue();
        _ = (await db.GeneratedProfileRevisionOperations.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task RecordGeneratedProfileOperationAsync_ReusedOperationIdAcrossExportAndPublish_ReturnsIdempotencyMismatch()
    {
        await using AppDbContext db = CreateContext();
        Guid printerId = Guid.NewGuid();
        CalibrationActor actor = new(Guid.NewGuid(), "owner", false);
        CalibrationProjectService service = CreateService(db, printerId);
        CalibrationApiResult<CalibrationProjectDto> project = await service.CreateProjectAsync(
            CreateProjectRequest(printerId, "publish-project"),
            actor,
            CancellationToken.None);
        Guid attempt = await AddAttemptAsync(db, project.Value!.Id, actor.Subject);
        CalibrationApiResult<GeneratedProfileRevisionDto> revision = await service.CreateGeneratedProfileAsync(
            project.Value.Id,
            CreateGeneratedProfileRequest(attempt, "publish-revision"),
            actor,
            CancellationToken.None);
        GeneratedProfileRevisionOperationRequest exportRequest = new()
        {
            ClientId = "desktop",
            OperationId = "shared-op-across-routes",
            ExportFormat = "orca-json",
        };
        GeneratedProfileRevisionOperationRequest publishRequest = new()
        {
            ClientId = "desktop",
            OperationId = "shared-op-across-routes",
            PublishedProfileId = Guid.NewGuid(),
        };

        CalibrationApiResult<GeneratedProfileRevisionDto> exported =
            await service.RecordGeneratedProfileOperationAsync(
                revision.Value!.Id,
                exportRequest,
                "export",
                actor,
                CancellationToken.None);
        CalibrationApiResult<GeneratedProfileRevisionDto> crossOperationTypeReplay =
            await service.RecordGeneratedProfileOperationAsync(
                revision.Value.Id,
                publishRequest,
                "publish",
                actor,
                CancellationToken.None);

        _ = exported.StatusCode.Should().Be(StatusCodes.Status201Created);
        _ = crossOperationTypeReplay.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        _ = crossOperationTypeReplay.Code.Should().Be("idempotency_payload_mismatch");
        _ = (await db.GeneratedProfileRevisionOperations.CountAsync()).Should().Be(1);
    }

    private static AppDbContext CreateContext()
    {
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"calibration-project-service-{Guid.NewGuid()}")
            .Options;
        return new(options);
    }

    private static CalibrationProjectService CreateService(AppDbContext db, Guid printerId) =>
        new(
            db,
            new TestPrinterContextService(printerId),
            new TestCalibrationBlobStore(),
            TimeProvider.System,
            NullLogger<CalibrationProjectService>.Instance);

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

    private static CalibrationSyncMutationRequest CreateSyncMutation(
        Guid projectId,
        string operationId,
        object payload,
        string clientId = "desktop",
        string operationType = "project.update") =>
        new()
        {
            ClientId = clientId,
            OperationId = operationId,
            OperationType = operationType,
            ProjectId = projectId,
            BaseRevision = 1,
            Payload = JsonSerializer.SerializeToElement(payload),
        };

    private static CalibrationAttemptCreateRequest CreateAttemptRequest(string requestId) =>
        new()
        {
            ClientId = "desktop",
            RequestId = requestId,
            CalibrationKind = "flow",
            Method = "manual",
            DefinitionVersion = "1",
            Input = JsonSerializer.SerializeToElement(new { flow = 0.98 }),
            Specification = JsonSerializer.SerializeToElement(new { target = 0.98 }),
            ProfileSnapshotIds = JsonSerializer.SerializeToElement(Array.Empty<Guid>()),
            PrinterConfigurationRevision = 1,
        };

    private static GeneratedProfileRevisionCreateRequest CreateGeneratedProfileRequest(
        Guid attemptId,
        string generationRequestId) =>
        new()
        {
            ClientId = "desktop",
            GenerationRequestId = generationRequestId,
            SourceAttemptId = attemptId,
            ProfileType = "filament",
            SchemaVersion = "1.0",
            SlicerEngine = CalibrationContractConstants.SlicerEngine,
            SlicerDistribution = CalibrationContractConstants.SlicerDistribution,
            Name = "Generated",
            NormalizedSettings = JsonSerializer.SerializeToElement(new { flow_ratio = 1.0 }),
            ExactProfileJson = """{"flow_ratio":1.0}""",
            SourceProfileFingerprint = new string('a', 64),
            GeneratorVersion = "test",
        };

    private static async Task<Guid> AddAttemptAsync(AppDbContext db, Guid projectId, string actorSubject) =>
        await AddAttemptAsync(db, projectId, actorSubject, sequence: 1);

    private static async Task<Guid> AddAttemptAsync(
        AppDbContext db,
        Guid projectId,
        string actorSubject,
        long sequence)
    {
        Guid attemptId = Guid.NewGuid();
        PrinterConfigurationSnapshot snapshot = await db.PrinterConfigurationSnapshots
            .Where(candidate => candidate.ProjectId == projectId)
            .OrderBy(candidate => candidate.CapturedAtUtc)
            .FirstAsync();
        _ = db.CalibrationAttempts.Add(new CalibrationAttempt
        {
            Id = attemptId,
            ProjectId = projectId,
            Sequence = sequence,
            CalibrationKind = "flow",
            Method = "manual",
            DefinitionVersion = "1",
            InputJson = "{}",
            SpecificationJson = "{}",
            SpecificationSha256 = new string('0', 64),
            PrinterConfigurationSnapshotId = snapshot.Id,
            ProfileSnapshotIdsJson = "[]",
            AttemptRequestId = $"attempt-{attemptId:N}",
            CreatedAtUtc = DateTime.UtcNow,
            CreatedBySubject = actorSubject,
        });
        _ = await db.SaveChangesAsync();
        return attemptId;
    }

    private sealed class TestPrinterContextService(Guid printerId) : ICalibrationContextResolver
    {
        public Task<CalibrationServiceResult<CalibrationContextDto>> GetContextAsync(
            Guid requestedPrinterId,
            long? configurationRevision,
            string capturedBySubject,
            CalibrationProfileAccessScope profileAccessScope,
            CancellationToken cancellationToken)
        {
            if (requestedPrinterId != printerId || configurationRevision != 1)
            {
                return Task.FromResult(new CalibrationServiceResult<CalibrationContextDto>(
                    null,
                    "printer_configuration_changed",
                    1));
            }

            CalibrationCandidateDto candidate = new()
            {
                Id = printerId,
                Eligible = true,
                ConfigurationRevision = 1,
                Firmware = new("Klipper", "Klipper", "Printer", "v1", null, null, DateTime.UtcNow, true),
                Slicer = new(
                    CalibrationContractConstants.SlicerEngine,
                    CalibrationContractConstants.SlicerDistribution,
                    CalibrationContractConstants.SlicerVersion,
                    CalibrationContractConstants.ProfileFormat),
            };
            CalibrationContextDto context = new(candidate)
            {
                CapturedAtUtc = DateTime.UtcNow,
                CapturedBySubject = capturedBySubject,
                Snapshot = new()
                {
                    PrinterId = printerId,
                    ConfigurationRevision = 1,
                    CapturedAtUtc = DateTime.UtcNow,
                    CapturedBySubject = capturedBySubject,
                    Firmware = candidate.Firmware,
                    Slicer = candidate.Slicer,
                    SnapshotSha256 = new string('b', 64),
                },
            };
            return Task.FromResult(new CalibrationServiceResult<CalibrationContextDto>(context));
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
}
