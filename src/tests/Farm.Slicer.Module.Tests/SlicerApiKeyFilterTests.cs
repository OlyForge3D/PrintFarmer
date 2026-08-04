using Farm.Slicer.Module.Api.Filters;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Farm.Slicer.Module.Tests;

public class SlicerApiKeyFilterTests
{
    [Fact]
    public async Task RequireSlicerApiKey_NoValidatorRegistered_ReturnsUnauthorizedProblem()
    {
        bool executed = false;
        ActionExecutingContext context = CreateContext("Production");
        RequireSlicerApiKeyAttribute filter = new RequireSlicerApiKeyAttribute();

        await filter.OnActionExecutionAsync(context, () =>
        {
            executed = true;
            return Task.FromResult(new ActionExecutedContext(context, [], new object()));
        });

        _ = executed.Should().BeFalse();
        ObjectResult result = context.Result.Should().BeOfType<ObjectResult>().Subject;
        _ = result.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        ProblemDetails problem = result.Value.Should().BeOfType<ProblemDetails>().Subject;
        _ = problem.Extensions["code"].Should().Be("authentication_required");
    }

    [Fact]
    public async Task RequireSlicerServiceApiKey_NoValidatorRegistered_ReturnsUnauthorizedProblem()
    {
        bool executed = false;
        ActionExecutingContext context = CreateContext("Production");
        RequireSlicerServiceApiKeyAttribute filter = new RequireSlicerServiceApiKeyAttribute();

        await filter.OnActionExecutionAsync(context, () =>
        {
            executed = true;
            return Task.FromResult(new ActionExecutedContext(context, [], new object()));
        });

        _ = executed.Should().BeFalse();
        ObjectResult result = context.Result.Should().BeOfType<ObjectResult>().Subject;
        _ = result.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        ProblemDetails problem = result.Value.Should().BeOfType<ProblemDetails>().Subject;
        _ = problem.Extensions["code"].Should().Be("authentication_required");
    }

    private static ActionExecutingContext CreateContext(string environmentName)
    {
        ServiceProvider services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IWebHostEnvironment>(new TestWebHostEnvironment(environmentName))
            .AddSingleton<IHostEnvironment>(sp => sp.GetRequiredService<IWebHostEnvironment>())
            .BuildServiceProvider();

        DefaultHttpContext httpContext = new DefaultHttpContext
        {
            RequestServices = services
        };

        ActionContext actionContext = new ActionContext(
            httpContext,
            new RouteData(),
            new ActionDescriptor());

        return new ActionExecutingContext(
            actionContext,
            [],
            new Dictionary<string, object?>(),
            new object());
    }

    private sealed class TestWebHostEnvironment(string environmentName) : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = "Farm.Slicer.Module.Tests";

        public string WebRootPath { get; set; } = string.Empty;

        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();

        public string ContentRootPath { get; set; } = string.Empty;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
