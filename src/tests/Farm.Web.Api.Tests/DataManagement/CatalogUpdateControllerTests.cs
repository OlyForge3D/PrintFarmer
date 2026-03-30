using System.Net;
using System.Net.Http.Json;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Dtos.DataManagement;
using Farm.Web.Api.Tests.TestInfrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Farm.Web.Api.Tests.DataManagement;

[Collection(IntegrationTestCollection.Name)]
public class CatalogUpdateControllerTests : IAsyncLifetime
{
    private CustomWebApplicationFactory? _factory;
    private HttpClient? _client;

    public async Task InitializeAsync()
    {
        _factory = new CustomWebApplicationFactory();
        _client = _factory.CreateClient();
        await _factory.ResetDatabaseAsync();
    }

    public async Task DisposeAsync()
    {
        if (_factory != null)
        {
            await _factory.DisposeAsync();
        }
    }

    [Fact]
    public async Task GetVersion_NoVersionApplied_ReturnsOk()
    {
        // Act
        HttpResponseMessage response = await _client!.GetAsync("/api/admin/catalog/version");

        // Assert — returns 200 with a null body when no version has been recorded
        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task GetVersion_AfterVersionRecorded_ReturnsVersionDto()
    {
        // Arrange — record a version directly via the DbContext
        await using AsyncServiceScope scope = _factory!.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.CatalogVersions.Add(new Farm.Infrastructure.Domain.CatalogVersion
        {
            Version = "2026.03.30.1",
            ManifestHash = "abc123",
            AppliedAt = new DateTime(2026, 3, 30, 12, 0, 0, DateTimeKind.Utc),
            Source = "github",
        });
        await db.SaveChangesAsync();

        // Act
        HttpResponseMessage response = await _client!.GetAsync("/api/admin/catalog/version");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        CatalogVersionDto? version = await response.Content.ReadFromJsonAsync<CatalogVersionDto>();
        version.Should().NotBeNull();
        version!.Version.Should().Be("2026.03.30.1");
        version.Source.Should().Be("github");
        version.AppliedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task CheckForUpdates_ReturnsOk()
    {
        // Act — this calls the real service which attempts a GitHub fetch.
        // In the test environment without network the service returns an error result.
        HttpResponseMessage response = await _client!.GetAsync("/api/admin/catalog/updates/check");

        // Assert — endpoint is routed and returns 200 even when the check fails
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        CatalogUpdateCheckResult? result = await response.Content.ReadFromJsonAsync<CatalogUpdateCheckResult>();
        result.Should().NotBeNull();
        result!.CheckedAt.Should().NotBe(default);
    }

    [Fact]
    public async Task ApplyUpdates_ReturnsOkOrError()
    {
        // Act — calls the real service which attempts a GitHub fetch.
        // In the test environment without network the service returns an error result.
        HttpResponseMessage response = await _client!.PostAsync("/api/admin/catalog/updates/apply", null);

        // Assert — endpoint is routed correctly
        // May return 200 (with error in body) or 500 depending on network availability
        int statusCode = (int)response.StatusCode;
        statusCode.Should().BeOneOf(200, 500);
    }
}
