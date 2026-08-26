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
    [InlineData("Development", true, "GET", true, true)]
    [InlineData("Development", true, "GET", false, false)]
    [InlineData("Development", false, "GET", true, false)]
    [InlineData("Production", true, "GET", true, false)]
    [InlineData("Staging", true, "GET", true, false)]
    [InlineData("Development", true, "HEAD", true, true)]
    [InlineData("Development", true, "OPTIONS", true, true)]
    [InlineData("Development", true, "POST", true, false)]
    public async Task HandleAsync_BypassesOnlySafeDevelopmentRequests(
        string environmentName,
        bool bypassRequested,
        string method,
        bool authenticated,
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
        ClaimsIdentity identity = authenticated
            ? new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())], "TestAuth")
            : new ClaimsIdentity();
        var authorizationContext = new AuthorizationHandlerContext(
            [requirement],
            new ClaimsPrincipal(identity),
            httpContext);

        await handler.HandleAsync(authorizationContext);

        authorizationContext.HasSucceeded.Should().Be(expectedSuccess);
    }

    private sealed class TestRequirement : IAuthorizationRequirement;
}
