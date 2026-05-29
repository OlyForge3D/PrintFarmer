using System.Net;
using System.Net.Sockets;

namespace Farm.Infrastructure.Services.Cameras;

/// <summary>
/// Validates camera URLs to prevent SSRF attacks before probing or fetching.
/// Private network IPs (10.x, 192.168.x, 172.16-31.x) are allowed since this is
/// a local network printer management application.
/// </summary>
internal static class CameraUrlValidator
{
    /// <summary>
    /// Returns <see langword="true"/> if <paramref name="url"/> is safe to probe:
    /// scheme is HTTP, HTTPS, or RTSP; host is not loopback or link-local.
    /// </summary>
    public static bool IsUrlSafeForProbing(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
        {
            return false;
        }

        // Block schemes other than HTTP(S) and RTSP
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps
            && !uri.Scheme.Equals("rtsp", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string host = uri.Host;

        // Block loopback hostnames
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(host, "127.0.0.1", StringComparison.Ordinal) ||
            string.Equals(host, "::1", StringComparison.Ordinal) ||
            string.Equals(host, "[::1]", StringComparison.Ordinal))
        {
            return false;
        }

        if (IPAddress.TryParse(host, out IPAddress? ip))
        {
            // Unwrap IPv4-mapped IPv6 addresses (e.g. ::ffff:127.0.0.1) and
            // treat them as plain IPv4 so the loopback/link-local checks below apply.
            if (ip.IsIPv4MappedToIPv6)
            {
                ip = ip.MapToIPv4();
            }

            if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            {
                // Loopback (::1)
                if (IPAddress.IsLoopback(ip))
                {
                    return false;
                }

                // Link-local (fe80::/10)
                if (ip.IsIPv6LinkLocal)
                {
                    return false;
                }

                // Site-local (fec0::/10, deprecated but still must be blocked)
                if (ip.IsIPv6SiteLocal)
                {
                    return false;
                }

                // Multicast (ff00::/8)
                if (ip.IsIPv6Multicast)
                {
                    return false;
                }

                // Unspecified (::)
                if (ip.Equals(IPAddress.IPv6Any))
                {
                    return false;
                }

                // Unique-local (fc00::/7 — covers fc00::/8 and fd00::/8)
                byte firstByte = ip.GetAddressBytes()[0];
                if (firstByte == 0xfc || firstByte == 0xfd)
                {
                    return false;
                }

                return true;
            }

            // IPv4 checks
            byte[] bytes = ip.GetAddressBytes();

            // Block IPv4 loopback range (127.0.0.0/8)
            if (bytes[0] == 127)
            {
                return false;
            }

            // Block link-local (169.254.x.x — cloud metadata endpoint range)
            if (bytes[0] == 169 && bytes[1] == 254)
            {
                return false;
            }

            // Private IPs (10.x, 192.168.x, 172.16-31.x) are ALLOWED
        }

        return true;
    }
}
