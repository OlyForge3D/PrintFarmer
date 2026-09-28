using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.PrinterCalibration;
using Farm.Infrastructure.Repositories.UnitOfWork;
using Farm.Infrastructure.Services.Catalog;
using Farm.Infrastructure.Services.Gcode;
using Farm.Slicer.Module.Api.Controllers.Slicing;
using Farm.Slicer.Module.Api.Hubs;
using Farm.Slicer.Module.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Slicer.Module.Tests.Controllers;

/// <summary>
/// Issue #3189 (follow-up to #3185): <c>POST /api/slicer/profiles/promote-from-calibration</c>
/// used to answer 403 when the supplied draft id had already been promoted by a different user.
/// An unknown id returns 201, so the 403 disclosed that the other user's draft existed. The
/// idempotency key is now scoped per owner, so another user's draft id behaves exactly like an
/// unknown id, and the owner's own promotion can no longer be blocked by someone claiming the id
/// first. Each test drives the real controller action against the real
/// <see cref="ProfilesService"/> and SQLite-backed repository, with a distinct
/// <see cref="ClaimsPrincipal"/> per user.
/// </summary>
public sealed class ProfilesControllerPromoteCalibrationDraftTwoUserTests : IDisposable
{
    private static readonly Guid OwnerId = Guid.NewGuid();
    private static readonly Guid OtherUserId = Guid.NewGuid();

    private readonly SlicerDbContext _db = TestInfrastructure.TestHelpers.CreateSqliteInMemoryDb();
    private readonly EfFilamentProfileRepository _filamentRepo;

    public ProfilesControllerPromoteCalibrationDraftTwoUserTests()
    {
        _filamentRepo = new EfFilamentProfileRepository(_db);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task OtherUser_OnDraftIdPromotedByOwner_GetsSameResponseAsUnknownDraftId_AndOwnersRowIsUntouched()
    {
        Guid ownersDraftId = Guid.NewGuid();
        CustomProfileDto owners = AssertCreated(await PromoteAsync(OwnerId, ownersDraftId, "Owner Secret PLA"));

        IActionResult foreign = await PromoteAsync(OtherUserId, ownersDraftId, "Other Foreign PLA");
        IActionResult unknown = await PromoteAsync(OtherUserId, Guid.NewGuid(), "Other Unknown PLA");

        // Same result shape and status for both: no 403/404 that could reveal the owner's draft.
        CustomProfileDto foreignDto = AssertCreated(foreign);
        CustomProfileDto unknownDto = AssertCreated(unknown);
        Assert.Equal(unknown.GetType(), foreign.GetType());

        // The foreign-id response is the caller's own new profile, never the owner's row.
        Assert.NotEqual(owners.Id, foreignDto.Id);
        Assert.Equal("Other Foreign PLA", foreignDto.Name);
        Assert.Equal("filament", foreignDto.ProfileType);
        Assert.Equal("filament", unknownDto.ProfileType);

        FilamentProfile? foreignRow = await _filamentRepo.GetByIdAsync(foreignDto.Id, CancellationToken.None);
        Assert.Equal(OtherUserId, foreignRow!.CreatedByUserId);
        Assert.Equal(ownersDraftId, foreignRow.PromotedFromCalibrationDraftProfileId);
        Assert.DoesNotContain("Owner Secret PLA", foreignRow.RawJson, StringComparison.Ordinal);

        FilamentProfile? ownersRow = await _filamentRepo.GetByIdAsync(owners.Id, CancellationToken.None);
        Assert.Equal(OwnerId, ownersRow!.CreatedByUserId);
        Assert.Equal("Owner Secret PLA", ownersRow.Name);
    }

    [Fact]
    public async Task Owner_CanStillPromote_AfterOtherUserClaimedTheDraftIdFirst_AndReplayStaysIdempotent()
    {
        Guid draftId = Guid.NewGuid();
        CustomProfileDto squatted = AssertCreated(await PromoteAsync(OtherUserId, draftId, "Squatter PLA"));

        CustomProfileDto owners = AssertCreated(await PromoteAsync(OwnerId, draftId, "Owner PLA"));
        Assert.NotEqual(squatted.Id, owners.Id);
        Assert.Equal("Owner PLA", owners.Name);

        IActionResult replay = await PromoteAsync(OwnerId, draftId, "Owner PLA");
        CustomProfileDto replayDto = Assert.IsType<CustomProfileDto>(Assert.IsType<OkObjectResult>(replay).Value);
        Assert.Equal(owners.Id, replayDto.Id);

        List<Guid?> promotedBy = await _db.FilamentProfiles.AsNoTracking()
            .Where(p => p.PromotedFromCalibrationDraftProfileId == draftId)
            .Select(p => p.CreatedByUserId)
            .ToListAsync();
        Assert.Equal(2, promotedBy.Count);
        Assert.Contains(OwnerId, promotedBy);
        Assert.Contains(OtherUserId, promotedBy);
    }

    [Fact]
    public async Task UniqueIndex_IsScopedPerOwner()
    {
        Guid draftId = Guid.NewGuid();

        await _filamentRepo.AddAsync(Row(OwnerId, draftId, "Owner Row"));
        await _filamentRepo.AddAsync(Row(OtherUserId, draftId, "Other Row"));
        _db.ChangeTracker.Clear();

        _ = await Assert.ThrowsAsync<DbUpdateException>(() =>
            _filamentRepo.AddAsync(Row(OwnerId, draftId, "Owner Duplicate Row")));
    }

    private static FilamentProfile Row(Guid ownerId, Guid draftId, string name) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        SlicerType = SlicerType.OrcaSlicer,
        CreatedByUserId = ownerId,
        PromotedFromCalibrationDraftProfileId = draftId,
        Hash = "hash-" + name,
        RawJson = $"{{\"name\":\"{name}\"}}",
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    private static CustomProfileDto AssertCreated(IActionResult result) =>
        Assert.IsType<CustomProfileDto>(Assert.IsType<CreatedResult>(result).Value);

    private async Task<IActionResult> PromoteAsync(Guid userId, Guid draftId, string name)
    {
        IActionResult result = await CreateController(userId).PromoteCalibrationDraftProfileAsync(
            new PromoteCalibrationDraftProfileRequestDto
            {
                Name = name,
                RawJson = $"{{\"name\":\"{name}\",\"filament_type\":[\"PLA\"]}}",
                SourceDraftProfileId = draftId,
            },
            CancellationToken.None);

        // Each request gets a fresh DbContext in production.
        _db.ChangeTracker.Clear();
        return result;
    }

    private ProfilesController CreateController(Guid userId)
    {
        ProfilesService service = new(
            new EfProfilesRepository(_db),
            NullLogger<ProfilesService>.Instance,
            new EfProcessProfileRepository(_db),
            new EfMachineProfileRepository(_db),
            _filamentRepo,
            new Mock<IUnitOfWork>(MockBehavior.Loose).Object,
            new Mock<ICatalogService>(MockBehavior.Loose).Object,
            new Mock<IProfileParsingService>(MockBehavior.Loose).Object,
            new Mock<IHubContext<SlicerHub>>(MockBehavior.Loose).Object,
            new Mock<ISlicersService>(MockBehavior.Loose).Object,
            new Mock<IPrinterModelAliasService>(MockBehavior.Loose).Object);

        ClaimsPrincipal user = new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "Test"));
        return new ProfilesController(
            NullLogger<ProfilesController>.Instance,
            service,
            new Mock<ICatalogServiceAdapter>(MockBehavior.Loose).Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = user } }
        };
    }
}
