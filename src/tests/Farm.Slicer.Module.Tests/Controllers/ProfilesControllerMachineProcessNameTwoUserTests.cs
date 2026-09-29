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
/// Issue #3198 (follow-up to #3192): <c>MachineProfiles</c> had a global unique index on
/// <c>(Name, SlicerType)</c> and <c>ProcessProfiles</c> on <c>(Name, SlicerType, PrinterModelId)</c>,
/// so one user's private machine or process name made another user's upload, clone or rename fail
/// with a 500, which also disclosed that the name existed. Uniqueness is now scoped per owner, and
/// a same-owner collision is a 409, never a 500. Each test drives the real controller action
/// against the real <see cref="ProfilesService"/> and SQLite-backed repositories, with a distinct
/// caller per user.
/// </summary>
public sealed class ProfilesControllerMachineProcessNameTwoUserTests : IDisposable
{
    private const string SharedName = "Shared Name Profile";

    private static readonly Guid UserA = Guid.NewGuid();
    private static readonly Guid UserB = Guid.NewGuid();
    private static readonly Guid ModelX = Guid.NewGuid();
    private static readonly Guid ModelY = Guid.NewGuid();

    private readonly SlicerDbContext _db = TestInfrastructure.TestHelpers.CreateSqliteInMemoryDb();
    private readonly EfMachineProfileRepository _machineRepo;
    private readonly EfProcessProfileRepository _processRepo;

    public ProfilesControllerMachineProcessNameTwoUserTests()
    {
        _machineRepo = new EfMachineProfileRepository(_db);
        _processRepo = new EfProcessProfileRepository(_db);
    }

    public void Dispose() => _db.Dispose();

    [Theory]
    [InlineData("machine")]
    [InlineData("process")]
    public async Task Upload_SameNameForDifferentUsers_BothSucceed(string type)
    {
        CustomProfileDto a = AssertCreated(await UploadAsync(UserA, type, SharedName, ModelX));
        CustomProfileDto b = AssertCreated(await UploadAsync(UserB, type, SharedName, ModelX));

        Assert.NotEqual(a.Id, b.Id);
        Assert.Equal(SharedName, a.Name);
        Assert.Equal(SharedName, b.Name);
        Assert.Equal(1, await CountOwnedAsync(type, UserA, SharedName));
        Assert.Equal(1, await CountOwnedAsync(type, UserB, SharedName));
    }

    [Theory]
    [InlineData("machine")]
    [InlineData("process")]
    public async Task Upload_SameUserCollision_Returns409_NotA500(string type)
    {
        _ = AssertCreated(await UploadAsync(UserA, type, SharedName, ModelX));

        IActionResult second = await UploadAsync(UserA, type, SharedName, ModelX);

        AssertNameConflict(second, SharedName);
        Assert.Equal(1, await CountOwnedAsync(type, UserA, SharedName));
    }

    [Theory]
    [InlineData("machine")]
    [InlineData("process")]
    public async Task Clone_SameNameAsOtherUsersProfile_Succeeds_AndSameUserCollisionReturns409(string type)
    {
        _ = AssertCreated(await UploadAsync(UserA, type, SharedName, ModelX));
        CustomProfileDto bSource = AssertCreated(await UploadAsync(UserB, type, "B Source", ModelX));

        CloneSingleProfileResponseDto bClone = ProfilesControllerNameConflictHarness.AssertCloned(
            await CloneAsync(UserB, type, bSource.Id, SharedName));
        Assert.Equal(SharedName, bClone.Name);

        IActionResult bDuplicate = await CloneAsync(UserB, type, bSource.Id, SharedName);
        AssertNameConflict(bDuplicate, SharedName);
        Assert.Equal(1, await CountOwnedAsync(type, UserB, SharedName));
    }

    [Theory]
    [InlineData("machine")]
    [InlineData("process")]
    public async Task Update_RenameToOtherUsersName_Succeeds_AndRenameToOwnNameReturns409(string type)
    {
        _ = AssertCreated(await UploadAsync(UserA, type, SharedName, ModelX));
        CustomProfileDto bFirst = AssertCreated(await UploadAsync(UserB, type, "B First", ModelX));
        CustomProfileDto bSecond = AssertCreated(await UploadAsync(UserB, type, "B Second", ModelX));

        Assert.Equal(SharedName, ProfilesControllerNameConflictHarness.AssertOk(
            await UpdateAsync(UserB, bFirst.Id, new UpdateCustomProfileRequestDto { Name = SharedName })).Name);

        IActionResult collision = await UpdateAsync(UserB, bSecond.Id, new UpdateCustomProfileRequestDto { Name = SharedName });
        AssertNameConflict(collision, SharedName);
        Assert.Equal("B Second", await GetNameAsync(type, bSecond.Id));
    }

    [Theory]
    [InlineData("machine")]
    [InlineData("process")]
    public async Task Update_KeepingTheSameName_IsNotAConflict(string type)
    {
        CustomProfileDto a = AssertCreated(await UploadAsync(UserA, type, SharedName, ModelX));

        IActionResult result = await UpdateAsync(UserA, a.Id, new UpdateCustomProfileRequestDto { Name = SharedName, RawJson = RawJson(SharedName) });

        _ = ProfilesControllerNameConflictHarness.AssertOk(result);
    }

    [Theory]
    [InlineData("machine")]
    [InlineData("process")]
    public async Task Update_OtherUsersPrivateProfile_Returns404_NotAConflictOr403(string type)
    {
        // Renaming someone else's private profile onto one of their own names must not reveal
        // either the profile or the name: it is simply not found (#3179, #3184, #3188).
        CustomProfileDto aTarget = AssertCreated(await UploadAsync(UserA, type, "A Target", ModelX));
        _ = AssertCreated(await UploadAsync(UserA, type, SharedName, ModelX));

        IActionResult result = await UpdateAsync(UserB, aTarget.Id, new UpdateCustomProfileRequestDto { Name = SharedName });

        _ = Assert.IsType<NotFoundObjectResult>(result);
        Assert.Equal("A Target", await GetNameAsync(type, aTarget.Id));
    }

    [Fact]
    public async Task Process_SameNameUnderDifferentPrinterModels_IsNotAConflict()
    {
        _ = AssertCreated(await UploadAsync(UserA, "process", SharedName, ModelX));

        _ = AssertCreated(await UploadAsync(UserA, "process", SharedName, ModelY));

        Assert.Equal(2, await CountOwnedAsync("process", UserA, SharedName));
    }

    [Fact]
    public async Task Process_SameNameWithoutPrinterModel_IsNotAConflict()
    {
        // A NULL PrinterModelId never participated in process name uniqueness; #3198 keeps that.
        _ = AssertCreated(await UploadAsync(UserA, "process", SharedName, printerModelId: null));

        _ = AssertCreated(await UploadAsync(UserA, "process", SharedName, printerModelId: null));

        Assert.Equal(2, await CountOwnedAsync("process", UserA, SharedName));
    }

    [Fact]
    public async Task Process_MovingToAPrinterModelThatAlreadyHasTheName_Returns409()
    {
        _ = AssertCreated(await UploadAsync(UserA, "process", SharedName, ModelX));
        CustomProfileDto onY = AssertCreated(await UploadAsync(UserA, "process", SharedName, ModelY));

        IActionResult result = await UpdateAsync(UserA, onY.Id, new UpdateCustomProfileRequestDto { PrinterModelId = ModelX });

        AssertNameConflict(result, SharedName);
        Assert.Equal(ModelY, (await _processRepo.GetByIdAsync(onY.Id, CancellationToken.None))!.PrinterModelId);
    }

    [Fact]
    public async Task Process_CloneOntoAPrinterModelThatAlreadyHasTheName_Returns409()
    {
        _ = AssertCreated(await UploadAsync(UserA, "process", SharedName, ModelX));
        CustomProfileDto source = AssertCreated(await UploadAsync(UserA, "process", "A Source", ModelY));

        IActionResult onY = await CloneAsync(UserA, "process", source.Id, SharedName);
        _ = ProfilesControllerNameConflictHarness.AssertCloned(onY);

        IActionResult onX = await CloneAsync(UserA, "process", source.Id, SharedName, ModelX);
        AssertNameConflict(onX, SharedName);
    }

    [Fact]
    public async Task MachineUniqueIndex_IsScopedPerOwner_AndStillUniqueAmongUnownedRows()
    {
        await _machineRepo.AddAsync(MachineRow(UserA, SharedName));
        await _machineRepo.AddAsync(MachineRow(UserB, SharedName));
        await _machineRepo.AddAsync(MachineRow(null, SharedName));
        _db.ChangeTracker.Clear();

        _ = await Assert.ThrowsAsync<DbUpdateException>(() => _machineRepo.AddAsync(MachineRow(UserA, SharedName)));
        _db.ChangeTracker.Clear();
        _ = await Assert.ThrowsAsync<DbUpdateException>(() => _machineRepo.AddAsync(MachineRow(null, SharedName)));
    }

    [Fact]
    public async Task ProcessUniqueIndex_IsScopedPerOwner_AndStillUniqueAmongUnownedRows()
    {
        await _processRepo.AddAsync(ProcessRow(UserA, SharedName, ModelX));
        await _processRepo.AddAsync(ProcessRow(UserB, SharedName, ModelX));
        await _processRepo.AddAsync(ProcessRow(null, SharedName, ModelX));
        // Unowned rows without a printer model stay exempt, as they were under the global index.
        await _processRepo.AddAsync(ProcessRow(null, SharedName, null));
        await _processRepo.AddAsync(ProcessRow(null, SharedName, null));
        _db.ChangeTracker.Clear();

        _ = await Assert.ThrowsAsync<DbUpdateException>(() => _processRepo.AddAsync(ProcessRow(UserA, SharedName, ModelX)));
        _db.ChangeTracker.Clear();
        _ = await Assert.ThrowsAsync<DbUpdateException>(() => _processRepo.AddAsync(ProcessRow(null, SharedName, ModelX)));
    }

    private static MachineProfile MachineRow(Guid? ownerId, string name) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        SlicerType = SlicerType.OrcaSlicer,
        IsSystem = ownerId is null,
        CreatedByUserId = ownerId,
        Hash = "hash-" + Guid.NewGuid().ToString("N"),
        RawJson = RawJson(name),
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    private static ProcessProfile ProcessRow(Guid? ownerId, string name, Guid? printerModelId) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        SlicerType = SlicerType.OrcaSlicer,
        IsSystem = ownerId is null,
        CreatedByUserId = ownerId,
        PrinterModelId = printerModelId,
        Hash = "hash-" + Guid.NewGuid().ToString("N"),
        RawJson = RawJson(name),
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    private static string RawJson(string name) => $"{{\"name\":\"{name}\"}}";

    private static CustomProfileDto AssertCreated(IActionResult result) =>
        ProfilesControllerNameConflictHarness.AssertCreated(result);

    private static void AssertNameConflict(IActionResult result, string name) =>
        ProfilesControllerNameConflictHarness.AssertNameConflict(result, name);

    private Task<int> CountOwnedAsync(string type, Guid ownerId, string name) => type == "machine"
        ? _db.MachineProfiles.AsNoTracking().CountAsync(p => p.CreatedByUserId == ownerId && p.Name == name)
        : _db.ProcessProfiles.AsNoTracking().CountAsync(p => p.CreatedByUserId == ownerId && p.Name == name);

    private async Task<string?> GetNameAsync(string type, Guid id) => type == "machine"
        ? (await _machineRepo.GetByIdAsync(id, CancellationToken.None))?.Name
        : (await _processRepo.GetByIdAsync(id, CancellationToken.None))?.Name;

    private async Task<IActionResult> UploadAsync(Guid userId, string type, string name, Guid? printerModelId) =>
        await RunAsync(controller => controller.UploadCustomProfileAsync(
            new UploadProfileRequestDto { Name = name, RawJson = RawJson(name), ProfileType = type, PrinterModelId = printerModelId },
            CancellationToken.None), userId);

    private async Task<IActionResult> CloneAsync(Guid userId, string type, Guid sourceId, string name, Guid? printerModelId = null) =>
        await RunAsync(controller => controller.CloneSingleProfileAsync(
            new CloneSingleProfileRequestDto { SourceProfileId = sourceId, ProfileType = type, Name = name, PrinterModelId = printerModelId },
            CancellationToken.None), userId);

    private async Task<IActionResult> UpdateAsync(Guid userId, Guid profileId, UpdateCustomProfileRequestDto request) =>
        await RunAsync(controller => controller.UpdateCustomProfileAsync(profileId, request, CancellationToken.None), userId);

    private async Task<IActionResult> RunAsync(Func<ProfilesController, Task<IActionResult>> action, Guid userId)
    {
        IActionResult result = await action(ProfilesControllerNameConflictHarness.CreateController(_db, userId));

        // Each request gets a fresh DbContext in production.
        _db.ChangeTracker.Clear();
        return result;
    }
}
