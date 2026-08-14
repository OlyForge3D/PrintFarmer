using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;

namespace Farm.Web.Api.Tests.Startup;

public sealed class HttpUrlStartupTests
{
    [Fact]
    public void ConfigureDefaultHttpUrl_WhenDeploymentUrlIsConfigured_PreservesDeploymentBinding()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Production,
        });
        const string configuredUrl = "http://+:5000";
        _ = builder.WebHost.UseUrls(configuredUrl);

        ProgramHelpers.ConfigureDefaultHttpUrl(builder);

        builder.WebHost.GetSetting(WebHostDefaults.ServerUrlsKey).Should().Be(configuredUrl);
    }

    [Fact]
    public void ConfigureDefaultHttpUrl_WhenNoUrlIsConfigured_UsesCanonicalLocalBinding()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Production,
        });

        ProgramHelpers.ConfigureDefaultHttpUrl(builder);

        builder.WebHost.GetSetting(WebHostDefaults.ServerUrlsKey).Should().Be("http://0.0.0.0:5245");
    }
}
