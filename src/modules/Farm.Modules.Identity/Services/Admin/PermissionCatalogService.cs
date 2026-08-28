using Farm.Infrastructure.Authorization;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Farm.Modules.Identity.Services.Admin;

/// <summary>
/// Derives the permission catalog by walking <see cref="EndpointDataSource"/> for
/// <see cref="RequirePermissionAttribute"/> metadata — the same source the OpenAPI
/// authorization transformer (<c>AuthorizationOpenApiTransformers.cs</c>) reads from
/// to annotate secured operations. Resource/action display metadata is joined from the
/// seeded <see cref="Resource"/>/<see cref="UserAction"/> catalog tables.
/// </summary>
public sealed class PermissionCatalogService : IPermissionCatalogService
{
    private readonly EndpointDataSource _endpointDataSource;
    private readonly AppDbContext _context;

    public PermissionCatalogService(EndpointDataSource endpointDataSource, AppDbContext context)
    {
        _endpointDataSource = endpointDataSource ?? throw new ArgumentNullException(nameof(endpointDataSource));
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public async Task<PermissionCatalogDto> GetCatalogAsync(CancellationToken cancellationToken = default)
    {
        DateTime generatedAt = DateTime.UtcNow;

        // permission (resource:action) -> ordered, deduplicated routes gating it.
        var routesByPermission = new Dictionary<string, List<PermissionRouteDto>>(StringComparer.Ordinal);
        var seenRoutesByPermission = new Dictionary<string, HashSet<(string Method, string Template)>>(StringComparer.Ordinal);
        var attributesByPermission = new Dictionary<string, RequirePermissionAttribute>(StringComparer.Ordinal);

        foreach (RouteEndpoint endpoint in _endpointDataSource.Endpoints.OfType<RouteEndpoint>())
        {
            IReadOnlyList<RequirePermissionAttribute> permissionAttributes =
                endpoint.Metadata.GetOrderedMetadata<RequirePermissionAttribute>();
            if (permissionAttributes.Count == 0)
            {
                continue;
            }

            string template = endpoint.RoutePattern.RawText ?? string.Empty;
            IReadOnlyList<string> methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods
                ?? [];
            if (methods.Count == 0)
            {
                methods = ["ANY"];
            }

            foreach (RequirePermissionAttribute attribute in permissionAttributes)
            {
                attributesByPermission.TryAdd(attribute.Permission, attribute);

                if (!seenRoutesByPermission.TryGetValue(attribute.Permission, out HashSet<(string, string)>? seen))
                {
                    seen = new HashSet<(string, string)>();
                    seenRoutesByPermission[attribute.Permission] = seen;
                    routesByPermission[attribute.Permission] = [];
                }

                foreach (string method in methods)
                {
                    if (seen.Add((method, template)))
                    {
                        routesByPermission[attribute.Permission].Add(new PermissionRouteDto
                        {
                            Method = method,
                            Template = template,
                        });
                    }
                }
            }
        }

        List<Resource> resources = await _context.Resources
            .AsNoTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        Dictionary<string, Resource> resourcesByName = resources.ToDictionary(r => r.Name, StringComparer.Ordinal);

        List<UserAction> actions = await _context.UserActions
            .AsNoTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        Dictionary<string, UserAction> actionsByName = actions.ToDictionary(a => a.Name, StringComparer.Ordinal);

        List<PermissionCatalogEntryDto> entries = attributesByPermission.Values
            .Select(attribute => BuildEntry(attribute, routesByPermission[attribute.Permission], actionsByName))
            .ToList();

        List<PermissionResourceGroupDto> resourceGroups = entries
            .GroupBy(entry => entry.Resource, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group =>
            {
                resourcesByName.TryGetValue(group.Key, out Resource? resource);
                return new PermissionResourceGroupDto
                {
                    Resource = group.Key,
                    DisplayName = resource?.DisplayName,
                    Description = resource?.Description,
                    Permissions = group.OrderBy(entry => entry.Action, StringComparer.Ordinal).ToList(),
                };
            })
            .ToList();

        List<OrphanedPermissionEntryDto> orphaned = await BuildOrphanedEntriesAsync(
            attributesByPermission.Keys,
            resourcesByName,
            actionsByName,
            cancellationToken).ConfigureAwait(false);

        return new PermissionCatalogDto
        {
            GeneratedAt = generatedAt,
            Resources = resourceGroups,
            OrphanedCatalogEntries = orphaned,
        };
    }

    private static PermissionCatalogEntryDto BuildEntry(
        RequirePermissionAttribute attribute,
        List<PermissionRouteDto> routes,
        Dictionary<string, UserAction> actionsByName)
    {
        actionsByName.TryGetValue(attribute.Action, out UserAction? action);
        return new PermissionCatalogEntryDto
        {
            Resource = attribute.Resource,
            Action = attribute.Action,
            Permission = attribute.Permission,
            ActionDisplayName = action?.DisplayName,
            ActionDescription = action?.Description,
            ImpliedByAdmin = !string.Equals(attribute.Action, "admin", StringComparison.Ordinal),
            Routes = routes,
        };
    }

    private async Task<List<OrphanedPermissionEntryDto>> BuildOrphanedEntriesAsync(
        IReadOnlyCollection<string> enforcedPermissions,
        Dictionary<string, Resource> resourcesByName,
        Dictionary<string, UserAction> actionsByName,
        CancellationToken cancellationToken)
    {
        var enforced = new HashSet<string>(enforcedPermissions, StringComparer.Ordinal);

        List<(string ResourceName, string ActionName)> grantedPairs = await _context.RolePermissions
            .AsNoTracking()
            .Select(rp => new { rp.Resource.Name, ActionName = rp.Action.Name })
            .Distinct()
            .Select(x => new ValueTuple<string, string>(x.Name, x.ActionName))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return grantedPairs
            .Where(pair => !enforced.Contains($"{pair.ResourceName}:{pair.ActionName}"))
            .OrderBy(pair => pair.ResourceName, StringComparer.Ordinal)
            .ThenBy(pair => pair.ActionName, StringComparer.Ordinal)
            .Select(pair =>
            {
                resourcesByName.TryGetValue(pair.ResourceName, out Resource? resource);
                actionsByName.TryGetValue(pair.ActionName, out UserAction? action);
                return new OrphanedPermissionEntryDto
                {
                    Resource = pair.ResourceName,
                    Action = pair.ActionName,
                    Permission = $"{pair.ResourceName}:{pair.ActionName}",
                    ResourceDisplayName = resource?.DisplayName,
                    ActionDisplayName = action?.DisplayName,
                };
            })
            .ToList();
    }
}
