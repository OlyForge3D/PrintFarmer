using System.Net;
using System.Net.Sockets;

namespace Farm.Infrastructure.Network;

/// <summary>
/// Validates URLs against SSRF (Server-Side Request Forgery) attacks by rejecting
/// loopback, link-local, and private network addresses unless explicitly allowed.
/// </summary>
public static class UrlSsrfValidator
{
    /// <summary>
    /// Validates that a URL is safe for server-side requests.
    /// </summary>
    /// <param name="url">The URL to validate.</param>
    /// <param name="allowPrivateNetworkTargets">
    /// When true, allows RFC1918 private ranges and unique-local IPv6.
    /// Loopback and link-local are ALWAYS rejected regardless of this flag.
    /// </param>
    /// <returns>Validation result indicating whether the URL is safe.</returns>
    public static UrlSsrfValidationResult Validate(string? url, bool allowPrivateNetworkTargets = false)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return UrlSsrfValidationResult.Fail("URL is required.");
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
        {
            return UrlSsrfValidationResult.Fail("URL is not a valid absolute URI.");
        }

        // Scheme check
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return UrlSsrfValidationResult.Fail("Only http and https schemes are allowed.");
        }

        string host = uri.Host;

        // Reject localhost by name
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            host.Equals("localhost.", StringComparison.OrdinalIgnoreCase))
        {
            return UrlSsrfValidationResult.Fail("Loopback addresses are not allowed.");
        }

        // Try to parse as IP directly
        if (IPAddress.TryParse(host, out IPAddress? ip))
        {
            return ValidateIpAddress(ip, allowPrivateNetworkTargets);
        }

        // For hostnames, we allow them through — DNS resolution happens at request time.
        // The hostname itself isn't dangerous; the resolved IP would be caught by HttpClient
        // message handler if needed for defense-in-depth.
        return UrlSsrfValidationResult.Ok();
    }

    private static UrlSsrfValidationResult ValidateIpAddress(IPAddress ip, bool allowPrivateNetworkTargets)
    {
        // Always reject loopback (127.0.0.0/8 for IPv4, ::1 for IPv6)
        if (IPAddress.IsLoopback(ip))
        {
            return UrlSsrfValidationResult.Fail("Loopback addresses are not allowed.");
        }

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            return ValidateIPv4(ip, allowPrivateNetworkTargets);
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return ValidateIPv6(ip, allowPrivateNetworkTargets);
        }

        return UrlSsrfValidationResult.Fail("Unsupported address family.");
    }

    private static UrlSsrfValidationResult ValidateIPv4(IPAddress ip, bool allowPrivateNetworkTargets)
    {
        byte[] bytes = ip.GetAddressBytes();

        // Link-local: 169.254.0.0/16 — always rejected
        if (bytes[0] == 169 && bytes[1] == 254)
        {
            return UrlSsrfValidationResult.Fail("Link-local addresses (169.254.0.0/16) are not allowed.");
        }

        // Private ranges — rejected unless override is set
        if (!allowPrivateNetworkTargets)
        {
            // 10.0.0.0/8
            if (bytes[0] == 10)
            {
                return UrlSsrfValidationResult.Fail(
                    "Private network address (10.0.0.0/8) is not allowed. Enable 'Allow private network targets' to permit internal addresses.");
            }

            // 172.16.0.0/12
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
            {
                return UrlSsrfValidationResult.Fail(
                    "Private network address (172.16.0.0/12) is not allowed. Enable 'Allow private network targets' to permit internal addresses.");
            }

            // 192.168.0.0/16
            if (bytes[0] == 192 && bytes[1] == 168)
            {
                return UrlSsrfValidationResult.Fail(
                    "Private network address (192.168.0.0/16) is not allowed. Enable 'Allow private network targets' to permit internal addresses.");
            }
        }

        return UrlSsrfValidationResult.Ok();
    }

    private static UrlSsrfValidationResult ValidateIPv6(IPAddress ip, bool allowPrivateNetworkTargets)
    {
        byte[] bytes = ip.GetAddressBytes();

        // Link-local: fe80::/10 — always rejected
        if (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80)
        {
            return UrlSsrfValidationResult.Fail("Link-local addresses (fe80::/10) are not allowed.");
        }

        // Unique-local: fc00::/7 — rejected unless override is set
        if (!allowPrivateNetworkTargets && (bytes[0] & 0xFE) == 0xFC)
        {
            return UrlSsrfValidationResult.Fail(
                "Unique-local IPv6 address (fc00::/7) is not allowed. Enable 'Allow private network targets' to permit internal addresses.");
        }

        return UrlSsrfValidationResult.Ok();
    }
}

/// <summary>Result of URL SSRF validation.</summary>
public sealed class UrlSsrfValidationResult
{
    public bool IsValid { get; private init; }

    public string? ErrorMessage { get; private init; }

    public static UrlSsrfValidationResult Ok() => new() { IsValid = true };

    public static UrlSsrfValidationResult Fail(string message) => new() { IsValid = false, ErrorMessage = message };
}
