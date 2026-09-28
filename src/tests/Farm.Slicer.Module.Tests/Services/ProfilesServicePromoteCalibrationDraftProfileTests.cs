using System;
using System.Collections.Generic;
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
/// behavior (issue #2180, gap 1, round-4 review fix - Hicks Blocking #2) and its per-owner
/// scoping (issue #3189). The calibration-side promotion claim is TTL-reclaimable, so this
/// endpoint's own service method may legitimately be invoked more than once for the same draft
/// profile; a replayed call must return the SAME promoted filament profile rather than minting a
/// second, user-visible duplicate in the owner's custom filament profile list. The draft profile
/// ID is caller-supplied and not an authorization boundary, so the lookup only ever reads the
/// caller's own rows. Two-user behavior against a real database is covered by
/// <c>ProfilesControllerPromoteCalibrationDraftTwoUserTests</c>.
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

    /// <summary>
    /// Sets up the per-owner name lookup (#3192) for <paramref name="userId"/> only: the strict mock
    /// has no setup for any other user, so consulting another user's names would throw.
    /// </summary>
    private static void SetupOwnerNames(Mock<IFilamentProfileRepository> filamentRepo, Guid userId, params string[] takenNames) =>
        filamentRepo
            .Setup(r => r.OwnerHasNameAsync(userId, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<SlicerType>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, string name, string _, SlicerType _, Guid? _, CancellationToken _) => Array.IndexOf(takenNames, name) >= 0);

    [Fact]
    public async Task PromoteCalibrationDraftProfileAsync_CreatesProfile_OnFirstCall()
    {
        Guid userId = Guid.NewGuid();
        Guid draftProfileId = Guid.NewGuid();
        FilamentProfile? added = null;

        Mock<IFilamentProfileRepository> filamentRepo = new(MockBehavior.Strict);
        _ = filamentRepo
            .Setup(r => r.GetByPromotedFromCalibrationDraftProfileIdAsync(userId, draftProfileId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((FilamentProfile?)null);
        _ = filamentRepo
            .Setup(r => r.AddAsync(It.IsAny<FilamentProfile>(), It.IsAny<CancellationToken>()))
            .Callback<FilamentProfile, CancellationToken>((p, _) => added = p)
            .Returns(Task.CompletedTask);
        SetupOwnerNames(filamentRepo, userId);

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
            .Setup(r => r.GetByPromotedFromCalibrationDraftProfileIdAsync(userId, draftProfileId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);

        ProfilesService svc = CreateService(filamentRepo.Object);

        (CustomProfileDto profile, bool wasCreated) = await svc.PromoteCalibrationDraftProfileAsync(
            MakeRequest(), userId, draftProfileId, CancellationToken.None);

        Assert.False(wasCreated);
        Assert.Equal(existing.Id, profile.Id);

        // Strict mock: AddAsync was never Setup, so any call to it would throw with a
        // MockException before this line, proving the replay path never attempted to insert a
        // second row.
        filamentRepo.Verify(r => r.GetByPromotedFromCalibrationDraftProfileIdAsync(userId, draftProfileId, It.IsAny<CancellationToken>()), Times.Once);
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
            .Setup(r => r.GetByPromotedFromCalibrationDraftProfileIdAsync(userId, draftProfileId, It.IsAny<CancellationToken>()))
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
        SetupOwnerNames(filamentRepo, userId);

        ProfilesService svc = CreateService(filamentRepo.Object);

        (CustomProfileDto profile, bool wasCreated) = await svc.PromoteCalibrationDraftProfileAsync(
            MakeRequest(), userId, draftProfileId, CancellationToken.None);

        Assert.False(wasCreated);
        Assert.Equal(winner.Id, profile.Id);
        filamentRepo.Verify(r => r.GetByPromotedFromCalibrationDraftProfileIdAsync(userId, draftProfileId, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task PromoteCalibrationDraftProfileAsync_LooksUpOnlyTheCallersOwnPromotion_AndCreatesCallersOwnProfile()
    {
        // Issue #3189: the idempotency lookup is scoped to the caller, so a draft id another user
        // already promoted is never read. The caller gets their own new profile, exactly as for an
        // unknown draft id. The strict mock has no setup for any other user id, so a lookup keyed
        // on anything but the caller would throw.
        Guid callerUserId = Guid.NewGuid();
        Guid draftProfileId = Guid.NewGuid();
        FilamentProfile? added = null;

        Mock<IFilamentProfileRepository> filamentRepo = new(MockBehavior.Strict);
        _ = filamentRepo
            .Setup(r => r.GetByPromotedFromCalibrationDraftProfileIdAsync(callerUserId, draftProfileId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((FilamentProfile?)null);
        _ = filamentRepo
            .Setup(r => r.AddAsync(It.IsAny<FilamentProfile>(), It.IsAny<CancellationToken>()))
            .Callback<FilamentProfile, CancellationToken>((p, _) => added = p)
            .Returns(Task.CompletedTask);
        SetupOwnerNames(filamentRepo, callerUserId);

        ProfilesService svc = CreateService(filamentRepo.Object);

        (CustomProfileDto profile, bool wasCreated) = await svc.PromoteCalibrationDraftProfileAsync(
            MakeRequest(), callerUserId, draftProfileId, CancellationToken.None);

        Assert.True(wasCreated);
        Assert.Equal(callerUserId, added!.CreatedByUserId);
        Assert.Equal(draftProfileId, added.PromotedFromCalibrationDraftProfileId);
        Assert.Equal(added.Id, profile.Id);
        filamentRepo.Verify(r => r.GetByPromotedFromCalibrationDraftProfileIdAsync(callerUserId, draftProfileId, It.IsAny<CancellationToken>()), Times.Once);
        filamentRepo.Verify(r => r.AddAsync(It.IsAny<FilamentProfile>(), It.IsAny<CancellationToken>()), Times.Once);
        filamentRepo.Verify(r => r.OwnerHasNameAsync(callerUserId, "Draft PLA", It.IsAny<string>(), It.IsAny<SlicerType>(), null, It.IsAny<CancellationToken>()), Times.Once);
        filamentRepo.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task PromoteCalibrationDraftProfileAsync_Rethrows_WhenInsertFailsAndCallerHasNoWinner()
    {
        // A DbUpdateException that is neither this caller's own replay race nor a collision with
        // one of the caller's own names (for example another unique index) is surfaced, never
        // resolved by reading some other user's row.
        Guid callerUserId = Guid.NewGuid();
        Guid draftProfileId = Guid.NewGuid();

        Mock<IFilamentProfileRepository> filamentRepo = new(MockBehavior.Strict);
        _ = filamentRepo
            .Setup(r => r.GetByPromotedFromCalibrationDraftProfileIdAsync(callerUserId, draftProfileId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((FilamentProfile?)null);
        _ = filamentRepo
            .Setup(r => r.AddAsync(It.IsAny<FilamentProfile>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DbUpdateException("unique constraint violation"));
        SetupOwnerNames(filamentRepo, callerUserId);

        ProfilesService svc = CreateService(filamentRepo.Object);

        _ = await Assert.ThrowsAsync<DbUpdateException>(() => svc.PromoteCalibrationDraftProfileAsync(
            MakeRequest(), callerUserId, draftProfileId, CancellationToken.None));

        filamentRepo.Verify(r => r.GetByPromotedFromCalibrationDraftProfileIdAsync(callerUserId, draftProfileId, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task PromoteCalibrationDraftProfileAsync_SuffixesName_WhenCallerAlreadyOwnsIt()
    {
        // Issue #3192: promotion is unattended, so a name the caller already uses is suffixed
        // rather than rejected. Only the caller's own names are consulted.
        Guid userId = Guid.NewGuid();
        Guid draftProfileId = Guid.NewGuid();
        FilamentProfile? added = null;

        Mock<IFilamentProfileRepository> filamentRepo = new(MockBehavior.Strict);
        _ = filamentRepo
            .Setup(r => r.GetByPromotedFromCalibrationDraftProfileIdAsync(userId, draftProfileId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((FilamentProfile?)null);
        _ = filamentRepo
            .Setup(r => r.AddAsync(It.IsAny<FilamentProfile>(), It.IsAny<CancellationToken>()))
            .Callback<FilamentProfile, CancellationToken>((p, _) => added = p)
            .Returns(Task.CompletedTask);
        SetupOwnerNames(filamentRepo, userId, "Draft PLA", "Draft PLA (2)");

        ProfilesService svc = CreateService(filamentRepo.Object);

        (CustomProfileDto profile, bool wasCreated) = await svc.PromoteCalibrationDraftProfileAsync(
            MakeRequest(), userId, draftProfileId, CancellationToken.None);

        Assert.True(wasCreated);
        Assert.Equal("Draft PLA (3)", added!.Name);
        Assert.Equal("Draft PLA (3)", profile.Name);
    }

    [Fact]
    public async Task PromoteCalibrationDraftProfileAsync_TruncatesBaseName_SoSuffixedNameFitsColumn()
    {
        Guid userId = Guid.NewGuid();
        Guid draftProfileId = Guid.NewGuid();
        string longName = new('x', 255);
        FilamentProfile? added = null;

        Mock<IFilamentProfileRepository> filamentRepo = new(MockBehavior.Strict);
        _ = filamentRepo
            .Setup(r => r.GetByPromotedFromCalibrationDraftProfileIdAsync(userId, draftProfileId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((FilamentProfile?)null);
        _ = filamentRepo
            .Setup(r => r.AddAsync(It.IsAny<FilamentProfile>(), It.IsAny<CancellationToken>()))
            .Callback<FilamentProfile, CancellationToken>((p, _) => added = p)
            .Returns(Task.CompletedTask);
        SetupOwnerNames(filamentRepo, userId, longName);

        ProfilesService svc = CreateService(filamentRepo.Object);

        _ = await svc.PromoteCalibrationDraftProfileAsync(MakeRequest(longName), userId, draftProfileId, CancellationToken.None);

        Assert.Equal(255, added!.Name.Length);
        Assert.EndsWith(" (2)", added.Name, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PromoteCalibrationDraftProfileAsync_TruncatesBaseName_WithoutSplittingSurrogatePair()
    {
        Guid userId = Guid.NewGuid();
        Guid draftProfileId = Guid.NewGuid();
        // The 251-char base budget (255 minus " (2)") would end on the high surrogate of the emoji.
        string longName = new string('x', 250) + "\U0001F600" + new string('y', 3);
        FilamentProfile? added = null;

        Mock<IFilamentProfileRepository> filamentRepo = new(MockBehavior.Strict);
        _ = filamentRepo
            .Setup(r => r.GetByPromotedFromCalibrationDraftProfileIdAsync(userId, draftProfileId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((FilamentProfile?)null);
        _ = filamentRepo
            .Setup(r => r.AddAsync(It.IsAny<FilamentProfile>(), It.IsAny<CancellationToken>()))
            .Callback<FilamentProfile, CancellationToken>((p, _) => added = p)
            .Returns(Task.CompletedTask);
        SetupOwnerNames(filamentRepo, userId, longName);

        ProfilesService svc = CreateService(filamentRepo.Object);

        _ = await svc.PromoteCalibrationDraftProfileAsync(MakeRequest(longName), userId, draftProfileId, CancellationToken.None);

        Assert.Equal(new string('x', 250) + " (2)", added!.Name);
        Assert.DoesNotContain(added.Name, char.IsSurrogate);
    }

    [Fact]
    public async Task PromoteCalibrationDraftProfileAsync_PicksNextFreeName_WhenConcurrentInsertTookTheChosenName()
    {
        Guid userId = Guid.NewGuid();
        Guid draftProfileId = Guid.NewGuid();
        List<string> taken = [];
        List<string> attempted = [];

        Mock<IFilamentProfileRepository> filamentRepo = new(MockBehavior.Strict);
        _ = filamentRepo
            .Setup(r => r.GetByPromotedFromCalibrationDraftProfileIdAsync(userId, draftProfileId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((FilamentProfile?)null);
        _ = filamentRepo
            .Setup(r => r.AddAsync(It.IsAny<FilamentProfile>(), It.IsAny<CancellationToken>()))
            .Returns((FilamentProfile p, CancellationToken _) =>
            {
                attempted.Add(p.Name);
                if (attempted.Count == 1)
                {
                    // A concurrent request from the same caller inserted this name first.
                    taken.Add(p.Name);
                    throw new DbUpdateException("unique constraint violation");
                }

                return Task.CompletedTask;
            });
        _ = filamentRepo
            .Setup(r => r.OwnerHasNameAsync(userId, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<SlicerType>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, string name, string _, SlicerType _, Guid? _, CancellationToken _) => taken.Contains(name));

        ProfilesService svc = CreateService(filamentRepo.Object);

        (CustomProfileDto profile, bool wasCreated) = await svc.PromoteCalibrationDraftProfileAsync(
            MakeRequest(), userId, draftProfileId, CancellationToken.None);

        Assert.True(wasCreated);
        Assert.Equal(["Draft PLA", "Draft PLA (2)"], attempted);
        Assert.Equal("Draft PLA (2)", profile.Name);
    }

    [Fact]
    public async Task PromoteCalibrationDraftProfileAsync_ThrowsNameConflict_WhenEveryAttemptLosesTheNameRace()
    {
        Guid userId = Guid.NewGuid();
        Guid draftProfileId = Guid.NewGuid();
        List<string> taken = [];

        Mock<IFilamentProfileRepository> filamentRepo = new(MockBehavior.Strict);
        _ = filamentRepo
            .Setup(r => r.GetByPromotedFromCalibrationDraftProfileIdAsync(userId, draftProfileId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((FilamentProfile?)null);
        _ = filamentRepo
            .Setup(r => r.AddAsync(It.IsAny<FilamentProfile>(), It.IsAny<CancellationToken>()))
            .Returns((FilamentProfile p, CancellationToken _) =>
            {
                taken.Add(p.Name);
                throw new DbUpdateException("unique constraint violation");
            });
        _ = filamentRepo
            .Setup(r => r.OwnerHasNameAsync(userId, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<SlicerType>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, string name, string _, SlicerType _, Guid? _, CancellationToken _) => taken.Contains(name));

        ProfilesService svc = CreateService(filamentRepo.Object);

        ProfileNameConflictException ex = await Assert.ThrowsAsync<ProfileNameConflictException>(() =>
            svc.PromoteCalibrationDraftProfileAsync(MakeRequest(), userId, draftProfileId, CancellationToken.None));

        Assert.IsType<DbUpdateException>(ex.InnerException);
        Assert.Equal(3, taken.Count);
    }

    [Fact]
    public async Task PromoteCalibrationDraftProfileAsync_Throws_WhenSourceDraftProfileIdIsEmpty()
    {
        Mock<IFilamentProfileRepository> filamentRepo = new(MockBehavior.Strict);
        ProfilesService svc = CreateService(filamentRepo.Object);

        _ = await Assert.ThrowsAsync<ArgumentException>(() => svc.PromoteCalibrationDraftProfileAsync(
            MakeRequest(), Guid.NewGuid(), Guid.Empty, CancellationToken.None));

        filamentRepo.VerifyNoOtherCalls();
    }
}
