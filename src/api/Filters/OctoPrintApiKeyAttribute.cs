using Farm.Web.Api.Services.OctoPrint;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Farm.Web.Api.Filters;

/// <summary>
/// Action filter that validates OctoPrint API keys based on OctoPrintSettings.RequireApiKey.
/// Apply to controllers/actions that need OctoPrint-style API key authentication.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class OctoPrintApiKeyAttribute : Attribute, IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var authService = context.HttpContext.RequestServices.GetRequiredService<IOctoPrintAuthService>();

        string? apiKey = context.HttpContext.Request.Headers["X-Api-Key"].ToString();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            apiKey = null;
        }

        bool allowed = await authService.ValidateApiKeyAsync(apiKey, targetPrinterId: null, userId: null);
        if (!allowed)
        {
            context.Result = new UnauthorizedObjectResult(new { error = "Invalid or missing API key" });
            return;
        }

        await next();
    }
}
