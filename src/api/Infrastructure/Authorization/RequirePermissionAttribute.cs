using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging;

namespace Farm.Web.Api.Infrastructure.Authorization;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class RequirePermissionAttribute(string resource, string action) : Attribute, IAuthorizationRequirement
{
    public string Resource { get; } = resource;

    public string Action { get; } = action;
}

public class PermissionAuthorizationHandler(ILogger<PermissionAuthorizationHandler> logger) : AuthorizationHandler<RequirePermissionAttribute>
{
    private readonly ILogger<PermissionAuthorizationHandler> _logger = logger;

    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        RequirePermissionAttribute requirement)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requirement);

        ClaimsPrincipal user = context.User;

        if (!user.Identity?.IsAuthenticated ?? true)
        {
            _logger.LogDebug("Authorization failed: User is not authenticated");
            context.Fail();
            return Task.CompletedTask;
        }

        // Check if user has admin role (admin has all permissions)
        if (user.IsInRole("farm_admin"))
        {
            _logger.LogDebug("Authorization succeeded: User has admin role");
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        // Check specific permission
        string permissionClaim = $"{requirement.Resource}:{requirement.Action}";
        if (user.HasClaim("permission", permissionClaim))
        {
            _logger.LogDebug("Authorization succeeded: User has permission {Permission}", permissionClaim);
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        _logger.LogDebug("Authorization failed: User lacks permission {Permission}", permissionClaim);
        context.Fail();
        return Task.CompletedTask;
    }
}
