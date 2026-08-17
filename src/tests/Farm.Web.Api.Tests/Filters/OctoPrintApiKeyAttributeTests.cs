using System.Security.Claims;
using Farm.Web.Api.Filters;
using Farm.Web.Api.Services.OctoPrint;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Filters;

public class OctoPrintApiKeyAttributeTests
{
    [Fact]
    public async Task OnAuthorizationAsync_AuthenticatedUser_DoesNotRequireApiKey()
    {
        DefaultHttpContext httpContext = new()
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())],
                "test")),
        };
        AuthorizationFilterContext context = CreateContext(httpContext);
        var attribute = new OctoPrintApiKeyAttribute { RequireValidKeyForAnonymous = true };

        await attribute.OnAuthorizationAsync(context);

        Assert.Null(context.Result);
    }

    [Fact]
    public async Task OnAuthorizationAsync_AnonymousUpload_RequiresValidApiKey()
    {
        var authService = new Mock<IOctoPrintAuthService>(MockBehavior.Strict);
        authService
            .Setup(service => service.ValidateApiKeyAsync(null, true))
            .ReturnsAsync(false);
        var services = new Mock<IServiceProvider>(MockBehavior.Strict);
        services
            .Setup(provider => provider.GetService(typeof(IOctoPrintAuthService)))
            .Returns(authService.Object);
        DefaultHttpContext httpContext = new()
        {
            RequestServices = services.Object,
        };
        AuthorizationFilterContext context = CreateContext(httpContext);
        var attribute = new OctoPrintApiKeyAttribute { RequireValidKeyForAnonymous = true };

        await attribute.OnAuthorizationAsync(context);

        _ = Assert.IsType<UnauthorizedObjectResult>(context.Result);
        authService.VerifyAll();
    }

    private static AuthorizationFilterContext CreateContext(HttpContext httpContext)
    {
        ActionContext actionContext = new(
            httpContext,
            new RouteData(),
            new ActionDescriptor());

        return new AuthorizationFilterContext(actionContext, []);
    }
}
