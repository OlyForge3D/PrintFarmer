using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Network;

/// <summary>
/// Result of an egress vetting check for a caller-supplied URL.
/// </summary>
public sealed record EgressCheckResult(bool IsAllowed, Uri? Uri, string? DenyReason)
{
    public static EgressCheckResult Allow(Uri uri) => new(true, uri, null);

    public static EgressCheckResult Deny(string reason, Uri? uri = null) => new(false, uri, reason);
}

/// <summary>
/// Centralized egress vetting for user-influenced outbound HTTP destinations (Obico
/// connectivity probes, camera proxying, and similar LAN-friendly integrations). Resolves the
/// destination host via DNS and rejects loopback, link-local, IPv6 unique-local, and multicast
/// addresses unless the destination is explicitly allowed via the ALLOWED_NETWORK_RANGES
/// configuration value (a comma-separated list of CIDR ranges, IP ranges, or single IPs).
/// RFC1918 private ranges are intentionally not blocked by default — this application
/// legitimately talks to LAN printer and integration hosts.
/// </summary>
public interface IEgressGuard
{
    /// <summary>
    /// Validates that <paramref name="url"/> is safe to call. Resolution failures (host does
    /// not exist) are allowed through — the real HTTP call will fail naturally rather than using
    /// DNS-resolution success/failure itself as an SSRF oracle.
    /// </summary>
    Task<EgressCheckResult> CheckAsync(string url, CancellationToken ct = default);
}

public sealed class EgressGuard(IConfiguration configuration, ILogger<EgressGuard> logger) : IEgressGuard
{
    public async Task<EgressCheckResult> CheckAsync(string url, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return EgressCheckResult.Deny("URL must be a valid HTTP or HTTPS URL");
        }

        IPAddress[] addresses;
        if (IPAddress.TryParse(uri.Host, out IPAddress? directIp))
        {
            addresses = [directIp];
        }
        else
        {
            try
            {
                addresses = await Dns.GetHostAddressesAsync(uri.Host, ct);
            }
            catch (SocketException)
            {
                // Host does not resolve at vetting time. Let the real HTTP call fail naturally
                // instead of treating DNS resolution itself as a security decision.
                return EgressCheckResult.Allow(uri);
            }

            if (addresses.Length == 0)
            {
                return EgressCheckResult.Allow(uri);
            }
        }

        string[] allowedRanges = GetAllowedRanges();

        foreach (IPAddress address in addresses)
        {
            if (NetworkDestinationClassifier.IsLoopbackLinkLocalOrMulticast(address) &&
                !IsExplicitlyAllowed(address, allowedRanges))
            {
                logger.LogWarning(
                    "Egress blocked to {Host} ({Address}): destination is loopback, link-local, or multicast and not covered by ALLOWED_NETWORK_RANGES",
                    uri.Host,
                    address);
                return EgressCheckResult.Deny(
                    "Destination resolves to a loopback, link-local, or multicast address",
                    uri);
            }
        }

        return EgressCheckResult.Allow(uri);
    }

    private string[] GetAllowedRanges()
    {
        string? raw = configuration["ALLOWED_NETWORK_RANGES"];
        return string.IsNullOrWhiteSpace(raw)
            ? []
            : raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static bool IsExplicitlyAllowed(IPAddress ip, string[] allowedRanges)
    {
        foreach (string range in allowedRanges)
        {
            if (NetworkRangeHelper.IsIpInRange(ip, range))
            {
                return true;
            }
        }

        return false;
    }
}
