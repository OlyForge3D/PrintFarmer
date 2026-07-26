using System.Collections.Generic;
using System.Text.Json;
using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.Services.FeatureFlags;
using Farm.Infrastructure.Services.OperatorFeatures;
using Farm.Web.Api.Controllers;
using Farm.Web.Api.Services.Capabilities;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Controllers;

/// <summary>
/// Controller-level unit tests for <see cref="SystemCapabilitiesController"/> — validates that
/// the capabilities endpoint exposes the effective operator feature flags (issue #725) and that
/// the response DTO stays compatible with older clients that do not know about the new field.
/// </summary>
public class SystemCapabilitiesControllerTests
{
    private static IConfiguration EmptyConfig()
        => new ConfigurationBuilder().AddInMemoryCollection([]).Build();

    private static SystemCapabilitiesController CreateController(
        IOperatorFeatureGate gate,
        IConfiguration? configuration = null)
    {
        Mock<IFeatureFlagService> featureFlags = new();
        featureFlags.Setup(f => f.GetAllFlags()).Returns(new Dictionary<string, bool>());
        IConfiguration effectiveConfiguration = configuration ?? EmptyConfig();
        bool modelFilesEnabled = effectiveConfiguration.GetValue("Platform:ModelFilesEnabled", true);
        Mock<ICalibrationCapabilityService> capabilityService = new();
        capabilityService
            .Setup(service => service.GetCapabilitiesAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlatformCapabilitiesDto
            {
                ClientThumbnailUploadEnabled = modelFilesEnabled,
                IdempotentModelUploadEnabled = modelFilesEnabled,
                ModelThumbnailReplacementEnabled = modelFilesEnabled,
            });
        var controller = new SystemCapabilitiesController(
            capabilityService.Object,
            featureFlags.Object,
            gate);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext(),
        };
        return controller;
    }

    [Fact]
    public async Task GetCapabilities_IncludesEffectiveOperatorFeatures()
    {
        OperatorFeatureFlagsDto expected = new()
        {
            AttentionEnabled = false,
            NativePushEnabled = true,
            FilamentCoverageEnabled = true,
            GuidedSwapEnabled = false,
            MultiSlotFallbackEnabled = true,
            ShiftPlanEnabled = true,
            PrintedPartsInventoryEnabled = false,
            OfflineWriteReplayEnabled = true,
        };

        Mock<IOperatorFeatureGate> gate = new();
        gate.Setup(g => g.GetEffectiveFlags()).Returns(expected);

        ActionResult<PlatformCapabilitiesDto> result =
            await CreateController(gate.Object).GetCapabilitiesAsync(default);

        OkObjectResult ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        PlatformCapabilitiesDto dto = ok.Value.Should().BeOfType<PlatformCapabilitiesDto>().Subject;

        dto.OperatorFeatures.Should().BeEquivalentTo(expected);
        gate.Verify(g => g.GetEffectiveFlags(), Times.Once);
    }

    [Fact]
    public async Task GetCapabilities_SerializedResponseUsesCamelCaseOperatorFeaturesField()
    {
        // Older React/iOS clients ignore unknown fields; this test asserts the wire name we
        // committed to (`operatorFeatures`) so a rename would be caught in tests, not in prod.
        Mock<IOperatorFeatureGate> gate = new();
        gate.Setup(g => g.GetEffectiveFlags()).Returns(new OperatorFeatureFlagsDto());

        ActionResult<PlatformCapabilitiesDto> result =
            await CreateController(gate.Object).GetCapabilitiesAsync(default);
        PlatformCapabilitiesDto dto = ((OkObjectResult)result.Result!).Value.Should()
            .BeOfType<PlatformCapabilitiesDto>().Subject;

        string json = JsonSerializer.Serialize(dto, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });

        json.Should().Contain("\"operatorFeatures\":");
        json.Should().Contain("\"attentionEnabled\":true");
        json.Should().Contain("\"nativePushEnabled\":false");
    }

    [Fact]
    public void PlatformCapabilitiesDto_MissingOperatorFeaturesInPayload_UsesDefaults()
    {
        // Backward-compat guard for #725: a capability payload from an older server (or a test
        // fixture written before the field existed) must still deserialize with sensible defaults.
        // Clients (React/iOS) rely on the same fallback semantics.
        string olderPayload = """
            {
              "architecture": "X64",
              "slicingEnabled": true,
              "modelFilesEnabled": true,
              "thumbnailGenerationEnabled": true,
              "gcodeUploadEnabled": true,
              "platformNote": null
            }
            """;

        PlatformCapabilitiesDto? dto = JsonSerializer.Deserialize<PlatformCapabilitiesDto>(
            olderPayload,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        dto.Should().NotBeNull();
        dto!.OperatorFeatures.Should().NotBeNull();
        dto.OperatorFeatures.AttentionEnabled.Should().BeTrue("default true when older server omits the flag");
        dto.OperatorFeatures.NativePushEnabled.Should().BeFalse("default false when older server omits the flag");
        dto.OperatorFeatures.FilamentCoverageEnabled.Should().BeTrue();
        dto.OperatorFeatures.OfflineWriteReplayEnabled.Should().BeTrue();
    }

    [Fact]
    public void OperatorFeatureFlagsDto_PartialPayload_KeepsDefaultsForMissingFlags()
    {
        // Simulates a newer client parsing an older server's operatorFeatures object that only
        // includes a subset of flags (e.g. attentionEnabled). Missing flags must keep the
        // documented defaults from #725, and this JsonPropertyName-driven mapping is the contract
        // the mobile app depends on.
        string partial = """{"attentionEnabled":false,"nativePushEnabled":true}""";

        OperatorFeatureFlagsDto? flags = JsonSerializer.Deserialize<OperatorFeatureFlagsDto>(
            partial,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = false });

        flags.Should().NotBeNull();
        flags!.AttentionEnabled.Should().BeFalse();
        flags.NativePushEnabled.Should().BeTrue();
        flags.FilamentCoverageEnabled.Should().BeTrue("missing flags fall back to the documented default");
        flags.GuidedSwapEnabled.Should().BeTrue();
        flags.MultiSlotFallbackEnabled.Should().BeTrue();
        flags.ShiftPlanEnabled.Should().BeTrue();
        flags.PrintedPartsInventoryEnabled.Should().BeTrue();
        flags.OfflineWriteReplayEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task GetCapabilities_WhenModelFilesEnabled_AdvertisesUploadCapabilities()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Platform:ModelFilesEnabled"] = "true"
            })
            .Build();
        Mock<IOperatorFeatureGate> gate = new();
        gate.Setup(g => g.GetEffectiveFlags()).Returns(new OperatorFeatureFlagsDto());
        SystemCapabilitiesController controller = CreateController(gate.Object, configuration);

        ActionResult<PlatformCapabilitiesDto> actionResult =
            await controller.GetCapabilitiesAsync(default);

        OkObjectResult okResult = Assert.IsType<OkObjectResult>(actionResult.Result);
        PlatformCapabilitiesDto capabilities = Assert.IsType<PlatformCapabilitiesDto>(okResult.Value);
        Assert.True(capabilities.ClientThumbnailUploadEnabled);
        Assert.True(capabilities.IdempotentModelUploadEnabled);
        Assert.True(capabilities.ModelThumbnailReplacementEnabled);
    }

    [Fact]
    public void PlatformCapabilitiesDto_WithUploadCapabilities_SerializesAsCamelCase()
    {
        PlatformCapabilitiesDto capabilities = new()
        {
            ClientThumbnailUploadEnabled = true,
            IdempotentModelUploadEnabled = true,
            ModelThumbnailReplacementEnabled = true
        };

        string json = JsonSerializer.Serialize(
            capabilities,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        using JsonDocument document = JsonDocument.Parse(json);
        Assert.True(document.RootElement.GetProperty("clientThumbnailUploadEnabled").GetBoolean());
        Assert.True(document.RootElement.GetProperty("idempotentModelUploadEnabled").GetBoolean());
        Assert.True(document.RootElement.GetProperty("modelThumbnailReplacementEnabled").GetBoolean());
    }
}
