using System.Security.Claims;
using Farm.Web.Api.Authorization;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Farm.Web.Api.Tests.Security;

public sealed class DevModeAuthorizationHandlerTests
{
    [Theory]
    [InlineData("Development", true, "GET", true)]
    [InlineData("Development", false, "GET", false)]
    [InlineData("Production", true, "GET", false)]
    [InlineData("Staging", true, "GET", false)]
    [InlineData("Development", true, "HEAD", true)]
    [InlineData("Development", true, "OPTIONS", true)]
    [InlineData("Development", true, "POST", false)]
    public async Task HandleAsync_BypassesOnlySafeDevelopmentRequests(
        string environmentName,
        bool bypassRequested,
        string method,
        bool expectedSuccess)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [DevModeAuthorizationHandler.ConfigurationKey] = bypassRequested.ToString(),
            })
            .Build();
        var environment = new Mock<IWebHostEnvironment>();
        environment.SetupGet(value => value.EnvironmentName).Returns(environmentName);
        var handler = new DevModeAuthorizationHandler(
            configuration,
            environment.Object,
            NullLogger<DevModeAuthorizationHandler>.Instance);
        var requirement = new TestRequirement();
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = method;
        var authorizationContext = new AuthorizationHandlerContext(
            [requirement],
            new ClaimsPrincipal(new ClaimsIdentity()),
            httpContext);

        await handler.HandleAsync(authorizationContext);

        authorizationContext.HasSucceeded.Should().Be(expectedSuccess);
    }

    private sealed class TestRequirement : IAuthorizationRequirement;
}
