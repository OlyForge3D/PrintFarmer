using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace Farm.Backend.Plugin.Core;

/// <summary>
/// HTTP Digest Authentication (RFC 7616) state machine, independent of any transport.
///
/// Flow:
/// 1. Pre-authenticate using the cached challenge if available (avoids consuming stream content)
/// 2. If no cached challenge, the initial request returns 401 with a WWW-Authenticate header
/// 3. Compute the digest response from the credentials plus the server nonce
/// 4. Retry a clone of the request carrying the Authorization header
///
/// Challenge caching is critical for file uploads: without it, the request body stream is
/// consumed on the initial 401 and cannot be replayed for the authenticated retry.
///
/// This type owns no connections and no disposable state, so it can be shared by a
/// <see cref="DigestAuthHandler"/> pipeline and by transports that must send through an
/// existing vetted <see cref="HttpClient"/> instead of a handler chain of their own.
/// </summary>
public sealed class DigestAuthenticator
{
    private readonly string? _username;
    private readonly string? _password;
    private readonly object _stateLock = new();
    private int _nonceCount;
    private DigestChallenge? _cachedChallenge;

    public DigestAuthenticator(string? username, string? password)
    {
        _username = username;
        _password = password;
    }

    /// <summary>
    /// Gets a value indicating whether credentials were supplied. Without them the
    /// authenticator is a pass-through and requests must be sent unauthenticated.
    /// </summary>
    public bool HasCredentials =>
        !string.IsNullOrWhiteSpace(_username) && !string.IsNullOrWhiteSpace(_password);

    /// <summary>
    /// Applies an Authorization header derived from a previously cached challenge, if any.
    /// </summary>
    /// <param name="request">Request to authenticate in place.</param>
    /// <returns><c>true</c> when a header was applied.</returns>
    public bool TryApplyCachedAuthorization(HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);

        lock (_stateLock)
        {
            if (!HasCredentials || _cachedChallenge is null)
            {
                return false;
            }

            ApplyAuthorization(request, _cachedChallenge);
            return true;
        }
    }

    /// <summary>
    /// Caches the digest challenge carried by a 401 response so the request can be retried.
    /// </summary>
    /// <param name="response">The unauthorized response to inspect.</param>
    /// <returns><c>true</c> when a usable digest challenge was accepted.</returns>
    public bool TryAcceptChallenge(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        if (!HasCredentials || response.StatusCode != HttpStatusCode.Unauthorized)
        {
            return false;
        }

        AuthenticationHeaderValue? digestHeader = response.Headers.WwwAuthenticate
            .FirstOrDefault(h => h.Scheme.Equals("Digest", StringComparison.OrdinalIgnoreCase));

        if (string.IsNullOrEmpty(digestHeader?.Parameter))
        {
            return false;
        }

        DigestChallenge challenge = ParseDigestChallenge(digestHeader.Parameter);
        lock (_stateLock)
        {
            // nc is per-nonce per RFC 7616. Repeated 401 responses commonly carry the
            // same nonce, so only a genuinely new nonce may restart the shared counter.
            if (_cachedChallenge is null ||
                !string.Equals(
                    _cachedChallenge.Nonce,
                    challenge.Nonce,
                    StringComparison.Ordinal))
            {
                _nonceCount = 0;
            }

            _cachedChallenge = challenge;
            return true;
        }
    }

    /// <summary>
    /// Applies an Authorization header for the most recently accepted challenge.
    /// </summary>
    /// <param name="request">Request to authenticate in place.</param>
    public void ApplyAuthorization(HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);

        lock (_stateLock)
        {
            if (_cachedChallenge is null)
            {
                throw new InvalidOperationException("No digest challenge has been accepted yet.");
            }

            ApplyAuthorization(request, _cachedChallenge);
        }
    }

    /// <summary>
    /// Clones a request that has already been sent so it can be replayed with credentials.
    /// </summary>
    /// <param name="request">The sent request.</param>
    /// <returns>A fresh, unsent copy. The caller owns and must dispose it.</returns>
    public static async Task<HttpRequestMessage> CloneRequestAsync(HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);

        HttpRequestMessage clone = new(request.Method, request.RequestUri)
        {
            Version = request.Version,
            VersionPolicy = request.VersionPolicy,
        };

        // Copy headers (skip Authorization — caller sets a fresh one)
        foreach (KeyValuePair<string, IEnumerable<string>> header in request.Headers)
        {
            if (string.Equals(header.Key, "Authorization", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

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

            foreach (KeyValuePair<string, IEnumerable<string>> header in request.Content.Headers)
            {
                clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        return clone;
    }

    private void ApplyAuthorization(HttpRequestMessage request, DigestChallenge challenge)
    {
        string parameter = ComputeDigestResponse(
            challenge,
            request.Method.Method,
            request.RequestUri?.PathAndQuery ?? "/",
            _username!,
            _password!);

        request.Headers.Authorization = new AuthenticationHeaderValue("Digest", parameter);
    }

    private static DigestChallenge ParseDigestChallenge(string challenge)
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

    private sealed class DigestChallenge
    {
        public string Realm { get; set; } = string.Empty;

        public string Nonce { get; set; } = string.Empty;

        public string? Qop { get; set; }

        public string? Opaque { get; set; }

        public string? Algorithm { get; set; }
    }
}
