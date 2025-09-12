using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Xunit;
using Farm.Web.Shared;

namespace Farm.Web.Api.Tests;

[Trait("Category", "DbHeavy")]
[Collection("DbHeavySerial")]
[TestTiming]
public class CatalogControllerIntegrationTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public CatalogControllerIntegrationTests(CustomWebApplicationFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
        _client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
    }

    [Fact]
    public async Task Manufacturers_Get_ReturnsEtag_And_Conditional304()
    {
        var resp1 = await _client.GetAsync("/api/catalog/manufacturers");
        resp1.StatusCode.Should().Be(HttpStatusCode.OK);
        resp1.Headers.ETag.Should().NotBeNull();
        var etag = resp1.Headers.ETag!.ToString(); // full value e.g. W/"HASH"
        etag.Should().NotBeNullOrEmpty("ETag header should be present and non-empty");

        var req2 = new HttpRequestMessage(HttpMethod.Get, "/api/catalog/manufacturers");
        req2.Headers.TryAddWithoutValidation("If-None-Match", etag);
        var resp2 = await _client.SendAsync(req2);
        resp2.StatusCode.Should().Be(HttpStatusCode.NotModified);
        resp2.Headers.ETag.Should().NotBeNull();
        resp2.Headers.ETag!.ToString().Should().Be(etag);
    }

    [Fact]
    public async Task Models_Get_ReturnsEtag_And_Conditional304()
    {
        var resp1 = await _client.GetAsync("/api/catalog/models");
        resp1.StatusCode.Should().Be(HttpStatusCode.OK);
        resp1.Headers.ETag.Should().NotBeNull();
        var etag = resp1.Headers.ETag!.ToString();
        etag.Should().NotBeNullOrEmpty();

        var req2 = new HttpRequestMessage(HttpMethod.Get, "/api/catalog/models");
        req2.Headers.TryAddWithoutValidation("If-None-Match", etag);
        var resp2 = await _client.SendAsync(req2);
        resp2.StatusCode.Should().Be(HttpStatusCode.NotModified);
        resp2.Headers.ETag.Should().NotBeNull();
        resp2.Headers.ETag!.ToString().Should().Be(etag);
    }

    [Fact]
    public async Task CreateManufacturer_NormalizesAndSetsHeader_OnDifference()
    {
        var uniqueBase = Guid.NewGuid().ToString("N").Substring(0, 8);
        var rawName = "  PRuSa-" + uniqueBase + "  "; // will normalize to "Prusa-<suffix>" (trim + casing adjustments if any rules)
        var createResp = await _client.PostAsJsonAsync("/api/catalog/manufacturers", new { name = rawName });
        createResp.StatusCode.Should().Be(HttpStatusCode.Created);
        createResp.Headers.TryGetValues("X-Normalized-Name", out var normVals).Should().BeTrue();
        var body = await createResp.Content.ReadFromJsonAsync<ManufacturerDto>();
        body.Should().NotBeNull();
        body!.Name.Should().NotBeNullOrEmpty();
        normVals!.Single().Should().Be(body.Name);

        // Duplicate raw submission (different whitespace/casing) should yield 409 with ProblemDetails
        var dupResp = await _client.PostAsJsonAsync("/api/catalog/manufacturers", new { name = rawName.ToLowerInvariant() });
        dupResp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        dupResp.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        dupResp.Headers.TryGetValues("X-Normalized-Name", out var existingVals).Should().BeTrue();
        existingVals!.Single().Should().Be(body.Name);
    }

    [Fact]
    public async Task CreateManufacturer_CaseOnlyDifference_WithHeaderValidation_Yields409()
    {
        var baseName = string.Concat("Maker", Guid.NewGuid().ToString("N").AsSpan(0, 6));
        var nameUpper = baseName.ToUpperInvariant();
        var nameLower = baseName.ToLowerInvariant();

        var firstResp = await _client.PostAsJsonAsync("/api/catalog/manufacturers", new { name = nameUpper });
        firstResp.StatusCode.Should().Be(HttpStatusCode.Created);
        var firstDto = await firstResp.Content.ReadFromJsonAsync<ManufacturerDto>();
        firstDto.Should().NotBeNull();

        var secondResp = await _client.PostAsJsonAsync("/api/catalog/manufacturers", new { name = nameLower });
        secondResp.StatusCode.Should().Be(HttpStatusCode.Conflict, "case-insensitive duplicate should yield 409");
        secondResp.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        // Should still provide existing normalized name header if emitted by filter
        if (secondResp.Headers.TryGetValues("X-Normalized-Name", out var vals))
        {
            vals.Single().Should().Be(firstDto!.Name);
        }
    }

    [Fact]
    public async Task CreateModel_DuplicateWithinManufacturer_Yields409()
    {
        // Create manufacturer first
        var mfgName = string.Concat("Maker", Guid.NewGuid().ToString("N").AsSpan(0, 6));
        var mfgResp = await _client.PostAsJsonAsync("/api/catalog/manufacturers", new { name = mfgName });
        mfgResp.StatusCode.Should().Be(HttpStatusCode.Created);
        var mfgDto = await mfgResp.Content.ReadFromJsonAsync<ManufacturerDto>();
        mfgDto.Should().NotBeNull();

        var modelName = string.Concat("Model", Guid.NewGuid().ToString("N").AsSpan(0, 6));
        var createModelResp = await _client.PostAsJsonAsync("/api/catalog/models", new { name = modelName, manufacturerId = mfgDto!.Id, maxX = 100, maxY = 100, maxZ = 100 });
        createModelResp.StatusCode.Should().Be(HttpStatusCode.Created);

        var dupModelResp = await _client.PostAsJsonAsync("/api/catalog/models", new { name = modelName.ToUpperInvariant(), manufacturerId = mfgDto!.Id, maxX = 100, maxY = 100, maxZ = 100 });
        dupModelResp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        dupModelResp.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
    }

    [Fact]
    public async Task CreateManufacturer_CaseOnlyDifference_Yields409()
    {
        var baseName = string.Concat("CaseTest", Guid.NewGuid().ToString("N").AsSpan(0, 6));
        var first = await _client.PostAsJsonAsync("/api/catalog/manufacturers", new { name = baseName });
        first.StatusCode.Should().Be(HttpStatusCode.Created);
        var second = await _client.PostAsJsonAsync("/api/catalog/manufacturers", new { name = baseName.ToUpperInvariant() });
        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
        second.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
    }

    [Fact]
    public async Task GetManufacturerById_NotFoundForRandomId()
    {
        var resp = await _client.GetAsync($"/api/catalog/manufacturers/{Guid.NewGuid():D}");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetModelById_NotFoundForRandomId()
    {
        var resp = await _client.GetAsync($"/api/catalog/models/{Guid.NewGuid():D}");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
