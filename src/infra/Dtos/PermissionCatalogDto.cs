namespace Farm.Infrastructure.Dtos;

/// <summary>
/// Full permission catalog derived at runtime from <c>EndpointDataSource</c> metadata
/// (every <c>[RequirePermission]</c> attribute actually enforced by a routed endpoint).
/// Returned by <c>GET /api/admin/permissions/catalog</c> so the role management UI (#1455)
/// can render a permission matrix without hardcoding the permission list.
/// </summary>
public record PermissionCatalogDto
{
    /// <summary>UTC timestamp when the catalog was derived.</summary>
    public required DateTime GeneratedAt { get; init; }

    /// <summary>
    /// Enforced permissions, grouped by resource. Resources are sorted alphabetically by
    /// name; permissions within a resource are sorted alphabetically by action.
    /// </summary>
    public required IReadOnlyList<PermissionResourceGroupDto> Resources { get; init; }

    /// <summary>
    /// Resource/action rows that exist in the database catalog (i.e. have at least one
    /// <c>RolePermission</c> row) but are not enforced by any routed endpoint. These are
    /// candidates for pruning, not an error condition.
    /// </summary>
    public required IReadOnlyList<OrphanedPermissionEntryDto> OrphanedCatalogEntries { get; init; }
}

/// <summary>
/// A single resource and the enforced permissions gating operations on it.
/// </summary>
public record PermissionResourceGroupDto
{
    /// <summary>Stable machine key for the resource (e.g. <c>"calibration"</c>, <c>"queue"</c>).</summary>
    public required string Resource { get; init; }

    /// <summary>
    /// Human-readable resource name from the seeded database catalog, when known.
    /// Null when the resource is enforced by an endpoint but has no matching seed row
    /// (e.g. an ad hoc resource name that predates the catalog).
    /// </summary>
    public string? DisplayName { get; init; }

    /// <summary>Human-readable resource description from the seeded database catalog, when known.</summary>
    public string? Description { get; init; }

    /// <summary>Enforced permissions for this resource, sorted alphabetically by action.</summary>
    public required IReadOnlyList<PermissionCatalogEntryDto> Permissions { get; init; }
}

/// <summary>
/// A single enforced <c>resource:action</c> permission and the routes it gates.
/// </summary>
public record PermissionCatalogEntryDto
{
    /// <summary>Resource key (e.g. <c>"calibration"</c>).</summary>
    public required string Resource { get; init; }

    /// <summary>Action key (e.g. <c>"read"</c>).</summary>
    public required string Action { get; init; }

    /// <summary>Canonical <c>resource:action</c> permission string.</summary>
    public required string Permission { get; init; }

    /// <summary>Human-readable action name from the seeded database catalog, when known.</summary>
    public string? ActionDisplayName { get; init; }

    /// <summary>Human-readable action description from the seeded database catalog, when known.</summary>
    public string? ActionDescription { get; init; }

    /// <summary>
    /// Whether <c>{resource}:admin</c> subsumes this permission. Always <see langword="false"/>
    /// for the <c>admin</c> action itself. This is forward-looking metadata for the
    /// resource-scoped admin implication work tracked separately (see the parent epic's D3
    /// item); today, only the <c>farm_admin</c> role bypasses permission checks entirely.
    /// </summary>
    public required bool ImpliedByAdmin { get; init; }

    /// <summary>Routes gated by this permission, in the order they were discovered.</summary>
    public required IReadOnlyList<PermissionRouteDto> Routes { get; init; }
}

/// <summary>A single HTTP route gated by a permission.</summary>
public record PermissionRouteDto
{
    /// <summary>HTTP method (e.g. <c>"GET"</c>).</summary>
    public required string Method { get; init; }

    /// <summary>Route template, relative to the app root (e.g. <c>"api/printers/{id}"</c>).</summary>
    public required string Template { get; init; }
}

/// <summary>
/// A resource/action row present in the database permission catalog with no enforcing
/// endpoint. Reported so operators can prune stale catalog rows; never silently dropped.
/// </summary>
public record OrphanedPermissionEntryDto
{
    /// <summary>Resource key (e.g. <c>"job_queue"</c>).</summary>
    public required string Resource { get; init; }

    /// <summary>Action key (e.g. <c>"read"</c>).</summary>
    public required string Action { get; init; }

    /// <summary>Canonical <c>resource:action</c> permission string.</summary>
    public required string Permission { get; init; }

    /// <summary>Human-readable resource name from the seeded database catalog, when known.</summary>
    public string? ResourceDisplayName { get; init; }

    /// <summary>Human-readable action name from the seeded database catalog, when known.</summary>
    public string? ActionDisplayName { get; init; }
}
