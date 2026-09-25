using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Farm.Backend.Plugins.Tests.Backends;

/// <summary>
/// Binds test Kestrel servers to an OS-assigned port on IPv4 loopback only (#3025).
/// Kestrel reserves the port itself, so no probe-then-release window exists and
/// nothing binds <c>[::1]</c>, which a <c>localhost</c> listener would also claim.
/// </summary>
internal static class LoopbackKestrel
{
    /// <summary>
    /// Makes 127.0.0.1:0 the only listener. Ambient <c>Kestrel:Endpoints</c> configuration is
    /// additive to code-defined endpoints, so it is replaced with an empty section; explicit
    /// <c>Listen</c> already overrides <c>URLS</c>/<c>ASPNETCORE_URLS</c>.
    /// </summary>
    public static void ListenOnEphemeralLoopbackPort(this WebApplicationBuilder builder) =>
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Configure(new ConfigurationBuilder().Build());
            options.Listen(IPAddress.Loopback, 0);
        });

    /// <summary>
    /// Starts the app and builds the caller's environment from its base URL. If startup, address
    /// discovery, or construction throws, the app is stopped and disposed so no listener leaks.
    /// </summary>
    public static async Task<T> StartOnLoopbackAsync<T>(
        this WebApplication app,
        Func<string, T> createEnvironment)
    {
        try
        {
            await app.StartAsync();
            return createEnvironment(app.GetLoopbackBaseUrl());
        }
        catch
        {
            try
            {
                await app.StopAsync();
                await app.DisposeAsync();
            }
            catch
            {
                // Preserve the original failure.
            }

            throw;
        }
    }

    /// <summary>
    /// Returns the base URL of the started server, read from <see cref="IServerAddressesFeature"/>.
    /// </summary>
    public static string GetLoopbackBaseUrl(this WebApplication app)
    {
        IServerAddressesFeature addresses =
            app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("Kestrel did not expose IServerAddressesFeature.");

        Uri bound = new(addresses.Addresses.Single());
        if (!IPAddress.TryParse(bound.Host, out IPAddress? host) || !host.Equals(IPAddress.Loopback) || bound.Port <= 0)
        {
            throw new InvalidOperationException($"Unexpected Kestrel address '{bound}'; call StartAsync first.");
        }

        return $"http://{IPAddress.Loopback}:{bound.Port}";
    }
}
