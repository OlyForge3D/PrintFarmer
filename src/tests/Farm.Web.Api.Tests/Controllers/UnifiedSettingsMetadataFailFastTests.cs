using Farm.Infrastructure.Services.Background;
using Farm.Infrastructure.Settings;
using Farm.Web.Api.Controllers;
using Farm.Web.Api.Services.Workers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Controllers;

/// <summary>
/// Guards the fail-fast behavior of the settings metadata endpoints. The service methods are
/// lazy <c>yield</c> iterators; the controller must materialize them inside its try/catch so a
/// throw surfaces as a clean 500 instead of a mid-stream truncated response
/// (ERR_INCOMPLETE_CHUNKED_ENCODING) after the 200 status is already written.
/// </summary>
public class UnifiedSettingsMetadataFailFastTests
{
    private static UnifiedSettingsController CreateController(Mock<ISettingsService> settings)
    {
        DiscoveryHeartbeatMonitorService monitor = new(
            Mock.Of<IBackgroundServiceMonitor>(),
            Mock.Of<IServiceScopeFactory>(),
            Mock.Of<ILogger<DiscoveryHeartbeatMonitorService>>());

        return new UnifiedSettingsController(
            settings.Object,
            monitor,
            Mock.Of<ILogger<UnifiedSettingsController>>());
    }

    /// <summary>A lazy iterator that throws partway through enumeration, like the real builder.</summary>
    private static IEnumerable<SettingMetadata> ThrowingMetadata()
    {
        yield return new SettingMetadata { Key = "Ok", ClassName = "OkSettings" };
        throw new InvalidOperationException("Property 'X' in settings class 'BadSettings' is missing [JsonPropertyName] attribute.");
    }

    private static IEnumerable<SettingGroupMetadata> ThrowingGroups()
    {
        yield return new SettingGroupMetadata { Key = "Ok" };
        throw new InvalidOperationException("boom");
    }

    [Fact]
    public void GetMetadata_WhenBuilderThrowsDuringEnumeration_Returns500_NotOk()
    {
        Mock<ISettingsService> settings = new();
        settings.Setup(s => s.GetAllMetadata()).Returns(ThrowingMetadata());
        UnifiedSettingsController controller = CreateController(settings);

        ActionResult<IEnumerable<SettingMetadata>> result = controller.GetMetadata();

        // Materialized inside the try => clean 500. Before the fail-fast fix the controller
        // returned Ok(lazyEnumerable) and the throw escaped during serialization (a 200 here).
        ObjectResult obj = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status500InternalServerError, obj.StatusCode);
    }

    [Fact]
    public void GetMetadata_WhenBuilderSucceeds_ReturnsOk()
    {
        Mock<ISettingsService> settings = new();
        settings.Setup(s => s.GetAllMetadata())
            .Returns(new[] { new SettingMetadata { Key = "Slicer", ClassName = "SlicerSettings" } });
        UnifiedSettingsController controller = CreateController(settings);

        ActionResult<IEnumerable<SettingMetadata>> result = controller.GetMetadata();

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public void GetGroups_WhenBuilderThrowsDuringEnumeration_Returns500_NotOk()
    {
        Mock<ISettingsService> settings = new();
        settings.Setup(s => s.GetAllGroupMetadata()).Returns(ThrowingGroups());
        UnifiedSettingsController controller = CreateController(settings);

        ActionResult<IEnumerable<SettingGroupMetadata>> result = controller.GetGroups();

        ObjectResult obj = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status500InternalServerError, obj.StatusCode);
    }
}
