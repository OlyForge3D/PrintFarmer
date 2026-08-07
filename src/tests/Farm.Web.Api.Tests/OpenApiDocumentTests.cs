using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Farm.Web.Api.Tests;

public class OpenApiDocumentTests
{
    [Fact]
    public async Task GetOpenApiDocumentAsync_AnonymousRequest_ReturnsValidOpenApiDocument()
    {
        await using CustomWebApplicationFactory factory = CustomWebApplicationFactory.CreateWithIsolatedDatabase();
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/openapi/v1.json");

        _ = response.StatusCode.Should().Be(HttpStatusCode.OK);
        await using Stream content = await response.Content.ReadAsStreamAsync();
        using JsonDocument document = await JsonDocument.ParseAsync(content);

        _ = document.RootElement.TryGetProperty("openapi", out JsonElement openApi).Should().BeTrue();
        _ = openApi.GetString().Should().NotBeNullOrWhiteSpace();
        _ = document.RootElement.TryGetProperty("paths", out JsonElement paths).Should().BeTrue();
        _ = paths.ValueKind.Should().Be(JsonValueKind.Object);
        _ = paths.EnumerateObject().Should().NotBeEmpty();
    }

    [Fact]
    public async Task RawGcodeEndpoint_RetiredRoute_IsNotRoutedOrDocumented()
    {
        await using CustomWebApplicationFactory factory = CustomWebApplicationFactory.CreateWithIsolatedDatabase();
        using HttpClient client = await factory.CreateAuthenticatedClientAsync();

        using HttpResponseMessage routeResponse = await client.PostAsJsonAsync(
            $"/api/printers/{Guid.NewGuid()}/gcode",
            new { command = "G28" });
        using HttpResponseMessage documentResponse = await client.GetAsync("/openapi/v1.json");

        _ = routeResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
        IEnumerable<string> routePatterns = factory.Services
            .GetRequiredService<EndpointDataSource>()
            .Endpoints
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText ?? string.Empty);
        _ = routePatterns.Any(IsRawGcodeRoute).Should().BeFalse();
        _ = documentResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        await using Stream content = await documentResponse.Content.ReadAsStreamAsync();
        using JsonDocument document = await JsonDocument.ParseAsync(content);
        JsonElement paths = document.RootElement.GetProperty("paths");
        _ = paths.EnumerateObject()
            .Select(path => path.Name)
            .Any(IsRawGcodeRoute)
            .Should()
            .BeFalse();
    }

    private static bool IsRawGcodeRoute(string route) =>
        (route.StartsWith("api/printers/", StringComparison.OrdinalIgnoreCase) ||
         route.StartsWith("/api/printers/", StringComparison.OrdinalIgnoreCase)) &&
        route.EndsWith("/gcode", StringComparison.OrdinalIgnoreCase);
}
