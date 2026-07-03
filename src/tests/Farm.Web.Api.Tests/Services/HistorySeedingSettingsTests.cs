using Farm.Web.Api.Services.Workers;
using FluentAssertions;

namespace Farm.Web.Api.Tests.Services;

public class HistorySeedingSettingsTests
{
    [Fact]
    public void Defaults_PreserveHistorySeedingAndEnableActiveSync()
    {
        HistorySeedingSettings settings = new();

        settings.Enabled.Should().BeTrue();
        settings.IntervalMinutes.Should().Be(15);
        settings.InitialDelaySeconds.Should().Be(60);

        settings.ActiveSyncEnabled.Should().BeTrue();
        settings.ActiveSyncIntervalSeconds.Should().Be(60);
        settings.ActiveSyncInitialDelaySeconds.Should().Be(30);
    }
}
