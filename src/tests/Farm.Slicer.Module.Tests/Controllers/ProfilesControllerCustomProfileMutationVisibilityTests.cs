using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.PrinterCalibration;
using Farm.Infrastructure.Repositories.UnitOfWork;
using Farm.Infrastructure.Security;
using Farm.Infrastructure.Services.Catalog;
using Farm.Infrastructure.Services.Gcode;
using Farm.Slicer.Module.Api.Controllers.Slicing;
using Farm.Slicer.Module.Api.Hubs;
using Farm.Slicer.Module.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Slicer.Module.Tests.Controllers;

/// <summary>
/// Issue #3185 (follow-up to #3179/#3180): <c>PUT</c> and <c>DELETE /api/slicer/profiles/custom/{id}</c>
/// must answer another user's <b>private</b> process, filament, or machine profile with the same
/// 404 and message as an id that does not exist, so the mutation paths no longer disclose that the
/// row exists. 403 stays only for a row the caller can already view but does not own: a public
/// (farm-wide) row, or any row for an administrator. Each test drives the real controller action
/// against the real <see cref="ProfilesService"/> and SQLite-backed repositories with a distinct
/// <see cref="ClaimsPrincipal"/> per user.
/// </summary>
public sealed class ProfilesControllerCustomProfileMutationVisibilityTests : IDisposable
{
    private static readonly Guid OwnerId = Guid.NewGuid();
    private static readonly Guid OtherUserId = Guid.NewGuid();
    private static readonly Guid FamilyAdminId = Guid.NewGuid();

    private readonly SlicerDbContext _db = TestInfrastructure.TestHelpers.CreateSqliteInMemoryDb();
    private readonly EfMachineProfileRepository _machineRepo;
    private readonly EfFilamentProfileRepository _filamentRepo;
    private readonly EfProcessProfileRepository _processRepo;
    private readonly Guid _printerModelId = Guid.NewGuid();
    private readonly Dictionary<(string Type, string Kind), Guid> _ids = [];

    public ProfilesControllerCustomProfileMutationVisibilityTests()
    {
        _machineRepo = new EfMachineProfileRepository(_db);
        _filamentRepo = new EfFilamentProfileRepository(_db);
        _processRepo = new EfProcessProfileRepository(_db);
    }

    public void Dispose() => _db.Dispose();

    [Theory]
    [InlineData("process")]
    [InlineData("filament")]
    [InlineData("machine")]
    public async Task Put_OtherUserOnOwnersPrivateProfile_Returns404IdenticalToMissingId_AndLeavesRowUnchanged(string profileType)
    {
        await SeedAsync();
        Guid privateId = _ids[(profileType, "private")];
        Guid missingId = Guid.NewGuid();

        IActionResult other = await PutAsync(Principal(OtherUserId), privateId);
        IActionResult missing = await PutAsync(Principal(OtherUserId), missingId);

        AssertSameNotFound(other, privateId, missing, missingId);
        Assert.Equal(Name(profileType, "private"), await GetNameAsync(profileType, privateId));
    }

    [Theory]
    [InlineData("process")]
    [InlineData("filament")]
    [InlineData("machine")]
    public async Task Delete_OtherUserOnOwnersPrivateProfile_Returns404IdenticalToMissingId_AndLeavesRowInPlace(string profileType)
    {
        await SeedAsync();
        Guid privateId = _ids[(profileType, "private")];
        Guid missingId = Guid.NewGuid();

        IActionResult other = await DeleteAsync(Principal(OtherUserId), privateId);
        IActionResult missing = await DeleteAsync(Principal(OtherUserId), missingId);

        AssertSameNotFound(other, privateId, missing, missingId);
        Assert.Equal(Name(profileType, "private"), await GetNameAsync(profileType, privateId));
    }

    [Theory]
    [InlineData("process")]
    [InlineData("filament")]
    [InlineData("machine")]
    public async Task Put_Owner_UpdatesOwnPrivateProfile(string profileType)
    {
        await SeedAsync();
        Guid privateId = _ids[(profileType, "private")];

        IActionResult result = await PutAsync(Principal(OwnerId), privateId, "Renamed By Owner");

        CustomProfileDto dto = Assert.IsType<CustomProfileDto>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(profileType, dto.ProfileType);
        Assert.Equal("Renamed By Owner", await GetNameAsync(profileType, privateId));
    }

    [Fact]
    public async Task Delete_Owner_DeletesOwnPrivateFilamentProfile()
    {
        await SeedAsync();
        Guid privateId = _ids[("filament", "private")];

        _ = Assert.IsType<NoContentResult>(await DeleteAsync(Principal(OwnerId), privateId));
        Assert.Null(await _filamentRepo.GetByIdAsync(privateId, CancellationToken.None));
    }

    [Theory]
    [InlineData("process")]
    [InlineData("filament")]
    [InlineData("machine")]
    public async Task Put_OtherUserOnVisibleFarmWideProfile_StillReturns403(string profileType)
    {
        // The caller can already list this row, so 403 discloses nothing new.
        await SeedAsync();
        Guid farmWideId = _ids[(profileType, "farm-wide")];

        _ = Assert.IsType<ForbidResult>(await PutAsync(Principal(OtherUserId), farmWideId));
        Assert.Equal(Name(profileType, "farm-wide"), await GetNameAsync(profileType, farmWideId));
    }

    [Fact]
    public async Task Delete_OtherUserOnVisibleFarmWideFilament_StillReturns403()
    {
        await SeedAsync();
        Guid farmWideId = _ids[("filament", "farm-wide")];

        _ = Assert.IsType<ForbidResult>(await DeleteAsync(Principal(OtherUserId), farmWideId));
        Assert.NotNull(await _filamentRepo.GetByIdAsync(farmWideId, CancellationToken.None));
    }

    [Theory]
    [InlineData("process")]
    [InlineData("filament")]
    [InlineData("machine")]
    public async Task Put_SystemProfile_StillReturns400(string profileType)
    {
        await SeedAsync();

        _ = Assert.IsType<BadRequestObjectResult>(await PutAsync(Principal(OtherUserId), _ids[(profileType, "system")]));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Admin_OnAnotherUsersPrivateProfile_Returns403_Unchanged(bool farmAdminRole, bool slicerEnginesAdminPermission)
    {
        // Decision for #3185: administrators can view every row, so they keep the pre-existing 403
        // (visible, not owned). Admin visibility never grants mutation of another user's profile.
        await SeedAsync();
        List<Claim> claims = [];
        if (farmAdminRole)
        {
            claims.Add(new Claim(ClaimTypes.Role, PrintFarmerPermissions.FarmAdminRole));
        }

        if (slicerEnginesAdminPermission)
        {
            claims.Add(new Claim(PrintFarmerPermissions.ClaimType, "slicer_engines:admin"));
        }

        ClaimsPrincipal admin = Principal(Guid.NewGuid(), [.. claims]);

        foreach (string profileType in new[] { "process", "filament", "machine" })
        {
            Guid privateId = _ids[(profileType, "private")];
            _ = Assert.IsType<ForbidResult>(await PutAsync(admin, privateId));
            Assert.Equal(Name(profileType, "private"), await GetNameAsync(profileType, privateId));
        }

        _ = Assert.IsType<ForbidResult>(await DeleteAsync(admin, _ids[("filament", "private")]));
        Assert.NotNull(await _filamentRepo.GetByIdAsync(_ids[("filament", "private")], CancellationToken.None));
    }

    [Fact]
    public async Task NonAdminPermissionHolder_DoesNotGetAdminVisibility_Returns404()
    {
        await SeedAsync();
        ClaimsPrincipal caller = Principal(
            OtherUserId,
            new Claim(PrintFarmerPermissions.ClaimType, PrintFarmerPermissions.Slicing.Submit),
            new Claim(PrintFarmerPermissions.ClaimType, PrintFarmerPermissions.Calibration.Update));

        _ = Assert.IsType<NotFoundObjectResult>(await PutAsync(caller, _ids[("machine", "private")]));
        _ = Assert.IsType<NotFoundObjectResult>(await DeleteAsync(caller, _ids[("filament", "private")]));
    }

    private static void AssertSameNotFound(IActionResult actual, Guid actualId, IActionResult missing, Guid missingId)
    {
        NotFoundObjectResult actualNotFound = Assert.IsType<NotFoundObjectResult>(actual);
        NotFoundObjectResult missingNotFound = Assert.IsType<NotFoundObjectResult>(missing);
        Assert.Equal(
            Assert.IsType<string>(missingNotFound.Value).Replace(missingId.ToString(), "{id}", StringComparison.Ordinal),
            Assert.IsType<string>(actualNotFound.Value).Replace(actualId.ToString(), "{id}", StringComparison.Ordinal));
    }

    private static string Name(string profileType, string kind) => $"{kind} {profileType}";

    private async Task<string?> GetNameAsync(string profileType, Guid id) => profileType switch
    {
        "process" => (await _processRepo.GetByIdAsync(id, CancellationToken.None))?.Name,
        "filament" => (await _filamentRepo.GetByIdAsync(id, CancellationToken.None))?.Name,
        "machine" => (await _machineRepo.GetByIdAsync(id, CancellationToken.None))?.Name,
        _ => throw new ArgumentOutOfRangeException(nameof(profileType), profileType, null)
    };

    private async Task SeedAsync()
    {
        foreach ((string kind, bool isSystem, bool isPublic, Guid? ownerId) in new (string, bool, bool, Guid?)[]
        {
            ("private", false, false, OwnerId),

            // Shape of a ProfileFamilyService.CloneFamilyAsync variant: farm-wide, non-system, public (#2056).
            ("farm-wide", false, true, FamilyAdminId),
            ("system", true, true, null),
        })
        {
            await AddProcessAsync(kind, isSystem, isPublic, ownerId);
            await AddFilamentAsync(kind, isSystem, isPublic, ownerId);
            await AddMachineAsync(kind, isSystem, isPublic, ownerId);
        }

        // Each request gets a fresh DbContext in production; drop the seeding entities so the
        // repositories' untracked reads can be attached by UpdateAsync/DeleteAsync.
        _db.ChangeTracker.Clear();
    }

    private async Task AddProcessAsync(string kind, bool isSystem, bool isPublic, Guid? ownerId)
    {
        string name = Name("process", kind);
        ProcessProfile profile = new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            SlicerType = SlicerType.OrcaSlicer,
            IsSystem = isSystem,
            IsPublic = isPublic,
            CreatedByUserId = ownerId,
            PrinterModelId = _printerModelId,
            Hash = "hash-" + name,
            RawJson = RawJson(name)
        };

        await _processRepo.AddAsync(profile);
        _ids[("process", kind)] = profile.Id;
    }

    private async Task AddFilamentAsync(string kind, bool isSystem, bool isPublic, Guid? ownerId)
    {
        string name = Name("filament", kind);
        FilamentProfile profile = new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            Material = "PLA",
            Manufacturer = "TestCo",
            SlicerType = SlicerType.OrcaSlicer,
            IsSystem = isSystem,
            IsPublic = isPublic,
            CreatedByUserId = ownerId,
            Hash = "hash-" + name,
            RawJson = RawJson(name),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        await _filamentRepo.AddAsync(profile);
        _ids[("filament", kind)] = profile.Id;
    }

    private async Task AddMachineAsync(string kind, bool isSystem, bool isPublic, Guid? ownerId)
    {
        string name = Name("machine", kind);
        MachineProfile profile = new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            Manufacturer = "TestCo",
            SlicerType = SlicerType.OrcaSlicer,
            PrinterModelId = _printerModelId,
            IsSystem = isSystem,
            IsPublic = isPublic,
            CreatedByUserId = ownerId,
            Hash = "hash-" + name,
            RawJson = RawJson(name),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        await _machineRepo.AddAsync(profile);
        _ids[("machine", kind)] = profile.Id;
    }

    private static string RawJson(string name) => $"{{\"name\":\"{name}\"}}";

    private Task<IActionResult> PutAsync(ClaimsPrincipal user, Guid id, string name = "Renamed By Caller") =>
        CreateController(user).UpdateCustomProfileAsync(id, new UpdateCustomProfileRequestDto { Name = name }, CancellationToken.None);

    private Task<IActionResult> DeleteAsync(ClaimsPrincipal user, Guid id) =>
        CreateController(user).DeleteCustomProfileAsync(id, CancellationToken.None);

    private static ClaimsPrincipal Principal(Guid userId, params Claim[] extra) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString()), .. extra], "Test"));

    private ProfilesController CreateController(ClaimsPrincipal user)
    {
        ProfilesService service = new(
            new EfProfilesRepository(_db),
            NullLogger<ProfilesService>.Instance,
            _processRepo,
            _machineRepo,
            _filamentRepo,
            new Mock<IUnitOfWork>(MockBehavior.Loose).Object,
            new Mock<ICatalogService>(MockBehavior.Loose).Object,
            new Mock<IProfileParsingService>(MockBehavior.Loose).Object,
            new Mock<IHubContext<SlicerHub>>(MockBehavior.Loose).Object,
            new Mock<ISlicersService>(MockBehavior.Loose).Object,
            new Mock<IPrinterModelAliasService>(MockBehavior.Loose).Object);

        return new ProfilesController(
            NullLogger<ProfilesController>.Instance,
            service,
            new Mock<ICatalogServiceAdapter>(MockBehavior.Loose).Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = user } }
        };
    }
}
