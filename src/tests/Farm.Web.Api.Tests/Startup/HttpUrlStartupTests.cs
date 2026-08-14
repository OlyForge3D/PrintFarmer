using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;

namespace Farm.Web.Api.Tests.Startup;

/// <summary>
/// Regression coverage for issue #1567's container-owned HTTP binding.
/// </summary>
public sealed class HttpUrlStartupTests
{
    [Fact]
    public void ConfigureDefaultHttpUrl_WhenDeploymentUrlIsConfigured_PreservesDeploymentBinding()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = ["--urls", "http://+:5000"],
            EnvironmentName = Environments.Production,
        });
        const string configuredUrl = "http://+:5000";

        ProgramHelpers.ConfigureDefaultHttpUrl(builder);

        builder.WebHost.GetSetting(WebHostDefaults.ServerUrlsKey).Should().Be(configuredUrl);
    }

    [Fact]
    public void ConfigureDefaultHttpUrl_WhenNoUrlIsConfigured_UsesCanonicalLocalBinding()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = [],
            EnvironmentName = Environments.Production,
        });
        builder.Configuration[WebHostDefaults.ServerUrlsKey] = null;

        ProgramHelpers.ConfigureDefaultHttpUrl(builder);

        builder.WebHost.GetSetting(WebHostDefaults.ServerUrlsKey).Should().Be("http://0.0.0.0:5245");
    }

    [Fact]
    public void ConfigureDefaultHttpUrl_WhenTesting_DoesNotConfigureNetworkBinding()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = [],
            EnvironmentName = "Testing",
        });
        builder.Configuration[WebHostDefaults.ServerUrlsKey] = null;

        ProgramHelpers.ConfigureDefaultHttpUrl(builder);

        builder.WebHost.GetSetting(WebHostDefaults.ServerUrlsKey).Should().BeNull();
    }
}
