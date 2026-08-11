using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Farm.Infrastructure.PrinterCalibration;
using FluentAssertions;

namespace Farm.Slicer.Module.Tests.Integration;

/// <summary>
/// Verifies that the production standalone slicer host actually exposes the calibration profile
/// resolution endpoint and its availability probe, since the main API's split-mode resolver adapter
/// depends on both routes existing exactly where the shared contract says they do.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class SlicerHostCalibrationResolutionRouteTests(
    StandaloneSlicerHostApplicationFactory factory)
    : IClassFixture<StandaloneSlicerHostApplicationFactory>
{
    private static readonly string ResolveRoute =
        "/" + CalibrationProfileResolutionContract.ResolveRelativeRoute;

    private static readonly string HealthRoute =
        "/" + CalibrationProfileResolutionContract.HealthRelativeRoute;

    [Fact]
    public async Task AvailabilityProbe_IsAnonymousAndReportsHealthy()
    {
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync(HealthRoute);
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        body.Trim().Should().Be("Healthy");
    }

    [Fact]
    public async Task ResolveEndpoint_WithoutAuthentication_Returns401()
    {
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await PostAsync(client);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ResolveEndpoint_WithoutCalibrationRead_Returns403()
    {
        using HttpClient client = factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await PostAsync(client);
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden, body);
        using JsonDocument document = JsonDocument.Parse(body);
        document.RootElement.GetProperty("code").GetString().Should().Be("permission_denied");
    }

    [Fact]
    public async Task ResolveEndpoint_ExposesNoBrowseOrListSurface()
    {
        using HttpClient client = factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.GetAsync(ResolveRoute);

        response.StatusCode.Should().BeOneOf(
            HttpStatusCode.NotFound,
            HttpStatusCode.MethodNotAllowed,
            HttpStatusCode.Unauthorized,
            HttpStatusCode.Forbidden);
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client)
    {
        string body =
            $$"""
              {"machineProfileId":"{{Guid.NewGuid()}}","processProfileId":"{{Guid.NewGuid()}}","filamentProfileId":"{{Guid.NewGuid()}}"}
              """;
        using StringContent content = new(body, Encoding.UTF8, "application/json");
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return await client.PostAsync(ResolveRoute, content);
    }
}
