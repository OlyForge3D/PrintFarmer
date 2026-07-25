using System.Security.Claims;
using Farm.Infrastructure.Security;
using Microsoft.Extensions.Logging;

namespace Farm.Slicer.Module.Api.Authorization;

/// <summary>
/// Applies the single-farm owner boundary used by slicer jobs and artifacts.
/// </summary>
public interface ISlicerResourceAccessAuthorizer
{
    bool CanAccess(ClaimsPrincipal user, Guid ownerUserId, string resourceType, Guid resourceId);
}

/// <summary>
/// Audits farm-admin bypasses while keeping regular users owner-scoped.
/// </summary>
public sealed class SlicerResourceAccessAuthorizer(
    ILogger<SlicerResourceAccessAuthorizer> logger) : ISlicerResourceAccessAuthorizer
{
    public bool CanAccess(
        ClaimsPrincipal user,
        Guid ownerUserId,
        string resourceType,
        Guid resourceId)
    {
        if (PrintFarmerPermissions.IsFarmAdmin(user))
        {
            PrintFarmerPermissions.TryGetUserId(user, out Guid adminUserId);
            logger.LogInformation(
                "Audited farm-admin resource bypass by user {UserId} for {ResourceType} {ResourceId}",
                adminUserId,
                resourceType,
                resourceId);
            return true;
        }

        return PrintFarmerPermissions.TryGetUserId(user, out Guid userId) &&
               userId == ownerUserId;
    }
}
