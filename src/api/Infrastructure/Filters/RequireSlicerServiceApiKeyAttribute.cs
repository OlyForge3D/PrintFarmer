using System;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Slicing;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Primitives;

namespace Farm.Web.Api.Infrastructure.Filters;

/// <summary>
/// Action filter that enforces per-service API key authentication for SlicerService endpoints.
/// Checks X-Slicer-ApiKey header against the registered SlicerService's ApiKey.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class RequireSlicerServiceApiKeyAttribute : Attribute, IAsyncActionFilter
{
    private const string HeaderName = "X-Slicer-ApiKey";

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        // Only enforce for endpoints with a SlicerService id in route
        if (!context.RouteData.Values.TryGetValue("id", out object? idObj) || idObj == null)
        {
            context.Result = new UnauthorizedObjectResult(new { error = "Missing SlicerService id in route" });
            return;
        }

        if (!Guid.TryParse(idObj.ToString(), out Guid id))
        {
            context.Result = new UnauthorizedObjectResult(new { error = "Invalid SlicerService id" });
            return;
        }

        if (!context.HttpContext.Request.Headers.TryGetValue(HeaderName, out StringValues provided) || string.IsNullOrWhiteSpace(provided))
        {
            context.Result = new UnauthorizedObjectResult(new { error = "Missing X-Slicer-ApiKey header" });
            return;
        }

        // Resolve ISlicersRepository from DI
        ISlicersRepository? repo = context.HttpContext.RequestServices.GetService(typeof(ISlicersRepository)) as ISlicersRepository;
        if (repo == null)
        {
            context.Result = new UnauthorizedObjectResult(new { error = "SlicersRepository unavailable" });
            return;
        }

        SlicerService? svc = await repo.GetByIdAsync(id, context.HttpContext.RequestAborted);
        if (svc == null)
        {
            context.Result = new UnauthorizedObjectResult(new { error = "SlicerService not found" });
            return;
        }

        if (!string.Equals(provided.ToString(), svc.ApiKey, StringComparison.Ordinal))
        {
            context.Result = new UnauthorizedObjectResult(new { error = "Invalid X-Slicer-ApiKey" });
            return;
        }

        _ = await next();
    }
}
