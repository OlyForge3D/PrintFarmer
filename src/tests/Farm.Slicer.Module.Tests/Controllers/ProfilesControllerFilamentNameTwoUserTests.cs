using System;
using System.Threading;
using System.Threading.Tasks;
using Farm.Slicer.Module.Api.Controllers.Slicing;
using Farm.Slicer.Module.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Farm.Slicer.Module.Tests.Controllers;

/// <summary>
/// Issue #3192 (follow-up to R3190-B02): <c>FilamentProfiles</c> had a global unique index on
/// <c>(Name, Material, SlicerType)</c>, so one user's private filament name made another user's
/// upload or promote fail with a 500, which also disclosed that the name existed. Uniqueness is
/// now scoped per owner, and a same-owner collision is a 409 (or a suffixed name for unattended
/// promotion), never a 500. Each test drives the real controller action against the real
/// <see cref="ProfilesService"/> and SQLite-backed repository, with a distinct
/// <see cref="System.Security.Claims.ClaimsPrincipal"/> per user.
/// </summary>
public sealed class ProfilesControllerFilamentNameTwoUserTests : IDisposable
{
    private const string SharedName = "Shared Name PLA";

    private static readonly Guid UserA = Guid.NewGuid();
    private static readonly Guid UserB = Guid.NewGuid();

    private readonly SlicerDbContext _db = TestInfrastructure.TestHelpers.CreateSqliteInMemoryDb();
    private readonly EfFilamentProfileRepository _filamentRepo;

    public ProfilesControllerFilamentNameTwoUserTests()
    {
        _filamentRepo = new EfFilamentProfileRepository(_db);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Upload_SameNameForDifferentUsers_BothSucceed()
    {
        CustomProfileDto a = AssertCreated(await UploadAsync(UserA, SharedName));
        CustomProfileDto b = AssertCreated(await UploadAsync(UserB, SharedName));

        Assert.NotEqual(a.Id, b.Id);
        Assert.Equal(SharedName, a.Name);
        Assert.Equal(SharedName, b.Name);
        Assert.Equal(UserA, (await _filamentRepo.GetByIdAsync(a.Id, CancellationToken.None))!.CreatedByUserId);
        Assert.Equal(UserB, (await _filamentRepo.GetByIdAsync(b.Id, CancellationToken.None))!.CreatedByUserId);
    }

    [Fact]
    public async Task Upload_SameUserCollision_Returns409_NotA500()
    {
        _ = AssertCreated(await UploadAsync(UserA, SharedName));

        IActionResult second = await UploadAsync(UserA, SharedName);

        AssertNameConflict(second, SharedName);
        Assert.Equal(1, await CountOwnedAsync(UserA, SharedName));
    }

    [Fact]
    public async Task Promote_SameNameForDifferentUsers_BothKeepTheExactName()
    {
        CustomProfileDto a = AssertCreated(await PromoteAsync(UserA, SharedName));
        CustomProfileDto b = AssertCreated(await PromoteAsync(UserB, SharedName));

        Assert.NotEqual(a.Id, b.Id);
        Assert.Equal(SharedName, a.Name);
        Assert.Equal(SharedName, b.Name);
    }

    [Fact]
    public async Task Promote_OtherUsersName_DoesNotAffectTheCallersName()
    {
        // Another user's private names must not influence the caller's result in any way,
        // including by suffixing: that would still disclose that the name exists.
        _ = AssertCreated(await UploadAsync(UserB, SharedName));
        _ = AssertCreated(await UploadAsync(UserB, SharedName + " (2)"));

        CustomProfileDto a = AssertCreated(await PromoteAsync(UserA, SharedName));

        Assert.Equal(SharedName, a.Name);
    }

    [Fact]
    public async Task Promote_SameUserCollision_SuffixesTheName_NotA500()
    {
        _ = AssertCreated(await UploadAsync(UserA, SharedName));

        CustomProfileDto second = AssertCreated(await PromoteAsync(UserA, SharedName));
        CustomProfileDto third = AssertCreated(await PromoteAsync(UserA, SharedName));

        Assert.Equal(SharedName + " (2)", second.Name);
        Assert.Equal(SharedName + " (3)", third.Name);
    }

    [Fact]
    public async Task Clone_SameNameAsOtherUsersProfile_Succeeds_AndSameUserCollisionReturns409()
    {
        _ = AssertCreated(await UploadAsync(UserA, SharedName));
        CustomProfileDto bSource = AssertCreated(await UploadAsync(UserB, "B Source PLA"));

        IActionResult bClone = await CloneAsync(UserB, bSource.Id, SharedName);
        CloneSingleProfileResponseDto bCloneDto = Assert.IsType<CloneSingleProfileResponseDto>(Assert.IsType<CreatedResult>(bClone).Value);
        Assert.Equal(SharedName, bCloneDto.Name);

        IActionResult bDuplicate = await CloneAsync(UserB, bSource.Id, SharedName);
        AssertNameConflict(bDuplicate, SharedName);
        Assert.Equal(1, await CountOwnedAsync(UserB, SharedName));
    }

    [Fact]
    public async Task Update_RenameToOtherUsersName_Succeeds_AndRenameToOwnNameReturns409()
    {
        _ = AssertCreated(await UploadAsync(UserA, SharedName));
        CustomProfileDto bFirst = AssertCreated(await UploadAsync(UserB, "B First PLA"));
        CustomProfileDto bSecond = AssertCreated(await UploadAsync(UserB, "B Second PLA"));

        IActionResult renamed = await RenameAsync(UserB, bFirst.Id, SharedName);
        Assert.Equal(SharedName, Assert.IsType<CustomProfileDto>(Assert.IsType<OkObjectResult>(renamed).Value).Name);

        IActionResult collision = await RenameAsync(UserB, bSecond.Id, SharedName);
        AssertNameConflict(collision, SharedName);
        Assert.Equal("B Second PLA", (await _filamentRepo.GetByIdAsync(bSecond.Id, CancellationToken.None))!.Name);
    }

    [Fact]
    public async Task Update_KeepingTheSameName_IsNotAConflict()
    {
        CustomProfileDto a = AssertCreated(await UploadAsync(UserA, SharedName));

        IActionResult result = await RenameAsync(UserA, a.Id, SharedName);

        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task UniqueIndex_IsScopedPerOwner()
    {
        await _filamentRepo.AddAsync(Row(UserA, SharedName));
        await _filamentRepo.AddAsync(Row(UserB, SharedName));
        _db.ChangeTracker.Clear();

        _ = await Assert.ThrowsAsync<DbUpdateException>(() => _filamentRepo.AddAsync(Row(UserA, SharedName)));
    }

    [Fact]
    public async Task UniqueIndex_StillEnforcesNameUniqueness_AmongUnownedSystemRows()
    {
        await _filamentRepo.AddAsync(Row(null, "Stock PLA"));
        // An owned profile may reuse a stock name; only unowned rows share a namespace.
        await _filamentRepo.AddAsync(Row(UserA, "Stock PLA"));
        _db.ChangeTracker.Clear();

        _ = await Assert.ThrowsAsync<DbUpdateException>(() => _filamentRepo.AddAsync(Row(null, "Stock PLA")));
    }

    private static FilamentProfile Row(Guid? ownerId, string name) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        SlicerType = SlicerType.OrcaSlicer,
        IsSystem = ownerId is null,
        CreatedByUserId = ownerId,
        Hash = "hash-" + Guid.NewGuid().ToString("N"),
        RawJson = $"{{\"name\":\"{name}\"}}",
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    private static string RawJson(string name) => $"{{\"name\":\"{name}\",\"filament_type\":[\"PLA\"]}}";

    private static CustomProfileDto AssertCreated(IActionResult result) =>
        ProfilesControllerNameConflictHarness.AssertCreated(result);

    private static void AssertNameConflict(IActionResult result, string name) =>
        ProfilesControllerNameConflictHarness.AssertNameConflict(result, name);

    private Task<int> CountOwnedAsync(Guid ownerId, string name) =>
        _db.FilamentProfiles.AsNoTracking().CountAsync(p => p.CreatedByUserId == ownerId && p.Name == name);

    private async Task<IActionResult> UploadAsync(Guid userId, string name) =>
        await RunAsync(controller => controller.UploadCustomProfileAsync(
            new UploadProfileRequestDto { Name = name, RawJson = RawJson(name), ProfileType = "filament" },
            CancellationToken.None), userId);

    private async Task<IActionResult> PromoteAsync(Guid userId, string name) =>
        await RunAsync(controller => controller.PromoteCalibrationDraftProfileAsync(
            new PromoteCalibrationDraftProfileRequestDto { Name = name, RawJson = RawJson(name), SourceDraftProfileId = Guid.NewGuid() },
            CancellationToken.None), userId);

    private async Task<IActionResult> CloneAsync(Guid userId, Guid sourceId, string name) =>
        await RunAsync(controller => controller.CloneSingleProfileAsync(
            new CloneSingleProfileRequestDto { SourceProfileId = sourceId, ProfileType = "filament", Name = name },
            CancellationToken.None), userId);

    private async Task<IActionResult> RenameAsync(Guid userId, Guid profileId, string name) =>
        await RunAsync(controller => controller.UpdateCustomProfileAsync(
            profileId,
            new UpdateCustomProfileRequestDto { Name = name },
            CancellationToken.None), userId);

    private async Task<IActionResult> RunAsync(Func<ProfilesController, Task<IActionResult>> action, Guid userId)
    {
        IActionResult result = await action(CreateController(userId));

        // Each request gets a fresh DbContext in production.
        _db.ChangeTracker.Clear();
        return result;
    }

    private ProfilesController CreateController(Guid userId) =>
        ProfilesControllerNameConflictHarness.CreateController(_db, userId);
}
