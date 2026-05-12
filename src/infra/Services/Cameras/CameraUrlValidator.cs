using System.Net;

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
            byte[] bytes = ip.GetAddressBytes();

            // Block IPv4 loopback range (127.0.0.0/8)
            if (bytes.Length == 4 && bytes[0] == 127)
            {
                return false;
            }

            // Block link-local (169.254.x.x — cloud metadata endpoint range)
            if (bytes.Length == 4 && bytes[0] == 169 && bytes[1] == 254)
            {
                return false;
            }

            // Private IPs (10.x, 192.168.x, 172.16-31.x) are ALLOWED
        }

        return true;
    }
}
