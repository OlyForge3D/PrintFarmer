using System.Net;
using System.Text.Json;

namespace Farm.Web.Api.Tests;

[Trait("Category", "DbHeavy")]
public class DebugDbInfoEndpointTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public DebugDbInfoEndpointTests(CustomWebApplicationFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task DbInfo_ReturnsNotFound_ByDefault_InTestingEnvironment()
    {
        // Testing environment is not Development and toggle not set
        var resp = await _client.GetAsync("/api/debug/db-info");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DbInfo_ReturnsOk_WhenToggleEnabled()
    {
        // Enable toggle then create a fresh client so hosting pipeline sees variable
        Environment.SetEnvironmentVariable("DEBUG_DB_INFO", "true");
        try
        {
            var client2 = _factory.CreateClient();
            var resp = await client2.GetAsync("/api/debug/db-info");
            resp.StatusCode.Should().Be(HttpStatusCode.OK);
            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            doc.RootElement.TryGetProperty("provider", out var prov).Should().BeTrue();
            prov.GetString().Should().NotBeNullOrWhiteSpace();
            doc.RootElement.TryGetProperty("entities", out var entities).Should().BeTrue();
            entities.ValueKind.Should().Be(JsonValueKind.Object);
            doc.RootElement.TryGetProperty("migration", out var migration).Should().BeTrue();
            migration.TryGetProperty("mode", out var modeProp).Should().BeTrue();
            modeProp.GetString().Should().NotBeNull();
        }
        finally
        {
            Environment.SetEnvironmentVariable("DEBUG_DB_INFO", null);
        }
    }
}
