using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Network;

/// <summary>
/// Result of an egress vetting check for a caller-supplied URL.
/// </summary>
/// <param name="IsAllowed">Whether the destination passed vetting and may be connected to.</param>
/// <param name="Uri">The original URI that was vetted.</param>
/// <param name="DenyReason">A human-readable reason the destination was denied, when applicable.</param>
/// <param name="ResolvedAddress">
/// The specific IP address that was actually vetted, when known. Callers that make the real
/// outbound connection MUST reuse this address (e.g. via <see cref="EgressGuard.CreatePinnedUri"/>)
/// rather than letting the hostname be re-resolved independently at connect time, or the vetting
/// decision can be bypassed by a DNS-rebinding attacker between check and connect.
/// </param>
public sealed record EgressCheckResult(bool IsAllowed, Uri? Uri, string? DenyReason, IPAddress? ResolvedAddress = null)
{
    public static EgressCheckResult Allow(Uri uri) => new(true, uri, null);

    public static EgressCheckResult Allow(Uri uri, IPAddress? resolvedAddress) => new(true, uri, null, resolvedAddress);

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
    /// not resolve, after a bounded retry) are denied — a security guard must fail closed rather
    /// than let DNS-resolution failure silently pass an unvetted destination through.
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
            addresses = await ResolveWithBoundedRetryAsync(uri.Host, ct);

            if (addresses.Length == 0)
            {
                // Host does not resolve at vetting time, even after a retry to absorb a
                // transient blip. Fail CLOSED: an egress guard that lets DNS-resolution failure
                // silently pass an unvetted destination through can be trivially defeated by an
                // attacker whose domain resolves intermittently.
                logger.LogWarning(
                    "Egress blocked to {Host}: destination hostname did not resolve",
                    uri.Host);
                return EgressCheckResult.Deny("Destination hostname could not be resolved", uri);
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

        // Pin the first vetted address so callers can reuse it for the actual outbound
        // connection (see CreatePinnedUri) instead of letting the hostname be re-resolved
        // independently at connect time, which is what makes this class of guard vulnerable to
        // TOCTOU/DNS-rebinding: the destination that gets connected to is guaranteed to be the
        // exact address that was just vetted above.
        return EgressCheckResult.Allow(uri, addresses[0]);
    }

    /// <summary>
    /// Resolves <paramref name="host"/> via DNS, retrying once immediately on failure to absorb a
    /// transient resolution blip before the caller treats it as a hard deny. Returns an empty
    /// array (never throws) when resolution fails on both attempts.
    /// </summary>
    private async Task<IPAddress[]> ResolveWithBoundedRetryAsync(string host, CancellationToken ct)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                IPAddress[] addresses = await Dns.GetHostAddressesAsync(host, ct);
                if (addresses.Length > 0)
                {
                    return addresses;
                }
            }
            catch (SocketException)
            {
                // Fall through to retry (or return empty on the final attempt).
            }
        }

        return [];
    }

    /// <summary>
    /// Rebuilds <paramref name="original"/> with <paramref name="pinnedAddress"/> as a literal
    /// host, preserving scheme/port/path/query. Consumers should use the returned URI for the
    /// actual outbound connection after a successful <see cref="CheckAsync"/>, and set the
    /// original hostname as the request's Host header (for HTTP callers) to preserve
    /// virtual-hosting/TLS SNI behavior — this guarantees the connection reuses the exact address
    /// that was vetted rather than re-resolving the hostname.
    /// </summary>
    public static Uri CreatePinnedUri(Uri original, IPAddress pinnedAddress)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(pinnedAddress);

        string host = pinnedAddress.AddressFamily == AddressFamily.InterNetworkV6
            ? $"[{pinnedAddress}]"
            : pinnedAddress.ToString();

        return new Uri($"{original.Scheme}://{host}:{original.Port}{original.PathAndQuery}");
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
