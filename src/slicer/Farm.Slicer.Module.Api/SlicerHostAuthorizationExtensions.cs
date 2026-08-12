using Farm.Infrastructure.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace Farm.Slicer.Module.Api;

/// <summary>
/// Registers the slicer host's <see cref="AuthorizationOptions"/> policies. Extracted from
/// <c>Farm.Slicer.Host/Program.cs</c> (issue #1467) so tests — notably
/// <c>AuthorizeRolesGateArchitectureTests</c> — can build the exact same
/// <see cref="AuthorizationOptions"/> the running host uses, without needing to spin up the
/// full <c>Farm.Slicer.Host</c> web application (JWT config, database, etc.).
/// </summary>
public static class SlicerHostAuthorizationExtensions
{
    /// <summary>
    /// Adds the slicer host's non-authentication-dependent authorization policies: the Desktop
    /// API-key exchange scope policies (issue #838) and the interactive-session gate (issue
    /// #1470-adjacent InteractiveSessionRequirement). Does not register JWT bearer
    /// authentication — callers that need real authentication should still call
    /// <c>AddAuthentication().AddJwtBearer(...)</c> separately, as <c>Farm.Slicer.Host/Program.cs</c>
    /// does.
    /// </summary>
    public static IServiceCollection AddSlicerHostAuthorization(this IServiceCollection services)
    {
        services.AddAuthorization(options =>
        {
            // Desktop exchange tokens remain scope-gated; regular JWTs pass these policies.
            options.AddPolicy("ModelRead", policy =>
            {
                _ = policy.RequireAuthenticatedUser();
                _ = policy.AddRequirements(new DesktopScopeRequirement("ModelRead"));
            });
            options.AddPolicy("ModelWrite", policy =>
            {
                _ = policy.RequireAuthenticatedUser();
                _ = policy.AddRequirements(new DesktopScopeRequirement("ModelWrite"));
            });
            options.AddPolicy("LibrarySync", policy =>
            {
                _ = policy.RequireAuthenticatedUser();
                _ = policy.AddRequirements(new DesktopScopeRequirement("LibrarySync"));
            });

            // Profile-state mutations require an interactive session. slicing:submit is a broad
            // class-level permission on ProfilesController, so a Desktop-exchange token issued
            // for calibration generation would otherwise also be able to upload, clone, and edit
            // custom profiles. Normal login/session principals - and the standalone-mode admin
            // principal - are unaffected.
            options.AddPolicy(InteractiveSessionRequirement.PolicyName, policy =>
            {
                _ = policy.RequireAuthenticatedUser();
                _ = policy.AddRequirements(new InteractiveSessionRequirement());
            });
        });
        services.AddSingleton<IAuthorizationHandler, DesktopScopeAuthorizationHandler>();
        services.AddSingleton<IAuthorizationHandler, InteractiveSessionAuthorizationHandler>();

        return services;
    }
}
