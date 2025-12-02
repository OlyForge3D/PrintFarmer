using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Farm.Web.Api.Services.Authentication;

/// <summary>
/// HTTP Digest Authentication handler for .NET HttpClient.
/// Implements RFC 7616 HTTP Digest Authentication.
/// Handles the 401 Unauthorized response with WWW-Authenticate: Digest challenge
/// and automatically retries the request with proper digest credentials.
/// </summary>
public class DigestAuthHandler : DelegatingHandler
{
    private readonly string _username;
    private readonly string _password;
    private readonly ConcurrentDictionary<string, DigestAuthContext> _authContexts = new();

    public DigestAuthHandler(string username, string password)
    {
        _username = username ?? throw new ArgumentNullException(nameof(username));
        _password = password ?? throw new ArgumentNullException(nameof(password));
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        // First attempt without authentication
        HttpResponseMessage response = await base.SendAsync(request, cancellationToken);

        // If 401 Unauthorized, try to handle digest auth challenge
        if (response.StatusCode == HttpStatusCode.Unauthorized &&
            response.Headers.WwwAuthenticate?.FirstOrDefault(h => h.Scheme == "Digest") != null)
        {
            response.Dispose();

            // Parse the challenge and retry with digest authentication
            string? challenge = response.Headers.WwwAuthenticate
                .FirstOrDefault(h => h.Scheme == "Digest")
                ?.Parameter;

            if (!string.IsNullOrEmpty(challenge))
            {
                var digestParams = ParseDigestChallenge(challenge);
                string realm = GetChallengeParameter(digestParams, "realm", "");
                string nonce = GetChallengeParameter(digestParams, "nonce", "");
                string uri = request.RequestUri?.PathAndQuery ?? "/";
                string method = request.Method.Method;
                string opaque = GetChallengeParameter(digestParams, "opaque", "");
                string algorithm = GetChallengeParameter(digestParams, "algorithm", "MD5");
                string qop = GetChallengeParameter(digestParams, "qop", "");

                // Get or create auth context for this realm
                string contextKey = $"{realm}:{request.RequestUri?.Host}";
                var context = _authContexts.GetOrAdd(contextKey, _ => new DigestAuthContext());

                // Build digest response
                string response1 = ComputeDigestResponse(
                    method, uri, realm, nonce, opaque, algorithm, qop, ref context);

                // Create new request with Authorization header
                HttpRequestMessage retryRequest = new(request.Method, request.RequestUri);
                retryRequest.Headers.Add("Authorization", response1);

                // Copy headers (except Authorization which we just set)
                foreach (var header in request.Headers)
                {
                    if (header.Key != "Authorization")
                    {
                        retryRequest.Headers.Add(header.Key, header.Value);
                    }
                }

                // Copy content if present
                if (request.Content != null)
                {
                    retryRequest.Content = new ByteArrayContent(
                        await request.Content.ReadAsByteArrayAsync(cancellationToken));
                    foreach (var header in request.Content.Headers)
                    {
                        retryRequest.Content.Headers.Add(header.Key, header.Value);
                    }
                }

                // Update context for next request
                _authContexts[contextKey] = context;

                // Retry the request
                return await base.SendAsync(retryRequest, cancellationToken);
            }
        }

        return response;
    }

    private Dictionary<string, string> ParseDigestChallenge(string challenge)
    {
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var parts = challenge.Split(',');

        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            var eqIndex = trimmed.IndexOf('=');
            if (eqIndex <= 0) continue;

            var key = trimmed.Substring(0, eqIndex).Trim();
            var value = trimmed.Substring(eqIndex + 1).Trim();

            // Remove quotes if present
            if (value.StartsWith('"') && value.EndsWith('"'))
            {
                value = value.Substring(1, value.Length - 2);
            }

            parameters[key] = value;
        }

        return parameters;
    }

    private string GetChallengeParameter(Dictionary<string, string> parameters, string key, string defaultValue)
    {
        return parameters.TryGetValue(key, out var value) ? value : defaultValue;
    }

    private string ComputeDigestResponse(
        string method,
        string uri,
        string realm,
        string nonce,
        string opaque,
        string algorithm,
        string qop,
        ref DigestAuthContext context)
    {
        // Compute HA1: MD5(username:realm:password)
        string ha1Input = $"{_username}:{realm}:{_password}";
        string ha1 = ComputeHash(ha1Input, algorithm);

        // Compute HA2: MD5(method:uri)
        string ha2Input = $"{method}:{uri}";
        string ha2 = ComputeHash(ha2Input, algorithm);

        // Compute response
        string response;
        if (!string.IsNullOrEmpty(qop) && qop.Contains("auth"))
        {
            context.NonceCount++;
            string nc = context.NonceCount.ToString("x8");
            string cnonce = GenerateCNonce();
            string responseInput = $"{ha1}:{nonce}:{nc}:{cnonce}:auth:{ha2}";
            response = ComputeHash(responseInput, algorithm);

            return $"Digest username=\"{_username}\", realm=\"{realm}\", nonce=\"{nonce}\", uri=\"{uri}\", " +
                   $"response=\"{response}\", opaque=\"{opaque}\", algorithm={algorithm}, qop=auth, nc={nc}, cnonce=\"{cnonce}\"";
        }
        else
        {
            string responseInput = $"{ha1}:{nonce}:{ha2}";
            response = ComputeHash(responseInput, algorithm);

            return $"Digest username=\"{_username}\", realm=\"{realm}\", nonce=\"{nonce}\", uri=\"{uri}\", " +
                   $"response=\"{response}\", opaque=\"{opaque}\", algorithm={algorithm}";
        }
    }

    private string ComputeHash(string input, string algorithm)
    {
        using (var hasher = algorithm.Equals("SHA-256", StringComparison.OrdinalIgnoreCase)
            ? (HashAlgorithm)SHA256.Create()
            : MD5.Create())
        {
            byte[] hash = hasher.ComputeHash(Encoding.UTF8.GetBytes(input));
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }
    }

    private string GenerateCNonce()
    {
        byte[] nonceBytes = new byte[16];
        using (var rng = RandomNumberGenerator.Create())
        {
            rng.GetBytes(nonceBytes);
        }
        return Convert.ToBase64String(nonceBytes);
    }

    /// <summary>
    /// Stores per-realm digest authentication context including nonce count.
    /// </summary>
    private class DigestAuthContext
    {
        public int NonceCount { get; set; } = 0;
    }
}
