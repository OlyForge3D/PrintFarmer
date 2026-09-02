using System.Net;
using Farm.Infrastructure.Services.RateLimiting;
using Microsoft.Extensions.Logging;

namespace Farm.Web.Api.Middleware;

/// <summary>
/// Middleware that enforces rate limiting on authentication endpoints (login, register,
/// and Desktop API-key exchange). Limits are applied per client IP address to prevent
/// brute force attacks and key enumeration.
///
/// The client IP is always sourced from <see cref="HttpContext.Connection"/>. Callers
/// must NOT parse <c>X-Forwarded-For</c> here — that is the job of the framework's
/// forwarded-headers middleware (see <see cref="Farm.Web.Api.Infrastructure.ForwardedHeadersConfiguration"/>),
/// which only rewrites <c>Connection.RemoteIpAddress</c> when the immediate connection
/// is from an operator-declared trusted proxy. Trusting the header directly would let a
/// caller rotate the header value on each request and bypass the per-IP limit
/// (security issue #862).
/// </summary>
public class AuthenticationRateLimitMiddleware(RequestDelegate next, ILogger<AuthenticationRateLimitMiddleware> logger)
{
    private const string UnknownIpKey = "unknown";
    private readonly RequestDelegate _next = next;
    private readonly ILogger<AuthenticationRateLimitMiddleware> _logger = logger;

    // Resolved once per middleware instance (not a static readonly field) so that
    // tests which mutate TEST_DISABLE_RATE_LIMITER and construct a fresh middleware
    // instance per test still observe the current env var value, while production
    // (where the middleware is instantiated once at app startup) pays the
    // environment-variable lookup cost only once instead of on every request.
    private readonly bool _rateLimiterDisabledForTests = IsRateLimiterDisabledForTests();

    private static bool IsRateLimiterDisabledForTests()
    {
        try
        {
            string? disabled = Environment.GetEnvironmentVariable("TEST_DISABLE_RATE_LIMITER");
            return !string.IsNullOrEmpty(disabled) && (string.Equals(disabled, "true", StringComparison.OrdinalIgnoreCase) || disabled == "1");
        }
        catch
        {
            // If anything goes wrong while checking env, fall back to normal behaviour
            return false;
        }
    }

    public async Task InvokeAsync(HttpContext context, IRateLimitService rateLimitService)
    {
        // Allow tests to opt-out of the authentication rate limiter by setting
        // the TEST_DISABLE_RATE_LIMITER environment variable. This keeps
        // integration tests stable in minimal test hosts.
        if (_rateLimiterDisabledForTests)
        {
            await _next(context);
            return;
        }

        string path = context.Request.Path.Value ?? string.Empty;

        // Path (not HTTP method) decides whether this request is subject to the
        // limiter. The underlying [HttpPost] actions only ever execute for POST
        // requests, but the limiter itself must not be skippable by sending a
        // different verb: the caller fully controls Request.Method, so gating the
        // sensitive rate-limit check on it would let an attacker bypass the limit
        // simply by varying the verb on each attempt.
        //
        // Note: this middleware matches by path *suffix*, not prefix, so
        // PathString.StartsWithSegments (used in TelemetryMiddleware) is not
        // equivalent here and must not be substituted. EndsWith with
        // StringComparison.OrdinalIgnoreCase avoids the ToLowerInvariant()
        // allocation while preserving case-insensitive matching.
        bool isLogin = path.EndsWith("/api/auth/login", StringComparison.OrdinalIgnoreCase);
        bool isRegister = path.EndsWith("/api/auth/register", StringComparison.OrdinalIgnoreCase);
        bool isApiKeyExchange = path.EndsWith("/api/auth/api-key/exchange", StringComparison.OrdinalIgnoreCase);

        if (!isLogin && !isRegister && !isApiKeyExchange)
        {
            await _next(context);
            return;
        }

        // Source the client IP strictly from Connection.RemoteIpAddress. When the app
        // sits behind a properly configured reverse proxy, the framework's
        // UseForwardedHeaders middleware (enabled via ForwardedHeaders:Enabled + trusted
        // KnownProxies / KnownNetworks) has already rewritten this to the forwarded
        // client IP; otherwise it is the direct TCP peer. Either way, an untrusted caller
        // cannot influence the value via a raw X-Forwarded-For header.
        string ipAddress = ResolveClientIp(context);

        // Check rate limit based on endpoint type
        RateLimitResult rateLimitResult;
        if (isLogin)
        {
            rateLimitResult = await rateLimitService.CheckLoginLimitAsync(ipAddress);
        }
        else if (isRegister)
        {
            rateLimitResult = await rateLimitService.CheckRegisterLimitAsync(ipAddress);
        }
        else
        {
            // isApiKeyExchange
            rateLimitResult = await rateLimitService.CheckApiKeyExchangeLimitAsync(ipAddress);
        }

        if (!rateLimitResult.IsAllowed)
        {
            // Rate limit exceeded - return 429 Too Many Requests
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.Response.ContentType = "application/json";

            if (rateLimitResult.RetryAfter.HasValue)
            {
                context.Response.Headers["Retry-After"] = ((int)rateLimitResult.RetryAfter.Value.TotalSeconds).ToString();
            }

            string endpoint = isLogin ? "login" : isRegister ? "register" : "api-key-exchange";
            _logger.LogWarning("Rate limit exceeded for {Endpoint} from IP {IpAddress}", endpoint, ipAddress);

            await context.Response.WriteAsJsonAsync(new
            {
                error = "Too Many Requests",
                message = rateLimitResult.Message ?? "Rate limit exceeded. Please try again later.",
                retryAfterSeconds = rateLimitResult.RetryAfter?.TotalSeconds
            });

            return;
        }

        // Rate limit not exceeded - record attempt and continue
        if (isLogin)
        {
            await rateLimitService.RecordLoginAttemptAsync(ipAddress);
        }
        else if (isRegister)
        {
            await rateLimitService.RecordRegisterAttemptAsync(ipAddress);
        }
        else
        {
            await rateLimitService.RecordApiKeyExchangeAttemptAsync(ipAddress);
        }

        await _next(context);
    }

    private static string ResolveClientIp(HttpContext context)
    {
        try
        {
            IPAddress? remote = context.Connection.RemoteIpAddress;
            if (remote is null)
            {
                return UnknownIpKey;
            }

            // Normalize IPv4-mapped IPv6 addresses (::ffff:1.2.3.4) so a single client
            // always maps to the same rate-limit bucket regardless of the socket family
            // the reverse proxy connected on.
            if (remote.IsIPv4MappedToIPv6)
            {
                remote = remote.MapToIPv4();
            }

            return remote.ToString();
        }
        catch
        {
            return UnknownIpKey;
        }
    }
}
