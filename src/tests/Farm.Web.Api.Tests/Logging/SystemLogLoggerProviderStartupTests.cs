using Farm.Infrastructure.Logging;
using Farm.Infrastructure.Services.Startup;
using Farm.Infrastructure.Settings;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace Farm.Web.Api.Tests.Logging;

public sealed class SystemLogLoggerProviderStartupTests
{
    [Fact]
    public void LogWarning_BeforeDatabaseReady_DoesNotResolveDatabaseBackedSettings()
    {
        var startupStatus = new StartupStatus();

        int settingsResolutionCount = 0;
        var settingsService = new Mock<ISettingsService>();
        _ = settingsService
            .Setup(service => service.Get<SystemLogSettings>())
            .Returns(new SystemLogSettings());

        var services = new ServiceCollection();
        _ = services.AddHttpContextAccessor();
        _ = services.AddScoped<ISettingsService>(_ =>
        {
            settingsResolutionCount++;
            return settingsService.Object;
        });

        using ServiceProvider serviceProvider = services.BuildServiceProvider();
        using var provider = new SystemLogLoggerProvider(
            serviceProvider,
            startupStatus,
            LogLevel.Warning);
        ILogger logger = provider.CreateLogger("Startup");

        logger.LogWarning("Warning emitted before schema initialization");

        settingsResolutionCount.Should().Be(
            0,
            "database-backed settings must not be resolved before migrations complete and the host starts");

        startupStatus.MarkReady();
        logger.LogWarning("Warning emitted after schema initialization");

        settingsResolutionCount.Should().Be(1);
    }
}
