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

    /// <summary>
    /// Issue #2242, generator-readiness smoke test: every local <c>$ref</c> pointer found anywhere
    /// in the live OpenAPI document — recursively, across every path/operation/response/schema —
    /// must resolve to an existing <c>components/schemas</c> entry. This is a document-wide,
    /// family-agnostic check distinct from the per-family fidelity assertions in
    /// <c>Contracts/*OpenApiSchemaTests.cs</c>: it does not assert a schema is *correct*, only
    /// that a generated client would never fail to resolve a reference while walking the document.
    /// </summary>
    [Fact]
    public async Task GetOpenApiDocumentAsync_EveryLocalSchemaRef_ResolvesToAnExistingComponentSchema()
    {
        await using CustomWebApplicationFactory factory = CustomWebApplicationFactory.CreateWithIsolatedDatabase();
        using HttpClient client = factory.CreateClient();
        using HttpResponseMessage response = await client.GetAsync("/openapi/v1.json");
        _ = response.StatusCode.Should().Be(HttpStatusCode.OK);
        await using Stream content = await response.Content.ReadAsStreamAsync();
        using JsonDocument document = await JsonDocument.ParseAsync(content);

        JsonElement schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        var declaredSchemaNames = new HashSet<string>(
            schemas.EnumerateObject().Select(property => property.Name), StringComparer.Ordinal);

        var unresolvedRefs = new List<string>();
        CollectUnresolvedRefs(document.RootElement, declaredSchemaNames, unresolvedRefs);

        _ = unresolvedRefs.Should().BeEmpty(
            "every '$ref' pointer in the document should resolve to a declared component schema");
    }

    private static void CollectUnresolvedRefs(JsonElement element, HashSet<string> declaredSchemaNames, List<string> unresolvedRefs)
    {
        const string schemaRefPrefix = "#/components/schemas/";

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    if (property.NameEquals("$ref") &&
                        property.Value.ValueKind == JsonValueKind.String &&
                        property.Value.GetString() is string pointer &&
                        pointer.StartsWith(schemaRefPrefix, StringComparison.Ordinal) &&
                        !declaredSchemaNames.Contains(pointer[schemaRefPrefix.Length..]))
                    {
                        unresolvedRefs.Add(pointer);
                    }

                    CollectUnresolvedRefs(property.Value, declaredSchemaNames, unresolvedRefs);
                }

                break;
            case JsonValueKind.Array:
                foreach (JsonElement item in element.EnumerateArray())
                {
                    CollectUnresolvedRefs(item, declaredSchemaNames, unresolvedRefs);
                }

                break;
        }
    }
}
