using System.Net;
using System.Text.Json;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Slicer.Module.Services.Configuration;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Farm.Web.Api.Tests.Controllers;

public sealed class InternalSlicerHostLookupsControllerTests(
    InternalSlicerHostLookupsControllerTests.Factory factory)
    : IClassFixture<InternalSlicerHostLookupsControllerTests.Factory>, IAsyncLifetime
{
    private const string SharedKey = "slicer-host-integration-key";

    public sealed class Factory : CustomWebApplicationFactory
    {
        public Factory() : base(new Dictionary<string, string?>
        {
            ["Security:DevModeBypassAuth"] = "false",
            [WorkerAuthConfiguration.SharedKeyPath] = SharedKey,
        })
        {
        }
    }

    private sealed class UnconfiguredFactory : CustomWebApplicationFactory
    {
        public UnconfiguredFactory() : base(new Dictionary<string, string?>
        {
            ["Security:DevModeBypassAuth"] = "false",
            ["Slicer:Enabled"] = "false",
            [WorkerAuthConfiguration.SharedKeyPath] = string.Empty,
        })
        {
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                const string validatorType =
                    "Farm.Slicer.Module.Api.HostedServices.SlicerApiKeyStartupValidationService";
                foreach (ServiceDescriptor descriptor in services
                    .Where(candidate =>
                        candidate.ServiceType == typeof(IHostedService)
                        && string.Equals(
                            candidate.ImplementationType?.FullName,
                            validatorType,
                            StringComparison.Ordinal))
                    .ToList())
                {
                    services.Remove(descriptor);
                }
            });
        }
    }

    private readonly Factory _factory = factory;

    public Task InitializeAsync() => _factory.ResetDataAsync();

    public Task DisposeAsync() => Task.CompletedTask;

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
    public async Task GetManufacturers_WithMissingServiceKey_ReturnsAuthenticationProblem()
    {
        using HttpClient client = _factory.CreateClient();

        using HttpResponseMessage response =
            await client.GetAsync(SlicerHostLookupContract.ManufacturersPath);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        string body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"code\":\"authentication_required\"");
    }

    [Fact]
    public async Task GetManufacturers_WithUnconfiguredServiceKey_ReturnsUnavailableProblem()
    {
        await using var unconfiguredFactory = new UnconfiguredFactory();
        await unconfiguredFactory.ResetDataAsync();
        using HttpClient client = unconfiguredFactory.CreateClient();
        using var request =
            new HttpRequestMessage(HttpMethod.Get, SlicerHostLookupContract.ManufacturersPath);
        _ = request.Headers.TryAddWithoutValidation(
            SlicerHostLookupContract.ApiKeyHeaderName,
            SharedKey);

        using HttpResponseMessage response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        string body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"code\":\"authentication_unavailable\"");
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
    public async Task GetPrinter_WithValidServiceKey_ReturnsOnlyMinimalProjection()
    {
        Guid manufacturerId = Guid.NewGuid();
        Guid modelId = Guid.NewGuid();
        Guid printerId = Guid.NewGuid();
        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Manufacturers.Add(new Manufacturer
            {
                Id = manufacturerId,
                Name = $"Projection Manufacturer {manufacturerId:N}",
            });
            db.PrinterModels.Add(new PrinterModel
            {
                Id = modelId,
                ManufacturerId = manufacturerId,
                Name = "Projection Model",
            });
            db.Printers.Add(new Printer
            {
                Id = printerId,
                Name = "Projection Printer",
                ServerUrl = "https://printer.internal",
                OriginalServerUrl = "printer-secret.internal",
                BackendPort = 443,
                Backend = (int)PrinterBackend.Moonraker,
                ManufacturerId = manufacturerId,
                ModelId = modelId,
                Notes = "must not cross the internal lookup boundary",
            });
            await db.SaveChangesAsync();
        }

        using HttpClient client = _factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            SlicerHostLookupContract.PrinterPath(printerId));
        _ = request.Headers.TryAddWithoutValidation(
            SlicerHostLookupContract.ApiKeyHeaderName,
            SharedKey);

        using HttpResponseMessage response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.EnumerateObject().Select(property => property.Name).Should()
            .BeEquivalentTo("id", "name", "modelId", "modelName");
        body.RootElement.GetProperty("id").GetGuid().Should().Be(printerId);
        body.RootElement.GetProperty("name").GetString().Should().Be("Projection Printer");
        body.RootElement.GetProperty("modelId").GetGuid().Should().Be(modelId);
        body.RootElement.GetProperty("modelName").GetString().Should().Be("Projection Model");
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
