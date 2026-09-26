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

    [Fact]
    public void ShippedAppSettings_BackendTimeouts_AreExpectedProductionTimeouts()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddJsonFile(
                Path.Combine(FindRepositoryRoot(), "src", "api", "appsettings.json"),
                optional: false)
            .Build();
        IConfigurationSection section = configuration.GetSection("BackendTimeouts");

        section.GetValue<int>("StatusPollTimeoutSeconds").Should().Be(10);
        section.GetValue<int>("CommandTimeoutSeconds").Should().Be(30);
        section.GetValue<int>("PrintControlTimeoutSeconds").Should().Be(60);
        section.GetValue<int>("FileUploadTimeoutSeconds").Should().Be(300);
        section.GetValue<int>("FileDownloadTimeoutSeconds").Should().Be(900);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "VERSION")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        return root.FullName;
    }
}
