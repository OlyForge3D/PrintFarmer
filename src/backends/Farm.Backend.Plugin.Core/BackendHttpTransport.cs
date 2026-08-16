namespace Farm.Backend.Plugin.Core;

/// <summary>
/// Sends backend requests through a single injected <see cref="HttpClient"/>, optionally
/// layering HTTP Digest authentication on top of it.
///
/// Backend clients receive a DI-provided <see cref="HttpClient"/> (the "VettedEgress"
/// named client) after callers have applied any destination vetting or address pinning.
/// Its primary handler carries the <c>AllowAutoRedirect = false</c> policy that stops
/// credentials leaking to a redirect target, the configured timeout, and pooled connections.
/// Creating a second
/// <see cref="HttpClient"/> around a raw <see cref="HttpClientHandler"/> just to attach a
/// <see cref="DigestAuthHandler"/> would silently opt every privileged request out of all of
/// them, so this transport keeps the injected client as the only egress path and drives the
/// digest challenge/retry handshake itself via <see cref="DigestAuthenticator"/>.
///
/// The transport borrows the client and owns no disposable state, so backend clients can hold
/// one per credential set for the lifetime of the injected client without leaking sockets.
/// </summary>
public sealed class BackendHttpTransport
{
    private readonly HttpClient _httpClient;
    private readonly DigestAuthenticator? _authenticator;

    /// <summary>
    /// Creates a transport that forwards requests to <paramref name="httpClient"/> unchanged.
    /// </summary>
    /// <param name="httpClient">The vetted client that owns all outbound connections.</param>
    public BackendHttpTransport(HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _httpClient = httpClient;
    }

    /// <summary>
    /// Creates a transport that authenticates with HTTP Digest while still routing every
    /// request — including the post-challenge retry — through <paramref name="httpClient"/>.
    /// </summary>
    /// <param name="httpClient">The vetted client that owns all outbound connections.</param>
    /// <param name="username">Digest username.</param>
    /// <param name="password">Digest password.</param>
    public BackendHttpTransport(HttpClient httpClient, string? username, string? password)
        : this(httpClient)
    {
        DigestAuthenticator authenticator = new(username, password);
        _authenticator = authenticator.HasCredentials ? authenticator : null;
    }

    /// <summary>
    /// Gets the request timeout enforced by the underlying vetted client.
    /// </summary>
    public TimeSpan Timeout => _httpClient.Timeout;

    /// <summary>
    /// Sends a request, buffering the response content like <see cref="HttpClient"/> does by default.
    /// </summary>
    /// <param name="request">The request to send.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The upstream response.</returns>
    public Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken ct) =>
        SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);

    /// <summary>
    /// Sends a request with an explicit completion option so streaming callers can keep
    /// enforcing their own incremental size limits.
    /// </summary>
    /// <param name="request">The request to send.</param>
    /// <param name="completionOption">When the returned task should complete.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The upstream response.</returns>
    public async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        HttpCompletionOption completionOption,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (_authenticator is null)
        {
            return await _httpClient.SendAsync(request, completionOption, ct).ConfigureAwait(false);
        }

        // Pre-authenticate from the cached challenge so a rewound body is not required.
        _authenticator.TryApplyCachedAuthorization(request);

        HttpResponseMessage response = await _httpClient
            .SendAsync(request, completionOption, ct)
            .ConfigureAwait(false);

        if (!_authenticator.TryAcceptChallenge(response))
        {
            return response;
        }

#pragma warning disable IDISP017 // Prefer using - intentional manual dispose before retry
        response.Dispose();
#pragma warning restore IDISP017

        // HttpClient refuses to resend a message it has already sent, so replay a clone.
        using HttpRequestMessage retryRequest = await DigestAuthenticator
            .CloneRequestAsync(request)
            .ConfigureAwait(false);
        _authenticator.ApplyAuthorization(retryRequest);

        return await _httpClient.SendAsync(retryRequest, completionOption, ct).ConfigureAwait(false);
    }
}
