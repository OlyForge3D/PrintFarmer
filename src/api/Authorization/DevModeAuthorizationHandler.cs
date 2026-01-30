using Microsoft.AspNetCore.Authorization;

namespace Farm.Web.Api.Authorization;

/// <summary>
/// Authorization handler that bypasses authentication for GET requests when DevModeBypassAuth is enabled.
/// This allows easier debugging in development while maintaining full security in production.
/// </summary>
/// <remarks>
/// Configuration:
/// - Set Security:DevModeBypassAuth=true in appsettings.Development.json to enable
/// - In production, this should ALWAYS be false (default)
///
/// Only GET requests are bypassed - all mutations (POST, PUT, DELETE, PATCH) still require auth.
/// </remarks>
public class DevModeAuthorizationHandler : IAuthorizationHandler
{
    private readonly bool _bypassEnabled;
    private readonly ILogger<DevModeAuthorizationHandler> _logger;

    public DevModeAuthorizationHandler(IConfiguration configuration, ILogger<DevModeAuthorizationHandler> logger)
    {
        _bypassEnabled = configuration.GetValue<bool>("Security:DevModeBypassAuth", false);
        _logger = logger;

        if (_bypassEnabled)
        {
            _logger.LogWarning("⚠️  DevModeBypassAuth is ENABLED - GET requests will bypass authentication. " +
                              "This should ONLY be used in development!");
        }
    }

    public Task HandleAsync(AuthorizationHandlerContext context)
    {
        if (!_bypassEnabled)
        {
            // Normal auth flow - don't interfere
            return Task.CompletedTask;
        }

        // Check if this is an HTTP context (web request)
        if (context.Resource is HttpContext httpContext)
        {
            var method = httpContext.Request.Method;

            // Only bypass auth for safe/read-only methods
            if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method))
            {
                // Mark all pending requirements as succeeded
                foreach (var requirement in context.PendingRequirements.ToList())
                {
                    _logger.LogDebug(
                        "DevMode: Bypassing auth requirement {Requirement} for {Method} {Path}",
                        requirement.GetType().Name,
                        method,
                        httpContext.Request.Path);
                    context.Succeed(requirement);
                }
            }
        }

        return Task.CompletedTask;
    }
}
