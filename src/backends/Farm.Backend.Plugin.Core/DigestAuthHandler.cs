using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace Farm.Backend.Plugin.Core;

/// <summary>
/// HTTP message handler that implements HTTP Digest Authentication (RFC 7616).
/// Used by PrusaLink for privileged API access that requires user credentials.
///
/// Flow:
/// 1. Pre-authenticate using cached nonce if available (avoids consuming stream content)
/// 2. If no cached nonce, initial request returns 401 with WWW-Authenticate header
/// 3. Client computes digest response using MD5 hash of credentials + nonce
/// 4. Retry request with Authorization header containing digest response
///
/// Nonce caching is critical for file uploads: without it, the request body stream
/// is consumed on the initial 401 and cannot be replayed for the authenticated retry.
/// </summary>
public class DigestAuthHandler : DelegatingHandler
{
    private readonly string? _username;
    private readonly string? _password;
    private int _nonceCount;
    private DigestChallenge? _cachedChallenge;

    public DigestAuthHandler(string? username, string? password)
        : base(new HttpClientHandler())
    {
        _username = username;
        _password = password;
    }

    public DigestAuthHandler(HttpMessageHandler innerHandler, string? username, string? password)
        : base(innerHandler)
    {
        _username = username;
        _password = password;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        // If no credentials, just pass through (no authentication)
        if (string.IsNullOrWhiteSpace(_username) || string.IsNullOrWhiteSpace(_password))
        {
            return await base.SendAsync(request, cancellationToken);
        }

        // Pre-authenticate using cached challenge if available.
        // This avoids a 401 round-trip and, critically, prevents consuming
        // non-rewindable stream content (e.g., file uploads) on the initial attempt.
        if (_cachedChallenge != null)
        {
            string preAuth = ComputeDigestResponse(
                _cachedChallenge,
                request.Method.Method,
                request.RequestUri?.PathAndQuery ?? "/",
                _username,
                _password);
            request.Headers.Authorization = new AuthenticationHeaderValue("Digest", preAuth);
        }

        // Send the request (pre-authenticated if nonce was cached)
        HttpResponseMessage response = await base.SendAsync(request, cancellationToken);

        if (response.StatusCode != HttpStatusCode.Unauthorized)
        {
            return response;
        }

        // Check for WWW-Authenticate header with Digest scheme
        if (!response.Headers.WwwAuthenticate.Any(h =>
            h.Scheme.Equals("Digest", StringComparison.OrdinalIgnoreCase)))
        {
            return response;
        }

        // Parse the WWW-Authenticate header
        AuthenticationHeaderValue digestHeader = response.Headers.WwwAuthenticate
            .First(h => h.Scheme.Equals("Digest", StringComparison.OrdinalIgnoreCase));

        if (string.IsNullOrEmpty(digestHeader.Parameter))
        {
            return response;
        }

        // Cache the new challenge and reset nonce count (nc is per-nonce per RFC 7616)
        _cachedChallenge = ParseDigestChallenge(digestHeader.Parameter);
        _nonceCount = 0;

        // Dispose the 401 response since we're going to retry
#pragma warning disable IDISP017 // Prefer using - intentional manual dispose before retry
        response.Dispose();
#pragma warning restore IDISP017

        // Create new request with digest auth (can't reuse the original after sending)
        using HttpRequestMessage retryRequest = await CloneRequestAsync(request);

        string digestResponse = ComputeDigestResponse(
            _cachedChallenge,
            retryRequest.Method.Method,
            retryRequest.RequestUri?.PathAndQuery ?? "/",
            _username,
            _password);

        retryRequest.Headers.Authorization = new AuthenticationHeaderValue("Digest", digestResponse);

        return await base.SendAsync(retryRequest, cancellationToken);
    }

    private static async Task<HttpRequestMessage> CloneRequestAsync(HttpRequestMessage request)
    {
        HttpRequestMessage clone = new(request.Method, request.RequestUri);

        // Copy headers (skip Authorization — caller sets a fresh one)
        foreach (KeyValuePair<string, IEnumerable<string>> header in request.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        // Copy content if present
        if (request.Content != null)
        {
            // After the first SendAsync the underlying stream has been consumed.
            // ReadAsByteArrayAsync() on an already-serialized StreamContent returns
            // empty bytes because the stream position is at the end and the internal
            // buffer was never populated. Instead, access the raw stream directly,
            // rewind it if possible, and copy into a fresh byte array.
            Stream contentStream = await request.Content.ReadAsStreamAsync();
            if (contentStream.CanSeek)
            {
                contentStream.Position = 0;
            }

            using MemoryStream ms = new();
            await contentStream.CopyToAsync(ms);
            byte[] contentBytes = ms.ToArray();

            clone.Content = new ByteArrayContent(contentBytes);

            // Copy content headers
            foreach (KeyValuePair<string, IEnumerable<string>> header in request.Content.Headers)
            {
                clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        return clone;
    }

    private DigestChallenge ParseDigestChallenge(string challenge)
    {
        DigestChallenge result = new();

        // Parse key="value" pairs from the challenge string
        // Example: realm="Prusalink", nonce="abc123", qop="auth", algorithm=MD5
        string[] parts = challenge.Split(',');

        foreach (string part in parts)
        {
            string trimmed = part.Trim();
            int equalsIndex = trimmed.IndexOf('=');

            if (equalsIndex <= 0)
            {
                continue;
            }

            string key = trimmed[..equalsIndex].Trim().ToLowerInvariant();
            string value = trimmed[(equalsIndex + 1)..].Trim().Trim('"');

            switch (key)
            {
                case "realm":
                    result.Realm = value;
                    break;
                case "nonce":
                    result.Nonce = value;
                    break;
                case "qop":
                    result.Qop = value;
                    break;
                case "opaque":
                    result.Opaque = value;
                    break;
                case "algorithm":
                    result.Algorithm = value;
                    break;
            }
        }

        return result;
    }

    private string ComputeDigestResponse(
        DigestChallenge challenge,
        string method,
        string uri,
        string username,
        string password)
    {
        // Increment nonce count for each request
        _nonceCount++;
        string nc = _nonceCount.ToString("x8");

        // Generate client nonce (cnonce)
        string cnonce = GenerateCnonce();

        // Compute HA1 = MD5(username:realm:password)
        string ha1 = ComputeMd5Hash($"{username}:{challenge.Realm}:{password}");

        // Compute HA2 = MD5(method:uri)
        string ha2 = ComputeMd5Hash($"{method}:{uri}");

        // Compute response based on QOP
        string response;
        if (!string.IsNullOrEmpty(challenge.Qop))
        {
            // QOP is present (auth or auth-int)
            // response = MD5(HA1:nonce:nc:cnonce:qop:HA2)
            response = ComputeMd5Hash($"{ha1}:{challenge.Nonce}:{nc}:{cnonce}:{challenge.Qop}:{ha2}");
        }
        else
        {
            // Legacy mode without QOP
            // response = MD5(HA1:nonce:HA2)
            response = ComputeMd5Hash($"{ha1}:{challenge.Nonce}:{ha2}");
        }

        // Build the Authorization header value
        StringBuilder sb = new();
        sb.Append($"username=\"{username}\"");
        sb.Append($", realm=\"{challenge.Realm}\"");
        sb.Append($", nonce=\"{challenge.Nonce}\"");
        sb.Append($", uri=\"{uri}\"");

        if (!string.IsNullOrEmpty(challenge.Qop))
        {
            sb.Append($", qop={challenge.Qop}");
            sb.Append($", nc={nc}");
            sb.Append($", cnonce=\"{cnonce}\"");
        }

        sb.Append($", response=\"{response}\"");

        if (!string.IsNullOrEmpty(challenge.Opaque))
        {
            sb.Append($", opaque=\"{challenge.Opaque}\"");
        }

        if (!string.IsNullOrEmpty(challenge.Algorithm))
        {
            sb.Append($", algorithm={challenge.Algorithm}");
        }

        return sb.ToString();
    }

    // MD5 is required by HTTP Digest Authentication (RFC 7616) and is not used for general-purpose hashing.
#pragma warning disable CA5351 // Do not use broken cryptographic algorithms
#pragma warning disable S4790 // HTTP Digest endpoints require MD5 per RFC 7616; stronger hashing would break protocol interoperability.
    private static string ComputeMd5Hash(string input)
    {
        byte[] inputBytes = Encoding.ASCII.GetBytes(input);
        byte[] hashBytes = MD5.HashData(inputBytes);
        return Convert.ToHexStringLower(hashBytes);
    }
#pragma warning restore CA5351
#pragma warning restore S4790

    private static string GenerateCnonce()
    {
        byte[] bytes = new byte[8];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexStringLower(bytes);
    }

    private class DigestChallenge
    {
        public string Realm { get; set; } = string.Empty;

        public string Nonce { get; set; } = string.Empty;

        public string? Qop { get; set; }

        public string? Opaque { get; set; }

        public string? Algorithm { get; set; }
    }
}
