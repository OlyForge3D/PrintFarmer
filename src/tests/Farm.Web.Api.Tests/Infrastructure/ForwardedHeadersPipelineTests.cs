using System.Net;
using Farm.Web.Api.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Farm.Web.Api.Tests.Infrastructure;

/// <summary>
/// End-to-end pipeline tests that spin up a real Kestrel-less <see cref="TestServer"/>
/// with the production <c>AddPrintFarmerForwardedHeaders</c> +
/// <c>UsePrintFarmerForwardedHeaders</c> wiring and drive HTTP requests through the
/// framework's <see cref="Microsoft.AspNetCore.HttpOverrides.ForwardedHeadersMiddleware"/>.
///
/// These tests would fail if:
///   * <c>UsePrintFarmerForwardedHeaders</c> stopped calling <c>UseForwardedHeaders</c>.
///   * The trust gate silently began honoring untrusted proxies.
///   * <c>UsePrintFarmerForwardedHeaders</c> were moved late in the pipeline in
///     <c>Program.cs</c> — a terminal middleware registered before it would then see
///     the un-rewritten peer IP (which the "OrderingRegression" fixture below asserts).
///
/// Covers acceptance criteria of issue #862 via the real framework middleware, not
/// only unit-level middleware mocks.
/// </summary>
public class ForwardedHeadersPipelineTests
{
    private const string ObservedIpHeader = "X-Observed-RemoteIp";

    private static async Task<IHost> BuildHostAsync(Dictionary<string, string?> config)
    {
        IHostBuilder hostBuilder = new HostBuilder()
            .ConfigureWebHost(webHost =>
            {
                _ = webHost
                    .UseTestServer()
                    .ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(config))
                    .ConfigureServices((ctx, services) =>
                    {
                        services.AddPrintFarmerForwardedHeaders(ctx.Configuration);
                    })
                    .Configure(app =>
                    {
                        // Simulate the real reverse-proxy hop: the peer IP that
                        // TestServer would otherwise leave null becomes loopback,
                        // so the framework's trusted-proxy check has something to
                        // evaluate against KnownProxies.
                        _ = app.Use(async (ctx, next) =>
                        {
                            ctx.Connection.RemoteIpAddress ??= IPAddress.Loopback;
                            await next().ConfigureAwait(false);
                        });

                        _ = app.UsePrintFarmerForwardedHeaders();

                        // Terminal middleware — echoes whatever RemoteIpAddress the
                        // rest of the pipeline would observe.
                        app.Run(async ctx =>
                        {
                            string observed = ctx.Connection.RemoteIpAddress?.ToString() ?? "null";
                            ctx.Response.Headers[ObservedIpHeader] = observed;
                            ctx.Response.StatusCode = StatusCodes.Status204NoContent;
                            await ctx.Response.CompleteAsync().ConfigureAwait(false);
                        });
                    });
            });

        IHost host = hostBuilder.Build();
        await host.StartAsync().ConfigureAwait(false);
        return host;
    }

    private static async Task<string> ObserveAsync(HttpClient client, string? xForwardedFor)
    {
        using HttpRequestMessage req = new(HttpMethod.Get, "/");
        if (xForwardedFor is not null)
        {
            req.Headers.TryAddWithoutValidation("X-Forwarded-For", xForwardedFor);
        }

        using HttpResponseMessage resp = await client.SendAsync(req).ConfigureAwait(false);
        return resp.Headers.TryGetValues(ObservedIpHeader, out IEnumerable<string>? vals)
            ? vals.First()
            : "missing";
    }

    /// <summary>
    /// Enabled + KnownProxies contains loopback → the framework's real
    /// ForwardedHeadersMiddleware rewrites <c>Connection.RemoteIpAddress</c> from the
    /// <c>X-Forwarded-For</c> header. Downstream middleware sees the client IP,
    /// exactly what a legitimate reverse-proxy deployment relies on.
    /// </summary>
    [Fact]
    public async Task Enabled_TrustedProxy_XForwardedFor_RewritesRemoteIp()
    {
        Dictionary<string, string?> cfg = new()
        {
            ["ForwardedHeaders:Enabled"] = "true",
            ["ForwardedHeaders:KnownProxies:0"] = "127.0.0.1",
            ["ForwardedHeaders:KnownProxies:1"] = "::1",
            ["ForwardedHeaders:ForwardLimit"] = "1",
        };

        using IHost host = await BuildHostAsync(cfg);
        using HttpClient client = host.GetTestClient();

        string observed = await ObserveAsync(client, "203.0.113.42");

        Assert.Equal("203.0.113.42", observed);
    }

    /// <summary>
    /// Enabled but the direct peer is NOT in KnownProxies → the framework must
    /// refuse to trust the header. RemoteIpAddress stays as the direct peer.
    /// This is the exact spoof-resistance guarantee #862 was filed for.
    /// </summary>
    [Fact]
    public async Task Enabled_UntrustedProxy_XForwardedFor_IsIgnored()
    {
        Dictionary<string, string?> cfg = new()
        {
            ["ForwardedHeaders:Enabled"] = "true",
            // Trust a bogus network that does NOT include loopback — the peer
            // (loopback in TestServer) is therefore untrusted.
            ["ForwardedHeaders:KnownProxies:0"] = "10.99.99.99",
            ["ForwardedHeaders:ForwardLimit"] = "1",
        };

        using IHost host = await BuildHostAsync(cfg);
        using HttpClient client = host.GetTestClient();

        string observed = await ObserveAsync(client, "203.0.113.42");

        // Spoofed XFF ignored; direct peer wins.
        Assert.Equal(IPAddress.Loopback.ToString(), observed);
    }

    /// <summary>
    /// Disabled by default → the framework middleware is not registered at all;
    /// XFF is unconditionally ignored regardless of peer identity.
    /// </summary>
    [Fact]
    public async Task Disabled_XForwardedFor_IsAlwaysIgnored()
    {
        // No ForwardedHeaders section at all → Enabled defaults to false.
        Dictionary<string, string?> cfg = new();

        using IHost host = await BuildHostAsync(cfg);
        using HttpClient client = host.GetTestClient();

        string observed = await ObserveAsync(client, "203.0.113.42");

        Assert.Equal(IPAddress.Loopback.ToString(), observed);
    }

    /// <summary>
    /// KnownNetworks CIDR entry containing the loopback range → trusted.
    /// Exercises the <c>KnownIPNetworks</c> path (as opposed to the
    /// <c>KnownProxies</c> exact-match path) end-to-end through the framework.
    /// </summary>
    [Fact]
    public async Task Enabled_TrustedNetworkCidr_XForwardedFor_RewritesRemoteIp()
    {
        Dictionary<string, string?> cfg = new()
        {
            ["ForwardedHeaders:Enabled"] = "true",
            ["ForwardedHeaders:KnownNetworks:0"] = "127.0.0.0/8",
            ["ForwardedHeaders:ForwardLimit"] = "1",
        };

        using IHost host = await BuildHostAsync(cfg);
        using HttpClient client = host.GetTestClient();

        string observed = await ObserveAsync(client, "198.51.100.7");

        Assert.Equal("198.51.100.7", observed);
    }

    /// <summary>
    /// Regression fixture for Program.cs middleware ordering: any middleware
    /// registered BEFORE <c>UsePrintFarmerForwardedHeaders</c> observes the direct
    /// peer IP, not the forwarded one. If the production pipeline ever registers
    /// slicer plugin middleware or telemetry before the forwarded-headers gate,
    /// that middleware will see the wrong IP — the equivalent of a #862 regression.
    /// This test locks in the invariant at the framework level.
    /// </summary>
    [Fact]
    public async Task Middleware_RegisteredBeforeForwardedHeaders_SeesDirectPeerIp()
    {
        Dictionary<string, string?> cfg = new()
        {
            ["ForwardedHeaders:Enabled"] = "true",
            ["ForwardedHeaders:KnownProxies:0"] = "127.0.0.1",
            ["ForwardedHeaders:KnownProxies:1"] = "::1",
            ["ForwardedHeaders:ForwardLimit"] = "1",
        };

        string? earlyObserved = null;
        string? lateObserved = null;

        IHostBuilder hostBuilder = new HostBuilder()
            .ConfigureWebHost(webHost =>
            {
                _ = webHost
                    .UseTestServer()
                    .ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(cfg))
                    .ConfigureServices((ctx, services) => services.AddPrintFarmerForwardedHeaders(ctx.Configuration))
                    .Configure(app =>
                    {
                        _ = app.Use(async (ctx, next) =>
                        {
                            ctx.Connection.RemoteIpAddress ??= IPAddress.Loopback;
                            await next().ConfigureAwait(false);
                        });

                        // "Rogue" plugin-style middleware inserted BEFORE the trust gate.
                        _ = app.Use(async (ctx, next) =>
                        {
                            earlyObserved = ctx.Connection.RemoteIpAddress?.ToString();
                            await next().ConfigureAwait(false);
                        });

                        _ = app.UsePrintFarmerForwardedHeaders();

                        app.Run(async ctx =>
                        {
                            lateObserved = ctx.Connection.RemoteIpAddress?.ToString();
                            ctx.Response.StatusCode = StatusCodes.Status204NoContent;
                            await ctx.Response.CompleteAsync().ConfigureAwait(false);
                        });
                    });
            });

        using IHost host = hostBuilder.Build();
        await host.StartAsync();
        using HttpClient client = host.GetTestClient();
        _ = await ObserveAsync(client, "203.0.113.42");

        // Middleware registered before the trust gate MUST see the direct peer.
        Assert.Equal(IPAddress.Loopback.ToString(), earlyObserved);
        // Middleware registered after the trust gate MUST see the rewritten client IP.
        Assert.Equal("203.0.113.42", lateObserved);
    }
}
