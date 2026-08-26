using System.Net;
using System.Text.Json;
using Farm.Infrastructure.Services.RateLimiting;
using Farm.Web.Api.Middleware;
using Farm.Web.Api.Tests.TestInfrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Middleware;

/// <summary>
/// Regression tests for issue #862 (spoofable X-Forwarded-For bypasses per-IP rate limits).
///
/// The middleware must key exclusively on <c>Connection.RemoteIpAddress</c>. Trusting
/// <c>X-Forwarded-For</c> is the framework's ForwardedHeadersMiddleware's job, and only
/// activates when the immediate connection is from an operator-declared trusted proxy.
/// From this test's perspective, whatever value the caller places in
/// <c>Connection.RemoteIpAddress</c> IS the effective client IP; a raw header must never
/// change it.
/// </summary>
[Collection(RateLimiterEnvSerialCollection.Name)]
public class AuthenticationRateLimitMiddlewareTests
{
    private const string LoginPath = "/api/auth/login";

    private static AuthenticationRateLimitMiddleware CreateMiddleware(RequestDelegate next)
        => new(next, NullLogger<AuthenticationRateLimitMiddleware>.Instance);

    private static DefaultHttpContext CreatePostRequest(string path, IPAddress? remoteIp, string? forwardedFor = null)
    {
        DefaultHttpContext ctx = new();
        ctx.Request.Method = HttpMethods.Post;
        ctx.Request.Path = path;
        ctx.Connection.RemoteIpAddress = remoteIp;
        if (forwardedFor is not null)
        {
            ctx.Request.Headers["X-Forwarded-For"] = forwardedFor;
        }

        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    private static Mock<IRateLimitService> CreateAllowingService()
    {
        Mock<IRateLimitService> mock = new(MockBehavior.Strict);
        _ = mock.Setup(s => s.CheckLoginLimitAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RateLimitResult(IsAllowed: true, RemainingAttempts: 10));
        _ = mock.Setup(s => s.RecordLoginAttemptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _ = mock.Setup(s => s.CheckRegisterLimitAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RateLimitResult(IsAllowed: true, RemainingAttempts: 10));
        _ = mock.Setup(s => s.RecordRegisterAttemptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _ = mock.Setup(s => s.CheckApiKeyExchangeLimitAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RateLimitResult(IsAllowed: true, RemainingAttempts: 5));
        _ = mock.Setup(s => s.RecordApiKeyExchangeAttemptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return mock;
    }

    [Fact]
    public async Task SpoofedXForwardedFor_FromUntrustedConnection_IsIgnored_UsesRemoteIpAddress()
    {
        // Arrange - TCP peer is 203.0.113.7; the header claims a different address. With
        // no forwarded-headers middleware configured in this unit test the header must not
        // influence the rate-limit key.
        DefaultHttpContext ctx = CreatePostRequest(
            LoginPath,
            IPAddress.Parse("203.0.113.7"),
            forwardedFor: "10.0.0.99");

        Mock<IRateLimitService> service = CreateAllowingService();
        AuthenticationRateLimitMiddleware middleware = CreateMiddleware(_ => Task.CompletedTask);

        await middleware.InvokeAsync(ctx, service.Object);

        service.Verify(s => s.CheckLoginLimitAsync("203.0.113.7", It.IsAny<CancellationToken>()), Times.Once);
        service.Verify(s => s.RecordLoginAttemptAsync("203.0.113.7", It.IsAny<CancellationToken>()), Times.Once);
        service.Verify(s => s.CheckLoginLimitAsync("10.0.0.99", It.IsAny<CancellationToken>()), Times.Never);
        service.Verify(s => s.RecordLoginAttemptAsync("10.0.0.99", It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RotatingSpoofedXForwardedFor_FromSameConnection_ShareSameRateLimitBucket()
    {
        // Arrange - same TCP peer, different spoofed header values on each request. Before
        // the fix a caller could rotate this header to get a fresh bucket per request;
        // after the fix every request from 198.51.100.5 must key on that IP.
        Mock<IRateLimitService> service = CreateAllowingService();
        AuthenticationRateLimitMiddleware middleware = CreateMiddleware(_ => Task.CompletedTask);

        for (int i = 0; i < 3; i++)
        {
            DefaultHttpContext ctx = CreatePostRequest(
                LoginPath,
                IPAddress.Parse("198.51.100.5"),
                forwardedFor: $"10.0.0.{i}");
            await middleware.InvokeAsync(ctx, service.Object);
        }

        service.Verify(s => s.CheckLoginLimitAsync("198.51.100.5", It.IsAny<CancellationToken>()), Times.Exactly(3));
        service.Verify(s => s.RecordLoginAttemptAsync("198.51.100.5", It.IsAny<CancellationToken>()), Times.Exactly(3));
    }

    [Fact]
    public async Task ForwardedHeadersRewroteRemoteIp_MiddlewareTrustsThatValue()
    {
        // Arrange - simulate the state after ForwardedHeadersMiddleware runs on a trusted
        // proxy connection: Connection.RemoteIpAddress has been rewritten to the real
        // client IP (203.0.113.42). The raw X-Forwarded-For header remains but is
        // irrelevant; we trust the rewritten connection value.
        DefaultHttpContext ctx = CreatePostRequest(
            LoginPath,
            IPAddress.Parse("203.0.113.42"),
            forwardedFor: "203.0.113.42, 10.1.2.3");

        Mock<IRateLimitService> service = CreateAllowingService();
        AuthenticationRateLimitMiddleware middleware = CreateMiddleware(_ => Task.CompletedTask);

        await middleware.InvokeAsync(ctx, service.Object);

        service.Verify(s => s.CheckLoginLimitAsync("203.0.113.42", It.IsAny<CancellationToken>()), Times.Once);
        service.Verify(s => s.RecordLoginAttemptAsync("203.0.113.42", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DirectConnection_NoForwardedHeader_KeysOnRemoteIpAddress()
    {
        DefaultHttpContext ctx = CreatePostRequest(LoginPath, IPAddress.Parse("192.0.2.10"));
        Mock<IRateLimitService> service = CreateAllowingService();
        AuthenticationRateLimitMiddleware middleware = CreateMiddleware(_ => Task.CompletedTask);

        await middleware.InvokeAsync(ctx, service.Object);

        service.Verify(s => s.CheckLoginLimitAsync("192.0.2.10", It.IsAny<CancellationToken>()), Times.Once);
        service.Verify(s => s.RecordLoginAttemptAsync("192.0.2.10", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task NullRemoteIpAddress_UsesUnknownFallback()
    {
        DefaultHttpContext ctx = CreatePostRequest(LoginPath, remoteIp: null);
        Mock<IRateLimitService> service = CreateAllowingService();
        AuthenticationRateLimitMiddleware middleware = CreateMiddleware(_ => Task.CompletedTask);

        await middleware.InvokeAsync(ctx, service.Object);

        service.Verify(s => s.CheckLoginLimitAsync("unknown", It.IsAny<CancellationToken>()), Times.Once);
        service.Verify(s => s.RecordLoginAttemptAsync("unknown", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Ipv4MappedIpv6RemoteIp_IsNormalizedToIpv4()
    {
        // Arrange - a proxy connecting on a dual-stack socket may present the client IP as
        // ::ffff:203.0.113.9. That must map to the same bucket as 203.0.113.9 to prevent a
        // trivial per-request bucket rotation just by changing socket family.
        DefaultHttpContext ctx = CreatePostRequest(LoginPath, IPAddress.Parse("::ffff:203.0.113.9"));
        Mock<IRateLimitService> service = CreateAllowingService();
        AuthenticationRateLimitMiddleware middleware = CreateMiddleware(_ => Task.CompletedTask);

        await middleware.InvokeAsync(ctx, service.Object);

        service.Verify(s => s.CheckLoginLimitAsync("203.0.113.9", It.IsAny<CancellationToken>()), Times.Once);
        service.Verify(s => s.RecordLoginAttemptAsync("203.0.113.9", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RateLimitExceeded_ReturnsFourTwoNine_AndDoesNotRecord()
    {
        DefaultHttpContext ctx = CreatePostRequest(LoginPath, IPAddress.Parse("198.51.100.6"));
        Mock<IRateLimitService> service = new(MockBehavior.Strict);
        _ = service.Setup(s => s.CheckLoginLimitAsync("198.51.100.6", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RateLimitResult(
                IsAllowed: false,
                RemainingAttempts: 0,
                RetryAfter: TimeSpan.FromSeconds(30),
                Message: "Too many login attempts"));

        bool nextInvoked = false;
        AuthenticationRateLimitMiddleware middleware = CreateMiddleware(_ =>
        {
            nextInvoked = true;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(ctx, service.Object);

        Assert.Equal(StatusCodes.Status429TooManyRequests, ctx.Response.StatusCode);
        Assert.Equal("30", ctx.Response.Headers["Retry-After"].ToString());
        Assert.False(nextInvoked, "Downstream pipeline must be short-circuited on 429");
        service.Verify(s => s.RecordLoginAttemptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

        ctx.Response.Body.Position = 0;
        using JsonDocument body = JsonDocument.Parse(ctx.Response.Body);
        Assert.Equal("Too Many Requests", body.RootElement.GetProperty("error").GetString());
        Assert.Equal("Too many login attempts", body.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task NonAuthEndpoint_BypassesRateLimiter()
    {
        DefaultHttpContext ctx = CreatePostRequest("/api/printers", IPAddress.Parse("203.0.113.1"));
        Mock<IRateLimitService> service = new(MockBehavior.Strict);
        bool nextInvoked = false;
        AuthenticationRateLimitMiddleware middleware = CreateMiddleware(_ =>
        {
            nextInvoked = true;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(ctx, service.Object);

        Assert.True(nextInvoked);
        service.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task GetOnAuthEndpoint_StillCountsAgainstRateLimiter()
    {
        // Regression test: the limiter must key on the request PATH only. Previously a
        // "method != POST" fast-path let a caller skip the limiter entirely just by
        // sending a non-POST verb (attacker-controlled bypass), even though the value
        // driving that skip - Request.Method - is fully attacker-controlled. Any verb
        // hitting a rate-limited auth path must still be checked/recorded.
        DefaultHttpContext ctx = new();
        ctx.Request.Method = HttpMethods.Get;
        ctx.Request.Path = LoginPath;
        ctx.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.2");
        ctx.Response.Body = new MemoryStream();

        Mock<IRateLimitService> service = CreateAllowingService();
        bool nextInvoked = false;
        AuthenticationRateLimitMiddleware middleware = CreateMiddleware(_ =>
        {
            nextInvoked = true;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(ctx, service.Object);

        Assert.True(nextInvoked);
        service.Verify(s => s.CheckLoginLimitAsync("203.0.113.2", It.IsAny<CancellationToken>()), Times.Once);
        service.Verify(s => s.RecordLoginAttemptAsync("203.0.113.2", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task NonPostVerbRotation_CannotBypassRateLimit()
    {
        // An attacker who rotates HTTP verbs (GET, PUT, DELETE, ...) on the same login
        // path must still hit the same limiter bucket as POST attempts - the verb must
        // never be a way to dodge CheckLoginLimitAsync/RecordLoginAttemptAsync.
        Mock<IRateLimitService> service = new(MockBehavior.Strict);
        _ = service.Setup(s => s.CheckLoginLimitAsync("203.0.113.3", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RateLimitResult(IsAllowed: true, RemainingAttempts: 10));
        _ = service.Setup(s => s.RecordLoginAttemptAsync("203.0.113.3", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        AuthenticationRateLimitMiddleware middleware = CreateMiddleware(_ => Task.CompletedTask);

        foreach (string verb in new[] { HttpMethods.Get, HttpMethods.Put, HttpMethods.Delete, HttpMethods.Post })
        {
            DefaultHttpContext ctx = new();
            ctx.Request.Method = verb;
            ctx.Request.Path = LoginPath;
            ctx.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.3");
            ctx.Response.Body = new MemoryStream();

            await middleware.InvokeAsync(ctx, service.Object);
        }

        service.Verify(s => s.CheckLoginLimitAsync("203.0.113.3", It.IsAny<CancellationToken>()), Times.Exactly(4));
        service.Verify(s => s.RecordLoginAttemptAsync("203.0.113.3", It.IsAny<CancellationToken>()), Times.Exactly(4));
    }

    [Fact]
    public async Task ApiKeyExchangeEndpoint_KeysOnRemoteIpAddress()
    {
        DefaultHttpContext ctx = CreatePostRequest(
            "/api/auth/api-key/exchange",
            IPAddress.Parse("192.0.2.55"),
            forwardedFor: "10.1.1.1");

        Mock<IRateLimitService> service = CreateAllowingService();
        AuthenticationRateLimitMiddleware middleware = CreateMiddleware(_ => Task.CompletedTask);

        await middleware.InvokeAsync(ctx, service.Object);

        service.Verify(s => s.CheckApiKeyExchangeLimitAsync("192.0.2.55", It.IsAny<CancellationToken>()), Times.Once);
        service.Verify(s => s.RecordApiKeyExchangeAttemptAsync("192.0.2.55", It.IsAny<CancellationToken>()), Times.Once);
        service.Verify(s => s.CheckApiKeyExchangeLimitAsync("10.1.1.1", It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TestDisableRateLimiterEnv_BypassesEverything()
    {
        const string envVar = "TEST_DISABLE_RATE_LIMITER";
        string? previous = Environment.GetEnvironmentVariable(envVar);
        try
        {
            Environment.SetEnvironmentVariable(envVar, "true");

            DefaultHttpContext ctx = CreatePostRequest(LoginPath, IPAddress.Parse("203.0.113.99"));
            Mock<IRateLimitService> service = new(MockBehavior.Strict);
            bool nextInvoked = false;
            AuthenticationRateLimitMiddleware middleware = CreateMiddleware(_ =>
            {
                nextInvoked = true;
                return Task.CompletedTask;
            });

            await middleware.InvokeAsync(ctx, service.Object);

            Assert.True(nextInvoked);
            service.VerifyNoOtherCalls();
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVar, previous);
        }
    }
}
