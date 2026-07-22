using Farm.Slicer.Module.Api;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Farm.Slicer.Module.Tests.Hubs;

/// <summary>
/// Verifies <see cref="SlicerHubAuth.ResolveHubAccessToken"/>, which lets the slicer host
/// authenticate SignalR WebSocket/SSE hub connections that send the JWT as a <c>?access_token=</c>
/// query parameter instead of an Authorization header.
/// </summary>
public class SlicerHubAuthTests
{
    private static HttpRequest BuildRequest(string path, string? queryString)
    {
        DefaultHttpContext context = new();
        context.Request.Path = path;
        if (queryString is not null)
        {
            context.Request.QueryString = new QueryString(queryString);
        }

        return context.Request;
    }

    [Fact]
    public void ReturnsToken_ForHubPath_WithAccessTokenQuery()
    {
        HttpRequest request = BuildRequest("/hubs/slicers", "?access_token=jwt-xyz");

        Assert.Equal("jwt-xyz", SlicerHubAuth.ResolveHubAccessToken(request));
    }

    [Fact]
    public void ReturnsToken_ForHubSubPath()
    {
        HttpRequest request = BuildRequest("/hubs/slicers", "?id=abc&access_token=jwt-xyz");

        Assert.Equal("jwt-xyz", SlicerHubAuth.ResolveHubAccessToken(request));
    }

    [Fact]
    public void ReturnsNull_ForNonHubPath()
    {
        HttpRequest request = BuildRequest("/api/slice", "?access_token=jwt-xyz");

        Assert.Null(SlicerHubAuth.ResolveHubAccessToken(request));
    }

    [Fact]
    public void ReturnsNull_WhenNoAccessTokenQuery()
    {
        HttpRequest request = BuildRequest("/hubs/slicers", "?id=abc");

        Assert.Null(SlicerHubAuth.ResolveHubAccessToken(request));
    }
}
