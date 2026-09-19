using Farm.Infrastructure.Authorization;
using Farm.Infrastructure.Security;
using Farm.Infrastructure.Settings;
using Farm.Modules.Devices.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;

namespace Farm.Web.Api.Authorization;

/// <summary>
/// Allows trusted-network uploads when API-key enforcement is disabled, without bypassing
/// queue permissions for authenticated callers.
/// </summary>
public static class OctoPrintUploadPolicy
{
    public const string Name = "OctoPrintUpload";

    /// <summary>Configures authentication and the setting-aware upload authorization gate.</summary>
    public static void Configure(AuthorizationPolicyBuilder policy)
    {
        policy.AddAuthenticationSchemes(
            JwtBearerDefaults.AuthenticationScheme,
            OctoPrintApiKeyDefaults.AuthenticationScheme);
        policy.RequireAssertion(AuthorizeAsync);
    }

    private static async Task<bool> AuthorizeAsync(AuthorizationHandlerContext context)
    {
        if (context.Resource is not HttpContext httpContext)
        {
            return false;
        }

        if (context.User.Identity?.IsAuthenticated == true)
        {
            var authorization = httpContext.RequestServices.GetRequiredService<IAuthorizationService>();
            AuthorizationResult result = await authorization.AuthorizeAsync(
                context.User,
                context.Resource,
                new RequirePermissionAttribute(PrintFarmerPermissions.Queue.Write));
            return result.Succeeded;
        }

        var settings = httpContext.RequestServices.GetRequiredService<ISettingsService>();
        if (settings.Get<OctoPrintSettings>().RequireApiKey)
        {
            return false;
        }

        // The combined policy authentication result discards individual scheme failures.
        // Never turn a rejected credential into a trusted anonymous queue actor.
        AuthenticateResult bearer = await httpContext.AuthenticateAsync(JwtBearerDefaults.AuthenticationScheme);
        AuthenticateResult apiKey = await httpContext.AuthenticateAsync(OctoPrintApiKeyDefaults.AuthenticationScheme);
        return bearer.Failure is null && apiKey.Failure is null;
    }
}
