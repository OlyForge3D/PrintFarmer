using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Primitives;

namespace Farm.Web.Api.Services.Discovery;

/// <summary>
/// Validates the shared key used by the printer-discovery service.
/// </summary>
public sealed class DiscoveryServiceAuthenticator(IConfiguration configuration)
{
    /// <summary>Header used for authenticated discovery event ingestion.</summary>
    public const string HeaderName = "X-Discovery-Service-Key";

    private readonly string? _sharedKey =
        configuration["DiscoveryAuth:SharedKey"]
        ?? Environment.GetEnvironmentVariable("DISCOVERY_SHARED_API_KEY");

    /// <summary>Gets whether service authentication has been configured.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(_sharedKey);

    /// <summary>Checks the request key using a fixed-time comparison.</summary>
    public bool IsAuthorized(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(_sharedKey) ||
            !request.Headers.TryGetValue(HeaderName, out StringValues values))
        {
            return false;
        }

        string presented = values.FirstOrDefault() ?? string.Empty;
        byte[] expectedBytes = Encoding.UTF8.GetBytes(_sharedKey);
        byte[] presentedBytes = Encoding.UTF8.GetBytes(presented);
        return expectedBytes.Length == presentedBytes.Length &&
               CryptographicOperations.FixedTimeEquals(expectedBytes, presentedBytes);
    }
}
