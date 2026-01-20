using System.Net;
using System.Text.Json;
using Farm.Web.IntegrationTests;
using Xunit;

namespace Farm.Web.Api.Tests.Integration.OctoPrint;

/// <summary>
/// Integration tests for OctoPrintCompatController endpoints.
/// 
/// These tests ensure PrintFarmer correctly mimics OctoPrint API responses
/// so that slicers (OrcaSlicer, PrusaSlicer, etc.) can connect and interact
/// with PrintFarmer as if it were an OctoPrint server.
/// 
/// ⚠️ CRITICAL: Response formats must match OctoPrint/fdm-monster exactly.
/// Slicers validate specific fields (e.g., "OctoPrint" in version.text) and
/// will reject connections if the format doesn't match expectations.
/// </summary>
public class OctoPrintCompatControllerTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public OctoPrintCompatControllerTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    /// <summary>
    /// Tests that GET /api/version returns the correct format expected by slicers.
    /// 
    /// This is a critical endpoint. Slicers check the 'text' field for the keyword
    /// "OctoPrint" to validate the print host type. If this field contains anything
    /// else, slicers reject the connection with "Mismatched type of print host" error.
    /// </summary>
    [Fact]
    public async Task GetVersion_ReturnsCorrectFormat()
    {
        // Act
        var response = await _client.GetAsync("/api/version");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        
        var content = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(content);
        var root = doc.RootElement;

        // Verify all required fields exist
        Assert.True(root.TryGetProperty("api", out var apiProp), "Missing 'api' field");
        Assert.True(root.TryGetProperty("server", out var serverProp), "Missing 'server' field");
        Assert.True(root.TryGetProperty("text", out var textProp), "Missing 'text' field");

        // Verify field values
        Assert.Equal("0.1", apiProp.GetString());
        Assert.Equal("1.9.0", serverProp.GetString());
        
        var textValue = textProp.GetString();
        Assert.NotNull(textValue);
        
        // ⚠️ CRITICAL: Slicers check for "OctoPrint" keyword in text field
        Assert.Contains("OctoPrint", textValue, StringComparison.Ordinal);
    }

    /// <summary>
    /// Tests that GET /api/version is accessible without authentication.
    /// Slicers need to check version compatibility before providing API keys.
    /// </summary>
    [Fact]
    public async Task GetVersion_AllowsAnonymousAccess()
    {
        // Act - no auth headers provided
        var response = await _client.GetAsync("/api/version");

        // Assert - should succeed without API key
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// Tests that GET /api/server returns the expected server status format.
    /// </summary>
    [Fact]
    public async Task GetServer_ReturnsCorrectFormat()
    {
        // Act
        var response = await _client.GetAsync("/api/server");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        
        var content = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(content);
        var root = doc.RootElement;

        // Verify all required fields exist
        Assert.True(root.TryGetProperty("version", out var versionProp), "Missing 'version' field");
        Assert.True(root.TryGetProperty("safemode", out var safemodeProp), "Missing 'safemode' field");

        // Verify field values
        Assert.Equal("1.9.3", versionProp.GetString());
        Assert.Equal(JsonValueKind.Null, safemodeProp.ValueKind);
    }

    /// <summary>
    /// Tests that GET /api/server is accessible without authentication.
    /// Slicers may check server status before connecting.
    /// </summary>
    [Fact]
    public async Task GetServer_AllowsAnonymousAccess()
    {
        // Act - no auth headers provided
        var response = await _client.GetAsync("/api/server");

        // Assert - should succeed without API key
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// Tests that /api/version response is valid JSON and parseable by slicers.
    /// </summary>
    [Fact]
    public async Task GetVersion_ReturnsValidJson()
    {
        // Act
        var response = await _client.GetAsync("/api/version");
        var content = await response.Content.ReadAsStringAsync();

        // Assert - should parse without exceptions
        _ = JsonDocument.Parse(content);
    }

    /// <summary>
    /// Tests that /api/server response is valid JSON and parseable by slicers.
    /// </summary>
    [Fact]
    public async Task GetServer_ReturnsValidJson()
    {
        // Act
        var response = await _client.GetAsync("/api/server");
        var content = await response.Content.ReadAsStringAsync();

        // Assert - should parse without exceptions
        _ = JsonDocument.Parse(content);
    }
}
