using System.Security.Claims;
using Farm.Infrastructure.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging;

namespace Farm.Web.Api.Infrastructure.Authorization;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public sealed class RequirePermissionAttribute
    : AuthorizeAttribute, IAuthorizationRequirement, IAuthorizationRequirementData
{
    public RequirePermissionAttribute(string resource, string action)
    {
        Resource = resource;
        Action = action;
        Permission = $"{resource}:{action}";
    }

    public RequirePermissionAttribute(string permission)
    {
        (Resource, Action) = PrintFarmerPermissions.Split(permission);
        Permission = permission;
    }

    public string Resource { get; }

    public string Action { get; }

    public string Permission { get; }

    public IEnumerable<IAuthorizationRequirement> GetRequirements()
    {
        yield return this;
    }
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
        string permissionClaim = $"{requirement.Resource}:{requirement.Action}";
        if (PrintFarmerPermissions.IsFarmAdmin(user))
        {
            _logger.LogInformation(
                "Audited farm-admin permission bypass for user {UserId}: {Permission}",
                user.FindFirstValue(ClaimTypes.NameIdentifier),
                permissionClaim);
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        if (user.HasClaim(PrintFarmerPermissions.ClaimType, permissionClaim))
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
