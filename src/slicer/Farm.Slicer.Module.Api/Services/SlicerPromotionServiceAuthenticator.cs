using System.Security.Cryptography;
using System.Text;
using Farm.Infrastructure;
using Microsoft.Extensions.Primitives;

namespace Farm.Slicer.Module.Api.Services;

/// <summary>Validates the dedicated shared key used to retrieve pinned promotion content.</summary>
public sealed class SlicerPromotionServiceAuthenticator(IConfiguration configuration)
{
    private readonly string? _sharedKey = configuration[SlicerPromotionContract.SharedKeyPath];

    /// <summary>Gets whether the dedicated service credential is configured.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(_sharedKey);

    /// <summary>Checks the request credential using a fixed-time comparison.</summary>
    /// <param name="request">Incoming internal request.</param>
    /// <returns><see langword="true"/> only for one matching credential value.</returns>
    public bool IsAuthorized(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(_sharedKey) ||
            !request.Headers.TryGetValue(SlicerPromotionContract.ApiKeyHeaderName, out StringValues values) ||
            values.Count != 1)
        {
            return false;
        }

        byte[] expected = Encoding.UTF8.GetBytes(_sharedKey);
        byte[] presented = Encoding.UTF8.GetBytes(values[0] ?? string.Empty);
        return expected.Length == presented.Length &&
            CryptographicOperations.FixedTimeEquals(expected, presented);
    }
}
