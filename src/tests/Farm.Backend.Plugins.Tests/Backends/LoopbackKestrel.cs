using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Farm.Backend.Plugins.Tests.Backends;

/// <summary>
/// Binds test Kestrel servers to an OS-assigned port on IPv4 loopback only (#3025).
/// Kestrel reserves the port itself, so no probe-then-release window exists and
/// nothing binds <c>[::1]</c>, which a <c>localhost</c> listener would also claim.
/// </summary>
internal static class LoopbackKestrel
{
    public static void ListenOnEphemeralLoopbackPort(this WebApplicationBuilder builder) =>
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));

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
