using Farm.Infrastructure.Exceptions;
using Farm.Web.Api.Infrastructure.Filters;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Xunit;

namespace Farm.Web.Api.Tests.Infrastructure;

public sealed class CollectionExceptionFilterTests
{
    private static ExceptionContext CreateContext(Exception exception)
    {
        var actionContext = new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor());
        return new ExceptionContext(actionContext, new List<IFilterMetadata>())
        {
            Exception = exception
        };
    }

    [Fact]
    public void OnException_AccessDenied_MapsTo403()
    {
        var filter = new CollectionExceptionFilter();
        ExceptionContext context = CreateContext(new CollectionAccessDeniedException());

        filter.OnException(context);

        var result = Assert.IsType<ObjectResult>(context.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
        Assert.True(context.ExceptionHandled);
    }

    [Fact]
    public void OnException_CollectionNotFound_MapsTo404()
    {
        var filter = new CollectionExceptionFilter();
        ExceptionContext context = CreateContext(new CollectionNotFoundException(Guid.NewGuid()));

        filter.OnException(context);

        var result = Assert.IsType<ObjectResult>(context.Result);
        Assert.Equal(StatusCodes.Status404NotFound, result.StatusCode);
    }

    [Fact]
    public void OnException_ModelNotFound_MapsTo404()
    {
        var filter = new CollectionExceptionFilter();
        ExceptionContext context = CreateContext(new CollectionModelNotFoundException(Guid.NewGuid()));

        filter.OnException(context);

        var result = Assert.IsType<ObjectResult>(context.Result);
        Assert.Equal(StatusCodes.Status404NotFound, result.StatusCode);
    }

    [Fact]
    public void OnException_UnrelatedException_IsIgnored()
    {
        var filter = new CollectionExceptionFilter();
        ExceptionContext context = CreateContext(new InvalidOperationException("boom"));

        filter.OnException(context);

        Assert.Null(context.Result);
        Assert.False(context.ExceptionHandled);
    }
}
