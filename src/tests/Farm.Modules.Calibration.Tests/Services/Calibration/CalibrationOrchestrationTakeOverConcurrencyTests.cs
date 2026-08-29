using System.Text.Json;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Modules.Calibration.Contracts;
using Farm.Modules.Calibration.Services.Calibration;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Farm.Modules.Calibration.Tests.Services.Calibration;

/// <summary>
/// Covers the take-over/adoption contract for issue #2181: a second device may adopt an
/// in-flight <see cref="CalibrationOrchestration"/> and drive it forward (full read-write
/// adoption, no per-device ownership), and two devices racing to persist a conflicting change to
/// the SAME orchestration are kept safe by its <c>Revision</c> optimistic-concurrency token - the
/// loser gets a genuine <see cref="DbUpdateConcurrencyException"/> at the database layer, never a
/// silent last-write-wins overwrite.
/// </summary>
/// <remarks>
/// This exercises the underlying persistence-layer invariant directly (two real
/// <see cref="AppDbContext"/> instances backed by the same SQLite file, each independently loading
/// then saving the orchestration row) rather than going through
/// <c>CalibrationOrchestrationSagaService.AdvanceAsync</c> twice in parallel: that method also
/// serializes concurrent callers with an in-process <c>SemaphoreSlim</c> keyed by orchestration id
/// (defense against the common single-process case), so two calls from the same test process can
/// never actually race at the database layer through that entry point - by design, per its own
/// remarks. The <c>Revision</c> concurrency token this test exercises is exactly the mechanism
/// <c>AdvanceLockedAsync</c>'s own <c>catch (DbUpdateConcurrencyException)</c> block relies on for
/// the residual multi-instance case (e.g. two horizontally-scaled API instances, each with its own
/// independent in-process lock table).
/// </remarks>
public sealed class CalibrationOrchestrationTakeOverConcurrencyTests
{
    [Fact]
    public async Task CalibrationOrchestration_TwoDevicesRaceToPersistChange_LoserGetsRevisionConflict()
    {
        await using SqliteCalibrationStore store = await SqliteCalibrationStore.CreateAsync();
        Guid ownerId = Guid.NewGuid();
        Guid projectId = await store.SeedProjectAsync(ownerId);
        CalibrationActor actor = new(ownerId, "owner", false);

        Guid orchestrationId;
        await using (AppDbContext seedContext = store.CreateContext())
        {
            CalibrationApiResult<CalibrationAttemptDto> attempt = await CreateService(seedContext, store.PrinterId)
                .CreateAttemptAsync(projectId, CreateAttemptRequest("attempt-1"), actor, CancellationToken.None);
            _ = attempt.StatusCode.Should().Be(StatusCodes.Status201Created);
            orchestrationId = await seedContext.CalibrationOrchestrations
                .Where(o => o.AttemptId == attempt.Value!.Id)
                .Select(o => o.Id)
                .SingleAsync();
        }

        // Both "devices" load the same in-flight orchestration (Revision 1) before either device
        // persists anything - modeling two clients that each fetched the project's in-flight state
        // and then both decided to act on it.
        await using AppDbContext deviceAContext = store.CreateContext();
        await using AppDbContext deviceBContext = store.CreateContext();
        CalibrationOrchestration deviceAView = await deviceAContext.CalibrationOrchestrations
            .SingleAsync(o => o.Id == orchestrationId);
        CalibrationOrchestration deviceBView = await deviceBContext.CalibrationOrchestrations
            .SingleAsync(o => o.Id == orchestrationId);
        _ = deviceAView.Revision.Should().Be(1);
        _ = deviceBView.Revision.Should().Be(1);

        // Revision is a plain `ValueGeneratedNever` concurrency token (see
        // CalibrationOrchestrationConfiguration), not a database-computed rowversion: the
        // application is responsible for incrementing it on every write, exactly as
        // CalibrationOrchestrationSagaService.AdvanceLockedAsync does. Mirror that here so this
        // test exercises the same mechanism production code relies on.
        deviceAView.CurrentStep = CalibrationSagaSteps.CloningProfile;
        deviceAView.Revision++;
        deviceAView.UpdatedAtUtc = DateTime.UtcNow;
        deviceBView.LastErrorCode = "device-b-lost-the-race";
        deviceBView.Revision++;
        deviceBView.UpdatedAtUtc = DateTime.UtcNow;

        // Device A wins the race and commits first.
        _ = await deviceAContext.SaveChangesAsync();
        _ = deviceAView.Revision.Should().Be(2);

        // Device B is still holding the pre-race snapshot (Revision 1): its save must fail with a
        // genuine, database-enforced concurrency conflict rather than silently clobbering device
        // A's committed change (last-write-wins) or throwing an unrelated/opaque error.
        _ = await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => deviceBContext.SaveChangesAsync());

        // The take-over contract expects the loser to refetch rather than blindly retry: a fresh
        // read observes exactly device A's committed change, never device B's, and never a mix.
        await using AppDbContext verificationContext = store.CreateContext();
        CalibrationOrchestration persisted = await verificationContext.CalibrationOrchestrations
            .SingleAsync(o => o.Id == orchestrationId);
        _ = persisted.Revision.Should().Be(2);
        _ = persisted.CurrentStep.Should().Be(CalibrationSagaSteps.CloningProfile);
        _ = persisted.LastErrorCode.Should().BeNull();
    }

    private static CalibrationProjectService CreateService(AppDbContext context, Guid printerId) =>
        new(
            context,
            new TestCalibrationBlobStore(),
            TimeProvider.System,
            NullLogger<CalibrationProjectService>.Instance);

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
                Path.Join(Path.GetTempPath(), $"calibration-orchestration-concurrency-{Guid.NewGuid():N}.db"),
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

        public AppDbContext CreateContext()
        {
            DbContextOptionsBuilder<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(ConnectionString);
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
