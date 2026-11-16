using System.Security.Claims;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Authentication;
using Farm.Web.Api;
using Farm.Web.Api.Startup;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Infrastructure;

/// <summary>
/// Verifies the JWT bearer <c>OnMessageReceived</c> handler resolves the access token for SignalR
/// hub connections whose transport (WebSocket / SSE) cannot send an Authorization header and must
/// fall back to the <c>?access_token=</c> query parameter.
/// </summary>
public class JwtEventsAccessTokenQueryTests
{
    private static async Task<string?> RunOnMessageReceivedAsync(string path, string? queryString, string? authHeader)
    {
        JwtBearerEvents events = ProgramHelpers.CreateJwtEvents(null, null);

        DefaultHttpContext httpContext = new();
        httpContext.Request.Path = path;
        if (queryString is not null)
        {
            httpContext.Request.QueryString = new QueryString(queryString);
        }
        if (authHeader is not null)
        {
            httpContext.Request.Headers.Authorization = authHeader;
        }

        AuthenticationScheme scheme = new(
            JwtBearerDefaults.AuthenticationScheme,
            displayName: null,
            handlerType: typeof(JwtBearerHandler));
        MessageReceivedContext context = new(httpContext, scheme, new JwtBearerOptions());

        await events.OnMessageReceived(context);
        return context.Token;
    }

    [Fact]
    public async Task ReadsAccessTokenFromQuery_ForHubPath_WhenNoAuthHeader()
    {
        string? token = await RunOnMessageReceivedAsync("/hubs/slicers", "?access_token=jwt-from-query", authHeader: null);
        Assert.Equal("jwt-from-query", token);
    }

    [Fact]
    public async Task IgnoresQueryAccessToken_ForNonHubPath()
    {
        string? token = await RunOnMessageReceivedAsync("/api/settings/metadata", "?access_token=jwt-from-query", authHeader: null);
        Assert.True(string.IsNullOrEmpty(token));
    }

    [Fact]
    public async Task AuthorizationHeaderTakesPrecedence_OverQueryAccessToken()
    {
        string? token = await RunOnMessageReceivedAsync("/hubs/slicers", "?access_token=jwt-from-query", authHeader: "Bearer header-token");
        Assert.Equal("header-token", token);
    }

    [Fact]
    public async Task ReadsBearerHeader_ForApiPath()
    {
        string? token = await RunOnMessageReceivedAsync("/api/printers", queryString: null, authHeader: "Bearer header-token");
        Assert.Equal("header-token", token);
    }

    [Fact]
    public async Task AddPrintFarmerAuthentication_Production_RegistersHubQueryAccessTokenReader()
    {
        ServiceCollection services = new();
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Key"] = "0123456789abcdef0123456789abcdef",
                ["Jwt:Issuer"] = "PrintFarmer",
                ["Jwt:Audience"] = "PrintFarmer"
            })
            .Build();
        Mock<IWebHostEnvironment> environment = new();
        environment.SetupGet(e => e.EnvironmentName).Returns("Production");

        _ = services.AddLogging();
        _ = services.AddPrintFarmerAuthentication(configuration, environment.Object);
        using ServiceProvider provider = services.BuildServiceProvider();
        JwtBearerOptions options = provider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get("Bearer");

        DefaultHttpContext httpContext = new();
        httpContext.Request.Path = "/hubs/maintenance";
        httpContext.Request.QueryString = new QueryString("?access_token=prod-query-token");
        AuthenticationScheme scheme = new(
            JwtBearerDefaults.AuthenticationScheme,
            displayName: null,
            handlerType: typeof(JwtBearerHandler));
        MessageReceivedContext context = new(httpContext, scheme, options);

        await options.Events.OnMessageReceived(context);

        Assert.Equal("prod-query-token", context.Token);
    }

    [Theory]
    [InlineData("wrapped-database")]
    [InlineData("wrapped-timeout")]
    [InlineData("invalid-operation")]
    [InlineData("missing-service")]
    [InlineData("disposed")]
    [InlineData("database")]
    [InlineData("update")]
    [InlineData("timeout")]
    [InlineData("internal-cancellation")]
    [InlineData("caller-cancellation")]
    [InlineData("unexpected")]
    public async Task TokenValidation_RevocationLookupFailure_HandlesOnlyExpectedFailures(string scenario)
    {
        using CancellationTokenSource cancellation = new();
        Exception failure = scenario switch
        {
            "wrapped-database" => new InvalidOperationException("Provider wrapper.", new SqliteException("Store unavailable.", 14)),
            "wrapped-timeout" => new InvalidOperationException("Provider wrapper.", new TimeoutException("Store timeout.")),
            "disposed" => new ObjectDisposedException("revocation-store"),
            "database" => new SqliteException("Revocation store unavailable.", 14),
            "update" => new DbUpdateException("Revocation store unavailable."),
            "timeout" => new TimeoutException("Revocation store unavailable."),
            "internal-cancellation" => new OperationCanceledException("Revocation store cancelled."),
            "caller-cancellation" => new OperationCanceledException(cancellation.Token),
            "unexpected" => new ArgumentException("Revocation implementation fault."),
            _ => new InvalidOperationException("Revocation store unavailable.")
        };
        Mock<ITokenRevocationService> revocationService = new(MockBehavior.Strict);
        revocationService
            .Setup(service => service.IsTokenRevokedAsync("access-token", It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                if (scenario == "caller-cancellation")
                {
                    await cancellation.CancelAsync();
                }
                return await Task.FromException<bool>(failure);
            });
        List<string> logs = [];
        Guid userId = Guid.NewGuid();
        ServiceCollection services = new();
        services
            .AddDbContext<AppDbContext>(options => options.UseInMemoryDatabase($"revocation-failure-{Guid.NewGuid()}"))
            .AddSingleton<ILoggerFactory>(new CapturingLoggerFactory(logs));
        if (scenario != "missing-service")
        {
            services.AddSingleton(revocationService.Object);
        }
        using ServiceProvider provider = services.BuildServiceProvider();
        AppDbContext db = provider.GetRequiredService<AppDbContext>();
        db.Users.Add(new User { Id = userId, Username = "active-user", Email = "active@example.com", PasswordHash = "unused", IsActive = true });
        await db.SaveChangesAsync();

        DefaultHttpContext httpContext = new()
        {
            RequestServices = provider,
            RequestAborted = cancellation.Token
        };
        httpContext.Request.Headers.Authorization = "Bearer access-token";
        AuthenticationScheme scheme = new(
            JwtBearerDefaults.AuthenticationScheme,
            displayName: null,
            handlerType: typeof(JwtBearerHandler));
        TokenValidatedContext context = new(httpContext, scheme, new JwtBearerOptions())
        {
            Principal = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, userId.ToString())],
                JwtBearerDefaults.AuthenticationScheme))
        };

        JwtBearerEvents events = ProgramHelpers.CreateJwtEvents(null, null);
        Exception? propagated = await Record.ExceptionAsync(() => events.OnTokenValidated(context));
        if (scenario == "missing-service")
        {
            InvalidOperationException missingService = Assert.IsType<InvalidOperationException>(propagated);
            Assert.Contains(nameof(ITokenRevocationService), missingService.Message, StringComparison.Ordinal);
            Assert.Null(context.Result);
            return;
        }
        revocationService.Verify(service => service.IsTokenRevokedAsync("access-token", cancellation.Token), Times.Once);
        if (scenario is "caller-cancellation" or "unexpected" or "invalid-operation" or "disposed")
        {
            Assert.Same(failure, propagated);
            Assert.Null(context.Result);
            Assert.DoesNotContain(logs, entry =>
                entry.StartsWith("Error: ", StringComparison.Ordinal) &&
                entry.Contains("Token revocation status could not be verified", StringComparison.Ordinal));
            return;
        }

        Assert.Null(propagated);
        Assert.False(context.Result!.Succeeded);
        Assert.Equal("Token revocation status could not be verified.", context.Result.Failure!.Message);
        Assert.Contains(
            logs,
            entry => entry.StartsWith("Error: ", StringComparison.Ordinal) &&
                     entry.Contains("Token revocation status could not be verified", StringComparison.Ordinal));
    }

    private sealed class CapturingLoggerFactory(ICollection<string> entries) : ILoggerFactory
    {
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(entries);

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class CapturingLogger(ICollection<string> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            entries.Add($"{logLevel}: {formatter(state, exception)}");
        }
    }
}
