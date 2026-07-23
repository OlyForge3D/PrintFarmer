using System.Net;
using System.Text.Json;

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
}
