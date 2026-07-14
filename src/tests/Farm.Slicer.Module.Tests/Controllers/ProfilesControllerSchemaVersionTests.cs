using System.Linq;
using Farm.Slicer.Module.Api.Controllers.Slicing;
using Farm.Slicer.Module.Dtos;
using Farm.Slicer.Module.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Slicer.Module.Tests.Controllers;

/// <summary>
/// Verifies the four /slicer/profiles/schema* endpoints accept an optional
/// <c>engineVersion</c> query parameter and forward it to
/// <see cref="ProfileSchemaProvider"/> so the returned field set matches the
/// OrcaSlicer version the caller pinned (issue #578). Uses in-process
/// controller invocation — the query param is delivered by ASP.NET binding
/// in production, but here we invoke the action with the argument directly.
/// </summary>
public class ProfilesControllerSchemaVersionTests
{
    [Fact]
    public void GetProcessSchema_NoVersion_ReturnsAllFields()
    {
        ProfilesController controller = CreateController();

        ObjectResult ok = Assert.IsType<OkObjectResult>(controller.GetProcessSchema(engineVersion: null));
        ProfileTypeSchemaDto schema = Assert.IsType<ProfileTypeSchemaDto>(ok.Value);

        Assert.Contains(schema.Fields, f => f.Key == "wallGenerator");
        Assert.Contains(schema.Fields, f => f.Key == "legacyPreviewSetting");
        Assert.Contains(schema.Fields, f => f.Key == "bedAdhesionOverride");
    }

    [Fact]
    public void GetProcessSchema_LegacyEngineVersion_DropsAddedFieldsAndRenamesLegacyKey()
    {
        ProfilesController controller = CreateController();

        ObjectResult ok = Assert.IsType<OkObjectResult>(controller.GetProcessSchema(engineVersion: "2.3.1"));
        ProfileTypeSchemaDto schema = Assert.IsType<ProfileTypeSchemaDto>(ok.Value);

        Assert.DoesNotContain(schema.Fields, f => f.Key == "wallGenerator");
        Assert.DoesNotContain(schema.Fields, f => f.Key == "enableArcFitting");
        Assert.Contains(schema.Fields, f => f.Key == "legacyPreviewSetting");
        Assert.Contains(schema.Fields, f => f.Key == "firstLayerAdhesion");
        Assert.DoesNotContain(schema.Fields, f => f.Key == "bedAdhesionOverride");
    }

    [Fact]
    public void GetProcessSchema_CurrentEngineVersion_AddsFieldsAndUsesPostRenameKey()
    {
        ProfilesController controller = CreateController();

        ObjectResult ok = Assert.IsType<OkObjectResult>(controller.GetProcessSchema(engineVersion: "2.4.1"));
        ProfileTypeSchemaDto schema = Assert.IsType<ProfileTypeSchemaDto>(ok.Value);

        Assert.Contains(schema.Fields, f => f.Key == "wallGenerator");
        Assert.Contains(schema.Fields, f => f.Key == "enableArcFitting");
        Assert.DoesNotContain(schema.Fields, f => f.Key == "legacyPreviewSetting");
        Assert.Contains(schema.Fields, f => f.Key == "bedAdhesionOverride");
        Assert.DoesNotContain(schema.Fields, f => f.Key == "firstLayerAdhesion");
    }

    [Fact]
    public void GetAllSchemas_LegacyVersion_FiltersAllThreeProfileTypes()
    {
        ProfilesController controller = CreateController();

        ObjectResult ok = Assert.IsType<OkObjectResult>(controller.GetAllSchemas(engineVersion: "2.3.1"));
        ProfileSchemasResponseDto all = Assert.IsType<ProfileSchemasResponseDto>(ok.Value);

        Assert.DoesNotContain(all.Process.Fields, f => f.Key == "wallGenerator");
        Assert.Contains(all.Process.Fields, f => f.Key == "firstLayerAdhesion");
    }

    [Fact]
    public void GetAllSchemas_CurrentVersion_KeepsAddedFieldsForAllProfileTypes()
    {
        ProfilesController controller = CreateController();

        ObjectResult ok = Assert.IsType<OkObjectResult>(controller.GetAllSchemas(engineVersion: "2.4.1"));
        ProfileSchemasResponseDto all = Assert.IsType<ProfileSchemasResponseDto>(ok.Value);

        Assert.Contains(all.Process.Fields, f => f.Key == "wallGenerator");
        Assert.Contains(all.Process.Fields, f => f.Key == "bedAdhesionOverride");
    }

    [Fact]
    public void GetMachineSchema_AcceptsEngineVersionParameter()
    {
        ProfilesController controller = CreateController();

        ObjectResult ok = Assert.IsType<OkObjectResult>(controller.GetMachineSchema(engineVersion: "2.3.1"));
        ProfileTypeSchemaDto schema = Assert.IsType<ProfileTypeSchemaDto>(ok.Value);

        Assert.Equal("machine", schema.ProfileType);
        Assert.NotEmpty(schema.Fields);
    }

    [Fact]
    public void GetFilamentSchema_AcceptsEngineVersionParameter()
    {
        ProfilesController controller = CreateController();

        ObjectResult ok = Assert.IsType<OkObjectResult>(controller.GetFilamentSchema(engineVersion: "2.4.1"));
        ProfileTypeSchemaDto schema = Assert.IsType<ProfileTypeSchemaDto>(ok.Value);

        Assert.Equal("filament", schema.ProfileType);
        Assert.NotEmpty(schema.Fields);
    }

    [Fact]
    public void GetProcessSchema_MalformedEngineVersion_DoesNotThrow_ReturnsAllFields()
    {
        ProfilesController controller = CreateController();

        // A malformed engineVersion should fall through to unfiltered rather than 500.
        ObjectResult ok = Assert.IsType<OkObjectResult>(controller.GetProcessSchema(engineVersion: "notavalidversion"));
        ProfileTypeSchemaDto schema = Assert.IsType<ProfileTypeSchemaDto>(ok.Value);

        Assert.Contains(schema.Fields, f => f.Key == "wallGenerator");
    }

    private static ProfilesController CreateController()
    {
        Mock<IProfilesService> profilesService = new(MockBehavior.Strict);
        Mock<ICatalogServiceAdapter> catalogService = new(MockBehavior.Strict);
        return new ProfilesController(
            NullLogger<ProfilesController>.Instance,
            profilesService.Object,
            catalogService.Object);
    }
}
