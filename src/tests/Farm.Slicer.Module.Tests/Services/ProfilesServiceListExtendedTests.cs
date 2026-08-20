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
using Farm.Slicer.Module.Data.Repositories;
using Farm.Slicer.Module.Domain;
using Farm.Slicer.Module.Dtos;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Slicer.Module.Tests.Services;

/// <summary>
/// Regression tests for #1779: <see cref="ProfilesService.ListExtendedAsync"/> must surface
/// <c>NozzleDiameter</c> and <c>PrinterVariant</c> for each machine profile, recovered from the
/// profile's already-stored settings JSON (no schema migration required).
/// </summary>
public class ProfilesServiceListExtendedTests
{
    [Fact]
    public async Task ListExtendedAsync_SettingsJsonContainsNozzleDiameterAndPrinterVariant_PopulatesDto()
    {
        // Arrange
        MachineProfile profile = new()
        {
            Id = Guid.NewGuid(),
            Name = "Prusa CORE One HF",
            Manufacturer = "Prusa Research",
            SlicerType = SlicerType.OrcaSlicer,
            SettingsJson = "{\"NozzleDiameter\": 0.6, \"PrinterVariant\": \"HF\"}",
            Hash = "hash-1"
        };

        ProfilesService svc = CreateService(new List<MachineProfile> { profile });

        // Act
        ExtendedProfilesResponseDto result = await svc.ListExtendedAsync(CancellationToken.None);

        // Assert
        MachineProfileListItemDto dto = Assert.Single(result.MachineProfiles);
        Assert.Equal(0.6, dto.NozzleDiameter);
        Assert.Equal("HF", dto.PrinterVariant);
    }

    [Fact]
    public async Task ListExtendedAsync_SettingsJsonMissing_FallsBackToRawJson()
    {
        // Arrange: SettingsJson absent, but RawJson still carries the original values.
        MachineProfile profile = new()
        {
            Id = Guid.NewGuid(),
            Name = "Prusa CORE One",
            Manufacturer = "Prusa Research",
            SlicerType = SlicerType.OrcaSlicer,
            SettingsJson = null,
            RawJson = "{\"NozzleDiameter\": 0.4, \"PrinterVariant\": null}",
            Hash = "hash-2"
        };

        ProfilesService svc = CreateService(new List<MachineProfile> { profile });

        // Act
        ExtendedProfilesResponseDto result = await svc.ListExtendedAsync(CancellationToken.None);

        // Assert
        MachineProfileListItemDto dto = Assert.Single(result.MachineProfiles);
        Assert.Equal(0.4, dto.NozzleDiameter);
        Assert.Null(dto.PrinterVariant);
    }

    [Fact]
    public async Task ListExtendedAsync_NoNozzleDiameterOrPrinterVariantInJson_YieldsNulls()
    {
        // Arrange
        MachineProfile profile = new()
        {
            Id = Guid.NewGuid(),
            Name = "Generic Machine",
            Manufacturer = "Generic Mfg",
            SlicerType = SlicerType.OrcaSlicer,
            SettingsJson = "{\"SomeOtherField\": 123}",
            Hash = "hash-3"
        };

        ProfilesService svc = CreateService(new List<MachineProfile> { profile });

        // Act
        ExtendedProfilesResponseDto result = await svc.ListExtendedAsync(CancellationToken.None);

        // Assert
        MachineProfileListItemDto dto = Assert.Single(result.MachineProfiles);
        Assert.Null(dto.NozzleDiameter);
        Assert.Null(dto.PrinterVariant);
    }

    [Fact]
    public async Task ListExtendedAsync_MalformedSettingsJson_DoesNotThrowAndYieldsNulls()
    {
        // Arrange
        MachineProfile profile = new()
        {
            Id = Guid.NewGuid(),
            Name = "Broken Machine",
            Manufacturer = "Generic Mfg",
            SlicerType = SlicerType.OrcaSlicer,
            SettingsJson = "{not valid json",
            Hash = "hash-4"
        };

        ProfilesService svc = CreateService(new List<MachineProfile> { profile });

        // Act
        ExtendedProfilesResponseDto result = await svc.ListExtendedAsync(CancellationToken.None);

        // Assert
        MachineProfileListItemDto dto = Assert.Single(result.MachineProfiles);
        Assert.Null(dto.NozzleDiameter);
        Assert.Null(dto.PrinterVariant);
    }

    private static ProfilesService CreateService(IReadOnlyList<MachineProfile> machineProfiles)
    {
        Mock<IProfilesRepository> profilesRepo = new(MockBehavior.Loose);
        Mock<IUnitOfWork> unitOfWork = new(MockBehavior.Loose);
        Mock<IHubContext<SlicerHub>> hubContext = new(MockBehavior.Loose);
        Mock<IPrinterModelAliasService> aliasService = new(MockBehavior.Loose);
        Mock<IProfileParsingService> parsingService = new(MockBehavior.Loose);
        Mock<ICatalogService> catalogService = new(MockBehavior.Loose);
        Mock<Farm.Slicer.Module.Services.ISlicersService> slicersService = new(MockBehavior.Loose);

        Mock<IProcessProfileRepository> processProfileRepo = new(MockBehavior.Loose);
        _ = processProfileRepo
            .Setup(r => r.GetByEngineAsync(SlicerType.OrcaSlicer, true, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProcessProfile>());

        Mock<IFilamentProfileRepository> filamentProfileRepo = new(MockBehavior.Loose);
        _ = filamentProfileRepo
            .Setup(r => r.GetByEngineAsync(SlicerType.OrcaSlicer, true, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<FilamentProfile>());

        Mock<IMachineProfileRepository> machineProfileRepo = new(MockBehavior.Loose);
        _ = machineProfileRepo
            .Setup(r => r.GetByEngineAsync(SlicerType.OrcaSlicer, true, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(machineProfiles);

        return new ProfilesService(
            profilesRepo.Object,
            NullLogger<ProfilesService>.Instance,
            processProfileRepo.Object,
            machineProfileRepo.Object,
            filamentProfileRepo.Object,
            unitOfWork.Object,
            catalogService.Object,
            parsingService.Object,
            hubContext.Object,
            slicersService.Object,
            aliasService.Object);
    }
}
