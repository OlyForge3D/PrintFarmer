using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
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
/// Issue #3180 (follow-up to #3174): a non-admin caller must not be able to list, select, clone,
/// or resolve another user's private <b>filament</b> or <b>machine</b> profile through any
/// <see cref="ProfilesController"/> database projection. Each test drives the real controller
/// action against the real <see cref="ProfilesService"/> and SQLite-backed repositories with a
/// distinct <see cref="ClaimsPrincipal"/> per user, so the controller's caller-identity derivation
/// and the shared <see cref="ProfileViewer"/> rule are exercised together.
/// </summary>
public sealed class ProfilesControllerPrivateFilamentMachineProfileVisibilityTests : IDisposable
{
    private static readonly Guid OwnerId = Guid.NewGuid();
    private static readonly Guid OtherUserId = Guid.NewGuid();
    private static readonly Guid FamilyAdminId = Guid.NewGuid();
    private static readonly Guid DevFallbackUserId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private const string OwnerPrivateMachine = "Owner Private Machine";
    private const string OtherPrivateMachine = "Other Private Machine";
    private const string DevFallbackPrivateMachine = "Dev Fallback Private Machine";
    private const string FarmWideMachine = "Farm-Wide Family Machine";
    private const string SystemMachine = "System Machine 0.4 nozzle";

    private const string OwnerPrivateFilament = "Owner Private Filament";
    private const string OtherPrivateFilament = "Other Private Filament";
    private const string DevFallbackPrivateFilament = "Dev Fallback Private Filament";
    private const string FarmWideFilament = "Farm-Wide Filament";
    private const string SystemFilament = "System Filament";

    private const string OwnerPrivateProcess = "Owner Private Process For Resolve";

    private static readonly string[] AllMachines = [OwnerPrivateMachine, OtherPrivateMachine, DevFallbackPrivateMachine, FarmWideMachine, SystemMachine];
    private static readonly string[] AllFilaments = [OwnerPrivateFilament, OtherPrivateFilament, DevFallbackPrivateFilament, FarmWideFilament, SystemFilament];

    private readonly SlicerDbContext _db = TestInfrastructure.TestHelpers.CreateSqliteInMemoryDb();
    private readonly EfMachineProfileRepository _machineRepo;
    private readonly EfFilamentProfileRepository _filamentRepo;
    private readonly EfProcessProfileRepository _processRepo;
    private readonly Guid _printerModelId = Guid.NewGuid();
    private readonly Dictionary<string, Guid> _ids = new(StringComparer.Ordinal);

    public ProfilesControllerPrivateFilamentMachineProfileVisibilityTests()
    {
        _machineRepo = new EfMachineProfileRepository(_db);
        _filamentRepo = new EfFilamentProfileRepository(_db);
        _processRepo = new EfProcessProfileRepository(_db);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task ListExtended_PrivateFilamentAndMachine_VisibleOnlyToOwner()
    {
        await SeedAsync();

        (IReadOnlyList<string> ownerMachines, IReadOnlyList<string> ownerFilaments) = await ListExtendedNamesAsync(Principal(OwnerId));
        (IReadOnlyList<string> otherMachines, IReadOnlyList<string> otherFilaments) = await ListExtendedNamesAsync(Principal(OtherUserId));

        AssertOwnerView(ownerMachines, ownerFilaments);
        AssertOtherView(otherMachines, otherFilaments);
    }

    [Fact]
    public async Task ListHierarchy_PrivateFilamentAndMachine_HiddenFromOtherUserInEveryProjection()
    {
        await SeedAsync();

        (IReadOnlyList<string> ownerMachines, IReadOnlyList<string> ownerFilaments) = HierarchyNames(await ListHierarchyAsync(Principal(OwnerId)));
        (IReadOnlyList<string> otherMachines, IReadOnlyList<string> otherFilaments) = HierarchyNames(await ListHierarchyAsync(Principal(OtherUserId)));

        AssertOwnerView(ownerMachines, ownerFilaments);
        AssertOtherView(otherMachines, otherFilaments);
    }

    [Fact]
    public async Task ListHierarchy_MachineProfileIdOfAnotherUsersPrivateMachine_Returns404IndistinguishableFromMissing()
    {
        await SeedAsync();
        Guid privateId = _ids[OwnerPrivateMachine];
        Guid missingId = Guid.NewGuid();

        IActionResult other = await ListHierarchyAsync(Principal(OtherUserId), privateId);
        IActionResult missing = await ListHierarchyAsync(Principal(OtherUserId), missingId);
        IActionResult owner = await ListHierarchyAsync(Principal(OwnerId), privateId);

        // 404, not 403, with the same message shape as an id that does not exist.
        NotFoundObjectResult otherNotFound = Assert.IsType<NotFoundObjectResult>(other);
        NotFoundObjectResult missingNotFound = Assert.IsType<NotFoundObjectResult>(missing);
        Assert.Equal($"Machine profile {privateId} not found", otherNotFound.Value);
        Assert.Equal($"Machine profile {missingId} not found", missingNotFound.Value);

        (IReadOnlyList<string> ownerMachines, _) = HierarchyNames(owner);
        Assert.Contains(OwnerPrivateMachine, ownerMachines);

        // Farm-wide (public, non-system) and system machines remain selectable by other users.
        _ = Assert.IsType<OkObjectResult>(await ListHierarchyAsync(Principal(OtherUserId), _ids[FarmWideMachine]));
        _ = Assert.IsType<OkObjectResult>(await ListHierarchyAsync(Principal(OtherUserId), _ids[SystemMachine]));
    }

    [Fact]
    public async Task ListHierarchy_SelectedVisibleMachine_DoesNotLeakOtherUsersCompatiblePrivateFilaments()
    {
        await SeedAsync();

        (IReadOnlyList<string> machines, IReadOnlyList<string> filaments) = HierarchyNames(await ListHierarchyAsync(Principal(OtherUserId), _ids[SystemMachine]));

        // Every seeded filament declares compatibility with SystemMachine, so only visibility filters them.
        AssertOtherView(machines, filaments);
    }

    [Fact]
    public async Task ImportedNames_PrivateFilamentAndMachineNames_VisibleOnlyToOwner()
    {
        await SeedAsync();

        ImportedProfileNamesDto owner = await ImportedNamesAsync(Principal(OwnerId));
        ImportedProfileNamesDto other = await ImportedNamesAsync(Principal(OtherUserId));

        AssertOwnerView(owner.MachineProfileNames.ToList(), owner.FilamentProfileNames.ToList());
        AssertOtherView(other.MachineProfileNames.ToList(), other.FilamentProfileNames.ToList());
    }

    [Theory]
    [InlineData("machine", OwnerPrivateMachine, FarmWideMachine, SystemMachine)]
    [InlineData("filament", OwnerPrivateFilament, FarmWideFilament, SystemFilament)]
    public async Task Clone_OtherUserCannotCloneOwnersPrivateProfile_Returns404_OwnerPublicAndSystemSourcesStillClone(
        string profileType, string privateName, string farmWideName, string systemName)
    {
        await SeedAsync();
        Guid privateId = _ids[privateName];
        Guid missingId = Guid.NewGuid();

        IActionResult otherClone = await CloneAsync(Principal(OtherUserId), privateId, profileType);
        IActionResult missingClone = await CloneAsync(Principal(OtherUserId), missingId, profileType);

        NotFoundObjectResult otherNotFound = Assert.IsType<NotFoundObjectResult>(otherClone);
        NotFoundObjectResult missingNotFound = Assert.IsType<NotFoundObjectResult>(missingClone);
        Assert.Equal(
            Assert.IsType<string>(missingNotFound.Value).Replace(missingId.ToString(), "{id}", StringComparison.Ordinal),
            Assert.IsType<string>(otherNotFound.Value).Replace(privateId.ToString(), "{id}", StringComparison.Ordinal));

        // Nothing owned by the other user carries the private source's settings.
        Assert.Empty(await OtherUserRowsWithRawJsonAsync(profileType, RawJson(privateName)));

        _ = Assert.IsType<CreatedResult>(await CloneAsync(Principal(OwnerId), privateId, profileType));
        _ = Assert.IsType<CreatedResult>(await CloneAsync(Principal(OtherUserId), _ids[farmWideName], profileType));
        _ = Assert.IsType<CreatedResult>(await CloneAsync(Principal(OtherUserId), _ids[systemName], profileType));
    }

    [Theory]
    [InlineData(ProfileResolutionType.Machine, OwnerPrivateMachine)]
    [InlineData(ProfileResolutionType.Filament, OwnerPrivateFilament)]
    [InlineData(ProfileResolutionType.Process, OwnerPrivateProcess)]
    public async Task ResolveForModel_OtherUserCannotResolveOwnersPrivateProfileIdByName(ProfileResolutionType type, string privateName)
    {
        await SeedAsync();
        Guid privateId = _ids[privateName];

        IActionResult owner = await ResolveAsync(Principal(OwnerId), type, privateName);
        IActionResult other = await ResolveAsync(Principal(OtherUserId), type, privateName);

        ResolveProfileForModelResultDto ownerDto = Assert.IsType<ResolveProfileForModelResultDto>(Assert.IsType<OkObjectResult>(owner).Value);
        Assert.Equal(privateId, ownerDto.ProfileId);

        // The name no longer matches a DB row the caller may see, so resolution falls through to the
        // catalog/worker import path (here: model absent from the service catalog) and fails without
        // ever returning the private id.
        BadRequestObjectResult otherBad = Assert.IsType<BadRequestObjectResult>(other);
        Assert.DoesNotContain(privateId.ToString(), Assert.IsType<string>(otherBad.Value), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(ProfileResolutionType.Machine, FarmWideMachine)]
    [InlineData(ProfileResolutionType.Machine, SystemMachine)]
    [InlineData(ProfileResolutionType.Filament, FarmWideFilament)]
    [InlineData(ProfileResolutionType.Filament, SystemFilament)]
    public async Task ResolveForModel_PublicAndSystemProfilesStillResolveForOtherUsers(ProfileResolutionType type, string name)
    {
        await SeedAsync();

        IActionResult result = await ResolveAsync(Principal(OtherUserId), type, name);

        ResolveProfileForModelResultDto dto = Assert.IsType<ResolveProfileForModelResultDto>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(_ids[name], dto.ProfileId);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Admin_SeesEveryUsersPrivateFilamentAndMachine(bool farmAdminRole, bool slicerEnginesAdminPermission)
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

        (IReadOnlyList<string> extMachines, IReadOnlyList<string> extFilaments) = await ListExtendedNamesAsync(admin);
        (IReadOnlyList<string> hierMachines, IReadOnlyList<string> hierFilaments) = HierarchyNames(await ListHierarchyAsync(admin));
        ImportedProfileNamesDto imported = await ImportedNamesAsync(admin);

        foreach (IReadOnlyList<string> machines in new[] { extMachines, hierMachines, imported.MachineProfileNames.ToList() })
        {
            Assert.All(AllMachines, n => Assert.Contains(n, machines));
        }

        foreach (IReadOnlyList<string> filaments in new[] { extFilaments, hierFilaments, imported.FilamentProfileNames.ToList() })
        {
            Assert.All(AllFilaments, n => Assert.Contains(n, filaments));
        }

        _ = Assert.IsType<OkObjectResult>(await ListHierarchyAsync(admin, _ids[OwnerPrivateMachine]));
        _ = Assert.IsType<CreatedResult>(await CloneAsync(admin, _ids[OwnerPrivateMachine], "machine"));
        _ = Assert.IsType<CreatedResult>(await CloneAsync(admin, _ids[OwnerPrivateFilament], "filament"));
    }

    [Fact]
    public async Task NonAdminPermissionHolder_DoesNotGetAdminVisibility()
    {
        await SeedAsync();
        ClaimsPrincipal submitter = Principal(
            OtherUserId,
            new Claim(PrintFarmerPermissions.ClaimType, PrintFarmerPermissions.Slicing.Submit),
            new Claim(PrintFarmerPermissions.ClaimType, PrintFarmerPermissions.Calibration.Update));

        (IReadOnlyList<string> machines, IReadOnlyList<string> filaments) = await ListExtendedNamesAsync(submitter);

        AssertOtherView(machines, filaments);
        _ = Assert.IsType<NotFoundObjectResult>(await ListHierarchyAsync(submitter, _ids[OwnerPrivateMachine]));
        _ = Assert.IsType<NotFoundObjectResult>(await CloneAsync(submitter, _ids[OwnerPrivateFilament], "filament"));
    }

    [Fact]
    public async Task CallerWithoutIdentityClaim_SeesOnlyPublicAndSystem_NotDevFallbackUsersPrivateProfiles()
    {
        await SeedAsync();
        ClaimsPrincipal anonymous = new(new ClaimsIdentity([], "Test"));

        (IReadOnlyList<string> extMachines, IReadOnlyList<string> extFilaments) = await ListExtendedNamesAsync(anonymous);
        (IReadOnlyList<string> hierMachines, IReadOnlyList<string> hierFilaments) = HierarchyNames(await ListHierarchyAsync(anonymous));
        ImportedProfileNamesDto imported = await ImportedNamesAsync(anonymous);

        foreach (IReadOnlyList<string> machines in new[] { extMachines, hierMachines, imported.MachineProfileNames.ToList() })
        {
            Assert.Equal([FarmWideMachine, SystemMachine], machines.Order(StringComparer.Ordinal).Distinct());
        }

        foreach (IReadOnlyList<string> filaments in new[] { extFilaments, hierFilaments, imported.FilamentProfileNames.ToList() })
        {
            Assert.Equal([FarmWideFilament, SystemFilament], filaments.Order(StringComparer.Ordinal).Distinct());
        }

        _ = Assert.IsType<NotFoundObjectResult>(await ListHierarchyAsync(anonymous, _ids[DevFallbackPrivateMachine]));
        _ = Assert.IsType<BadRequestObjectResult>(await ResolveAsync(anonymous, ProfileResolutionType.Machine, DevFallbackPrivateMachine));
    }

    [Fact]
    public async Task Repositories_UserScopedGetByEngine_IncludesOwnPublicAndSystem_ExcludesOtherUsersPrivate()
    {
        await SeedAsync();

        IReadOnlyList<string> machines = (await _machineRepo.GetByEngineAsync(SlicerType.OrcaSlicer, includeSystem: true, OtherUserId, CancellationToken.None))
            .Select(m => m.Name).ToList();
        IReadOnlyList<string> filaments = (await _filamentRepo.GetByEngineAsync(SlicerType.OrcaSlicer, includeSystem: true, OtherUserId, CancellationToken.None))
            .Select(f => f.Name).ToList();

        AssertOtherView(machines, filaments);
    }

    private static void AssertOwnerView(IReadOnlyList<string> machines, IReadOnlyList<string> filaments)
    {
        Assert.Contains(OwnerPrivateMachine, machines);
        Assert.Contains(OwnerPrivateFilament, filaments);
        Assert.DoesNotContain(OtherPrivateMachine, machines);
        Assert.DoesNotContain(OtherPrivateFilament, filaments);
        AssertSharedView(machines, filaments);
    }

    private static void AssertOtherView(IReadOnlyList<string> machines, IReadOnlyList<string> filaments)
    {
        Assert.DoesNotContain(OwnerPrivateMachine, machines);
        Assert.DoesNotContain(OwnerPrivateFilament, filaments);
        Assert.Contains(OtherPrivateMachine, machines);
        Assert.Contains(OtherPrivateFilament, filaments);
        AssertSharedView(machines, filaments);
    }

    private static void AssertSharedView(IReadOnlyList<string> machines, IReadOnlyList<string> filaments)
    {
        Assert.Contains(FarmWideMachine, machines);
        Assert.Contains(SystemMachine, machines);
        Assert.Contains(FarmWideFilament, filaments);
        Assert.Contains(SystemFilament, filaments);
        Assert.DoesNotContain(DevFallbackPrivateMachine, machines);
        Assert.DoesNotContain(DevFallbackPrivateFilament, filaments);
    }

    private async Task SeedAsync()
    {
        await AddMachineAsync(OwnerPrivateMachine, isSystem: false, isPublic: false, OwnerId);
        await AddMachineAsync(OtherPrivateMachine, isSystem: false, isPublic: false, OtherUserId);
        await AddMachineAsync(DevFallbackPrivateMachine, isSystem: false, isPublic: false, DevFallbackUserId);

        // Shape of a ProfileFamilyService.CloneFamilyAsync variant: farm-wide, non-system, public (#2056).
        await AddMachineAsync(FarmWideMachine, isSystem: false, isPublic: true, FamilyAdminId);
        await AddMachineAsync(SystemMachine, isSystem: true, isPublic: true, ownerId: null);

        await AddFilamentAsync(OwnerPrivateFilament, isSystem: false, isPublic: false, OwnerId);
        await AddFilamentAsync(OtherPrivateFilament, isSystem: false, isPublic: false, OtherUserId);
        await AddFilamentAsync(DevFallbackPrivateFilament, isSystem: false, isPublic: false, DevFallbackUserId);
        await AddFilamentAsync(FarmWideFilament, isSystem: false, isPublic: true, FamilyAdminId);
        await AddFilamentAsync(SystemFilament, isSystem: true, isPublic: true, ownerId: null);

        ProcessProfile process = new()
        {
            Id = Guid.NewGuid(),
            Name = OwnerPrivateProcess,
            SlicerType = SlicerType.OrcaSlicer,
            IsSystem = false,
            IsPublic = false,
            CreatedByUserId = OwnerId,
            PrinterModelId = _printerModelId,
            Hash = "hash-" + OwnerPrivateProcess,
            RawJson = RawJson(OwnerPrivateProcess)
        };
        await _processRepo.AddAsync(process);
        _ids[OwnerPrivateProcess] = process.Id;
    }

    private async Task AddMachineAsync(string name, bool isSystem, bool isPublic, Guid? ownerId)
    {
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
        _ids[name] = profile.Id;
    }

    private async Task AddFilamentAsync(string name, bool isSystem, bool isPublic, Guid? ownerId)
    {
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
            CompatiblePrinters = SystemMachine,
            Hash = "hash-" + name,
            RawJson = RawJson(name),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        await _filamentRepo.AddAsync(profile);
        _ids[name] = profile.Id;
    }

    private static string RawJson(string name) => $"{{\"name\":\"{name}\",\"secret_setting\":\"{name}-value\"}}";

    private async Task<List<Guid>> OtherUserRowsWithRawJsonAsync(string profileType, string rawJson) =>
        profileType == "machine"
            ? (await _machineRepo.GetByEngineAsync(SlicerType.OrcaSlicer, includeSystem: true, userId: null, CancellationToken.None))
                .Where(p => p.CreatedByUserId == OtherUserId && p.RawJson == rawJson).Select(p => p.Id).ToList()
            : (await _filamentRepo.GetByEngineAsync(SlicerType.OrcaSlicer, includeSystem: true, userId: null, CancellationToken.None))
                .Where(p => p.CreatedByUserId == OtherUserId && p.RawJson == rawJson).Select(p => p.Id).ToList();

    private async Task<(IReadOnlyList<string> Machines, IReadOnlyList<string> Filaments)> ListExtendedNamesAsync(ClaimsPrincipal user)
    {
        IActionResult result = await CreateController(user).ListExtendedAsync(CancellationToken.None);
        ExtendedProfilesResponseDto dto = Assert.IsType<ExtendedProfilesResponseDto>(Assert.IsType<OkObjectResult>(result).Value);
        return (dto.MachineProfiles.Select(p => p.Name).ToList(), dto.FilamentProfiles.Select(p => p.Name).ToList());
    }

    private Task<IActionResult> ListHierarchyAsync(ClaimsPrincipal user, Guid? machineProfileId = null) =>
        CreateController(user).ListHierarchyAsync(machineProfileId: machineProfileId, ct: CancellationToken.None);

    /// <summary>Collects names from every machine and filament projection in a hierarchy response.</summary>
    private static (IReadOnlyList<string> Machines, IReadOnlyList<string> Filaments) HierarchyNames(IActionResult result)
    {
        HierarchicalProfilesResponseDto dto = Assert.IsType<HierarchicalProfilesResponseDto>(Assert.IsType<OkObjectResult>(result).Value);
        List<HierarchicalPrinterModelProfilesDto> models = dto.ByHierarchy.Values.SelectMany(m => m.Models.Values).ToList();
        Assert.NotEmpty(models);

        List<string> machines = dto.MachineProfiles.Values.SelectMany(v => v)
            .Concat(models.SelectMany(m => m.MachineProfiles))
            .Select(p => p.Name)
            .ToList();
        List<string> filaments = dto.FilamentProfiles.Values.SelectMany(v => v)
            .Concat(models.SelectMany(m => m.FilamentProfiles))
            .Select(p => p.Name)
            .ToList();
        return (machines, filaments);
    }

    private async Task<ImportedProfileNamesDto> ImportedNamesAsync(ClaimsPrincipal user)
    {
        IActionResult result = await CreateController(user).GetImportedProfileNamesAsync(_printerModelId, CancellationToken.None);
        return Assert.IsType<ImportedProfileNamesDto>(Assert.IsType<OkObjectResult>(result).Value);
    }

    private Task<IActionResult> CloneAsync(ClaimsPrincipal user, Guid sourceId, string profileType) =>
        CreateController(user).CloneSingleProfileAsync(
            new CloneSingleProfileRequestDto { SourceProfileId = sourceId, ProfileType = profileType, Name = $"Clone {Guid.NewGuid():N}" },
            CancellationToken.None);

    private async Task<IActionResult> ResolveAsync(ClaimsPrincipal user, ProfileResolutionType type, string name)
    {
        using HttpClient httpClient = new();
        return await CreateController(user).ResolveProfileForModelAsync(
            httpClient,
            _printerModelId,
            new ResolveProfileForModelRequest { ProfileType = type, ProfileName = name },
            CancellationToken.None);
    }

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

        Mock<ICatalogServiceAdapter> catalogAdapter = new(MockBehavior.Loose);
        _ = catalogAdapter
            .Setup(c => c.GetModelByIdAsync(_printerModelId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CatalogModelInfo(_printerModelId, "Test Model", "TestCo"));

        return new ProfilesController(
            NullLogger<ProfilesController>.Instance,
            service,
            catalogAdapter.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = user } }
        };
    }
}
