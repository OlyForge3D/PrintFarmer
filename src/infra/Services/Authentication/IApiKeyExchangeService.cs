using System.Threading;
using System.Threading.Tasks;

namespace Farm.Infrastructure.Services.Authentication;

/// <summary>
/// Exchanges a Desktop-purpose <see cref="Domain.ApiKey"/> for a short-lived, minimally
/// scoped JWT that both the main API and the slicer host can validate (issue #838).
/// </summary>
public interface IApiKeyExchangeService
{
    /// <summary>
    /// Validates the given raw API key and, if it is an active, unexpired, Desktop-purpose
    /// key with at least one granted scope and an active owner, issues a short-lived JWT
    /// carrying only the key's granted scopes (no roles, no permissions).
    /// </summary>
    /// <param name="rawApiKey">The raw (unhashed) API key presented by the client.</param>
    /// <param name="ipAddress">Caller IP address, for audit logging only.</param>
    /// <param name="userAgent">Caller user agent, for audit logging only.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A successful <see cref="ApiKeyExchangeResult"/> with a token on success, or a failed
    /// result with a generic error message on any failure (never reveals which check failed).
    /// </returns>
    Task<ApiKeyExchangeResult> ExchangeApiKeyAsync(string rawApiKey, string? ipAddress, string? userAgent, CancellationToken ct = default);
}
