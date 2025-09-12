using Farm.Web.Api.Controllers.Slicing;
using Farm.Web.Api.Services.SlicerServices;
using Farm.Web.Shared;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Farm.Web.Api.Tests.Slicing;

public class SlicerSettingsControllerTests
{
    private class FakeSettingsService : ISlicerSettingsService
    {
        public SlicerSettingsDto? Saved;
        public SlicerSettingsDto GetSettings() => new SlicerSettingsDto(true, new Dictionary<SlicerEngineType, PerEngineSlicerSetting>(), 15.0);
        public void SaveSettings(SlicerSettingsDto settings) { Saved = settings; }
    }

    [Fact]
    public void Save_InvalidJitter_ReturnsBadRequest()
    {
        var svc = new FakeSettingsService();
        var controller = new SlicerSettingsController(svc);
        var dto = new SlicerSettingsDto(true, new Dictionary<SlicerEngineType, PerEngineSlicerSetting>(), -5.0);

        var result = controller.Save(dto);

        Assert.IsType<BadRequestObjectResult>(result);
        var bad = result as BadRequestObjectResult;
        Assert.Contains("JitterPercent must be between 0 and 100", bad?.Value?.ToString() ?? string.Empty);
        Assert.Null(svc.Saved);
    }

    [Fact]
    public void Save_ValidJitter_CallsSaveAndReturnsNoContent()
    {
        var svc = new FakeSettingsService();
        var controller = new SlicerSettingsController(svc);
        var dto = new SlicerSettingsDto(true, new Dictionary<SlicerEngineType, PerEngineSlicerSetting>(), 12.5);

        var result = controller.Save(dto);

        Assert.IsType<NoContentResult>(result);
        Assert.NotNull(svc.Saved);
        Assert.Equal(12.5, svc.Saved!.JitterPercent);
    }
}
