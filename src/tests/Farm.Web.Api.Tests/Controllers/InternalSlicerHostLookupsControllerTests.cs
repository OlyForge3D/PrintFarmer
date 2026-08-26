using System.Net;
using Farm.Infrastructure;
using Farm.Slicer.Module.Services.Configuration;
using Farm.Web.Api.Tests.TestInfrastructure;
using FluentAssertions;

namespace Farm.Web.Api.Tests.Controllers;

[Collection(IntegrationTestCollection.Name)]
public sealed class InternalSlicerHostLookupsControllerTests : IAsyncLifetime
{
    private const string SharedKey = "slicer-host-integration-key";

    private CustomWebApplicationFactory _factory = null!;

    public async Task InitializeAsync()
    {
        _factory = new CustomWebApplicationFactory(new Dictionary<string, string?>
        {
            ["Security:DevModeBypassAuth"] = "false",
            [WorkerAuthConfiguration.SharedKeyPath] = SharedKey,
        });
        await _factory.ResetDatabaseAsync();
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task GetManufacturers_WithValidServiceKeyAndNoJwt_ReturnsSuccess()
    {
        using HttpClient client = _factory.CreateClient();
        using var request =
            new HttpRequestMessage(HttpMethod.Get, SlicerHostLookupContract.ManufacturersPath);
        _ = request.Headers.TryAddWithoutValidation(
            SlicerHostLookupContract.ApiKeyHeaderName,
            SharedKey);

        using HttpResponseMessage response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetManufacturers_WithInvalidServiceKey_ReturnsAuthenticationProblem()
    {
        using HttpClient client = _factory.CreateClient();
        using var request =
            new HttpRequestMessage(HttpMethod.Get, SlicerHostLookupContract.ManufacturersPath);
        _ = request.Headers.TryAddWithoutValidation(
            SlicerHostLookupContract.ApiKeyHeaderName,
            "incorrect-key");

        using HttpResponseMessage response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        string body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"code\":\"authentication_required\"");
    }

    [Fact]
    public async Task GetPrinter_WithValidServiceKeyAndNoJwt_ReachesAction()
    {
        using HttpClient client = _factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            SlicerHostLookupContract.PrinterPath(Guid.NewGuid()));
        _ = request.Headers.TryAddWithoutValidation(
            SlicerHostLookupContract.ApiKeyHeaderName,
            SharedKey);

        using HttpResponseMessage response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetManufacturers_WithDuplicateServiceKeys_ReturnsAuthenticationProblem()
    {
        using HttpClient client = _factory.CreateClient();
        using var request =
            new HttpRequestMessage(HttpMethod.Get, SlicerHostLookupContract.ManufacturersPath);
        _ = request.Headers.TryAddWithoutValidation(
            SlicerHostLookupContract.ApiKeyHeaderName,
            [SharedKey, "incorrect-key"]);

        using HttpResponseMessage response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
