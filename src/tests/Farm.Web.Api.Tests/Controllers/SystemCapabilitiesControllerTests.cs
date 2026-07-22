using System.Text.Json;
using Farm.Infrastructure.Dtos;
using Farm.Web.Api.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Farm.Web.Api.Tests.Controllers;

public class SystemCapabilitiesControllerTests
{
    [Fact]
    public void GetCapabilities_WhenModelFilesEnabled_AdvertisesUploadCapabilities()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Platform:ModelFilesEnabled"] = "true"
            })
            .Build();
        SystemCapabilitiesController controller = new(configuration);

        ActionResult<PlatformCapabilitiesDto> actionResult = controller.GetCapabilities();

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
