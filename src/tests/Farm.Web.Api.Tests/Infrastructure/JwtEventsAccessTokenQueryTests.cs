using Farm.Web.Api;
using Farm.Web.Api.Startup;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
}
