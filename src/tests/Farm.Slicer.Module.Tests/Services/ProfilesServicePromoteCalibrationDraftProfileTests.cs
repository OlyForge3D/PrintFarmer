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
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Slicer.Module.Tests.Services;

/// <summary>
/// Covers <see cref="ProfilesService.PromoteCalibrationDraftProfileAsync"/>'s idempotent-replay
/// behavior (issue #2180, gap 1, round-4 review fix - Hicks Blocking #2). The calibration-side
/// promotion claim is TTL-reclaimable, so this endpoint's own service method may legitimately be
/// invoked more than once for the same draft profile; a replayed call must return the SAME
/// promoted filament profile rather than minting a second, user-visible duplicate in the owner's
/// custom filament profile list.
/// </summary>
public class ProfilesServicePromoteCalibrationDraftProfileTests
{
    private static ProfilesService CreateService(IFilamentProfileRepository filamentRepo)
    {
        Mock<IProfilesRepository> repo = new(MockBehavior.Loose);
        Mock<IProcessProfileRepository> processProfileRepo = new(MockBehavior.Loose);
        Mock<IMachineProfileRepository> machineProfileRepo = new(MockBehavior.Loose);
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
            processProfileRepo.Object,
            machineProfileRepo.Object,
            filamentRepo,
            unitOfWork.Object,
            catalogService.Object,
            parsingService.Object,
            hubContext.Object,
            slicersService.Object,
            aliasService.Object);
    }

    private static UploadProfileRequestDto MakeRequest(string name = "Draft PLA") => new()
    {
        Name = name,
        RawJson = "{\"name\":\"Draft PLA\",\"filament_type\":[\"PLA\"]}",
        ProfileType = "filament",
    };

    [Fact]
    public async Task PromoteCalibrationDraftProfileAsync_CreatesProfile_OnFirstCall()
    {
        Guid userId = Guid.NewGuid();
        Guid draftProfileId = Guid.NewGuid();
        FilamentProfile? added = null;

        Mock<IFilamentProfileRepository> filamentRepo = new(MockBehavior.Strict);
        _ = filamentRepo
            .Setup(r => r.GetByPromotedFromCalibrationDraftProfileIdAsync(draftProfileId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((FilamentProfile?)null);
        _ = filamentRepo
            .Setup(r => r.AddAsync(It.IsAny<FilamentProfile>(), It.IsAny<CancellationToken>()))
            .Callback<FilamentProfile, CancellationToken>((p, _) => added = p)
            .Returns(Task.CompletedTask);

        ProfilesService svc = CreateService(filamentRepo.Object);

        (CustomProfileDto profile, bool wasCreated) = await svc.PromoteCalibrationDraftProfileAsync(
            MakeRequest(), userId, draftProfileId, CancellationToken.None);

        Assert.True(wasCreated);
        Assert.NotNull(added);
        Assert.Equal(draftProfileId, added!.PromotedFromCalibrationDraftProfileId);
        Assert.Equal(userId, added.CreatedByUserId);
        Assert.Equal(added.Id, profile.Id);
        Assert.Equal("filament", profile.ProfileType);
        filamentRepo.Verify(r => r.AddAsync(It.IsAny<FilamentProfile>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PromoteCalibrationDraftProfileAsync_ReturnsExistingProfile_OnReplay_WithoutInsertingAgain()
    {
        Guid userId = Guid.NewGuid();
        Guid draftProfileId = Guid.NewGuid();
        FilamentProfile existing = new()
        {
            Id = Guid.NewGuid(),
            Name = "Draft PLA",
            RawJson = "{\"name\":\"Draft PLA\"}",
            CreatedByUserId = userId,
            PromotedFromCalibrationDraftProfileId = draftProfileId,
            CreatedAt = DateTime.UtcNow.AddMinutes(-20),
            UpdatedAt = DateTime.UtcNow.AddMinutes(-20),
        };

        Mock<IFilamentProfileRepository> filamentRepo = new(MockBehavior.Strict);
        _ = filamentRepo
            .Setup(r => r.GetByPromotedFromCalibrationDraftProfileIdAsync(draftProfileId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);

        ProfilesService svc = CreateService(filamentRepo.Object);

        (CustomProfileDto profile, bool wasCreated) = await svc.PromoteCalibrationDraftProfileAsync(
            MakeRequest(), userId, draftProfileId, CancellationToken.None);

        Assert.False(wasCreated);
        Assert.Equal(existing.Id, profile.Id);

        // Strict mock: AddAsync was never Setup, so any call to it would throw with a
        // MockException before this line, proving the replay path never attempted to insert a
        // second row.
        filamentRepo.Verify(r => r.GetByPromotedFromCalibrationDraftProfileIdAsync(draftProfileId, It.IsAny<CancellationToken>()), Times.Once);
        filamentRepo.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task PromoteCalibrationDraftProfileAsync_ReturnsWinnersProfile_WhenConcurrentInsertLosesUniqueIndexRace()
    {
        Guid userId = Guid.NewGuid();
        Guid draftProfileId = Guid.NewGuid();
        FilamentProfile winner = new()
        {
            Id = Guid.NewGuid(),
            Name = "Draft PLA",
            RawJson = "{\"name\":\"Draft PLA\"}",
            CreatedByUserId = userId,
            PromotedFromCalibrationDraftProfileId = draftProfileId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        Mock<IFilamentProfileRepository> filamentRepo = new(MockBehavior.Strict);
        int lookupCalls = 0;
        _ = filamentRepo
            .Setup(r => r.GetByPromotedFromCalibrationDraftProfileIdAsync(draftProfileId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                lookupCalls++;
                // First lookup (the fast-path check) finds nothing; the second lookup (after the
                // insert lost the unique-index race to a concurrent/replayed caller) finds the
                // winner.
                return lookupCalls == 1 ? null : winner;
            });
        _ = filamentRepo
            .Setup(r => r.AddAsync(It.IsAny<FilamentProfile>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DbUpdateException("unique constraint violation"));

        ProfilesService svc = CreateService(filamentRepo.Object);

        (CustomProfileDto profile, bool wasCreated) = await svc.PromoteCalibrationDraftProfileAsync(
            MakeRequest(), userId, draftProfileId, CancellationToken.None);

        Assert.False(wasCreated);
        Assert.Equal(winner.Id, profile.Id);
        filamentRepo.Verify(r => r.GetByPromotedFromCalibrationDraftProfileIdAsync(draftProfileId, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }
}
