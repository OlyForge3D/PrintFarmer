using Farm.Infrastructure.Logging;
using Microsoft.AspNetCore.Authorization;

namespace Farm.Web.Api.Authorization;

/// <summary>
/// Authorization handler that bypasses authorization requirements for safe requests when DevModeBypassAuth is enabled.
/// This allows easier debugging in development while maintaining full security in production.
/// </summary>
/// <remarks>
/// Configuration:
/// - Set Security:DevModeBypassAuth=true in appsettings.Development.json to enable
/// - The setting is ignored unless the host environment is Development
///
/// Only GET, HEAD, and OPTIONS requests are bypassed - all mutations still require authorization.
/// </remarks>
public class DevModeAuthorizationHandler : IAuthorizationHandler
{
    public const string ConfigurationKey = "Security:DevModeBypassAuth";

    private readonly bool _bypassEnabled;
    private readonly ILogger<DevModeAuthorizationHandler> _logger;

    public DevModeAuthorizationHandler(
        IConfiguration configuration,
        IWebHostEnvironment environment,
        ILogger<DevModeAuthorizationHandler> logger)
    {
        _bypassEnabled = environment.IsDevelopment()
            && configuration.GetValue<bool>(ConfigurationKey, false);
        _logger = logger;
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
                        LogSanitizer.Sanitize(method),
                        LogSanitizer.Sanitize(httpContext.Request.Path));
                    context.Succeed(requirement);
                }
            }
        }

        return Task.CompletedTask;
    }
}
