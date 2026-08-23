using System;
using System.Collections.Generic;
using System.Linq;
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
/// Targeted tests for the applicability filtering added to
/// <see cref="ProfilesService.ListCustomProfilesAsync"/> (issue #1900): custom process/filament
/// profiles must be filtered by an optional <c>machineNames</c> list, matching the
/// <c>CompatiblePrinters</c> based rule already used for built-in profile browsing, while custom
/// machine profiles are always returned unfiltered.
/// </summary>
public class ProfilesServiceListCustomProfilesTests
{
    private static ProfilesService CreateService(
        IProcessProfileRepository processRepo,
        IFilamentProfileRepository filamentRepo,
        IMachineProfileRepository machineRepo)
    {
        Mock<IProfilesRepository> repo = new(MockBehavior.Loose);
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
            processRepo,
            machineRepo,
            filamentRepo,
            unitOfWork.Object,
            catalogService.Object,
            parsingService.Object,
            hubContext.Object,
            slicersService.Object,
            aliasService.Object);
    }

    private static Mock<IProcessProfileRepository> MockProcessRepo(Guid userId, params ProcessProfile[] profiles)
    {
        Mock<IProcessProfileRepository> mock = new(MockBehavior.Loose);
        _ = mock.Setup(r => r.GetByUserAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(profiles.ToList());
        return mock;
    }

    private static Mock<IFilamentProfileRepository> MockFilamentRepo(Guid userId, params FilamentProfile[] profiles)
    {
        Mock<IFilamentProfileRepository> mock = new(MockBehavior.Loose);
        _ = mock.Setup(r => r.GetByEngineAsync(SlicerType.OrcaSlicer, false, userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(profiles.ToList());
        return mock;
    }

    private static Mock<IMachineProfileRepository> MockMachineRepo(Guid userId, params MachineProfile[] profiles)
    {
        Mock<IMachineProfileRepository> mock = new(MockBehavior.Loose);
        _ = mock.Setup(r => r.GetByEngineAsync(SlicerType.OrcaSlicer, false, userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(profiles.ToList());
        return mock;
    }

    [Fact]
    public async Task ListCustomProfilesAsync_NoMachineNames_ReturnsAllCustomProfiles()
    {
        Guid userId = Guid.NewGuid();
        ProcessProfile compatibleProcess = new()
        {
            Id = Guid.NewGuid(),
            Name = "Process A",
            IsSystem = false,
            CreatedByUserId = userId,
            CompatiblePrinters = "Prusa MK4"
        };
        ProcessProfile unrestrictedProcess = new()
        {
            Id = Guid.NewGuid(),
            Name = "Process B",
            IsSystem = false,
            CreatedByUserId = userId,
            CompatiblePrinters = null
        };
        FilamentProfile filament = new()
        {
            Id = Guid.NewGuid(),
            Name = "Filament A",
            IsSystem = false,
            CreatedByUserId = userId,
            CompatiblePrinters = "Bambu X1C"
        };
        MachineProfile machine = new()
        {
            Id = Guid.NewGuid(),
            Name = "Prusa MK4",
            IsSystem = false,
            CreatedByUserId = userId
        };

        ProfilesService svc = CreateService(
            MockProcessRepo(userId, compatibleProcess, unrestrictedProcess).Object,
            MockFilamentRepo(userId, filament).Object,
            MockMachineRepo(userId, machine).Object);

        CustomProfilesListResponseDto result = await svc.ListCustomProfilesAsync(userId, CancellationToken.None);

        Assert.Equal(4, result.TotalCount);
        Assert.Equal(2, result.ProcessProfileCount);
        Assert.Equal(1, result.FilamentProfileCount);
        Assert.Equal(1, result.MachineProfileCount);
    }

    [Fact]
    public async Task ListCustomProfilesAsync_WithMachineNames_FiltersProcessAndFilamentByCompatiblePrinters()
    {
        Guid userId = Guid.NewGuid();
        ProcessProfile compatibleProcess = new()
        {
            Id = Guid.NewGuid(),
            Name = "Compatible Process",
            IsSystem = false,
            CreatedByUserId = userId,
            CompatiblePrinters = "Prusa MK4,Prusa MK3S"
        };
        ProcessProfile incompatibleProcess = new()
        {
            Id = Guid.NewGuid(),
            Name = "Incompatible Process",
            IsSystem = false,
            CreatedByUserId = userId,
            CompatiblePrinters = "Bambu X1C"
        };
        ProcessProfile unrestrictedProcess = new()
        {
            Id = Guid.NewGuid(),
            Name = "Unrestricted Process",
            IsSystem = false,
            CreatedByUserId = userId,
            CompatiblePrinters = null
        };
        FilamentProfile compatibleFilament = new()
        {
            Id = Guid.NewGuid(),
            Name = "Compatible Filament",
            IsSystem = false,
            CreatedByUserId = userId,
            CompatiblePrinters = "Prusa MK4"
        };
        FilamentProfile incompatibleFilament = new()
        {
            Id = Guid.NewGuid(),
            Name = "Incompatible Filament",
            IsSystem = false,
            CreatedByUserId = userId,
            CompatiblePrinters = "Bambu X1C"
        };
        MachineProfile unrelatedMachine = new()
        {
            Id = Guid.NewGuid(),
            Name = "Some Other Printer",
            IsSystem = false,
            CreatedByUserId = userId
        };

        ProfilesService svc = CreateService(
            MockProcessRepo(userId, compatibleProcess, incompatibleProcess, unrestrictedProcess).Object,
            MockFilamentRepo(userId, compatibleFilament, incompatibleFilament).Object,
            MockMachineRepo(userId, unrelatedMachine).Object);

        CustomProfilesListResponseDto result = await svc.ListCustomProfilesAsync(
            userId, CancellationToken.None, machineNames: ["Prusa MK4"]);

        Assert.Equal(3, result.TotalCount);
        Assert.Equal(1, result.ProcessProfileCount);
        Assert.Equal(1, result.FilamentProfileCount);
        Assert.Equal(1, result.MachineProfileCount);

        CustomProfileDto process = Assert.Single(result.Profiles, p => p.ProfileType == "process");
        Assert.Equal(compatibleProcess.Id, process.Id);
        Assert.Contains("Prusa MK4", process.CompatiblePrinters!);

        CustomProfileDto filament = Assert.Single(result.Profiles, p => p.ProfileType == "filament");
        Assert.Equal(compatibleFilament.Id, filament.Id);

        // Machine profiles are never filtered by machineNames — they represent the printer itself.
        CustomProfileDto machine = Assert.Single(result.Profiles, p => p.ProfileType == "machine");
        Assert.Equal(unrelatedMachine.Id, machine.Id);
    }

    [Fact]
    public async Task ListCustomProfilesAsync_WithMachineNames_MatchIsCaseInsensitive()
    {
        Guid userId = Guid.NewGuid();
        ProcessProfile process = new()
        {
            Id = Guid.NewGuid(),
            Name = "Process",
            IsSystem = false,
            CreatedByUserId = userId,
            CompatiblePrinters = "prusa mk4"
        };

        ProfilesService svc = CreateService(
            MockProcessRepo(userId, process).Object,
            MockFilamentRepo(userId).Object,
            MockMachineRepo(userId).Object);

        CustomProfilesListResponseDto result = await svc.ListCustomProfilesAsync(
            userId, CancellationToken.None, machineNames: ["PRUSA MK4"]);

        Assert.Equal(1, result.TotalCount);
        Assert.Equal(process.Id, Assert.Single(result.Profiles).Id);
    }

    [Fact]
    public async Task ListCustomProfilesAsync_WithMachineNames_ExcludesProfilesWithNoMatch()
    {
        Guid userId = Guid.NewGuid();
        ProcessProfile unrestrictedProcess = new()
        {
            Id = Guid.NewGuid(),
            Name = "Unrestricted Process",
            IsSystem = false,
            CreatedByUserId = userId,
            CompatiblePrinters = null
        };
        FilamentProfile incompatibleFilament = new()
        {
            Id = Guid.NewGuid(),
            Name = "Incompatible Filament",
            IsSystem = false,
            CreatedByUserId = userId,
            CompatiblePrinters = "Some Other Printer"
        };

        ProfilesService svc = CreateService(
            MockProcessRepo(userId, unrestrictedProcess).Object,
            MockFilamentRepo(userId, incompatibleFilament).Object,
            MockMachineRepo(userId).Object);

        CustomProfilesListResponseDto result = await svc.ListCustomProfilesAsync(
            userId, CancellationToken.None, machineNames: ["Prusa MK4"]);

        Assert.Equal(0, result.TotalCount);
        Assert.Empty(result.Profiles);
    }
}
