using Farm.Infrastructure.Settings;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Farm.Infrastructure.Tests.Settings;

public class BackendTimeoutSettingsTests
{
    [Fact]
    public void Defaults_AreExpectedProductionTimeouts()
    {
        var settings = new BackendTimeoutSettings();

        settings.StatusPollTimeoutSeconds.Should().Be(10);
        settings.CommandTimeoutSeconds.Should().Be(30);
        settings.PrintControlTimeoutSeconds.Should().Be(60);
        settings.FileUploadTimeoutSeconds.Should().Be(300);
        settings.FileDownloadTimeoutSeconds.Should().Be(900);
    }

    [Fact]
    public void Bind_BackendTimeoutsSection_OverridesFileUploadDefault()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BackendTimeouts:FileUploadTimeoutSeconds"] = "347",
            })
            .Build();
        var settings = new BackendTimeoutSettings();

        configuration.GetSection(BackendTimeoutSettings.SectionName).Bind(settings);

        settings.FileUploadTimeoutSeconds.Should().Be(347);
    }
}
