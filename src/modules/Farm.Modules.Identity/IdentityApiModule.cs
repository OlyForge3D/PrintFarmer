using Farm.Modules.Abstractions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Farm.Modules.Identity;

/// <summary>
/// Vertical-slice module for identity: authentication, users, API keys, print quotas, roles,
/// permissions, security audit, and password policy (issue #2041, epic #2019). Owns
/// <see cref="Farm.Web.Api.Controllers.AuthController"/>,
/// <see cref="Farm.Web.Api.Controllers.UsersController"/>,
/// <see cref="Farm.Web.Api.Controllers.UserApiKeysController"/>,
/// <see cref="Farm.Web.Api.Controllers.QuotaController"/>,
/// <see cref="Farm.Web.Api.Controllers.PasswordPolicyController"/>,
/// <see cref="Farm.Web.Api.Controllers.Admin.RolesController"/>,
/// <see cref="Farm.Web.Api.Controllers.Admin.PermissionCatalogController"/>,
/// <see cref="Farm.Web.Api.Controllers.Admin.RolePermissionsController"/>, and
/// <see cref="Farm.Web.Api.Controllers.Admin.SecurityAuditController"/>, plus the permission
/// catalog / role permission grant services and the token-revocation cleanup hosted service.
/// Phase 13 of the Farm.Web.Api decomposition epic (see docs/MODULE_MIGRATION_PATTERN.md).
/// Namespaces are intentionally unchanged from their prior Farm.Web.Api location
/// (move-first-rename-last). The underlying authentication/authorization services
/// (AuthenticationService, TokenRevocationService, AccountLockoutService,
/// ApiKeyExchangeService, PasskeyService, RoleManagementService, EffectivePermissionsRevocationService,
/// UsersService) remain registered by Farm.Infrastructure's own DI wiring and are not part of
/// this move.
/// </summary>
public sealed class IdentityApiModule : IApiModule
{
    /// <inheritdoc />
    public string Name => "Identity";

    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        // Permission catalog derived from EndpointDataSource (issue #1446). Read-only; does
        // not seed or mutate the database catalog.
        _ = services.AddScoped<
            Farm.Web.Api.Services.Admin.IPermissionCatalogService,
            Farm.Web.Api.Services.Admin.PermissionCatalogService>();

        // Role permission grant read/write API (issue #1449). Reads/writes RolePermission
        // rows, validated against the permission catalog above.
        _ = services.AddScoped<
            Farm.Web.Api.Services.Admin.IRolePermissionService,
            Farm.Web.Api.Services.Admin.RolePermissionService>();

        // Periodically purges expired revoked-token entries so the revocation store doesn't
        // grow unbounded.
        _ = services.AddHostedService<
            Farm.Web.Api.Services.Authentication.TokenRevocationCleanupService>();
    }

    /// <inheritdoc />
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        // No minimal-API endpoints or SignalR hubs -- all nine controllers are attribute-routed
        // and discovered via the ApplicationPart added during module discovery.
    }
}
