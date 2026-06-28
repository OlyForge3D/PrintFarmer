using System.Text.Encodings.Web;
using Farm.Web.Api;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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
}
