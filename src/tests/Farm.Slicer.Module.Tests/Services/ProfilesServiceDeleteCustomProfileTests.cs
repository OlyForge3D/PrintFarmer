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
/// delete path that lets a non-admin caller remove their own custom process/filament/machine
/// profile without <c>slicer_engines:admin</c>. Mirrors the ownership/system-profile checks
/// already covered for <c>UpdateCustomProfileAsync</c> and
/// <c>PromoteCalibrationDraftProfileAsync</c> (#2180/#2189): a system profile must never be
/// deletable through this path, and an ownership mismatch must throw
/// <see cref="UnauthorizedAccessException"/> (mapped to 403 by the controller) rather than
/// silently succeeding or returning 404.
/// </summary>
public class ProfilesServiceDeleteCustomProfileTests
{
    private static ProfilesService CreateService(
        IProcessProfileRepository? processProfileRepo = null,
        IFilamentProfileRepository? filamentProfileRepo = null,
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
    public async Task DeleteCustomProfileAsync_DeletesProcessProfile_WhenOwnedByCaller()
    {
        Guid userId = Guid.NewGuid();
        Guid profileId = Guid.NewGuid();
        ProcessProfile profile = new() { Id = profileId, Name = "Mine", IsSystem = false, CreatedByUserId = userId };

        Mock<IProcessProfileRepository> processRepo = new(MockBehavior.Strict);
        _ = processRepo.Setup(r => r.GetByIdAsync(profileId, It.IsAny<CancellationToken>())).ReturnsAsync(profile);
        _ = processRepo.Setup(r => r.DeleteAsync(profile, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        Mock<IFilamentProfileRepository> filamentRepo = new(MockBehavior.Strict);
        Mock<IMachineProfileRepository> machineRepo = new(MockBehavior.Strict);

        ProfilesService svc = CreateService(processRepo.Object, filamentRepo.Object, machineRepo.Object);

        await svc.DeleteCustomProfileAsync(profileId, userId, CancellationToken.None);

        processRepo.Verify(r => r.DeleteAsync(profile, It.IsAny<CancellationToken>()), Times.Once);
        filamentRepo.VerifyNoOtherCalls();
        machineRepo.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task DeleteCustomProfileAsync_DeletesFilamentProfile_WhenNotFoundInProcessTable()
    {
        Guid userId = Guid.NewGuid();
        Guid profileId = Guid.NewGuid();
        FilamentProfile profile = new() { Id = profileId, Name = "Mine", IsSystem = false, CreatedByUserId = userId };

        Mock<IProcessProfileRepository> processRepo = new(MockBehavior.Strict);
        _ = processRepo.Setup(r => r.GetByIdAsync(profileId, It.IsAny<CancellationToken>())).ReturnsAsync((ProcessProfile?)null);

        Mock<IFilamentProfileRepository> filamentRepo = new(MockBehavior.Strict);
        _ = filamentRepo.Setup(r => r.GetByIdAsync(profileId, It.IsAny<CancellationToken>())).ReturnsAsync(profile);
        _ = filamentRepo.Setup(r => r.DeleteAsync(profile, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        Mock<IMachineProfileRepository> machineRepo = new(MockBehavior.Strict);

        ProfilesService svc = CreateService(processRepo.Object, filamentRepo.Object, machineRepo.Object);

        await svc.DeleteCustomProfileAsync(profileId, userId, CancellationToken.None);

        filamentRepo.Verify(r => r.DeleteAsync(profile, It.IsAny<CancellationToken>()), Times.Once);
        machineRepo.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task DeleteCustomProfileAsync_DeletesMachineProfile_WhenNotFoundInProcessOrFilamentTables()
    {
        Guid userId = Guid.NewGuid();
        Guid profileId = Guid.NewGuid();
        MachineProfile profile = new() { Id = profileId, Name = "Mine", IsSystem = false, CreatedByUserId = userId };

        Mock<IProcessProfileRepository> processRepo = new(MockBehavior.Strict);
        _ = processRepo.Setup(r => r.GetByIdAsync(profileId, It.IsAny<CancellationToken>())).ReturnsAsync((ProcessProfile?)null);

        Mock<IFilamentProfileRepository> filamentRepo = new(MockBehavior.Strict);
        _ = filamentRepo.Setup(r => r.GetByIdAsync(profileId, It.IsAny<CancellationToken>())).ReturnsAsync((FilamentProfile?)null);

        Mock<IMachineProfileRepository> machineRepo = new(MockBehavior.Strict);
        _ = machineRepo.Setup(r => r.GetByIdAsync(profileId, It.IsAny<CancellationToken>())).ReturnsAsync(profile);
        _ = machineRepo.Setup(r => r.DeleteAsync(profile, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        ProfilesService svc = CreateService(processRepo.Object, filamentRepo.Object, machineRepo.Object);

        await svc.DeleteCustomProfileAsync(profileId, userId, CancellationToken.None);

        machineRepo.Verify(r => r.DeleteAsync(profile, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeleteCustomProfileAsync_Throws_WhenProfileNotFoundInAnyTable()
    {
        Guid userId = Guid.NewGuid();
        Guid profileId = Guid.NewGuid();

        Mock<IProcessProfileRepository> processRepo = new(MockBehavior.Strict);
        _ = processRepo.Setup(r => r.GetByIdAsync(profileId, It.IsAny<CancellationToken>())).ReturnsAsync((ProcessProfile?)null);

        Mock<IFilamentProfileRepository> filamentRepo = new(MockBehavior.Strict);
        _ = filamentRepo.Setup(r => r.GetByIdAsync(profileId, It.IsAny<CancellationToken>())).ReturnsAsync((FilamentProfile?)null);

        Mock<IMachineProfileRepository> machineRepo = new(MockBehavior.Strict);
        _ = machineRepo.Setup(r => r.GetByIdAsync(profileId, It.IsAny<CancellationToken>())).ReturnsAsync((MachineProfile?)null);

        ProfilesService svc = CreateService(processRepo.Object, filamentRepo.Object, machineRepo.Object);

        _ = await Assert.ThrowsAsync<KeyNotFoundException>(() => svc.DeleteCustomProfileAsync(profileId, userId, CancellationToken.None));
    }

    [Fact]
    public async Task DeleteCustomProfileAsync_Throws_WhenProfileIsSystem()
    {
        // Structurally excludes system profiles from being targetable at all, matching
        // UpdateProcessProfileAsync's IsSystem guard.
        Guid userId = Guid.NewGuid();
        Guid profileId = Guid.NewGuid();
        ProcessProfile profile = new() { Id = profileId, Name = "System Profile", IsSystem = true, CreatedByUserId = null };

        Mock<IProcessProfileRepository> processRepo = new(MockBehavior.Strict);
        _ = processRepo.Setup(r => r.GetByIdAsync(profileId, It.IsAny<CancellationToken>())).ReturnsAsync(profile);

        ProfilesService svc = CreateService(processProfileRepo: processRepo.Object);

        _ = await Assert.ThrowsAsync<InvalidOperationException>(() => svc.DeleteCustomProfileAsync(profileId, userId, CancellationToken.None));

        // Strict mock: DeleteAsync was never Setup, so any call to it would throw before this
        // line, proving a system profile is never actually removed.
        processRepo.Verify(r => r.GetByIdAsync(profileId, It.IsAny<CancellationToken>()), Times.Once);
        processRepo.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task DeleteCustomProfileAsync_Throws_WhenOwnedByDifferentUser()
    {
        Guid callerUserId = Guid.NewGuid();
        Guid otherUserId = Guid.NewGuid();
        Guid profileId = Guid.NewGuid();
        FilamentProfile profile = new() { Id = profileId, Name = "Someone Else's", IsSystem = false, CreatedByUserId = otherUserId };

        Mock<IProcessProfileRepository> processRepo = new(MockBehavior.Strict);
        _ = processRepo.Setup(r => r.GetByIdAsync(profileId, It.IsAny<CancellationToken>())).ReturnsAsync((ProcessProfile?)null);

        Mock<IFilamentProfileRepository> filamentRepo = new(MockBehavior.Strict);
        _ = filamentRepo.Setup(r => r.GetByIdAsync(profileId, It.IsAny<CancellationToken>())).ReturnsAsync(profile);

        ProfilesService svc = CreateService(processRepo.Object, filamentRepo.Object);

        _ = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.DeleteCustomProfileAsync(profileId, callerUserId, CancellationToken.None));

        filamentRepo.Verify(r => r.GetByIdAsync(profileId, It.IsAny<CancellationToken>()), Times.Once);
        filamentRepo.VerifyNoOtherCalls();
    }
}
