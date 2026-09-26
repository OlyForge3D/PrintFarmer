using Farm.Infrastructure.Security;

namespace Farm.Infrastructure.Authorization;

/// <summary>
/// Exposes a permission to catalog consumers without authenticating or authorizing requests.
/// The endpoint must enforce the permission through its own authorization policy.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public sealed class PermissionCatalogAttribute : Attribute, IPermissionMetadata
{
    /// <summary>Creates catalog-only metadata for the specified resource:action permission.</summary>
    public PermissionCatalogAttribute(string permission)
    {
        (Resource, Action) = PrintFarmerPermissions.Split(permission);
        Permission = permission;
    }

    /// <inheritdoc />
    public string Resource { get; }

    /// <inheritdoc />
    public string Action { get; }

    /// <inheritdoc />
    public string Permission { get; }
}
