using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Web.Api.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Farm.Web.Api.Tests.Middleware;

/// <summary>
/// Regression tests for issue #2348: a client abort (request cancellation) must not be
/// reported as an HTTP 500 "unhandled exception" by <see cref="GlobalExceptionMiddleware"/>.
/// This is the monolith-hosting counterpart to the slicer profile hierarchy controller
/// fix - <c>Farm.Slicer.Module.Api</c> controllers are loaded into this host via
/// <c>Program.cs</c>, so rethrowing <see cref="OperationCanceledException"/> from a
/// controller action only avoids a 500 if this middleware also treats it as a client
/// disconnect rather than an unhandled exception.
/// </summary>
public class GlobalExceptionMiddlewareCancellationTests
{
    private sealed class CapturingLogger : ILogger<GlobalExceptionMiddleware>
    {
        public List<LogLevel> Levels { get; } = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Levels.Add(logLevel);
        }
    }

    [Fact]
    public async Task ClientAbort_DoesNotWrite500Response_AndDoesNotLogAtErrorSeverity()
    {
        using CancellationTokenSource cts = new();
        cts.Cancel();

        DefaultHttpContext context = new()
        {
            RequestAborted = cts.Token
        };

        CapturingLogger logger = new();
        GlobalExceptionMiddleware middleware = new(_ => throw new OperationCanceledException(cts.Token));

        // Should NOT throw and should NOT set a 500 status - the general exception
        // handler must never see this exception.
        await middleware.InvokeAsync(context, logger);

        Assert.NotEqual(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        Assert.DoesNotContain(LogLevel.Error, logger.Levels);
    }

    [Fact]
    public async Task UnrelatedCancellation_StillReturns500_AndLogsAtErrorSeverity()
    {
        // An OperationCanceledException NOT tied to the request's own aborted token
        // (e.g. an internal timeout) must still be treated as a genuine unhandled
        // exception, so a real internal fault is never silently masked as a benign
        // client disconnect.
        using CancellationTokenSource unrelatedCts = new();
        unrelatedCts.Cancel();

        DefaultHttpContext context = new()
        {
            RequestAborted = CancellationToken.None
        };
        context.Response.Body = new MemoryStream();

        CapturingLogger logger = new();
        GlobalExceptionMiddleware middleware = new(_ => throw new OperationCanceledException(unrelatedCts.Token));

        await middleware.InvokeAsync(context, logger);

        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        Assert.Contains(LogLevel.Error, logger.Levels);
    }
}
