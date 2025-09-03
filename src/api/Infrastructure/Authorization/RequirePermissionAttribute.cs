using Microsoft.AspNetCore.Authorization;

namespace Farm.Web.Api.Infrastructure.Authorization;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class RequirePermissionAttribute : Attribute, IAuthorizationRequirement
{
    public string Resource { get; }
    public string Action { get; }

    public RequirePermissionAttribute(string resource, string action)
    {
        Resource = resource;
        Action = action;
    }
}

public class PermissionAuthorizationHandler : AuthorizationHandler<RequirePermissionAttribute>
{
    private readonly ILogger<PermissionAuthorizationHandler> _logger;

    public PermissionAuthorizationHandler(ILogger<PermissionAuthorizationHandler> logger)
    {
        _logger = logger;
    }

    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        RequirePermissionAttribute requirement)
    {
        var user = context.User;

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
        var permissionClaim = $"{requirement.Resource}:{requirement.Action}";
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
