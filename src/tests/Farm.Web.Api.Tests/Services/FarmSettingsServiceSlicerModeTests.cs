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
}
