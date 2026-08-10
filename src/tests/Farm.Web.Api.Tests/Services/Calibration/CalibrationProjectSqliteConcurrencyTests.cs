using System.Data.Common;
using System.Text.Json;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.PrinterCalibration;
using Farm.Web.Api.Contracts;
using Farm.Web.Api.Services.Calibration;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace Farm.Web.Api.Tests.Services.Calibration;

public sealed class CalibrationProjectSqliteConcurrencyTests
{
    [Fact]
    public async Task UpdateProjectAsync_ConcurrentSqliteContexts_ReturnsRefreshedConflictWithoutJournalDuplicate()
    {
        await using SqliteCalibrationStore store = await SqliteCalibrationStore.CreateAsync();
        Guid ownerId = Guid.NewGuid();
        Guid projectId = await store.SeedProjectAsync(ownerId);

        await using AppDbContext winningContext = store.CreateContext();
        await using AppDbContext staleContext = store.CreateContext();
        _ = await staleContext.CalibrationProjects.SingleAsync(project => project.Id == projectId);

        CalibrationActor actor = new(ownerId, "owner", false);
        CalibrationApiResult<CalibrationProjectDto> winner = await CreateService(winningContext, store.PrinterId)
            .UpdateProjectAsync(
                projectId,
                new CalibrationProjectUpdateRequest { BaseRevision = 1, Name = "Winning name" },
                ProjectEtag(projectId, 1),
                actor,
                CancellationToken.None);
        CalibrationApiResult<CalibrationProjectDto> conflict = await CreateService(staleContext, store.PrinterId)
            .UpdateProjectAsync(
                projectId,
                new CalibrationProjectUpdateRequest { BaseRevision = 1, Name = "Stale name" },
                ProjectEtag(projectId, 1),
                actor,
                CancellationToken.None);

        _ = winner.Value!.Revision.Should().Be(2);
        _ = conflict.StatusCode.Should().Be(StatusCodes.Status412PreconditionFailed);
        _ = conflict.Code.Should().Be("revision_conflict");
        _ = conflict.Conflict!.CurrentRevision.Should().Be(2);
        _ = ((CalibrationProjectDto)conflict.Conflict.CurrentRepresentation!).Name.Should().Be("Winning name");

        await using AppDbContext verificationContext = store.CreateContext();
        _ = (await verificationContext.CalibrationChanges.CountAsync()).Should().Be(1);
        _ = (await verificationContext.CalibrationProjects.SingleAsync(project => project.Id == projectId))
            .Name.Should().Be("Winning name");
    }

    [Fact]
    public async Task UpsertDraftAsync_ConcurrentSqliteContexts_ReturnsRefreshedConflictWithoutJournalDuplicate()
    {
        await using SqliteCalibrationStore store = await SqliteCalibrationStore.CreateAsync();
        Guid ownerId = Guid.NewGuid();
        Guid projectId = await store.SeedProjectAsync(ownerId);
        CalibrationActor actor = new(ownerId, "owner", false);
        CalibrationDraftUpsertRequest initial = CreateDraftRequest();

        await using (AppDbContext initialContext = store.CreateContext())
        {
            CalibrationApiResult<CalibrationDraftDto> created = await CreateService(initialContext, store.PrinterId)
                .UpsertDraftAsync(projectId, "flow", initial, null, actor, CancellationToken.None);
            _ = created.Value!.Revision.Should().Be(1);
        }

        await using AppDbContext winningContext = store.CreateContext();
        await using AppDbContext staleContext = store.CreateContext();
        CalibrationDraft staleDraft = await staleContext.CalibrationDrafts.SingleAsync();
        CalibrationDraftUpsertRequest winnerRequest = CreateDraftRequest(method: "winner", baseRevision: 1);
        CalibrationDraftUpsertRequest staleRequest = CreateDraftRequest(method: "stale", baseRevision: 1);

        CalibrationApiResult<CalibrationDraftDto> winner = await CreateService(winningContext, store.PrinterId)
            .UpsertDraftAsync(
                projectId,
                "flow",
                winnerRequest,
                DraftEtag(staleDraft.Id, 1),
                actor,
                CancellationToken.None);
        CalibrationApiResult<CalibrationDraftDto> conflict = await CreateService(staleContext, store.PrinterId)
            .UpsertDraftAsync(
                projectId,
                "flow",
                staleRequest,
                DraftEtag(staleDraft.Id, 1),
                actor,
                CancellationToken.None);

        _ = winner.Value!.Revision.Should().Be(2);
        _ = conflict.StatusCode.Should().Be(StatusCodes.Status412PreconditionFailed);
        _ = conflict.Code.Should().Be("revision_conflict");
        _ = ((CalibrationDraftDto)conflict.Conflict!.CurrentRepresentation!).Method.Should().Be("winner");

        await using AppDbContext verificationContext = store.CreateContext();
        _ = (await verificationContext.CalibrationChanges.CountAsync()).Should().Be(2);
        _ = (await verificationContext.CalibrationDrafts.SingleAsync()).Method.Should().Be("winner");
    }

    [Fact]
    public async Task UpsertDraftAsync_ConcurrentFirstCreate_UsesUniqueIndexAndReturnsReplay()
    {
        await using SqliteCalibrationStore store = await SqliteCalibrationStore.CreateAsync();
        Guid ownerId = Guid.NewGuid();
        Guid projectId = await store.SeedProjectAsync(ownerId);
        DraftReadBarrierInterceptor barrier = new();
        await using AppDbContext firstContext = store.CreateContext(barrier);
        await using AppDbContext secondContext = store.CreateContext(barrier);
        CalibrationActor actor = new(ownerId, "owner", false);
        CalibrationDraftUpsertRequest request = CreateDraftRequest(deviceLineageId: " device-a ");

        Task<CalibrationApiResult<CalibrationDraftDto>> first = CreateService(firstContext, store.PrinterId)
            .UpsertDraftAsync(projectId, "flow", request, null, actor, CancellationToken.None);
        Task<CalibrationApiResult<CalibrationDraftDto>> second = CreateService(secondContext, store.PrinterId)
            .UpsertDraftAsync(projectId, "flow", request, null, actor, CancellationToken.None);
        CalibrationApiResult<CalibrationDraftDto>[] results = await Task.WhenAll(first, second);

        _ = results.Should().OnlyContain(result => result.IsSuccess);
        _ = results.Count(result => result.Replayed).Should().Be(1);
        _ = results.Select(result => result.Value!.Id).Distinct().Should().ContainSingle();

        await using AppDbContext verificationContext = store.CreateContext();
        _ = (await verificationContext.CalibrationDrafts.CountAsync()).Should().Be(1);
        _ = (await verificationContext.CalibrationChanges.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task UpsertDraftAsync_AfterSoftDelete_RecreatesActiveDraft()
    {
        await using SqliteCalibrationStore store = await SqliteCalibrationStore.CreateAsync();
        Guid ownerId = Guid.NewGuid();
        Guid projectId = await store.SeedProjectAsync(ownerId);
        await using AppDbContext context = store.CreateContext();
        CalibrationActor actor = new(ownerId, "owner", false);
        CalibrationProjectService service = CreateService(context, store.PrinterId);
        CalibrationDraftUpsertRequest request = CreateDraftRequest();

        CalibrationApiResult<CalibrationDraftDto> created = await service.UpsertDraftAsync(
            projectId,
            "flow",
            request,
            null,
            actor,
            CancellationToken.None);
        CalibrationApiResult<CalibrationDraftDto> deleted = await service.DeleteDraftAsync(
            projectId,
            "flow",
            request.DeviceLineageId,
            created.Value!.Revision,
            DraftEtag(created.Value.Id, created.Value.Revision),
            actor,
            CancellationToken.None);
        CalibrationApiResult<CalibrationDraftDto> recreated = await service.UpsertDraftAsync(
            projectId,
            "flow",
            request,
            null,
            actor,
            CancellationToken.None);

        _ = deleted.IsSuccess.Should().BeTrue();
        _ = recreated.IsSuccess.Should().BeTrue();
        _ = recreated.Value!.Id.Should().NotBe(created.Value.Id);
        _ = (await context.CalibrationDrafts.CountAsync()).Should().Be(2);
        _ = (await context.CalibrationDrafts.CountAsync(draft => draft.DeletedAtUtc == null)).Should().Be(1);
    }

    [Fact]
    public async Task CreateProjectAsync_AdminIdempotencyIsIsolatedByActor()
    {
        await using SqliteCalibrationStore store = await SqliteCalibrationStore.CreateAsync();
        await using AppDbContext context = store.CreateContext();
        CalibrationProjectService service = CreateService(context, store.PrinterId);
        CalibrationActor firstAdmin = new(Guid.NewGuid(), "admin-one", true);
        CalibrationActor secondAdmin = new(Guid.NewGuid(), "admin-two", true);
        CalibrationProjectCreateRequest request = CreateProjectRequest(store.PrinterId, "shared-operation");

        CalibrationApiResult<CalibrationProjectDto> first = await service.CreateProjectAsync(
            request,
            firstAdmin,
            CancellationToken.None);
        CalibrationApiResult<CalibrationProjectDto> second = await service.CreateProjectAsync(
            request,
            secondAdmin,
            CancellationToken.None);

        _ = first.StatusCode.Should().Be(StatusCodes.Status201Created);
        _ = second.StatusCode.Should().Be(StatusCodes.Status201Created);
        _ = second.Replayed.Should().BeFalse();
        _ = second.Value!.Id.Should().NotBe(first.Value!.Id);
        _ = (await context.CalibrationIdempotencyRecords
            .Select(record => record.Scope)
            .Distinct()
            .CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task GetChangesAsync_ConcurrentSqliteWritersAndReconnect_ReturnsContiguousChanges()
    {
        await using SqliteCalibrationStore store = await SqliteCalibrationStore.CreateAsync();
        Guid ownerId = Guid.NewGuid();
        CalibrationActor actor = new(ownerId, "owner", false);
        TwoCallerPrinterContextService printerContext = new(store.PrinterId);
        await using AppDbContext firstWriterContext = store.CreateContext();
        await using AppDbContext secondWriterContext = store.CreateContext();
        CalibrationProjectService firstWriter = CreateService(firstWriterContext, printerContext);
        CalibrationProjectService secondWriter = CreateService(secondWriterContext, printerContext);

        Task<CalibrationApiResult<CalibrationProjectDto>> first = firstWriter.CreateProjectAsync(
            CreateProjectRequest(store.PrinterId, "concurrent-1"),
            actor,
            CancellationToken.None);
        Task<CalibrationApiResult<CalibrationProjectDto>> second = secondWriter.CreateProjectAsync(
            CreateProjectRequest(store.PrinterId, "concurrent-2"),
            actor,
            CancellationToken.None);
        CalibrationApiResult<CalibrationProjectDto>[] created = await Task.WhenAll(first, second);
        _ = created.Should().OnlyContain(result => result.StatusCode == StatusCodes.Status201Created);

        await using AppDbContext firstReaderContext = store.CreateContext();
        CalibrationApiResult<CalibrationChangesResponse> firstPage = await CreateService(
                firstReaderContext,
                store.PrinterId)
            .GetChangesAsync(null, 1, actor, CancellationToken.None);
        await using AppDbContext reconnectContext = store.CreateContext();
        CalibrationApiResult<CalibrationChangesResponse> reconnectPage = await CreateService(
                reconnectContext,
                store.PrinterId)
            .GetChangesAsync(firstPage.Value!.NextCursor, 1, actor, CancellationToken.None);

        long[] sequences = firstPage.Value.Changes
            .Concat(reconnectPage.Value!.Changes)
            .Select(change => change.Sequence)
            .Order()
            .ToArray();
        _ = sequences.Should().Equal(1, 2);
        _ = reconnectPage.Value.HasMore.Should().BeFalse();
    }

    [Fact]
    public async Task CreateAttemptAsync_ChangeFeedPreservesCausalAggregateOrder()
    {
        await using SqliteCalibrationStore store = await SqliteCalibrationStore.CreateAsync();
        await using AppDbContext context = store.CreateContext();
        CalibrationActor actor = new(Guid.NewGuid(), "owner", false);
        CalibrationProjectService service = CreateService(context, store.PrinterId);
        CalibrationApiResult<CalibrationProjectDto> project = await service.CreateProjectAsync(
            CreateProjectRequest(store.PrinterId, "project"),
            actor,
            CancellationToken.None);

        CalibrationApiResult<CalibrationAttemptDto> attempt = await service.CreateAttemptAsync(
            project.Value!.Id,
            new CalibrationAttemptCreateRequest
            {
                ClientId = "desktop",
                RequestId = "attempt",
                CalibrationKind = "flow",
                Method = "manual",
                DefinitionVersion = "1",
                Input = JsonSerializer.SerializeToElement(new { flow = 0.98 }),
                Specification = JsonSerializer.SerializeToElement(new { target = 0.98 }),
                ProfileSnapshotIds = JsonSerializer.SerializeToElement(Array.Empty<Guid>()),
                PrinterConfigurationRevision = 1,
            },
            actor,
            CancellationToken.None);
        CalibrationApiResult<CalibrationChangesResponse> changes = await service.GetChangesAsync(
            null,
            10,
            actor,
            CancellationToken.None);

        _ = attempt.StatusCode.Should().Be(StatusCodes.Status201Created);
        _ = changes.Value!.Changes.Select(change => change.EntityType)
            .Should().Equal("project", "attempt", "orchestration");
    }

    [Fact]
    public async Task AppendAttemptEventAsync_ConcurrentDistinctOperations_AllocateUniqueSequences()
    {
        await using SqliteCalibrationStore store = await SqliteCalibrationStore.CreateAsync();
        CalibrationActor actor = new(Guid.NewGuid(), "owner", false);
        Guid attemptId;
        await using (AppDbContext setupContext = store.CreateContext())
        {
            CalibrationProjectService setupService = CreateService(setupContext, store.PrinterId);
            CalibrationApiResult<CalibrationProjectDto> project = await setupService.CreateProjectAsync(
                CreateProjectRequest(store.PrinterId, "event-project"),
                actor,
                CancellationToken.None);
            CalibrationApiResult<CalibrationAttemptDto> attempt = await setupService.CreateAttemptAsync(
                project.Value!.Id,
                new CalibrationAttemptCreateRequest
                {
                    ClientId = "desktop",
                    RequestId = "event-attempt",
                    CalibrationKind = "flow",
                    Method = "manual",
                    DefinitionVersion = "1",
                    Input = JsonSerializer.SerializeToElement(new { }),
                    Specification = JsonSerializer.SerializeToElement(new { }),
                    ProfileSnapshotIds = JsonSerializer.SerializeToElement(Array.Empty<Guid>()),
                    PrinterConfigurationRevision = 1,
                },
                actor,
                CancellationToken.None);
            attemptId = attempt.Value!.Id;
        }

        EventSequenceReadBarrierInterceptor barrier = new();
        await using AppDbContext firstContext = store.CreateContext(barrier);
        await using AppDbContext secondContext = store.CreateContext(barrier);
        Task<CalibrationApiResult<CalibrationAttemptEventDto>> first =
            CreateService(firstContext, store.PrinterId).AppendAttemptEventAsync(
                attemptId,
                CreateEventRequest("event-1"),
                actor,
                CancellationToken.None);
        Task<CalibrationApiResult<CalibrationAttemptEventDto>> second =
            CreateService(secondContext, store.PrinterId).AppendAttemptEventAsync(
                attemptId,
                CreateEventRequest("event-2"),
                actor,
                CancellationToken.None);

        CalibrationApiResult<CalibrationAttemptEventDto>[] results = await Task.WhenAll(first, second);

        _ = results.Should().OnlyContain(result => result.StatusCode == StatusCodes.Status201Created);
        _ = results.Should().OnlyContain(result => !result.Replayed);
        _ = results.Select(result => result.Value!.Sequence).Order().Should().Equal(1, 2);
        await using AppDbContext verificationContext = store.CreateContext();
        _ = (await verificationContext.CalibrationAttemptEvents.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task CreateGeneratedProfileAsync_ConcurrentDistinctOperations_AllocateUniqueRevisions()
    {
        await using SqliteCalibrationStore store = await SqliteCalibrationStore.CreateAsync();
        CalibrationActor actor = new(Guid.NewGuid(), "owner", false);
        Guid projectId;
        Guid attemptId;
        await using (AppDbContext setupContext = store.CreateContext())
        {
            CalibrationProjectService setupService = CreateService(setupContext, store.PrinterId);
            CalibrationApiResult<CalibrationProjectDto> project = await setupService.CreateProjectAsync(
                CreateProjectRequest(store.PrinterId, "profile-project"),
                actor,
                CancellationToken.None);
            CalibrationApiResult<CalibrationAttemptDto> attempt = await setupService.CreateAttemptAsync(
                project.Value!.Id,
                new CalibrationAttemptCreateRequest
                {
                    ClientId = "desktop",
                    RequestId = "profile-attempt",
                    CalibrationKind = "flow",
                    Method = "manual",
                    DefinitionVersion = "1",
                    Input = JsonSerializer.SerializeToElement(new { }),
                    Specification = JsonSerializer.SerializeToElement(new { }),
                    ProfileSnapshotIds = JsonSerializer.SerializeToElement(Array.Empty<Guid>()),
                    PrinterConfigurationRevision = 1,
                },
                actor,
                CancellationToken.None);
            projectId = project.Value.Id;
            attemptId = attempt.Value!.Id;
        }

        GeneratedProfileRevisionReadBarrierInterceptor barrier = new();
        await using AppDbContext firstContext = store.CreateContext(barrier);
        await using AppDbContext secondContext = store.CreateContext(barrier);
        Task<CalibrationApiResult<GeneratedProfileRevisionDto>> first =
            CreateService(firstContext, store.PrinterId).CreateGeneratedProfileAsync(
                projectId,
                CreateGeneratedProfileRequest(attemptId, "profile-1", "First profile", 0.98m),
                actor,
                CancellationToken.None);
        Task<CalibrationApiResult<GeneratedProfileRevisionDto>> second =
            CreateService(secondContext, store.PrinterId).CreateGeneratedProfileAsync(
                projectId,
                CreateGeneratedProfileRequest(attemptId, "profile-2", "Second profile", 1.02m),
                actor,
                CancellationToken.None);

        CalibrationApiResult<GeneratedProfileRevisionDto>[] results = await Task.WhenAll(first, second);

        _ = results.Should().OnlyContain(result => result.StatusCode == StatusCodes.Status201Created);
        _ = results.Should().OnlyContain(result => !result.Replayed);
        _ = results.Select(result => result.Value!.RevisionNumber).Order().Should().Equal(1, 2);
        await using AppDbContext verificationContext = store.CreateContext();
        GeneratedProfileRevision[] revisions = await verificationContext.GeneratedProfileRevisions
            .OrderBy(revision => revision.RevisionNumber)
            .ToArrayAsync();
        _ = revisions.Should().HaveCount(2);
        _ = revisions.Select(revision => revision.Name).Should().BeEquivalentTo("First profile", "Second profile");
    }

    [Fact]
    public async Task UploadPhotoAsync_ConcurrentExactRetry_ReplaysSinglePhoto()
    {
        await using SqliteCalibrationStore store = await SqliteCalibrationStore.CreateAsync();
        CalibrationActor actor = new(Guid.NewGuid(), "owner", false);
        Guid attemptId;
        await using (AppDbContext setupContext = store.CreateContext())
        {
            CalibrationProjectService setupService = CreateService(setupContext, store.PrinterId);
            CalibrationApiResult<CalibrationProjectDto> project = await setupService.CreateProjectAsync(
                CreateProjectRequest(store.PrinterId, "photo-project"),
                actor,
                CancellationToken.None);
            CalibrationApiResult<CalibrationAttemptDto> attempt = await setupService.CreateAttemptAsync(
                project.Value!.Id,
                new CalibrationAttemptCreateRequest
                {
                    ClientId = "desktop",
                    RequestId = "photo-attempt",
                    CalibrationKind = "flow",
                    Method = "manual",
                    DefinitionVersion = "1",
                    Input = JsonSerializer.SerializeToElement(new { }),
                    Specification = JsonSerializer.SerializeToElement(new { }),
                    ProfileSnapshotIds = JsonSerializer.SerializeToElement(Array.Empty<Guid>()),
                    PrinterConfigurationRevision = 1,
                },
                actor,
                CancellationToken.None);
            attemptId = attempt.Value!.Id;
        }

        IdempotencyReadBarrierInterceptor barrier = new();
        await using AppDbContext firstContext = store.CreateContext(barrier);
        await using AppDbContext secondContext = store.CreateContext(barrier);
        Task<CalibrationApiResult<CalibrationPhotoDto>> first =
            CreateService(firstContext, store.PrinterId).UploadPhotoAsync(
                attemptId,
                "upload-1",
                "photo.png",
                "image/png",
                null,
                "caption",
                0,
                new MemoryStream([1]),
                actor,
                CancellationToken.None);
        Task<CalibrationApiResult<CalibrationPhotoDto>> second =
            CreateService(secondContext, store.PrinterId).UploadPhotoAsync(
                attemptId,
                "upload-1",
                "photo.png",
                "image/png",
                null,
                "caption",
                0,
                new MemoryStream([1]),
                actor,
                CancellationToken.None);

        CalibrationApiResult<CalibrationPhotoDto>[] results = await Task.WhenAll(first, second);

        _ = results.Should().OnlyContain(result => result.IsSuccess);
        _ = results.Count(result => result.Replayed).Should().Be(1);
        _ = results.Select(result => result.Value!.Id).Distinct().Should().ContainSingle();
        await using AppDbContext verificationContext = store.CreateContext();
        _ = (await verificationContext.CalibrationPhotos.CountAsync()).Should().Be(1);
    }

    private static CalibrationProjectService CreateService(AppDbContext context, Guid printerId) =>
        CreateService(context, new StaticPrinterContextService(printerId));

    private static CalibrationProjectService CreateService(
        AppDbContext context,
        IPrinterCalibrationContextService printerContext) =>
        new(
            context,
            printerContext,
            new TestCalibrationBlobStore(),
            TimeProvider.System,
            NullLogger<CalibrationProjectService>.Instance);

    private static CalibrationDraftUpsertRequest CreateDraftRequest(
        string method = "manual",
        long? baseRevision = null,
        string deviceLineageId = "device-a") =>
        new()
        {
            BaseRevision = baseRevision,
            DeviceLineageId = deviceLineageId,
            Method = method,
            Values = JsonSerializer.SerializeToElement(new { flow = 0.98 }),
            Prerequisites = JsonSerializer.SerializeToElement(new { nozzle = "clean" }),
        };

    private static CalibrationProjectCreateRequest CreateProjectRequest(Guid printerId, string requestId) =>
        new()
        {
            ClientId = "desktop",
            RequestId = requestId,
            Name = "PLA baseline",
            PrinterId = printerId,
            PrinterConfigurationRevision = 1,
            FilamentProvider = "catalog",
            FilamentProductId = "pla-blue",
            FilamentProductName = "PLA Blue",
            FilamentMaterial = "PLA",
            FilamentSnapshot = JsonSerializer.SerializeToElement(new { product = "PLA Blue" }),
            OrderedSteps = JsonSerializer.SerializeToElement(new[] { "flow" }),
            CurrentSelections = JsonSerializer.SerializeToElement(new { }),
            ExperienceMode = "Coach",
        };

    private static CalibrationAttemptEventCreateRequest CreateEventRequest(string operationId) =>
        new()
        {
            ClientId = "desktop",
            OperationId = operationId,
            EventType = "started",
        };

    private static GeneratedProfileRevisionCreateRequest CreateGeneratedProfileRequest(
        Guid attemptId,
        string operationId,
        string name,
        decimal flowRatio) =>
        new()
        {
            ClientId = "desktop",
            GenerationRequestId = operationId,
            SourceAttemptId = attemptId,
            ProfileType = "filament",
            SchemaVersion = "1.0",
            SlicerEngine = CalibrationContractConstants.SlicerEngine,
            SlicerDistribution = CalibrationContractConstants.SlicerDistribution,
            Name = name,
            NormalizedSettings = JsonSerializer.SerializeToElement(new { flow_ratio = flowRatio }),
            ExactProfileJson = JsonSerializer.Serialize(new { flow_ratio = flowRatio }),
            SourceProfileFingerprint = new string('a', 64),
            GeneratorVersion = "test",
            FlowRatio = flowRatio,
        };

    private static string ProjectEtag(Guid projectId, long revision) =>
        $"\"calibration-project-{projectId:N}-{revision}\"";

    private static string DraftEtag(Guid draftId, long revision) =>
        $"\"calibration-draft-{draftId:N}-{revision}\"";

    private sealed class SqliteCalibrationStore : IAsyncDisposable
    {
        private SqliteCalibrationStore(string databasePath, Guid printerId)
        {
            DatabasePath = databasePath;
            PrinterId = printerId;
            ConnectionString = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                DefaultTimeout = 30,
                Pooling = false,
            }.ToString();
        }

        public string ConnectionString { get; }

        public string DatabasePath { get; }

        public Guid PrinterId { get; }

        public static async Task<SqliteCalibrationStore> CreateAsync()
        {
            SqliteCalibrationStore store = new(
                Path.Join(Path.GetTempPath(), $"calibration-concurrency-{Guid.NewGuid():N}.db"),
                Guid.NewGuid());
            await using AppDbContext context = store.CreateContext();
            _ = await context.Database.EnsureCreatedAsync();
            if (!await context.CalibrationChangeFeedStates.AnyAsync())
            {
                _ = context.CalibrationChangeFeedStates.Add(new CalibrationChangeFeedState { Id = 1 });
            }

            Guid manufacturerId = Guid.NewGuid();
            Guid modelId = Guid.NewGuid();
            _ = context.Manufacturers.Add(new Manufacturer
            {
                Id = manufacturerId,
                Name = "Calibration test manufacturer",
            });
            _ = context.PrinterModels.Add(new PrinterModel
            {
                Id = modelId,
                ManufacturerId = manufacturerId,
                Name = "Calibration test model",
            });
            _ = context.Printers.Add(new Printer
            {
                Id = store.PrinterId,
                Name = "Calibration test printer",
                ServerUrl = $"http://{store.PrinterId:N}.test",
                BackendPort = 7125,
                ManufacturerId = manufacturerId,
                ModelId = modelId,
            });
            _ = await context.SaveChangesAsync();
            return store;
        }

        public AppDbContext CreateContext(params IInterceptor[] interceptors)
        {
            DbContextOptionsBuilder<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(ConnectionString);
            if (interceptors.Length > 0)
            {
                _ = options.AddInterceptors(interceptors);
            }

            return new AppDbContext(options.Options);
        }

        public async Task<Guid> SeedProjectAsync(Guid ownerId)
        {
            await using AppDbContext context = CreateContext();
            Guid projectId = Guid.NewGuid();
            DateTime nowUtc = DateTime.UtcNow;
            _ = context.CalibrationProjects.Add(new CalibrationProject
            {
                Id = projectId,
                OwnerUserId = ownerId,
                Name = "Seed project",
                PrinterId = PrinterId,
                FilamentProvider = "catalog",
                FilamentProductId = $"product-{projectId:N}",
                FilamentProductName = "PLA",
                FilamentMaterial = "PLA",
                FilamentSnapshotJson = "{}",
                OrderedStepsJson = "[]",
                CurrentSelectionsJson = "{}",
                CreateRequestId = $"seed-{projectId:N}",
                CreatedAtUtc = nowUtc,
                UpdatedAtUtc = nowUtc,
                CreatedBySubject = "seed",
                UpdatedBySubject = "seed",
            });
            _ = await context.SaveChangesAsync();
            return projectId;
        }

        public ValueTask DisposeAsync()
        {
            File.Delete(DatabasePath);
            return ValueTask.CompletedTask;
        }
    }

    private class StaticPrinterContextService(Guid printerId) : IPrinterCalibrationContextService
    {
        public Task<CalibrationServiceResult<IReadOnlyList<CalibrationCandidateDto>>> GetCandidatesAsync(
            CalibrationProfileAccessScope profileAccessScope,
            CancellationToken cancellationToken) =>
            Task.FromResult(new CalibrationServiceResult<IReadOnlyList<CalibrationCandidateDto>>([]));

        public virtual Task<CalibrationServiceResult<CalibrationContextDto>> GetContextAsync(
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

            return Task.FromResult(new CalibrationServiceResult<CalibrationContextDto>(
                CreateContext(printerId, capturedBySubject)));
        }

        private static CalibrationContextDto CreateContext(Guid printerId, string subject)
        {
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
            return new CalibrationContextDto(candidate)
            {
                CapturedAtUtc = DateTime.UtcNow,
                CapturedBySubject = subject,
                Snapshot = new()
                {
                    PrinterId = printerId,
                    ConfigurationRevision = 1,
                    CapturedAtUtc = DateTime.UtcNow,
                    CapturedBySubject = subject,
                    Firmware = candidate.Firmware,
                    Slicer = candidate.Slicer,
                    SnapshotSha256 = new string('a', 64),
                },
            };
        }
    }

    private sealed class TwoCallerPrinterContextService(Guid printerId) : StaticPrinterContextService(printerId)
    {
        private readonly TaskCompletionSource _bothCallersReachedContext = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _callerCount;

        public override async Task<CalibrationServiceResult<CalibrationContextDto>> GetContextAsync(
            Guid requestedPrinterId,
            long? configurationRevision,
            string capturedBySubject,
            CalibrationProfileAccessScope profileAccessScope,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _callerCount) == 2)
            {
                _ = _bothCallersReachedContext.TrySetResult();
            }

            await _bothCallersReachedContext.Task.WaitAsync(cancellationToken);
            return await base.GetContextAsync(
                requestedPrinterId,
                configurationRevision,
                capturedBySubject,
                profileAccessScope,
                cancellationToken);
        }
    }

    private sealed class DraftReadBarrierInterceptor : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _bothDraftReadsReachedBarrier = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _draftReadCount;

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("CalibrationDrafts", StringComparison.Ordinal) &&
                Interlocked.Increment(ref _draftReadCount) <= 2)
            {
                if (_draftReadCount == 2)
                {
                    _ = _bothDraftReadsReachedBarrier.TrySetResult();
                }

                await _bothDraftReadsReachedBarrier.Task.WaitAsync(cancellationToken);
            }

            return await base.ReaderExecutedAsync(
                command,
                eventData,
                result,
                cancellationToken);
        }
    }

    private sealed class EventSequenceReadBarrierInterceptor : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _bothSequenceReadsReachedBarrier = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _sequenceReadCount;

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("CalibrationAttemptEvents", StringComparison.Ordinal) &&
                command.CommandText.Contains("\"Sequence\"", StringComparison.Ordinal) &&
                Interlocked.Increment(ref _sequenceReadCount) <= 2)
            {
                if (_sequenceReadCount == 2)
                {
                    _ = _bothSequenceReadsReachedBarrier.TrySetResult();
                }

                await _bothSequenceReadsReachedBarrier.Task.WaitAsync(cancellationToken);
            }

            return await base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed class GeneratedProfileRevisionReadBarrierInterceptor : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _bothRevisionReadsReachedBarrier = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _revisionReadCount;

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("GeneratedProfileRevisions", StringComparison.Ordinal) &&
                command.CommandText.Contains("\"RevisionNumber\"", StringComparison.Ordinal) &&
                Interlocked.Increment(ref _revisionReadCount) <= 2)
            {
                if (_revisionReadCount == 2)
                {
                    _ = _bothRevisionReadsReachedBarrier.TrySetResult();
                }

                await _bothRevisionReadsReachedBarrier.Task.WaitAsync(cancellationToken);
            }

            return await base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed class IdempotencyReadBarrierInterceptor : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _bothReadsReachedBarrier = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _readCount;

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("CalibrationIdempotencyRecords", StringComparison.Ordinal) &&
                Interlocked.Increment(ref _readCount) <= 2)
            {
                if (_readCount == 2)
                {
                    _ = _bothReadsReachedBarrier.TrySetResult();
                }

                await _bothReadsReachedBarrier.Task.WaitAsync(cancellationToken);
            }

            return await base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
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
