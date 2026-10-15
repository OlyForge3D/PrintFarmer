using Farm.Infrastructure.Settings;
using Farm.Web.Api.Startup;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services.SystemInfo;

public sealed class SystemInfoRegistrationTests
{
    [Fact]
    public void Startup_SimplifiedStatus_PreservesReleaseCheckerWithoutVerifiedDiscovery()
    {
        ServiceCollection services = new();
        IConfiguration config = new ConfigurationBuilder().Build();
        Mock<IWebHostEnvironment> environment = new();
        environment.SetupGet(value => value.EnvironmentName).Returns(Environments.Production);
        services.AddPrintFarmerFeatureServices(config, environment.Object);
        services.AddPrintFarmerBackgroundServices(config);

        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IHostedService)
            && descriptor.ImplementationType?.Name == "ApplicationReleaseUpdateCheckService");
        Assert.Contains(services, descriptor => descriptor.ServiceType.Name == "IApplicationReleaseSource");
        Assert.Contains(services, descriptor => descriptor.ServiceType.Name == "IApplicationReleaseUpdateStatusProvider");
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType.Namespace == "Farm.Infrastructure.Services.HostUpdates"
            || descriptor.ImplementationType?.Namespace == "Farm.Infrastructure.Services.HostUpdates");
        Assert.DoesNotContain(typeof(IAppSetting).Assembly.GetTypes(), type => type.Name == "UpdateChannelSettings");
    }
}
