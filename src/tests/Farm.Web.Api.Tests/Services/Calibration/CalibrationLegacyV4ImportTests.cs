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

/// <summary>
/// Covers D9 (#1986): the "legacy-v4" bulk import path (POST /api/calibration-imports/legacy-v4)
/// was audited against the post-D1/D2/D3 reshaped, filament-oriented CalibrationProject/
/// CalibrationAttempt aggregate. Its request/result contracts (<see cref="LegacyCalibrationImportRequest"/>,
/// <see cref="CalibrationProjectCreateRequest"/>) never carried printer-eligibility, hardware-attestation,
/// snapshot, or generator-only fields, so the decision was to keep the path and confirm it maps v4
/// payloads directly into the reshaped entity via the same CreateProjectAsync used by non-legacy callers.
/// The import commits inside a serializable transaction (BeginTransactionAsync with an explicit
/// IsolationLevel), which the EF Core InMemory provider does not support, so these tests use a real
/// SQLite-backed context like the sibling concurrency tests in this directory.
/// </summary>
public sealed class CalibrationLegacyV4ImportTests
{
    [Fact]
    public async Task ImportLegacyV4Async_DryRun_ReturnsMappingsWithoutPersisting()
    {
        await using SqliteImportStore store = await SqliteImportStore.CreateAsync();
        await using AppDbContext context = store.CreateContext();
        CalibrationProjectService service = CreateService(context);
        CalibrationActor actor = new(Guid.NewGuid(), "owner", false);
        LegacyCalibrationImportRequest request = CreateImportRequest(
            "dry-run-op",
            dryRun: true,
            CreateProjectRequest(store.PrinterId, "dry-run-project"));

        CalibrationApiResult<LegacyCalibrationImportResultDto> result =
            await service.ImportLegacyV4Async(request, actor, CancellationToken.None);

        _ = result.IsSuccess.Should().BeTrue();
        _ = result.Value!.DryRun.Should().BeTrue();
        _ = result.Value.Mappings.Should().ContainSingle().Which.Should().Be("projects[0]=>calibration-project");
        _ = result.Value.RejectedRecords.Should().BeEmpty();
        _ = result.Value.ProjectIds.Should().BeEmpty();
        _ = (await context.CalibrationProjects.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task ImportLegacyV4Async_Commit_ImportsIntoReshapedFilamentOrientedProject()
    {
        await using SqliteImportStore store = await SqliteImportStore.CreateAsync();
        await using AppDbContext context = store.CreateContext();
        CalibrationProjectService service = CreateService(context);
        CalibrationActor actor = new(Guid.NewGuid(), "owner", false);
        LegacyCalibrationImportRequest request = CreateImportRequest(
            "commit-op",
            dryRun: false,
            CreateProjectRequest(store.PrinterId, "commit-project"));

        CalibrationApiResult<LegacyCalibrationImportResultDto> result =
            await service.ImportLegacyV4Async(request, actor, CancellationToken.None);

        _ = result.StatusCode.Should().Be(StatusCodes.Status201Created);
        _ = result.Value!.DryRun.Should().BeFalse();
        _ = result.Value.RejectedRecords.Should().BeEmpty();
        _ = result.Value.ProjectIds.Should().ContainSingle();

        await using AppDbContext verificationContext = store.CreateContext();
        CalibrationProject imported = await verificationContext.CalibrationProjects
            .SingleAsync(project => project.Id == result.Value.ProjectIds[0]);
        _ = imported.PrinterId.Should().Be(store.PrinterId);
        _ = imported.FilamentProvider.Should().Be("catalog");
        _ = imported.FilamentProductId.Should().Be("sku-pla-blue");
        _ = imported.FilamentMaterial.Should().Be("PLA");
        _ = imported.Revision.Should().Be(1);
    }

    [Fact]
    public async Task ImportLegacyV4Async_InvalidProject_RejectsRecordWithoutPersisting()
    {
        await using SqliteImportStore store = await SqliteImportStore.CreateAsync();
        await using AppDbContext context = store.CreateContext();
        CalibrationProjectService service = CreateService(context);
        CalibrationActor actor = new(Guid.NewGuid(), "owner", false);
        CalibrationProjectCreateRequest validProject = CreateProjectRequest(store.PrinterId, "invalid-project");
        CalibrationProjectCreateRequest invalidProject = new()
        {
            ClientId = validProject.ClientId,
            RequestId = validProject.RequestId,
            Name = string.Empty,
            PrinterId = store.PrinterId,
            PrinterConfigurationRevision = 1,
            FilamentProvider = validProject.FilamentProvider,
            FilamentProductId = validProject.FilamentProductId,
            FilamentProductName = validProject.FilamentProductName,
            FilamentMaterial = validProject.FilamentMaterial,
            FilamentSnapshot = validProject.FilamentSnapshot,
            OrderedSteps = validProject.OrderedSteps,
            CurrentSelections = validProject.CurrentSelections,
            ExperienceMode = validProject.ExperienceMode,
        };
        LegacyCalibrationImportRequest request = CreateImportRequest(
            "invalid-op",
            dryRun: false,
            invalidProject);

        CalibrationApiResult<LegacyCalibrationImportResultDto> result =
            await service.ImportLegacyV4Async(request, actor, CancellationToken.None);

        _ = result.IsSuccess.Should().BeTrue();
        _ = result.Value!.RejectedRecords.Should().ContainSingle().Which.Should().Be("projects[0]:project_invalid");
        _ = result.Value.ProjectIds.Should().BeEmpty();
        _ = (await context.CalibrationProjects.CountAsync()).Should().Be(0);
    }

    private static CalibrationProjectService CreateService(AppDbContext context) =>
        new(
            context,
            new TestCalibrationBlobStore(),
            TimeProvider.System,
            NullLogger<CalibrationProjectService>.Instance);

    private static LegacyCalibrationImportRequest CreateImportRequest(
        string operationId,
        bool dryRun,
        params CalibrationProjectCreateRequest[] projects) =>
        new()
        {
            ClientId = "legacy-v4-client",
            OperationId = operationId,
            DryRun = dryRun,
            Projects = projects,
        };

    private static CalibrationProjectCreateRequest CreateProjectRequest(Guid printerId, string requestId) =>
        new()
        {
            ClientId = "legacy-v4-client",
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

    private sealed class SqliteImportStore : IAsyncDisposable
    {
        private SqliteImportStore(string databasePath, Guid printerId)
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

        public static async Task<SqliteImportStore> CreateAsync()
        {
            SqliteImportStore store = new(
                Path.Join(Path.GetTempPath(), $"calibration-legacy-v4-import-{Guid.NewGuid():N}.db"),
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
                Name = "Legacy import test manufacturer",
            });
            _ = context.PrinterModels.Add(new PrinterModel
            {
                Id = modelId,
                ManufacturerId = manufacturerId,
                Name = "Legacy import test model",
            });
            _ = context.Printers.Add(new Printer
            {
                Id = store.PrinterId,
                Name = "Legacy import test printer",
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
