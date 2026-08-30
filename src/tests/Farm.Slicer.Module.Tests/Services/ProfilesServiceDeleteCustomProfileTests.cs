using System;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.PrinterCalibration;
using Farm.Infrastructure.Repositories.UnitOfWork;
using Farm.Infrastructure.Services.Catalog;
using Farm.Infrastructure.Services.Gcode;
using Farm.Slicer.Module.Api.Hubs;
using Farm.Slicer.Module.Api.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Slicer.Module.Tests.Services;

/// <summary>
/// Covers <see cref="ProfilesService.DeleteCustomProfileAsync"/> (issue #2203): the owner-scoped
/// delete path that lets a non-admin caller remove their own custom filament profile without
/// <c>slicer_engines:admin</c>. Mirrors the ownership/system-profile checks already covered for
/// <c>UpdateCustomProfileAsync</c> and <c>PromoteCalibrationDraftProfileAsync</c> (#2180/#2189): a
/// system profile must never be deletable through this path, and an ownership mismatch must throw
/// <see cref="UnauthorizedAccessException"/> (mapped to 403 by the controller) rather than
/// silently succeeding or returning 404.
///
/// Deliberately narrowed to filament profiles only (unlike <c>UpdateCustomProfileAsync</c>, which
/// requires an interactive session and can target any custom profile type): this endpoint is
/// reachable by a Desktop API-key exchange token, so a process or machine profile ID must be
/// treated as not-found rather than deletable, keeping the blast radius of a leaked desktop token
/// limited to what PrintFarmerDesktop's calibration wizard actually needs to clean up.
/// </summary>
public class ProfilesServiceDeleteCustomProfileTests
{
    private static ProfilesService CreateService(
        IFilamentProfileRepository? filamentProfileRepo = null,
        IProcessProfileRepository? processProfileRepo = null,
        IMachineProfileRepository? machineProfileRepo = null)
    {
        Mock<IProfilesRepository> repo = new(MockBehavior.Loose);
        Mock<IProcessProfileRepository> processRepoMock = new(MockBehavior.Loose);
        Mock<IFilamentProfileRepository> filamentRepoMock = new(MockBehavior.Loose);
        Mock<IMachineProfileRepository> machineRepoMock = new(MockBehavior.Loose);
        Mock<IUnitOfWork> unitOfWork = new(MockBehavior.Loose);
        Mock<ICatalogService> catalogService = new(MockBehavior.Loose);
        Mock<IProfileParsingService> parsingService = new(MockBehavior.Loose);
        Mock<IHubContext<SlicerHub>> hubContext = new(MockBehavior.Loose);
        Mock<Farm.Slicer.Module.Services.ISlicersService> slicersService = new(MockBehavior.Loose);
        Mock<IPrinterModelAliasService> aliasService = new(MockBehavior.Loose);
        ILogger<ProfilesService> logger = NullLogger<ProfilesService>.Instance;

        return new ProfilesService(
            repo.Object,
            logger,
            processProfileRepo ?? processRepoMock.Object,
            machineProfileRepo ?? machineRepoMock.Object,
            filamentProfileRepo ?? filamentRepoMock.Object,
            unitOfWork.Object,
            catalogService.Object,
            parsingService.Object,
            hubContext.Object,
            slicersService.Object,
            aliasService.Object);
    }

    [Fact]
    public async Task DeleteCustomProfileAsync_DeletesFilamentProfile_WhenOwnedByCaller()
    {
        Guid userId = Guid.NewGuid();
        Guid profileId = Guid.NewGuid();
        FilamentProfile profile = new() { Id = profileId, Name = "Mine", IsSystem = false, CreatedByUserId = userId };

        Mock<IFilamentProfileRepository> filamentRepo = new(MockBehavior.Strict);
        _ = filamentRepo.Setup(r => r.GetByIdAsync(profileId, It.IsAny<CancellationToken>())).ReturnsAsync(profile);
        _ = filamentRepo.Setup(r => r.DeleteAsync(profile, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        Mock<IProcessProfileRepository> processRepo = new(MockBehavior.Strict);
        Mock<IMachineProfileRepository> machineRepo = new(MockBehavior.Strict);

        ProfilesService svc = CreateService(filamentRepo.Object, processRepo.Object, machineRepo.Object);

        await svc.DeleteCustomProfileAsync(profileId, userId, CancellationToken.None);

        filamentRepo.Verify(r => r.DeleteAsync(profile, It.IsAny<CancellationToken>()), Times.Once);

        // Strict mocks with no other setups: proves this endpoint never even looks at the
        // process/machine tables, let alone deletes from them.
        processRepo.VerifyNoOtherCalls();
        machineRepo.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task DeleteCustomProfileAsync_Throws_WhenProfileNotFoundInFilamentTable()
    {
        Guid userId = Guid.NewGuid();
        Guid profileId = Guid.NewGuid();

        Mock<IFilamentProfileRepository> filamentRepo = new(MockBehavior.Strict);
        _ = filamentRepo.Setup(r => r.GetByIdAsync(profileId, It.IsAny<CancellationToken>())).ReturnsAsync((FilamentProfile?)null);

        ProfilesService svc = CreateService(filamentProfileRepo: filamentRepo.Object);

        _ = await Assert.ThrowsAsync<KeyNotFoundException>(() => svc.DeleteCustomProfileAsync(profileId, userId, CancellationToken.None));
    }

    [Fact]
    public async Task DeleteCustomProfileAsync_TreatsProcessProfileIdAsNotFound_NeverConsultsProcessOrMachineTables()
    {
        // Regression guard for the deliberate filament-only narrowing: an ID that exists only as a
        // process/machine profile must not be deletable (or even looked up) through this endpoint,
        // since it is reachable by a desktop exchange token and must not be able to touch
        // process/machine profiles. Strict mocks on the process/machine repos with zero setups
        // prove the service never calls them at all.
        Guid userId = Guid.NewGuid();
        Guid profileId = Guid.NewGuid();

        Mock<IFilamentProfileRepository> filamentRepo = new(MockBehavior.Strict);
        _ = filamentRepo.Setup(r => r.GetByIdAsync(profileId, It.IsAny<CancellationToken>())).ReturnsAsync((FilamentProfile?)null);

        Mock<IProcessProfileRepository> processRepo = new(MockBehavior.Strict);
        Mock<IMachineProfileRepository> machineRepo = new(MockBehavior.Strict);

        ProfilesService svc = CreateService(filamentRepo.Object, processRepo.Object, machineRepo.Object);

        _ = await Assert.ThrowsAsync<KeyNotFoundException>(() => svc.DeleteCustomProfileAsync(profileId, userId, CancellationToken.None));

        processRepo.VerifyNoOtherCalls();
        machineRepo.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task DeleteCustomProfileAsync_Throws_WhenProfileIsSystem()
    {
        // Structurally excludes system profiles from being targetable at all, matching
        // UpdateFilamentProfileAsync's IsSystem guard.
        Guid userId = Guid.NewGuid();
        Guid profileId = Guid.NewGuid();
        FilamentProfile profile = new() { Id = profileId, Name = "System Profile", IsSystem = true, CreatedByUserId = null };

        Mock<IFilamentProfileRepository> filamentRepo = new(MockBehavior.Strict);
        _ = filamentRepo.Setup(r => r.GetByIdAsync(profileId, It.IsAny<CancellationToken>())).ReturnsAsync(profile);

        ProfilesService svc = CreateService(filamentProfileRepo: filamentRepo.Object);

        _ = await Assert.ThrowsAsync<InvalidOperationException>(() => svc.DeleteCustomProfileAsync(profileId, userId, CancellationToken.None));

        // Strict mock: DeleteAsync was never Setup, so any call to it would throw before this
        // line, proving a system profile is never actually removed.
        filamentRepo.Verify(r => r.GetByIdAsync(profileId, It.IsAny<CancellationToken>()), Times.Once);
        filamentRepo.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task DeleteCustomProfileAsync_Throws_WhenOwnedByDifferentUser()
    {
        Guid callerUserId = Guid.NewGuid();
        Guid otherUserId = Guid.NewGuid();
        Guid profileId = Guid.NewGuid();
        FilamentProfile profile = new() { Id = profileId, Name = "Someone Else's", IsSystem = false, CreatedByUserId = otherUserId };

        Mock<IFilamentProfileRepository> filamentRepo = new(MockBehavior.Strict);
        _ = filamentRepo.Setup(r => r.GetByIdAsync(profileId, It.IsAny<CancellationToken>())).ReturnsAsync(profile);

        ProfilesService svc = CreateService(filamentProfileRepo: filamentRepo.Object);

        _ = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.DeleteCustomProfileAsync(profileId, callerUserId, CancellationToken.None));

        // Strict mock: DeleteAsync was never Setup, so any call to it would throw before this
        // line, proving another user's profile is never actually removed.
        filamentRepo.Verify(r => r.GetByIdAsync(profileId, It.IsAny<CancellationToken>()), Times.Once);
        filamentRepo.VerifyNoOtherCalls();
    }
}
