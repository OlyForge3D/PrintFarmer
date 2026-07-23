using System.Net;
using Farm.Infrastructure.Network;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Farm.Web.IntegrationTests;

[Collection("Sequential")]
public sealed class CustomWebApplicationFactoryTests
{
    private static readonly string[] ForwardedHeadersEnvironmentVariables =
    [
        "ForwardedHeaders__Enabled",
        "ForwardedHeaders__KnownProxies__0",
        "ForwardedHeaders__KnownProxies__1",
        "ForwardedHeaders__ForwardLimit",
    ];

    [Fact]
    public void Constructor_WhenFactoriesAreCreatedInParallel_DoesNotMutateProcessConfiguration()
    {
        Dictionary<string, string?> before = SnapshotForwardedHeadersEnvironment();

        Parallel.For(0, 8, _ =>
        {
            using CustomWebApplicationFactory factory = new();
        });

        AssertForwardedHeadersEnvironmentUnchanged(before);
    }

    [Fact]
    public void Services_WhenHostStarts_BindsTrustedProxyConfigurationWithoutEnvironmentMutation()
    {
        Dictionary<string, string?> before = SnapshotForwardedHeadersEnvironment();
        using CustomWebApplicationFactory factory = new();

        ForwardedHeadersSettings settings =
            factory.Services.GetRequiredService<IOptions<ForwardedHeadersSettings>>().Value;
        ForwardedHeadersOptions options =
            factory.Services.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;

        Assert.True(settings.Enabled);
        Assert.Equal(1, settings.ForwardLimit);
        Assert.Equal(new[] { IPAddress.Loopback, IPAddress.IPv6Loopback }, options.KnownProxies);
        Assert.True(options.ForwardedHeaders.HasFlag(ForwardedHeaders.XForwardedFor));
        AssertForwardedHeadersEnvironmentUnchanged(before);
    }

    private static Dictionary<string, string?> SnapshotForwardedHeadersEnvironment()
        => ForwardedHeadersEnvironmentVariables.ToDictionary(
            key => key,
            Environment.GetEnvironmentVariable);

    private static void AssertForwardedHeadersEnvironmentUnchanged(
        IReadOnlyDictionary<string, string?> expected)
    {
        foreach (string key in ForwardedHeadersEnvironmentVariables)
        {
            Assert.Equal(expected[key], Environment.GetEnvironmentVariable(key));
        }
    }
}
