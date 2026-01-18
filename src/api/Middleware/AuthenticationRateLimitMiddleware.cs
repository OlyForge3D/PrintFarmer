using Farm.Infrastructure.Services.RateLimiting;
using Farm.Infrastructure.Telemetry;
using Microsoft.Extensions.Primitives;

namespace Farm.Web.Api.Middleware;

/// <summary>
/// Middleware that enforces rate limiting on authentication endpoints (login and register).
/// Limits are applied per IP address to prevent brute force attacks.
/// </summary>
public class AuthenticationRateLimitMiddleware(RequestDelegate next, IUnifiedLoggingService logger)
{
    private readonly RequestDelegate _next = next;
    private readonly IUnifiedLoggingService _logger = logger;

    public async Task InvokeAsync(HttpContext context, IRateLimitService rateLimitService)
    {
        // Allow tests to opt-out of the authentication rate limiter by setting
        // the TEST_DISABLE_RATE_LIMITER environment variable. This keeps
        // integration tests stable in minimal test hosts.
        try
        {
            string? disabled = Environment.GetEnvironmentVariable("TEST_DISABLE_RATE_LIMITER");
            if (!string.IsNullOrEmpty(disabled) && (string.Equals(disabled, "true", StringComparison.OrdinalIgnoreCase) || disabled == "1"))
            {
                await _next(context);
                return;
            }
        }
        catch
        {
            // If anything goes wrong while checking env, fall back to normal behaviour
        }
        string path = context.Request.Path.Value?.ToLowerInvariant() ?? string.Empty;
        string method = context.Request.Method.ToUpperInvariant();

        // Only apply rate limiting to POST requests on auth endpoints
        if (method != "POST")
        {
            await _next(context);
            return;
        }

        // Check if this is a login or register endpoint
        bool isLogin = path.EndsWith("/api/auth/login");
        bool isRegister = path.EndsWith("/api/auth/register");

        if (!isLogin && !isRegister)
        {
            await _next(context);
            return;
        }

        // Get client IP address. Prefer X-Forwarded-For if present (tests and proxies
        // can set this). Fall back to connection remote address or "unknown".
        string ipAddress = "unknown";
        try
        {
            if (context.Request.Headers.TryGetValue("X-Forwarded-For", out StringValues headerVal) && !string.IsNullOrEmpty(headerVal))
            {
                ipAddress = headerVal.ToString();
            }
            else
            {
                ipAddress = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            }
        }
        catch
        {
            ipAddress = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        }

        // Check rate limit based on endpoint type
        RateLimitResult rateLimitResult;
        if (isLogin)
        {
            rateLimitResult = await rateLimitService.CheckLoginLimitAsync(ipAddress);
        }
        else // isRegister
        {
            rateLimitResult = await rateLimitService.CheckRegisterLimitAsync(ipAddress);
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

            string endpoint = isLogin ? "login" : "register";
            _logger.LogWarning($"Rate limit exceeded for {endpoint} from IP {ipAddress}", null, new
            {
                Endpoint = endpoint,
                IpAddress = ipAddress,
                RetryAfterSeconds = rateLimitResult.RetryAfter?.TotalSeconds
            });

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
        else
        {
            await rateLimitService.RecordRegisterAttemptAsync(ipAddress);
        }

        await _next(context);
    }
}
