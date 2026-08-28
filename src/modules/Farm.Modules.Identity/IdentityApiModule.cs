using Farm.Modules.Abstractions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Farm.Modules.Identity;

/// <summary>
/// Vertical-slice module for identity: authentication, users, API keys, print quotas, roles,
/// permissions, security audit, and password policy (issue #2041, epic #2019). Owns
/// <see cref="Farm.Modules.Identity.Controllers.AuthController"/>,
/// <see cref="Farm.Modules.Identity.Controllers.UsersController"/>,
/// <see cref="Farm.Modules.Identity.Controllers.UserApiKeysController"/>,
/// <see cref="Farm.Modules.Identity.Controllers.QuotaController"/>,
/// <see cref="Farm.Modules.Identity.Controllers.PasswordPolicyController"/>,
/// <see cref="Farm.Modules.Identity.Controllers.Admin.RolesController"/>,
/// <see cref="Farm.Modules.Identity.Controllers.Admin.PermissionCatalogController"/>,
/// <see cref="Farm.Modules.Identity.Controllers.Admin.RolePermissionsController"/>, and
/// <see cref="Farm.Modules.Identity.Controllers.Admin.SecurityAuditController"/>, plus the permission
/// catalog / role permission grant services and the token-revocation cleanup hosted service.
/// Phase 13 of the Farm.Web.Api decomposition epic (see docs/MODULE_MIGRATION_PATTERN.md).
/// Namespaces were renamed from Farm.Web.Api.* to Farm.Modules.Identity.* by Phase 19
/// (issue #2047), completing the move-first-rename-last strategy. The underlying authentication/authorization services
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
            Farm.Modules.Identity.Services.Admin.IPermissionCatalogService,
            Farm.Modules.Identity.Services.Admin.PermissionCatalogService>();

        // Role permission grant read/write API (issue #1449). Reads/writes RolePermission
        // rows, validated against the permission catalog above.
        _ = services.AddScoped<
            Farm.Modules.Identity.Services.Admin.IRolePermissionService,
            Farm.Modules.Identity.Services.Admin.RolePermissionService>();

        // Periodically purges expired revoked-token entries so the revocation store doesn't
        // grow unbounded.
        _ = services.AddHostedService<
            Farm.Modules.Identity.Services.Authentication.TokenRevocationCleanupService>();
    }

    /// <inheritdoc />
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        // No minimal-API endpoints or SignalR hubs -- all nine controllers are attribute-routed
        // and discovered via the ApplicationPart added during module discovery.
    }
}
