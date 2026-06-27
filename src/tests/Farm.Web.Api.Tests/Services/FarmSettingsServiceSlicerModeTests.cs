using Farm.Infrastructure.Data;
using Farm.Infrastructure.Services;
using Farm.Infrastructure.Settings;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services;

public class FarmSettingsServiceSlicerModeTests
{
    private readonly Mock<ISettingsService> _settingsMock = new();
    private readonly IDbContextFactory<AppDbContext> _dbFactory;

    public FarmSettingsServiceSlicerModeTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        var dbFactoryMock = new Mock<IDbContextFactory<AppDbContext>>();
        dbFactoryMock.Setup(f => f.CreateDbContext()).Returns(new AppDbContext(options));
        _dbFactory = dbFactoryMock.Object;
    }

    [Fact]
    public void SlicerSettings_DefaultValue_IsSimple()
    {
        var settings = new SlicerSettings();

        Assert.Equal(SlicerMode.Simple, settings.SlicerMode);
    }

    [Fact]
    public void GetFarmSettings_ReturnsSlicerModeFromSettings()
    {
        _settingsMock
            .Setup(s => s.Get<CostTrackingSettings>())
            .Returns(new CostTrackingSettings());

        _settingsMock
            .Setup(s => s.Get<SlicerSettings>())
            .Returns(new SlicerSettings { SlicerMode = SlicerMode.Advanced });

        var service = new FarmSettingsService(_settingsMock.Object, _dbFactory);

        FarmSettingsDto result = service.GetFarmSettings();

        Assert.Equal(SlicerMode.Advanced, result.SlicerMode);
    }

    [Fact]
    public void UpdateFarmSettings_WithSlicerMode_SavesSlicerSettings()
    {
        var capturedSlicer = new SlicerSettings { SlicerMode = SlicerMode.Advanced };

        _settingsMock
            .Setup(s => s.Get<CostTrackingSettings>())
            .Returns(new CostTrackingSettings());

        _settingsMock
            .Setup(s => s.Get<SlicerSettings>())
            .Returns(capturedSlicer);

        _settingsMock.Setup(s => s.Save(It.IsAny<CostTrackingSettings>()));
        _settingsMock.Setup(s => s.Save(It.IsAny<SlicerSettings>()));

        var service = new FarmSettingsService(_settingsMock.Object, _dbFactory);
        var request = new UpdateFarmSettingsRequest(null, null, null, SlicerMode: SlicerMode.Simple);

        service.UpdateFarmSettings(request);

        _settingsMock.Verify(
            s => s.Save(It.Is<SlicerSettings>(ss => ss.SlicerMode == SlicerMode.Simple)),
            Times.Once);
    }

    [Fact]
    public void GetEnabledModes_WhenUnset_FallsBackToSlicerMode()
    {
        var settings = new SlicerSettings { SlicerMode = SlicerMode.Advanced };

        Assert.Equal(new[] { SlicerMode.Advanced }, settings.EffectiveEnabledModes);
    }

    [Fact]
    public void GetEnabledModes_WhenSet_ReturnsConfiguredModes()
    {
        var settings = new SlicerSettings
        {
            SlicerMode = SlicerMode.Simple,
            EnabledModes = new[] { SlicerMode.Simple, SlicerMode.Advanced },
        };

        Assert.Equal(new[] { SlicerMode.Simple, SlicerMode.Advanced }, settings.EffectiveEnabledModes);
    }

    [Fact]
    public void GetFarmSettings_ReturnsEnabledModes_FallingBackToSlicerModeForLegacySettings()
    {
        _settingsMock
            .Setup(s => s.Get<CostTrackingSettings>())
            .Returns(new CostTrackingSettings());

        // Legacy settings: EnabledModes never persisted.
        _settingsMock
            .Setup(s => s.Get<SlicerSettings>())
            .Returns(new SlicerSettings { SlicerMode = SlicerMode.Advanced });

        var service = new FarmSettingsService(_settingsMock.Object, _dbFactory);

        FarmSettingsDto result = service.GetFarmSettings();

        Assert.Equal(new[] { SlicerMode.Advanced }, result.EnabledModes);
    }

    [Fact]
    public void UpdateFarmSettings_WithEnabledModes_SavesDistinctModesAndKeepsDefaultInSet()
    {
        var capturedSlicer = new SlicerSettings { SlicerMode = SlicerMode.Simple };

        _settingsMock.Setup(s => s.Get<CostTrackingSettings>()).Returns(new CostTrackingSettings());
        _settingsMock.Setup(s => s.Get<SlicerSettings>()).Returns(capturedSlicer);
        _settingsMock.Setup(s => s.Save(It.IsAny<SlicerSettings>()));

        var service = new FarmSettingsService(_settingsMock.Object, _dbFactory);
        var request = new UpdateFarmSettingsRequest(
            null, null, null,
            EnabledModes: new[] { SlicerMode.Simple, SlicerMode.Advanced, SlicerMode.Simple });

        service.UpdateFarmSettings(request);

        _settingsMock.Verify(
            s => s.Save(It.Is<SlicerSettings>(ss =>
                ss.EnabledModes != null
                && ss.EnabledModes.Count == 2
                && ss.EnabledModes.Contains(ss.SlicerMode))),
            Times.Once);
    }

    [Fact]
    public void UpdateFarmSettings_WhenDefaultModeNotEnabled_ClampsDefaultToFirstEnabled()
    {
        var capturedSlicer = new SlicerSettings { SlicerMode = SlicerMode.Advanced };

        _settingsMock.Setup(s => s.Get<CostTrackingSettings>()).Returns(new CostTrackingSettings());
        _settingsMock.Setup(s => s.Get<SlicerSettings>()).Returns(capturedSlicer);
        _settingsMock.Setup(s => s.Save(It.IsAny<SlicerSettings>()));

        var service = new FarmSettingsService(_settingsMock.Object, _dbFactory);
        // Enable only Simple while the prior default was Advanced.
        var request = new UpdateFarmSettingsRequest(null, null, null, EnabledModes: new[] { SlicerMode.Simple });

        service.UpdateFarmSettings(request);

        _settingsMock.Verify(
            s => s.Save(It.Is<SlicerSettings>(ss => ss.SlicerMode == SlicerMode.Simple)),
            Times.Once);
    }

    [Fact]
    public void UpdateFarmSettings_WithEmptyEnabledModes_Throws()
    {
        var capturedSlicer = new SlicerSettings();

        _settingsMock.Setup(s => s.Get<CostTrackingSettings>()).Returns(new CostTrackingSettings());
        _settingsMock.Setup(s => s.Get<SlicerSettings>()).Returns(capturedSlicer);

        var service = new FarmSettingsService(_settingsMock.Object, _dbFactory);
        var request = new UpdateFarmSettingsRequest(null, null, null, EnabledModes: Array.Empty<SlicerMode>());

        Assert.Throws<System.ComponentModel.DataAnnotations.ValidationException>(
            () => service.UpdateFarmSettings(request));
    }

    [Fact]
    public void SlicerSettings_Validate_RejectsDefaultNotInEnabledModes()
    {
        var settings = new SlicerSettings
        {
            SlicerMode = SlicerMode.Advanced,
            EnabledModes = new[] { SlicerMode.Simple },
        };

        Assert.Throws<System.ComponentModel.DataAnnotations.ValidationException>(() => settings.Validate());
    }
}
