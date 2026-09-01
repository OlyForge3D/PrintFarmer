using System.Net;
using System.Text;
using System.Text.Json;
using Farm.Infrastructure;
using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.PrinterCalibration;
using Farm.Infrastructure.Repositories.UnitOfWork;
using Farm.Infrastructure.Services.Catalog;
using Farm.Infrastructure.Services.Gcode;
using Farm.Slicer.Module.Api.Hubs;
using Farm.Slicer.Module.Api.Services;
using Farm.Slicer.Module.Data.Repositories;
using Farm.Slicer.Module.Domain;
using Farm.Slicer.Module.Dtos;
using Farm.Slicer.Module.Services;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Farm.Slicer.Module.Tests.Services;

public sealed class ProfilesServiceLibraryHierarchyTests
{
    [Fact]
    public async Task ListHierarchyAsync_UnboundProcess_AppearsOnlyForUnboundMachine()
    {
        Guid modelId = Guid.NewGuid();
        MachineProfile linkedMachine = new()
        {
            Id = Guid.NewGuid(),
            Name = "Linked",
            Manufacturer = "Test",
            PrinterModelId = modelId,
            Hash = "linked"
        };
        MachineProfile unboundMachine = new()
        {
            Id = Guid.NewGuid(),
            Name = "Unbound",
            Manufacturer = "Test",
            Hash = "unbound"
        };
        ProcessProfile unboundProcess = new()
        {
            Id = Guid.NewGuid(),
            Name = "Universal",
            Hash = "process"
        };
        Mock<IMachineProfileRepository> machines = new(MockBehavior.Strict);
        _ = machines.Setup(repository => repository.GetByEngineAsync(
                SlicerType.OrcaSlicer,
                true,
                null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([linkedMachine, unboundMachine]);
        Mock<IProcessProfileRepository> processes = new(MockBehavior.Strict);
        _ = processes.Setup(repository => repository.GetByEngineAsync(
                SlicerType.OrcaSlicer,
                true,
                null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([unboundProcess]);
        Mock<IFilamentProfileRepository> filaments = new(MockBehavior.Strict);
        _ = filaments.Setup(repository => repository.GetByEngineAsync(
                SlicerType.OrcaSlicer,
                true,
                null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        Mock<ICatalogService> catalog = new(MockBehavior.Loose);
        _ = catalog.Setup(service => service.GetManufacturersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(((IReadOnlyList<ManufacturerDto>)[], null));
        _ = catalog.Setup(service => service.GetModelByIdAsync(
                modelId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((PrinterModelDto?)null);
        ProfilesService service = new(
            Mock.Of<IProfilesRepository>(),
            NullLogger<ProfilesService>.Instance,
            processes.Object,
            machines.Object,
            filaments.Object,
            Mock.Of<IUnitOfWork>(),
            catalog.Object,
            Mock.Of<IProfileParsingService>(),
            Mock.Of<IHubContext<SlicerHub>>(),
            Mock.Of<ISlicersService>(),
            Mock.Of<IPrinterModelAliasService>());

        HierarchicalProfilesResponseDto result =
            await service.ListHierarchyAsync(null, null, CancellationToken.None);

        result.ByHierarchy["Test"].Models[modelId.ToString()].ProcessProfiles.Should().BeEmpty();
        result.ByHierarchy["Test"].Models[unboundMachine.Id.ToString()].ProcessProfiles
            .Should().ContainSingle(profile => profile.Name == "Universal");
    }

    [Fact]
    public async Task GetCatalogAttributedWorkerHierarchyAsync_AliasedFamily_RekeysAndRewritesManufacturer()
    {
        Guid manufacturerId = Guid.NewGuid();
        Guid modelId = Guid.NewGuid();
        const string workerManufacturer = "PrintFarmer-1234567890abcdef";
        const string alias = "Micron Farm";
        AllProfilesResponseDto workerResponse = new()
        {
            ByHierarchy =
            {
                [workerManufacturer] = new ManufacturerProfilesDto
                {
                    Name = workerManufacturer,
                    Models =
                    {
                        [alias] = new PrinterModelProfilesDto
                        {
                            Name = alias,
                            ModelId = alias,
                            MachineProfiles =
                            [
                                new MachineProfileDto
                                {
                                    Name = "Micron Farm 0.4 nozzle",
                                    PrinterModel = alias,
                                    Manufacturer = workerManufacturer
                                }
                            ]
                        }
                    }
                }
            },
            MachineProfiles =
            {
                [workerManufacturer] =
                [
                    new MachineProfileDto
                    {
                        Name = "Micron Farm 0.4 nozzle",
                        PrinterModel = alias,
                        Manufacturer = workerManufacturer
                    }
                ]
            }
        };
        Mock<IPrinterModelAliasService> aliases = new(MockBehavior.Strict);
        _ = aliases.Setup(service => service.ListAliasesAsync(
                "OrcaSlicer",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(
                [new SlicerModelAliasEntry(alias, "OrcaSlicer", modelId)]);
        Mock<ICatalogService> catalog = new(MockBehavior.Strict);
        _ = catalog.Setup(service => service.GetModelsAsync(
                null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((
                (IReadOnlyList<PrinterModelDto>)[new PrinterModelDto(modelId, "Micron 180", manufacturerId)],
                null));
        _ = catalog.Setup(service => service.GetManufacturersAsync(
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((
                (IReadOnlyList<ManufacturerDto>)[new ManufacturerDto(manufacturerId, "PrintersForAnts")],
                null));
        ProfilesService service = CreateService(aliases.Object, catalog.Object);
        using HttpClient httpClient = new(new JsonHandler(workerResponse));

        AllProfilesResponseDto? attributed =
            await service.GetCatalogAttributedWorkerHierarchyAsync(
                httpClient,
                "all",
                CancellationToken.None);
        AllProfilesResponseDto? raw =
            await service.GetWorkerProfilesHierarchyAsync(httpClient, CancellationToken.None);

        attributed.Should().NotBeNull();
        attributed!.ByHierarchy.Should().ContainKey("PrintersForAnts");
        attributed.ByHierarchy.Should().NotContainKey(workerManufacturer);
        attributed.ByHierarchy["PrintersForAnts"].Models.Should().ContainKey(alias);
        attributed.ByHierarchy["PrintersForAnts"].Models[alias].MachineProfiles
            .Should().OnlyContain(profile => profile.Manufacturer == "PrintersForAnts");
        attributed.MachineProfiles.Should().ContainKey("PrintersForAnts");
        raw!.ByHierarchy.Should().ContainKey(workerManufacturer);
        raw.ByHierarchy.Should().NotContainKey("PrintersForAnts");
    }

    private static ProfilesService CreateService(
        IPrinterModelAliasService aliasService,
        ICatalogService catalogService)
    {
        Mock<ISlicersService> slicers = new(MockBehavior.Strict);
        _ = slicers.Setup(service => service.ListAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new SlicerService
                {
                    Id = Guid.NewGuid(),
                    Name = "worker",
                    SlicerType = (int)SlicerType.OrcaSlicer,
                    Version = "2.4.2",
                    Host = "http://worker",
                    Status = "Online",
                    LastSeen = DateTime.UtcNow,
                    CapabilitiesJson =
                        $"[\"{CalibrationContractConstants.UpstreamSlicerCapability}\"]"
                }
            ]);

        return new ProfilesService(
            Mock.Of<IProfilesRepository>(),
            NullLogger<ProfilesService>.Instance,
            Mock.Of<IProcessProfileRepository>(),
            Mock.Of<IMachineProfileRepository>(),
            Mock.Of<IFilamentProfileRepository>(),
            Mock.Of<IUnitOfWork>(),
            catalogService,
            Mock.Of<IProfileParsingService>(),
            Mock.Of<IHubContext<SlicerHub>>(),
            slicers.Object,
            aliasService);
    }

    private sealed class JsonHandler(AllProfilesResponseDto response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(response),
                    Encoding.UTF8,
                    "application/json")
            });
    }
}
