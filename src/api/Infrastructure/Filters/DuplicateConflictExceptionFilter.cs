using Farm.Web.Api.Infrastructure.Exceptions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Farm.Web.Api.Infrastructure.Filters;

/// <summary>
/// Converts DuplicateEntityException into a standardized 409 ProblemDetails response.
/// </summary>
public sealed class DuplicateConflictExceptionFilter : IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        if (context.Exception is not DuplicateEntityException dup)
        {
            return;
        }

        var problem = new ProblemDetails
        {
            Title = $"{dup.EntityType} already exists",
            Detail = dup.Message,
            Status = StatusCodes.Status409Conflict
        };
        problem.Extensions["existing"] = dup.ExistingDto;
        if (!string.IsNullOrEmpty(dup.NormalizedName))
        {
            problem.Extensions["normalizedName"] = dup.NormalizedName;
            context.HttpContext.Response.Headers["X-Normalized-Name"] = dup.NormalizedName;
        }

        context.Result = new ObjectResult(problem)
        {
            StatusCode = StatusCodes.Status409Conflict
        };
        context.ExceptionHandled = true;
    }
}
