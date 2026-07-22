using Farm.Infrastructure.Exceptions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Farm.Web.Api.Infrastructure.Filters;

/// <summary>
/// Converts model-collection domain exceptions into standardized ProblemDetails responses so the
/// controller can stay free of repetitive try/catch blocks.
/// </summary>
public sealed class CollectionExceptionFilter : IExceptionFilter
{
    /// <inheritdoc/>
    public void OnException(ExceptionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        int? status = context.Exception switch
        {
            CollectionNotFoundException => StatusCodes.Status404NotFound,
            CollectionModelNotFoundException => StatusCodes.Status404NotFound,
            CollectionAccessDeniedException => StatusCodes.Status403Forbidden,
            _ => null
        };

        if (status is null)
        {
            return;
        }

        var problem = new ProblemDetails
        {
            Title = ReasonPhrases(status.Value),
            Detail = context.Exception.Message,
            Status = status.Value
        };

        context.Result = new ObjectResult(problem)
        {
            StatusCode = status.Value
        };
        context.ExceptionHandled = true;
    }

    private static string ReasonPhrases(int status) => status switch
    {
        StatusCodes.Status403Forbidden => "Forbidden",
        StatusCodes.Status404NotFound => "Not Found",
        _ => "Error"
    };
}
