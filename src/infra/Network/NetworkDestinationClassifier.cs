using System.Net;
using System.Net.Sockets;

namespace Farm.Infrastructure.Network;

/// <summary>
/// Shared IP-address classification used by outbound egress vetting. Centralizes the
/// loopback/link-local/reserved checks so they are defined once and reused by every
/// caller that resolves a destination host before making an outbound HTTP request.
/// </summary>
public static class NetworkDestinationClassifier
{
    /// <summary>
    /// Returns <see langword="true"/> for loopback, link-local (including the
    /// 169.254.0.0/16 cloud-metadata range), IPv6 unique-local, and multicast addresses.
    /// RFC1918 private ranges (10/8, 172.16/12, 192.168/16) are intentionally NOT included:
    /// PrintFarmer legitimately talks to LAN devices, so a blanket private-IP block would
    /// break the product's core use case. Use this classifier for LAN-friendly outbound
    /// probes (Obico, camera proxy) where only loopback/link-local-class destinations are
    /// unexpected.
    /// </summary>
    public static bool IsLoopbackLinkLocalOrMulticast(IPAddress ip)
    {
        ArgumentNullException.ThrowIfNull(ip);

        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        if (IPAddress.IsLoopback(ip))
        {
            return true;
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast)
            {
                return true;
            }

            if (ip.Equals(IPAddress.IPv6Any))
            {
                return true;
            }

            // Unique-local (fc00::/7 — covers fc00::/8 and fd00::/8)
            byte firstByte = ip.GetAddressBytes()[0];
            return firstByte is 0xfc or 0xfd;
        }

        byte[] bytes = ip.GetAddressBytes();
        if (bytes.Length != 4)
        {
            return false;
        }

        // Link-local, including the 169.254.169.254 cloud metadata endpoint
        if (bytes[0] == 169 && bytes[1] == 254)
        {
            return true;
        }

        // Unspecified (0.0.0.0/8)
        if (bytes[0] == 0)
        {
            return true;
        }

        // Multicast (224.0.0.0/4) and reserved (240.0.0.0/4)
        return bytes[0] >= 224;
    }

    /// <summary>
    /// Returns <see langword="true"/> for loopback, RFC1918 private ranges, link-local, and
    /// their IPv6 equivalents. Used for outbound destinations that are expected to be public
    /// internet endpoints (e.g. webhook subscriptions), where private-network destinations are
    /// unexpected and should be rejected outright.
    /// </summary>
    public static bool IsPrivateOrReserved(IPAddress ip)
    {
        ArgumentNullException.ThrowIfNull(ip);

        if (IPAddress.IsLoopback(ip))
        {
            return true;
        }

        byte[] bytes = ip.GetAddressBytes();
        return bytes.Length switch
        {
            4 => bytes[0] == 10
                || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
                || (bytes[0] == 192 && bytes[1] == 168)
                || (bytes[0] == 169 && bytes[1] == 254)
                || bytes[0] == 0,
            16 => ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || bytes.All(b => b == 0),
            _ => false,
        };
    }
}
