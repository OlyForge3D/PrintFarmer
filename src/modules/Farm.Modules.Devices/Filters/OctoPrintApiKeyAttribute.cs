using Farm.Modules.Devices.Services.OctoPrint;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;

namespace Farm.Modules.Devices.Filters;

/// <summary>
/// Authorization filter that accepts an authenticated user or validates an OctoPrint API key.
/// Apply to controllers/actions that need OctoPrint-style API key authentication.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class OctoPrintApiKeyAttribute : Attribute, IAsyncAuthorizationFilter
{
    public bool RequireValidKeyForAnonymous { get; init; }

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        if (context.HttpContext.User.Identity?.IsAuthenticated == true)
        {
            return;
        }

        var authService = context.HttpContext.RequestServices.GetRequiredService<IOctoPrintAuthService>();

        string? apiKey = context.HttpContext.Request.Headers["X-Api-Key"].ToString();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            apiKey = null;
        }

        bool allowed = await authService.ValidateApiKeyAsync(
            apiKey,
            requireValidKey: RequireValidKeyForAnonymous);
        if (!allowed)
        {
            context.Result = new UnauthorizedObjectResult(new { error = "Invalid or missing API key" });
        }
    }
}
