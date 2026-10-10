using Farm.Modules.Devices.Filters;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace Farm.Modules.Devices.Tests.Filters;

public class CameraConfigErrorFilterAttributeTests
{
    private readonly Mock<ILogger> _logger = new();

    [Fact]
    public void OnException_ClientAbort_LeavesCancellationToGlobalMiddleware()
    {
        ExceptionContext context = CreateContext(new OperationCanceledException(), requestAborted: true);

        new CameraConfigErrorFilterAttribute().OnException(context);

        context.ExceptionHandled.Should().BeFalse();
        context.Result.Should().BeNull();
        VerifyErrorLogged(Times.Never());
    }

    [Fact]
    public void OnException_CancellationWithoutClientAbort_IsSanitizedAndLogged()
    {
        ExceptionContext context = CreateContext(new OperationCanceledException("inner-detail"), requestAborted: false);

        new CameraConfigErrorFilterAttribute().OnException(context);

        AssertSanitizedProblem(context);
        VerifyErrorLogged(Times.Once());
    }

    [Fact]
    public void OnException_UnexpectedFailure_ReturnsGenericCorrelatedProblemAndLogs()
    {
        InvalidOperationException failure = new("outer-detail", new TimeoutException("inner-detail"));
        ExceptionContext context = CreateContext(failure, requestAborted: false);
        context.HttpContext.Items["CorrelationId"] = "corr-123";

        new CameraConfigErrorFilterAttribute().OnException(context);

        ProblemDetails problem = AssertSanitizedProblem(context);
        problem.Extensions["correlationId"].Should().Be("corr-123");
        VerifyErrorLogged(Times.Once(), failure);
    }

    private static ProblemDetails AssertSanitizedProblem(ExceptionContext context)
    {
        context.ExceptionHandled.Should().BeTrue();
        ObjectResult result = context.Result.Should().BeOfType<ObjectResult>().Subject;
        result.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
        ProblemDetails problem = result.Value.Should().BeOfType<ProblemDetails>().Subject;
        problem.Extensions["code"].Should().Be(CameraConfigErrorFilterAttribute.ProblemCode);
        problem.Detail.Should().BeNull();
        problem.Title.Should().NotContain("detail");
        return problem;
    }

    private void VerifyErrorLogged(Times times, Exception? exception = null) =>
        _logger.Verify(
            l => l.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.Is<Exception>(e => exception == null || ReferenceEquals(e, exception)),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            times);

    private ExceptionContext CreateContext(Exception exception, bool requestAborted)
    {
        Mock<ILoggerFactory> loggerFactory = new();
        loggerFactory.Setup(f => f.CreateLogger(It.IsAny<string>())).Returns(_logger.Object);
        ServiceCollection services = new();
        services.AddSingleton(loggerFactory.Object);
        DefaultHttpContext httpContext = new()
        {
            RequestServices = services.BuildServiceProvider(),
            RequestAborted = new CancellationToken(requestAborted),
        };
        ActionContext actionContext = new(httpContext, new RouteData(), new ActionDescriptor());
        return new ExceptionContext(actionContext, []) { Exception = exception };
    }
}
