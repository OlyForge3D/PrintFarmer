using System;
using System.Collections.Generic;
using System.Linq;
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
/// Issue #3174: a non-admin caller holding only <c>slicing:submit</c> must not be able to list,
/// retrieve, or clone another user's private process profile through any
/// <see cref="ProfilesController"/> read projection. Each test drives the real controller action
/// against the real <see cref="ProfilesService"/> and SQLite-backed repositories, with a distinct
/// <see cref="ClaimsPrincipal"/> per user, so both the controller's caller-identity derivation and
/// the service's visibility rule are exercised together.
/// </summary>
public sealed class ProfilesControllerPrivateProcessProfileVisibilityTests : IDisposable
{
    private static readonly Guid OwnerId = Guid.NewGuid();
    private static readonly Guid OtherUserId = Guid.NewGuid();
    private static readonly Guid DevFallbackUserId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private const string OwnerPrivateName = "Owner Private Process";
    private const string OtherPrivateName = "Other Private Process";
    private const string DevFallbackPrivateName = "Dev Fallback Private Process";
    private const string OtherPublicName = "Other Public Process";
    private const string SystemName = "System Process";

    private readonly SlicerDbContext _db = TestInfrastructure.TestHelpers.CreateSqliteInMemoryDb();
    private readonly EfProcessProfileRepository _processRepo;
    private readonly Guid _printerModelId = Guid.NewGuid();
    private readonly Dictionary<string, Guid> _ids = new(StringComparer.Ordinal);

    public ProfilesControllerPrivateProcessProfileVisibilityTests()
    {
        _processRepo = new EfProcessProfileRepository(_db);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task ListExtended_OwnerSeesOwnPrivate_OtherUserDoesNot_PublicAndSystemVisibleToBoth()
    {
        await SeedAsync();

        IReadOnlyList<string> ownerNames = await ListExtendedNamesAsync(Principal(OwnerId));
        IReadOnlyList<string> otherNames = await ListExtendedNamesAsync(Principal(OtherUserId));

        Assert.Contains(OwnerPrivateName, ownerNames);
        Assert.DoesNotContain(OtherPrivateName, ownerNames);
        Assert.DoesNotContain(OwnerPrivateName, otherNames);
        Assert.Contains(OtherPrivateName, otherNames);

        foreach (IReadOnlyList<string> names in new[] { ownerNames, otherNames })
        {
            Assert.Contains(OtherPublicName, names);
            Assert.Contains(SystemName, names);
            Assert.DoesNotContain(DevFallbackPrivateName, names);
        }
    }

    [Fact]
    public async Task ListProfiles_OtherUserCannotListOwnersPrivateProfile()
    {
        await SeedAsync();

        IReadOnlyList<string> ownerNames = await ListNamesAsync(Principal(OwnerId));
        IReadOnlyList<string> otherNames = await ListNamesAsync(Principal(OtherUserId));

        Assert.Contains(OwnerPrivateName, ownerNames);
        Assert.DoesNotContain(OwnerPrivateName, otherNames);
        Assert.Contains(OtherPublicName, otherNames);
        Assert.Contains(SystemName, otherNames);
    }

    [Fact]
    public async Task GetProfileById_OwnerRetrievesPrivate_OtherUserGets404()
    {
        await SeedAsync();
        Guid privateId = _ids[OwnerPrivateName];

        IActionResult ownerResult = await CreateController(Principal(OwnerId)).GetProfileAsync(privateId);
        IActionResult otherResult = await CreateController(Principal(OtherUserId)).GetProfileAsync(privateId);

        OkObjectResult ok = Assert.IsType<OkObjectResult>(ownerResult);
        Assert.Equal(privateId, Assert.IsType<ProcessProfileResponseDto>(ok.Value).Id);

        // 404, not 403: the response must be indistinguishable from an id that does not exist.
        _ = Assert.IsType<NotFoundResult>(otherResult);
        _ = Assert.IsType<NotFoundResult>(await CreateController(Principal(OtherUserId)).GetProfileAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task GetProfileById_PublicAndSystemProfilesRemainVisibleToOtherUsers()
    {
        await SeedAsync();
        ProfilesController other = CreateController(Principal(OtherUserId));

        _ = Assert.IsType<OkObjectResult>(await CreateController(Principal(OwnerId)).GetProfileAsync(_ids[OtherPublicName]));
        _ = Assert.IsType<OkObjectResult>(await other.GetProfileAsync(_ids[SystemName]));
    }

    [Fact]
    public async Task ListHierarchy_OtherUserCannotSeeOwnersPrivateProfileInAnyProjection()
    {
        await SeedAsync();

        IReadOnlyList<string> ownerNames = await ListHierarchyNamesAsync(Principal(OwnerId));
        IReadOnlyList<string> otherNames = await ListHierarchyNamesAsync(Principal(OtherUserId));

        Assert.Contains(OwnerPrivateName, ownerNames);
        Assert.DoesNotContain(OwnerPrivateName, otherNames);
        Assert.Contains(OtherPublicName, otherNames);
        Assert.Contains(SystemName, otherNames);
    }

    [Fact]
    public async Task ImportedNames_OtherUserCannotSeeOwnersPrivateProcessName()
    {
        await SeedAsync();

        ImportedProfileNamesDto owner = await ImportedNamesAsync(Principal(OwnerId));
        ImportedProfileNamesDto other = await ImportedNamesAsync(Principal(OtherUserId));

        Assert.Contains(OwnerPrivateName, owner.ProcessProfileNames);
        Assert.DoesNotContain(OwnerPrivateName, other.ProcessProfileNames);
        Assert.Contains(OtherPublicName, other.ProcessProfileNames);
        Assert.Contains(SystemName, other.ProcessProfileNames);
    }

    [Fact]
    public async Task CloneProcess_OtherUserCannotCloneOwnersPrivateProfile_OwnerAndPublicSourcesStillClone()
    {
        await SeedAsync();

        IActionResult otherClone = await CloneAsync(Principal(OtherUserId), _ids[OwnerPrivateName]);
        IActionResult ownerClone = await CloneAsync(Principal(OwnerId), _ids[OwnerPrivateName]);
        IActionResult publicClone = await CloneAsync(Principal(OtherUserId), _ids[OtherPublicName]);

        _ = Assert.IsType<NotFoundObjectResult>(otherClone);
        _ = Assert.IsType<CreatedResult>(ownerClone);
        _ = Assert.IsType<CreatedResult>(publicClone);
        Assert.DoesNotContain(
            (await _processRepo.GetByEngineAsync(SlicerType.OrcaSlicer, includeSystem: true, userId: null, CancellationToken.None)),
            p => p.CreatedByUserId == OtherUserId && p.RawJson == PrivateRawJson(OwnerPrivateName));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Admin_SeesEveryUsersPrivateProfile(bool farmAdminRole, bool slicerEnginesAdminPermission)
    {
        await SeedAsync();
        List<Claim> claims = [new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())];
        if (farmAdminRole)
        {
            claims.Add(new Claim(ClaimTypes.Role, PrintFarmerPermissions.FarmAdminRole));
        }

        if (slicerEnginesAdminPermission)
        {
            claims.Add(new Claim(PrintFarmerPermissions.ClaimType, "slicer_engines:admin"));
        }

        ClaimsPrincipal admin = new(new ClaimsIdentity(claims, "Test"));

        IReadOnlyList<string> extended = await ListExtendedNamesAsync(admin);
        IReadOnlyList<string> list = await ListNamesAsync(admin);
        IReadOnlyList<string> hierarchy = await ListHierarchyNamesAsync(admin);

        foreach (IReadOnlyList<string> names in new[] { extended, list, hierarchy })
        {
            Assert.Contains(OwnerPrivateName, names);
            Assert.Contains(OtherPrivateName, names);
            Assert.Contains(OtherPublicName, names);
            Assert.Contains(SystemName, names);
        }

        _ = Assert.IsType<OkObjectResult>(await CreateController(admin).GetProfileAsync(_ids[OwnerPrivateName]));
    }

    [Fact]
    public async Task NonAdminPermissionHolder_DoesNotGetAdminVisibility()
    {
        await SeedAsync();
        ClaimsPrincipal submitter = Principal(OtherUserId, new Claim(PrintFarmerPermissions.ClaimType, PrintFarmerPermissions.Slicing.Submit));

        Assert.DoesNotContain(OwnerPrivateName, await ListExtendedNamesAsync(submitter));
        _ = Assert.IsType<NotFoundResult>(await CreateController(submitter).GetProfileAsync(_ids[OwnerPrivateName]));
    }

    [Fact]
    public async Task CallerWithoutIdentityClaim_SeesOnlyPublicAndSystem_NotDevFallbackUsersPrivateProfiles()
    {
        await SeedAsync();
        ClaimsPrincipal anonymous = new(new ClaimsIdentity([], "Test"));

        IReadOnlyList<string> names = await ListExtendedNamesAsync(anonymous);

        Assert.DoesNotContain(DevFallbackPrivateName, names);
        Assert.DoesNotContain(OwnerPrivateName, names);
        Assert.Contains(OtherPublicName, names);
        Assert.Contains(SystemName, names);
        _ = Assert.IsType<NotFoundResult>(await CreateController(anonymous).GetProfileAsync(_ids[DevFallbackPrivateName]));
    }

    private async Task SeedAsync()
    {
        await AddAsync(OwnerPrivateName, isSystem: false, isPublic: false, OwnerId);
        await AddAsync(OtherPrivateName, isSystem: false, isPublic: false, OtherUserId);
        await AddAsync(DevFallbackPrivateName, isSystem: false, isPublic: false, DevFallbackUserId);
        await AddAsync(OtherPublicName, isSystem: false, isPublic: true, OtherUserId);
        await AddAsync(SystemName, isSystem: true, isPublic: true, ownerId: null);

        // An unlinked system machine profile so ListHierarchy emits per-model process projections.
        await new EfMachineProfileRepository(_db).AddAsync(new MachineProfile
        {
            Id = Guid.NewGuid(),
            Name = "System Machine 0.4 nozzle",
            Manufacturer = "TestCo",
            SlicerType = SlicerType.OrcaSlicer,
            IsSystem = true,
            IsPublic = true,
            Hash = "machine-hash",
            RawJson = "{}",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
    }

    private async Task AddAsync(string name, bool isSystem, bool isPublic, Guid? ownerId)
    {
        ProcessProfile profile = new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            SlicerType = SlicerType.OrcaSlicer,
            IsSystem = isSystem,
            IsPublic = isPublic,
            CreatedByUserId = ownerId,
            PrinterModelId = null,
            Hash = "hash-" + name,
            RawJson = PrivateRawJson(name)
        };

        await _processRepo.AddAsync(profile);
        _ids[name] = profile.Id;
    }

    private static string PrivateRawJson(string name) => $"{{\"name\":\"{name}\",\"layer_height\":\"0.2\"}}";

    private async Task<IReadOnlyList<string>> ListExtendedNamesAsync(ClaimsPrincipal user)
    {
        IActionResult result = await CreateController(user).ListExtendedAsync(CancellationToken.None);
        ExtendedProfilesResponseDto dto = Assert.IsType<ExtendedProfilesResponseDto>(Assert.IsType<OkObjectResult>(result).Value);
        return dto.ProcessProfiles.Select(p => p.Name).ToList();
    }

    private async Task<IReadOnlyList<string>> ListNamesAsync(ClaimsPrincipal user)
    {
        IActionResult result = await CreateController(user).GetProfilesAsync();
        IEnumerable<ProcessProfileListEntryDto> dto = Assert.IsAssignableFrom<IEnumerable<ProcessProfileListEntryDto>>(Assert.IsType<OkObjectResult>(result).Value);
        return dto.Select(p => p.Name).ToList();
    }

    private async Task<IReadOnlyList<string>> ListHierarchyNamesAsync(ClaimsPrincipal user)
    {
        IActionResult result = await CreateController(user).ListHierarchyAsync(ct: CancellationToken.None);
        HierarchicalProfilesResponseDto dto = Assert.IsType<HierarchicalProfilesResponseDto>(Assert.IsType<OkObjectResult>(result).Value);

        // Every process projection in the response: the flat grouping and each per-model entry.
        List<ProcessProfileListItemDto> perModel = dto.ByHierarchy.Values
            .SelectMany(m => m.Models.Values)
            .SelectMany(m => m.ProcessProfiles)
            .ToList();
        Assert.NotEmpty(perModel);
        List<string> flat = dto.ProcessProfiles.Values.SelectMany(v => v).Select(p => p.Name).ToList();
        Assert.Equal(flat.OrderBy(n => n, StringComparer.Ordinal), perModel.Select(p => p.Name).Distinct().OrderBy(n => n, StringComparer.Ordinal));
        return flat;
    }

    private async Task<ImportedProfileNamesDto> ImportedNamesAsync(ClaimsPrincipal user)
    {
        foreach (ProcessProfile p in _db.ProcessProfiles)
        {
            p.PrinterModelId = _printerModelId;
        }

        _ = await _db.SaveChangesAsync();

        IActionResult result = await CreateController(user).GetImportedProfileNamesAsync(_printerModelId, CancellationToken.None);
        return Assert.IsType<ImportedProfileNamesDto>(Assert.IsType<OkObjectResult>(result).Value);
    }

    private Task<IActionResult> CloneAsync(ClaimsPrincipal user, Guid sourceId) =>
        CreateController(user).CloneSingleProfileAsync(
            new CloneSingleProfileRequestDto { SourceProfileId = sourceId, ProfileType = "process", Name = $"Clone {Guid.NewGuid():N}" },
            CancellationToken.None);

    private static ClaimsPrincipal Principal(Guid userId, params Claim[] extra) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString()), .. extra], "Test"));

    private ProfilesController CreateController(ClaimsPrincipal user)
    {
        ProfilesService service = new(
            new EfProfilesRepository(_db),
            NullLogger<ProfilesService>.Instance,
            _processRepo,
            new EfMachineProfileRepository(_db),
            new EfFilamentProfileRepository(_db),
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
