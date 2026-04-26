using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Dtos;
using Farm.Slicer.Module.Api.Controllers.Slicing;
using Farm.Slicer.Module.Dtos;
using Farm.Slicer.Module.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Slicer.Module.Tests.Controllers;

public class ProfilesControllerMachineProfilesTests
{
    [Fact]
    public async Task GetMachineProfilesForModelIdAsync_UnknownModel_Returns404()
    {
        Guid modelId = Guid.NewGuid();
        Mock<IProfilesService> profilesService = new(MockBehavior.Strict);
        Mock<ICatalogServiceAdapter> catalogService = new(MockBehavior.Strict);
        _ = catalogService
            .Setup(c => c.GetModelByIdAsync(modelId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((CatalogModelInfo?)null);

        ProfilesController controller = CreateController(profilesService, catalogService);

        IActionResult result = await controller.GetMachineProfilesForModelIdAsync(new HttpClient(), modelId, CancellationToken.None);

        NotFoundObjectResult notFound = Assert.IsType<NotFoundObjectResult>(result);
        Assert.Contains(modelId.ToString(), Assert.IsType<string>(notFound.Value), StringComparison.Ordinal);
        profilesService.Verify(
            s => s.GetMachineProfilesForCatalogModelAsync(It.IsAny<HttpClient>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task GetMachineProfilesForModelIdAsync_NoOrcaAliases_Returns404()
    {
        Guid modelId = Guid.NewGuid();
        Mock<IProfilesService> profilesService = new(MockBehavior.Strict);
        Mock<ICatalogServiceAdapter> catalogService = new(MockBehavior.Strict);
        _ = catalogService
            .Setup(c => c.GetModelByIdAsync(modelId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CatalogModelInfo(modelId, "Test Model", "Test"));
        _ = catalogService
            .Setup(c => c.GetModelAliasesAsync(modelId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new SlicerModelAliasDto(Guid.NewGuid(), modelId, "Test Model", "PrusaSlicer")
            ]);

        ProfilesController controller = CreateController(profilesService, catalogService);

        IActionResult result = await controller.GetMachineProfilesForModelIdAsync(new HttpClient(), modelId, CancellationToken.None);

        NotFoundObjectResult notFound = Assert.IsType<NotFoundObjectResult>(result);
        Assert.Contains("No OrcaSlicer alias configured", Assert.IsType<string>(notFound.Value), StringComparison.Ordinal);
        profilesService.Verify(
            s => s.GetMachineProfilesForCatalogModelAsync(It.IsAny<HttpClient>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task GetMachineProfilesForModelIdAsync_AliasHit_Returns200WithProfiles()
    {
        Guid modelId = Guid.NewGuid();
        List<string>? capturedAliases = null;
        List<MachineProfileDto> profiles = [new() { Name = "Alias One 0.4 nozzle", Manufacturer = "Test" }];
        Mock<IProfilesService> profilesService = new(MockBehavior.Strict);
        Mock<ICatalogServiceAdapter> catalogService = new(MockBehavior.Strict);
        _ = catalogService
            .Setup(c => c.GetModelByIdAsync(modelId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CatalogModelInfo(modelId, "Test Model", "Test"));
        _ = catalogService
            .Setup(c => c.GetModelAliasesAsync(modelId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new SlicerModelAliasDto(Guid.NewGuid(), modelId, "Alias One", "OrcaSlicer")
            ]);
        _ = profilesService
            .Setup(s => s.GetMachineProfilesForCatalogModelAsync(It.IsAny<HttpClient>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .Callback<HttpClient, IEnumerable<string>, CancellationToken>((_, aliases, _) => capturedAliases = aliases.ToList())
            .ReturnsAsync(profiles);

        ProfilesController controller = CreateController(profilesService, catalogService);

        IActionResult result = await controller.GetMachineProfilesForModelIdAsync(new HttpClient(), modelId, CancellationToken.None);

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result);
        IReadOnlyList<MachineProfileDto> value = Assert.IsAssignableFrom<IReadOnlyList<MachineProfileDto>>(ok.Value);
        MachineProfileDto profile = Assert.Single(value);
        Assert.Equal("Alias One 0.4 nozzle", profile.Name);
        Assert.Equal(["Alias One"], capturedAliases);
    }

    [Fact]
    public async Task GetMachineProfilesForModelIdAsync_AliasMiss_Returns200EmptyArray()
    {
        Guid modelId = Guid.NewGuid();
        Mock<IProfilesService> profilesService = new(MockBehavior.Strict);
        Mock<ICatalogServiceAdapter> catalogService = new(MockBehavior.Strict);
        _ = catalogService
            .Setup(c => c.GetModelByIdAsync(modelId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CatalogModelInfo(modelId, "Test Model", "Test"));
        _ = catalogService
            .Setup(c => c.GetModelAliasesAsync(modelId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new SlicerModelAliasDto(Guid.NewGuid(), modelId, "Alias One", "OrcaSlicer")
            ]);
        _ = profilesService
            .Setup(s => s.GetMachineProfilesForCatalogModelAsync(It.IsAny<HttpClient>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        ProfilesController controller = CreateController(profilesService, catalogService);

        IActionResult result = await controller.GetMachineProfilesForModelIdAsync(new HttpClient(), modelId, CancellationToken.None);

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result);
        IReadOnlyList<MachineProfileDto> value = Assert.IsAssignableFrom<IReadOnlyList<MachineProfileDto>>(ok.Value);
        Assert.Empty(value);
    }

    [Fact]
    public async Task GetMachineProfilesForModelIdAsync_WorkerFailure_Returns503WithoutRawExceptionMessage()
    {
        Guid modelId = Guid.NewGuid();
        const string rawExceptionMessage = "Failed to reach http://internal-worker:8080/api/profiles";
        Mock<IProfilesService> profilesService = new(MockBehavior.Strict);
        Mock<ICatalogServiceAdapter> catalogService = new(MockBehavior.Strict);
        _ = catalogService
            .Setup(c => c.GetModelByIdAsync(modelId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CatalogModelInfo(modelId, "Test Model", "Test"));
        _ = catalogService
            .Setup(c => c.GetModelAliasesAsync(modelId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new SlicerModelAliasDto(Guid.NewGuid(), modelId, "Alias One", "OrcaSlicer")
            ]);
        _ = profilesService
            .Setup(s => s.GetMachineProfilesForCatalogModelAsync(It.IsAny<HttpClient>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException(rawExceptionMessage));

        ProfilesController controller = CreateController(profilesService, catalogService);

        IActionResult result = await controller.GetMachineProfilesForModelIdAsync(new HttpClient(), modelId, CancellationToken.None);

        ObjectResult objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, objectResult.StatusCode);
        string body = Assert.IsType<string>(objectResult.Value);
        Assert.Equal("OrcaSlicer worker unavailable", body);
        Assert.DoesNotContain(rawExceptionMessage, body, StringComparison.Ordinal);
    }

    private static ProfilesController CreateController(
        Mock<IProfilesService> profilesService,
        Mock<ICatalogServiceAdapter> catalogService)
    {
        return new ProfilesController(
            NullLogger<ProfilesController>.Instance,
            profilesService.Object,
            catalogService.Object);
    }
}
