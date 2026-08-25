using System;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Security;
using Farm.Slicer.Module.Api.Controllers.Slicing;
using Farm.Slicer.Module.Api.Filters;
using Farm.Slicer.Module.Dtos;
using Farm.Slicer.Module.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Slicer.Module.Tests.Controllers;

/// <summary>
/// Regression tests for #2004: <see cref="ProfilesController.ResolveProfileForModelAsync"/> is the
/// new non-admin endpoint that lets a caller holding only calibration scopes resolve — and,
/// if necessary, auto-import — a catalog profile's database identity without any prior admin
/// action. Also covers that the sibling import wizard action
/// (<see cref="ProfilesController.ImportSelectedProfilesForModelAsync"/>) still wires its now
/// dependency-injected <see cref="HttpClient"/> through correctly.
/// </summary>
public class ProfilesControllerResolveProfileTests
{
    /// <summary>
    /// Review finding (Hicks): the tests above call the action method directly, which never
    /// exercises the MVC filter pipeline, so they cannot prove the endpoint is actually gated by
    /// <c>Calibration.Update</c> rather than accidentally left open or still requiring
    /// <c>slicer_engines:admin</c> like its sibling actions. This asserts, via reflection, that
    /// <see cref="ProfilesController.ResolveProfileForModelAsync"/> carries the method-level
    /// <see cref="RequirePermissionAttribute"/> with exactly <see cref="PrintFarmerPermissions.Calibration.Update"/>,
    /// layered on top of the controller's class-level <see cref="PrintFarmerPermissions.Slicing.Submit"/>
    /// requirement — matching the desktop client's actual scopes (Slicing.Submit +
    /// Calibration read/write) and explicitly NOT the admin-only permission the import wizard uses.
    /// </summary>
    [Fact]
    public void ResolveProfileForModelAsync_IsGatedByCalibrationUpdate_NotAdmin()
    {
        MethodInfo method = typeof(ProfilesController).GetMethod(nameof(ProfilesController.ResolveProfileForModelAsync))
            ?? throw new InvalidOperationException($"{nameof(ProfilesController.ResolveProfileForModelAsync)} not found via reflection");

        RequirePermissionAttribute methodAttribute = method.GetCustomAttributes<RequirePermissionAttribute>().SingleOrDefault()
            ?? throw new InvalidOperationException("Expected exactly one method-level RequirePermissionAttribute");

        Assert.Equal(PrintFarmerPermissions.Calibration.Update, methodAttribute.Permission);
        Assert.NotEqual("slicer_engines:admin", methodAttribute.Permission);

        RequirePermissionAttribute classAttribute = typeof(ProfilesController).GetCustomAttributes<RequirePermissionAttribute>().SingleOrDefault()
            ?? throw new InvalidOperationException("Expected exactly one class-level RequirePermissionAttribute");

        Assert.Equal(PrintFarmerPermissions.Slicing.Submit, classAttribute.Permission);
    }

    [Fact]
    public async Task ResolveProfileForModelAsync_NullRequest_Returns400()
    {
        Mock<IProfilesService> profilesService = new(MockBehavior.Strict);
        Mock<ICatalogServiceAdapter> catalogService = new(MockBehavior.Strict);
        ProfilesController controller = CreateController(profilesService, catalogService);

        using HttpClient httpClient = new();
        IActionResult result = await controller.ResolveProfileForModelAsync(httpClient, Guid.NewGuid(), null, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task ResolveProfileForModelAsync_BlankProfileName_Returns400()
    {
        Mock<IProfilesService> profilesService = new(MockBehavior.Strict);
        Mock<ICatalogServiceAdapter> catalogService = new(MockBehavior.Strict);
        ProfilesController controller = CreateController(profilesService, catalogService);

        using HttpClient httpClient = new();
        ResolveProfileForModelRequest request = new() { ProfileType = ProfileResolutionType.Machine, ProfileName = "   " };
        IActionResult result = await controller.ResolveProfileForModelAsync(httpClient, Guid.NewGuid(), request, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task ResolveProfileForModelAsync_UnknownModel_Returns404()
    {
        Guid modelId = Guid.NewGuid();
        Mock<IProfilesService> profilesService = new(MockBehavior.Strict);
        Mock<ICatalogServiceAdapter> catalogService = new(MockBehavior.Strict);
        _ = catalogService
            .Setup(c => c.GetModelByIdAsync(modelId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((CatalogModelInfo?)null);

        ProfilesController controller = CreateController(profilesService, catalogService);

        using HttpClient httpClient = new();
        ResolveProfileForModelRequest request = new() { ProfileType = ProfileResolutionType.Machine, ProfileName = "Qidi X-Plus 4" };
        IActionResult result = await controller.ResolveProfileForModelAsync(httpClient, modelId, request, CancellationToken.None);

        NotFoundObjectResult notFound = Assert.IsType<NotFoundObjectResult>(result);
        Assert.Contains(modelId.ToString(), Assert.IsType<string>(notFound.Value), StringComparison.Ordinal);
        profilesService.Verify(
            s => s.ResolveOrImportProfileForModelAsync(It.IsAny<HttpClient>(), It.IsAny<Guid>(), It.IsAny<ProfileResolutionType>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// This is the actual reproduction case (#2004): a never-imported catalog model (e.g. Qidi
    /// X-Plus 4) resolves successfully — with no prior admin action — to a newly-imported Guid.
    /// </summary>
    [Fact]
    public async Task ResolveProfileForModelAsync_NeverImportedProfileAutoImports_Returns200WithNewId()
    {
        Guid modelId = Guid.NewGuid();
        Guid newProfileId = Guid.NewGuid();
        const string profileName = "Qidi X-Plus 4";

        Mock<IProfilesService> profilesService = new(MockBehavior.Strict);
        Mock<ICatalogServiceAdapter> catalogService = new(MockBehavior.Strict);
        _ = catalogService
            .Setup(c => c.GetModelByIdAsync(modelId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CatalogModelInfo(modelId, profileName, "Qidi Technology"));
        _ = profilesService
            .Setup(s => s.ResolveOrImportProfileForModelAsync(It.IsAny<HttpClient>(), modelId, ProfileResolutionType.Machine, profileName, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResolveProfileForModelResultDto
            {
                PrinterModelId = modelId,
                ProfileType = ProfileResolutionType.Machine,
                ProfileName = profileName,
                ProfileId = newProfileId,
                Imported = true
            });

        ProfilesController controller = CreateController(profilesService, catalogService);

        using HttpClient httpClient = new();
        ResolveProfileForModelRequest request = new() { ProfileType = ProfileResolutionType.Machine, ProfileName = profileName };
        IActionResult result = await controller.ResolveProfileForModelAsync(httpClient, modelId, request, CancellationToken.None);

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result);
        ResolveProfileForModelResultDto dto = Assert.IsType<ResolveProfileForModelResultDto>(ok.Value);
        Assert.True(dto.Imported);
        Assert.Equal(newProfileId, dto.ProfileId);
        Assert.Null(dto.Error);
    }

    [Fact]
    public async Task ResolveProfileForModelAsync_AlreadyImportedProfile_Returns200WithoutImportFlag()
    {
        Guid modelId = Guid.NewGuid();
        Guid existingProfileId = Guid.NewGuid();
        const string profileName = "Prusa MK4";

        Mock<IProfilesService> profilesService = new(MockBehavior.Strict);
        Mock<ICatalogServiceAdapter> catalogService = new(MockBehavior.Strict);
        _ = catalogService
            .Setup(c => c.GetModelByIdAsync(modelId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CatalogModelInfo(modelId, profileName, "Prusa Research"));
        _ = profilesService
            .Setup(s => s.ResolveOrImportProfileForModelAsync(It.IsAny<HttpClient>(), modelId, ProfileResolutionType.Machine, profileName, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResolveProfileForModelResultDto
            {
                PrinterModelId = modelId,
                ProfileType = ProfileResolutionType.Machine,
                ProfileName = profileName,
                ProfileId = existingProfileId,
                Imported = false
            });

        ProfilesController controller = CreateController(profilesService, catalogService);

        using HttpClient httpClient = new();
        ResolveProfileForModelRequest request = new() { ProfileType = ProfileResolutionType.Machine, ProfileName = profileName };
        IActionResult result = await controller.ResolveProfileForModelAsync(httpClient, modelId, request, CancellationToken.None);

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result);
        ResolveProfileForModelResultDto dto = Assert.IsType<ResolveProfileForModelResultDto>(ok.Value);
        Assert.False(dto.Imported);
        Assert.Equal(existingProfileId, dto.ProfileId);
    }

    [Fact]
    public async Task ResolveProfileForModelAsync_WorkerCommunicationError_Returns503()
    {
        Guid modelId = Guid.NewGuid();
        const string profileName = "Qidi X-Plus 4";

        Mock<IProfilesService> profilesService = new(MockBehavior.Strict);
        Mock<ICatalogServiceAdapter> catalogService = new(MockBehavior.Strict);
        _ = catalogService
            .Setup(c => c.GetModelByIdAsync(modelId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CatalogModelInfo(modelId, profileName, "Qidi Technology"));
        _ = profilesService
            .Setup(s => s.ResolveOrImportProfileForModelAsync(It.IsAny<HttpClient>(), modelId, ProfileResolutionType.Machine, profileName, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResolveProfileForModelResultDto
            {
                PrinterModelId = modelId,
                ProfileType = ProfileResolutionType.Machine,
                ProfileName = profileName,
                Error = "Failed to communicate with OrcaSlicer worker"
            });

        ProfilesController controller = CreateController(profilesService, catalogService);

        using HttpClient httpClient = new();
        ResolveProfileForModelRequest request = new() { ProfileType = ProfileResolutionType.Machine, ProfileName = profileName };
        IActionResult result = await controller.ResolveProfileForModelAsync(httpClient, modelId, request, CancellationToken.None);

        ObjectResult objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, objectResult.StatusCode);
    }

    [Fact]
    public async Task ResolveProfileForModelAsync_ProfileNotFoundOrIncompatible_Returns400()
    {
        Guid modelId = Guid.NewGuid();
        const string profileName = "Nonexistent Profile";

        Mock<IProfilesService> profilesService = new(MockBehavior.Strict);
        Mock<ICatalogServiceAdapter> catalogService = new(MockBehavior.Strict);
        _ = catalogService
            .Setup(c => c.GetModelByIdAsync(modelId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CatalogModelInfo(modelId, "Qidi X-Plus 4", "Qidi Technology"));
        _ = profilesService
            .Setup(s => s.ResolveOrImportProfileForModelAsync(It.IsAny<HttpClient>(), modelId, ProfileResolutionType.Machine, profileName, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResolveProfileForModelResultDto
            {
                PrinterModelId = modelId,
                ProfileType = ProfileResolutionType.Machine,
                ProfileName = profileName,
                Error = $"Profile '{profileName}' was not found or is not compatible with printer model 'Qidi X-Plus 4'"
            });

        ProfilesController controller = CreateController(profilesService, catalogService);

        using HttpClient httpClient = new();
        ResolveProfileForModelRequest request = new() { ProfileType = ProfileResolutionType.Machine, ProfileName = profileName };
        IActionResult result = await controller.ResolveProfileForModelAsync(httpClient, modelId, request, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    /// <summary>
    /// Regression check that the existing import wizard action still passes its now
    /// dependency-injected <see cref="HttpClient"/> through to the service unchanged.
    /// </summary>
    [Fact]
    public async Task ImportSelectedProfilesForModelAsync_PassesInjectedHttpClientThrough()
    {
        Guid modelId = Guid.NewGuid();
        HttpClient? capturedClient = null;

        Mock<IProfilesService> profilesService = new(MockBehavior.Strict);
        Mock<ICatalogServiceAdapter> catalogService = new(MockBehavior.Strict);
        _ = catalogService
            .Setup(c => c.GetModelByIdAsync(modelId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CatalogModelInfo(modelId, "Qidi X-Plus 4", "Qidi Technology"));
        _ = profilesService
            .Setup(s => s.ImportSelectedProfilesForModelAsync(It.IsAny<HttpClient>(), modelId, It.IsAny<SelectiveProfileImportRequest>(), It.IsAny<CancellationToken>()))
            .Callback<HttpClient, Guid, SelectiveProfileImportRequest, CancellationToken>((client, _, _, _) => capturedClient = client)
            .ReturnsAsync(new SelectiveProfileImportResultDto { PrinterModelId = modelId, MachineProfilesImported = 1 });

        ProfilesController controller = CreateController(profilesService, catalogService);

        using HttpClient httpClient = new();
        SelectiveProfileImportRequest request = new() { ManufacturerName = "Qidi Technology", SelectedMachineProfiles = ["Qidi X-Plus 4"] };
        IActionResult result = await controller.ImportSelectedProfilesForModelAsync(httpClient, modelId, request, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Same(httpClient, capturedClient);
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
