namespace Farm.Backend.Plugin.Core;

/// <summary>
/// HTTP message handler that implements HTTP Digest Authentication (RFC 7616).
/// Used by PrusaLink for privileged API access that requires user credentials.
///
/// The protocol itself lives in <see cref="DigestAuthenticator"/> so that transports which
/// must send through an existing vetted <see cref="HttpClient"/> — rather than owning a
/// handler chain — can reuse exactly the same state machine. See
/// <see cref="BackendHttpTransport"/>.
/// </summary>
public class DigestAuthHandler : DelegatingHandler
{
    private readonly DigestAuthenticator _authenticator;

    public DigestAuthHandler(string? username, string? password)
        : base(new HttpClientHandler { AllowAutoRedirect = false })
    {
        _authenticator = new DigestAuthenticator(username, password);
    }

    public DigestAuthHandler(HttpMessageHandler innerHandler, string? username, string? password)
        : base(innerHandler)
    {
        _authenticator = new DigestAuthenticator(username, password);
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        // If no credentials, just pass through (no authentication)
        if (!_authenticator.HasCredentials)
        {
            return await base.SendAsync(request, cancellationToken);
        }

        // Pre-authenticate using cached challenge if available.
        // This avoids a 401 round-trip and, critically, prevents consuming
        // non-rewindable stream content (e.g., file uploads) on the initial attempt.
        _authenticator.TryApplyCachedAuthorization(request);

        HttpResponseMessage response = await base.SendAsync(request, cancellationToken);

        if (!_authenticator.TryAcceptChallenge(response))
        {
            return response;
        }

        // Dispose the 401 response since we're going to retry
#pragma warning disable IDISP017 // Prefer using - intentional manual dispose before retry
        response.Dispose();
#pragma warning restore IDISP017

        // Create new request with digest auth (can't reuse the original after sending)
        using HttpRequestMessage retryRequest = await DigestAuthenticator.CloneRequestAsync(request);
        _authenticator.ApplyAuthorization(retryRequest);

        return await base.SendAsync(retryRequest, cancellationToken);
    }
}
