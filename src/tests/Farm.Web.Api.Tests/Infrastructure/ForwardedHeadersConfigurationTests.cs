using System.Net;
using Farm.Infrastructure.Network;
using Farm.Web.Api.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Farm.Web.Api.Tests.Infrastructure;

/// <summary>
/// Verifies that <see cref="ForwardedHeadersConfiguration"/> binds
/// <see cref="ForwardedHeadersSettings"/> from configuration and translates it
/// into <see cref="ForwardedHeadersOptions"/> only when explicitly enabled,
/// protecting against silent X-Forwarded-For trust (issue #862).
/// </summary>
public class ForwardedHeadersConfigurationTests
{
    private static IConfiguration BuildConfig(Dictionary<string, string?> values)
        => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Fact]
    public void Disabled_ByDefault_DoesNotRegisterForwardedHeadersOptions()
    {
        IConfiguration config = BuildConfig(new Dictionary<string, string?>());
        ServiceCollection services = new();

        services.AddPrintFarmerForwardedHeaders(config);
        ServiceProvider provider = services.BuildServiceProvider();

        // ForwardedHeadersSettings is still bound (so consumers can read it),
        // but nothing should have configured ForwardedHeadersOptions.
        ForwardedHeadersSettings settings = provider.GetRequiredService<IOptions<ForwardedHeadersSettings>>().Value;
        Assert.False(settings.Enabled);

        ForwardedHeadersOptions options = provider.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;
        Assert.Equal(ForwardedHeaders.None, options.ForwardedHeaders);
    }

    [Fact]
    public void Enabled_WithKnownProxy_ClearsFrameworkDefaultsAndTrustsOnlyDeclaredProxy()
    {
        IConfiguration config = BuildConfig(new Dictionary<string, string?>
        {
            ["ForwardedHeaders:Enabled"] = "true",
            ["ForwardedHeaders:KnownProxies:0"] = "192.0.2.10",
            ["ForwardedHeaders:ForwardLimit"] = "2",
        });

        ServiceCollection services = new();
        services.AddPrintFarmerForwardedHeaders(config);
        ServiceProvider provider = services.BuildServiceProvider();

        ForwardedHeadersOptions options = provider.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;

        Assert.True(options.ForwardedHeaders.HasFlag(ForwardedHeaders.XForwardedFor));
        Assert.True(options.ForwardedHeaders.HasFlag(ForwardedHeaders.XForwardedProto));
        Assert.True(options.ForwardedHeaders.HasFlag(ForwardedHeaders.XForwardedHost));
        Assert.Equal(2, options.ForwardLimit);

        // Loopback default must be gone; only the declared proxy is trusted.
        Assert.Single(options.KnownProxies);
        Assert.Equal(IPAddress.Parse("192.0.2.10"), options.KnownProxies[0]);
        Assert.Empty(options.KnownIPNetworks);
    }

    [Fact]
    public void Enabled_WithKnownNetwork_ParsesCidrAndPopulatesKnownIPNetworks()
    {
        IConfiguration config = BuildConfig(new Dictionary<string, string?>
        {
            ["ForwardedHeaders:Enabled"] = "true",
            ["ForwardedHeaders:KnownNetworks:0"] = "10.0.0.0/8",
        });

        ServiceCollection services = new();
        services.AddPrintFarmerForwardedHeaders(config);
        ServiceProvider provider = services.BuildServiceProvider();

        ForwardedHeadersOptions options = provider.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;

        Assert.Empty(options.KnownProxies);
        Assert.Single(options.KnownIPNetworks);
        Assert.Equal(IPAddress.Parse("10.0.0.0"), options.KnownIPNetworks[0].BaseAddress);
        Assert.Equal(8, options.KnownIPNetworks[0].PrefixLength);
    }

    [Fact]
    public void Enabled_WithInvalidEntries_SkipsThemWithoutThrowing()
    {
        IConfiguration config = BuildConfig(new Dictionary<string, string?>
        {
            ["ForwardedHeaders:Enabled"] = "true",
            ["ForwardedHeaders:KnownProxies:0"] = "not-an-ip",
            ["ForwardedHeaders:KnownProxies:1"] = "192.0.2.10",
            ["ForwardedHeaders:KnownNetworks:0"] = "not-a-cidr",
            ["ForwardedHeaders:KnownNetworks:1"] = "10.0.0.0/8",
        });

        ServiceCollection services = new();
        services.AddPrintFarmerForwardedHeaders(config);
        ServiceProvider provider = services.BuildServiceProvider();

        ForwardedHeadersOptions options = provider.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;

        Assert.Single(options.KnownProxies);
        Assert.Equal(IPAddress.Parse("192.0.2.10"), options.KnownProxies[0]);
        Assert.Single(options.KnownIPNetworks);
    }

    [Fact]
    public void ForwardLimit_LessThanOne_IsCoercedToOne()
    {
        IConfiguration config = BuildConfig(new Dictionary<string, string?>
        {
            ["ForwardedHeaders:Enabled"] = "true",
            ["ForwardedHeaders:KnownProxies:0"] = "192.0.2.10",
            ["ForwardedHeaders:ForwardLimit"] = "0",
        });

        ServiceCollection services = new();
        services.AddPrintFarmerForwardedHeaders(config);
        ServiceProvider provider = services.BuildServiceProvider();

        ForwardedHeadersOptions options = provider.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;
        Assert.Equal(1, options.ForwardLimit);
    }
}
