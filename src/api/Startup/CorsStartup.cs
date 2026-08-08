using System.Net;
using System.Net.Sockets;
using Farm.Infrastructure.Network;

namespace Farm.Web.Api.Startup;

/// <summary>
/// Configures CORS policies for API access.
/// </summary>
public static class CorsStartup
{
    /// <summary>
    /// Adds PrintFarmer CORS configuration.
    /// </summary>
    public static IServiceCollection AddPrintFarmerCors(this IServiceCollection services)
    {
        // CORS configuration for API access
        services.AddCors(options =>
        {
            options.AddPolicy("Default", policy =>
            {
                // Get allowed origins from environment variable or use defaults.
                string allowedOrigins = Environment.GetEnvironmentVariable("ALLOWED_ORIGINS")
                    ?? Environment.GetEnvironmentVariable("CORS__AllowedOrigins")
                    ?? "http://localhost:3000,https://localhost:3000,http://localhost:8081,https://localhost:8443,http://localhost:5000,http://localhost:5001";
                bool allowLocalNetwork = Environment.GetEnvironmentVariable("ALLOW_LOCAL_NETWORK") == "true";
                string[] configuredOrigins = allowedOrigins.Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(o => o.Trim()).ToArray();

                // This policy is paired with AllowCredentials(), so the origin predicate must
                // never reflect an unrestricted set of origins (equivalent to AllowAnyOrigin()).
                // ALLOW_LOCAL_NETWORK only widens acceptance to origins that actually resolve to
                // a private/loopback network address — it does not accept arbitrary origins.
                _ = policy.SetIsOriginAllowed(origin => IsOriginAllowed(origin, configuredOrigins, allowLocalNetwork));
                _ = policy.AllowCredentials();
                _ = policy.WithHeaders("Content-Type", "Authorization", "x-correlation-id", "traceparent", "x-signalr-user-agent", "x-requested-with");
                _ = policy.WithMethods("GET", "POST", "PUT", "PATCH", "DELETE", "OPTIONS");
            });
        });

        return services;
    }

    /// <summary>
    /// Determines whether a CORS request origin is allowed. An explicit match against the
    /// configured origin allowlist always succeeds. When <paramref name="allowLocalNetwork"/> is
    /// enabled, origins whose host is <c>localhost</c>, a private/loopback IP literal (RFC1918,
    /// 127.0.0.0/8, link-local), or a <c>.local</c> mDNS hostname that resolves only to
    /// private/loopback addresses are also accepted, so LAN deployments keep working without
    /// reflecting every possible origin.
    /// </summary>
    internal static bool IsOriginAllowed(string origin, string[] configuredOrigins, bool allowLocalNetwork)
    {
        if (configuredOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        return allowLocalNetwork && IsLocalNetworkOrigin(origin);
    }

    private static bool IsLocalNetworkOrigin(string origin)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out Uri? uri))
        {
            return false;
        }

        if (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (IPAddress.TryParse(uri.Host, out IPAddress? literalIp))
        {
            return NetworkDestinationClassifier.IsPrivateOrReserved(literalIp);
        }

        // Not an IP literal. Only resolve hostnames under the ".local" mDNS/Bonjour TLD
        // (RFC 6762), so LAN device names such as "printfarmer.local" keep working. This TLD
        // is reserved and cannot be delegated in the public DNS namespace, unlike arbitrary
        // internet domains. We deliberately do NOT resolve other hostnames: public "DNS
        // rebinding" services (e.g. "127.0.0.1.sslip.io", "127-0-0-1.nip.io") let anyone
        // register a public domain that resolves to a private/loopback address, which would
        // let an attacker-controlled page pass this check if we trusted DNS resolution alone.
        if (!uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // The browser sets the Origin header to the page's own host, so resolving it here
        // reflects the real network location of the calling page, not an attacker-supplied
        // value. Every resolved address must be private/loopback — if any address is public,
        // the origin is rejected rather than allowed on a partial match.
        try
        {
            IPAddress[] addresses = Dns.GetHostAddresses(uri.Host);
            return addresses.Length > 0 && addresses.All(NetworkDestinationClassifier.IsPrivateOrReserved);
        }
        catch (SocketException)
        {
            return false;
        }
    }
}
