using System.Security.Cryptography;
using System.Text;
using Farm.Infrastructure;
using Farm.Slicer.Module.Services.Configuration;
using Microsoft.Extensions.Primitives;

namespace Farm.Web.Api.Services.SlicerHost;

/// <summary>
/// Validates the shared key used by the standalone slicer host for internal lookups.
/// </summary>
public sealed class SlicerHostServiceAuthenticator(IConfiguration configuration)
{
    private readonly string? _sharedKey =
        WorkerAuthConfiguration.ResolveSharedKey(configuration)?.Value;

    /// <summary>Gets whether service authentication has been configured.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(_sharedKey);

    /// <summary>Checks the request key using a fixed-time comparison.</summary>
    /// <param name="request">Incoming internal lookup request.</param>
    /// <returns><see langword="true"/> when the configured and presented keys match.</returns>
    public bool IsAuthorized(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(_sharedKey)
            || !request.Headers.TryGetValue(
                SlicerHostLookupContract.ApiKeyHeaderName,
                out StringValues values)
            || values.Count != 1)
        {
            return false;
        }

        string presented = values[0] ?? string.Empty;
        byte[] expectedBytes = Encoding.UTF8.GetBytes(_sharedKey);
        byte[] presentedBytes = Encoding.UTF8.GetBytes(presented);
        return expectedBytes.Length == presentedBytes.Length
            && CryptographicOperations.FixedTimeEquals(expectedBytes, presentedBytes);
    }
}
