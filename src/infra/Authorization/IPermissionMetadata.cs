namespace Farm.Infrastructure.Authorization;

/// <summary>
/// Describes a resource-action permission for discovery without implying authorization enforcement.
/// </summary>
public interface IPermissionMetadata
{
    /// <summary>Gets the resource name.</summary>
    string Resource { get; }

    /// <summary>Gets the action name.</summary>
    string Action { get; }

    /// <summary>Gets the complete resource:action permission name.</summary>
    string Permission { get; }
}
