using System.Text.Json;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.PrinterCalibration;
using Farm.Modules.Calibration.Contracts;
using Farm.Modules.Calibration.Services.Calibration;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Farm.Modules.Calibration.Tests.Services.Calibration;

/// <summary>
/// Verifies strict complete-or-missing selected-toolhead validation against the
/// immutable server-captured snapshot toolhead list. Both fields absent is permitted
/// by the existing create contract; any partial, mismatched, or unknown selection is
/// deterministically rejected with <c>toolhead_selection_invalid</c>.
/// </summary>
public sealed class CalibrationProjectServiceToolheadSelectionTests
{
    private static readonly Guid ToolheadAId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ToolheadBId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private const int ToolheadAIndex = 0;
    private const int ToolheadBIndex = 1;

    [Fact]
    public async Task CreateProjectAsync_BothSelectionFieldsAbsent_CreatesProject()
    {
        await using AppDbContext db = CreateContext();
        Guid printerId = Guid.NewGuid();
        Guid ownerId = Guid.NewGuid();
        CalibrationProjectService service = CreateService(db, printerId);
        CalibrationProjectCreateRequest request = BaseRequest(printerId, "absent");

        CalibrationApiResult<CalibrationProjectDto> result = await service.CreateProjectAsync(
            request,
            new(ownerId, ownerId.ToString(), false),
            CancellationToken.None);

        _ = result.StatusCode.Should().Be(StatusCodes.Status201Created);
        _ = result.Value!.SelectedToolheadId.Should().BeNull();
        _ = result.Value.SelectedToolheadIndex.Should().BeNull();
    }

    [Fact]
    public async Task CreateProjectAsync_SelectedToolheadPairWithNoContext_Rejects()
    {
        await using AppDbContext db = CreateContext();
        Guid printerId = Guid.NewGuid();
        Guid ownerId = Guid.NewGuid();
        CalibrationProjectService service = CreateService(db, printerId);
        CalibrationProjectCreateRequest matching = CloneWithSelection(
            BaseRequest(printerId, "match"),
            ToolheadBId,
            ToolheadBIndex);

        CalibrationApiResult<CalibrationProjectDto> result = await service.CreateProjectAsync(
            matching,
            new(ownerId, ownerId.ToString(), false),
            CancellationToken.None);

        // Path D (#1981): no printer configuration context is resolved, so there is no captured
        // toolhead list to validate a selection against - any selection is now rejected, even one
        // that would previously have matched a resolved context.
        _ = result.StatusCode.Should().Be(StatusCodes.Status422UnprocessableEntity);
        _ = result.Code.Should().Be("toolhead_selection_invalid");
        _ = (await db.CalibrationProjects.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task CreateProjectAsync_SelectedIdWithoutIndex_Rejects()
    {
        await using AppDbContext db = CreateContext();
        Guid printerId = Guid.NewGuid();
        Guid ownerId = Guid.NewGuid();
        CalibrationProjectService service = CreateService(db, printerId);
        CalibrationProjectCreateRequest request = CloneWithSelection(
            BaseRequest(printerId, "partial-id"),
            ToolheadAId,
            null);

        CalibrationApiResult<CalibrationProjectDto> result = await service.CreateProjectAsync(
            request,
            new(ownerId, ownerId.ToString(), false),
            CancellationToken.None);

        _ = result.StatusCode.Should().Be(StatusCodes.Status422UnprocessableEntity);
        _ = result.Code.Should().Be("toolhead_selection_invalid");
        _ = (await db.CalibrationProjects.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task CreateProjectAsync_SelectedIndexWithoutId_Rejects()
    {
        await using AppDbContext db = CreateContext();
        Guid printerId = Guid.NewGuid();
        Guid ownerId = Guid.NewGuid();
        CalibrationProjectService service = CreateService(db, printerId);
        CalibrationProjectCreateRequest request = CloneWithSelection(
            BaseRequest(printerId, "partial-index"),
            null,
            ToolheadAIndex);

        CalibrationApiResult<CalibrationProjectDto> result = await service.CreateProjectAsync(
            request,
            new(ownerId, ownerId.ToString(), false),
            CancellationToken.None);

        _ = result.StatusCode.Should().Be(StatusCodes.Status422UnprocessableEntity);
        _ = result.Code.Should().Be("toolhead_selection_invalid");
        _ = (await db.CalibrationProjects.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task CreateProjectAsync_UnknownSelectedToolheadId_Rejects()
    {
        await using AppDbContext db = CreateContext();
        Guid printerId = Guid.NewGuid();
        Guid ownerId = Guid.NewGuid();
        CalibrationProjectService service = CreateService(db, printerId);
        CalibrationProjectCreateRequest request = CloneWithSelection(
            BaseRequest(printerId, "unknown-id"),
            Guid.NewGuid(),
            ToolheadAIndex);

        CalibrationApiResult<CalibrationProjectDto> result = await service.CreateProjectAsync(
            request,
            new(ownerId, ownerId.ToString(), false),
            CancellationToken.None);

        _ = result.StatusCode.Should().Be(StatusCodes.Status422UnprocessableEntity);
        _ = result.Code.Should().Be("toolhead_selection_invalid");
        _ = (await db.CalibrationProjects.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task CreateProjectAsync_UnknownSelectedToolheadIndex_Rejects()
    {
        await using AppDbContext db = CreateContext();
        Guid printerId = Guid.NewGuid();
        Guid ownerId = Guid.NewGuid();
        CalibrationProjectService service = CreateService(db, printerId);
        CalibrationProjectCreateRequest request = CloneWithSelection(
            BaseRequest(printerId, "unknown-index"),
            ToolheadAId,
            99);

        CalibrationApiResult<CalibrationProjectDto> result = await service.CreateProjectAsync(
            request,
            new(ownerId, ownerId.ToString(), false),
            CancellationToken.None);

        _ = result.StatusCode.Should().Be(StatusCodes.Status422UnprocessableEntity);
        _ = result.Code.Should().Be("toolhead_selection_invalid");
        _ = (await db.CalibrationProjects.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task CreateProjectAsync_MismatchedIdAndIndexPair_Rejects()
    {
        await using AppDbContext db = CreateContext();
        Guid printerId = Guid.NewGuid();
        Guid ownerId = Guid.NewGuid();
        CalibrationProjectService service = CreateService(db, printerId);
        CalibrationProjectCreateRequest request = CloneWithSelection(
            BaseRequest(printerId, "mismatched"),
            ToolheadAId,
            ToolheadBIndex);

        CalibrationApiResult<CalibrationProjectDto> result = await service.CreateProjectAsync(
            request,
            new(ownerId, ownerId.ToString(), false),
            CancellationToken.None);

        _ = result.StatusCode.Should().Be(StatusCodes.Status422UnprocessableEntity);
        _ = result.Code.Should().Be("toolhead_selection_invalid");
        _ = (await db.CalibrationProjects.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task CreateProjectAsync_EmptyGuidSelectedId_Rejects()
    {
        await using AppDbContext db = CreateContext();
        Guid printerId = Guid.NewGuid();
        Guid ownerId = Guid.NewGuid();
        CalibrationProjectService service = CreateService(db, printerId);
        CalibrationProjectCreateRequest request = CloneWithSelection(
            BaseRequest(printerId, "empty-id"),
            Guid.Empty,
            ToolheadAIndex);

        CalibrationApiResult<CalibrationProjectDto> result = await service.CreateProjectAsync(
            request,
            new(ownerId, ownerId.ToString(), false),
            CancellationToken.None);

        _ = result.StatusCode.Should().Be(StatusCodes.Status422UnprocessableEntity);
        _ = result.Code.Should().Be("toolhead_selection_invalid");
        _ = (await db.CalibrationProjects.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task CreateProjectAsync_MatchingPairWithNoContext_RejectsWithoutPersisting()
    {
        await using AppDbContext db = CreateContext();
        Guid printerId = Guid.NewGuid();
        Guid ownerId = Guid.NewGuid();
        CalibrationProjectService service = CreateService(db, printerId);
        CalibrationProjectCreateRequest request = CloneWithSelection(
            BaseRequest(printerId, "persist"),
            ToolheadAId,
            ToolheadAIndex);

        CalibrationApiResult<CalibrationProjectDto> result = await service.CreateProjectAsync(
            request,
            new(ownerId, ownerId.ToString(), false),
            CancellationToken.None);

        // Path D (#1981): without a resolved context there is no captured toolhead identity to
        // persist - the selection is rejected and nothing is written.
        _ = result.StatusCode.Should().Be(StatusCodes.Status422UnprocessableEntity);
        _ = result.Code.Should().Be("toolhead_selection_invalid");
        _ = (await db.CalibrationProjects.CountAsync()).Should().Be(0);
    }

    private static AppDbContext CreateContext()
    {
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"calibration-project-toolhead-{Guid.NewGuid()}")
            .Options;
        return new(options);
    }

    private static CalibrationProjectService CreateService(AppDbContext db, Guid printerId) =>
        new(
            db,
            new NoopCalibrationBlobStore(),
            TimeProvider.System,
            NullLogger<CalibrationProjectService>.Instance);

    private static CalibrationProjectCreateRequest BaseRequest(Guid printerId, string requestId) =>
        new()
        {
            ClientId = "client-a",
            RequestId = requestId,
            Name = "Toolhead selection",
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

    private static CalibrationProjectCreateRequest CloneWithSelection(
        CalibrationProjectCreateRequest source,
        Guid? selectedId,
        int? selectedIndex) =>
        new()
        {
            ClientId = source.ClientId,
            RequestId = source.RequestId,
            Name = source.Name,
            PrinterId = source.PrinterId,
            PrinterConfigurationRevision = source.PrinterConfigurationRevision,
            SelectedToolheadId = selectedId,
            SelectedToolheadIndex = selectedIndex,
            FilamentProvider = source.FilamentProvider,
            FilamentProductId = source.FilamentProductId,
            FilamentSku = source.FilamentSku,
            FilamentVendor = source.FilamentVendor,
            FilamentProductName = source.FilamentProductName,
            FilamentMaterial = source.FilamentMaterial,
            FilamentDiameter = source.FilamentDiameter,
            FilamentColor = source.FilamentColor,
            FilamentTypeId = source.FilamentTypeId,
            SpoolmanFilamentId = source.SpoolmanFilamentId,
            LocalSpoolId = source.LocalSpoolId,
            SpoolmanSpoolId = source.SpoolmanSpoolId,
            FilamentSnapshot = source.FilamentSnapshot,
            OrderedSteps = source.OrderedSteps,
            CurrentStep = source.CurrentStep,
            CurrentSelections = source.CurrentSelections,
            ExperienceMode = source.ExperienceMode,
        };

    private sealed class NoopCalibrationBlobStore : ICalibrationBlobStore
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
                0,
                new string('0', 64),
                1,
                1,
                new string('0', 64)));
    }
}
