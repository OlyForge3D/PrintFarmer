using System.Net;
using Farm.Web.Api.Tests.TestInfrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Farm.Web.Api.Tests.Integration;

public class MappedEndpointAnonymousAccessTests : IClassFixture<MappedEndpointAnonymousAccessTests.Factory>, IAsyncLifetime
{
    public class Factory : CustomWebApplicationFactory
    {
        public Factory()
            : base(new Dictionary<string, string?>
            {
                ["Security:DevModeBypassAuth"] = "false",
                ["DEPLOYMENT_MODE"] = "monolith"
            })
        {
        }
    }

    private readonly Factory _factory;

    public MappedEndpointAnonymousAccessTests(Factory factory)
    {
        _factory = factory;
    }

    public async Task InitializeAsync()
    {
        await _factory.ResetDataAsync();
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    [Fact]
    public async Task PrinterHubNegotiate_Unauthenticated_ReturnsUnauthorized()
    {
        using HttpClient anon = _factory.CreateClient();
        HttpResponseMessage response = await anon.PostAsync("/hubs/printers/negotiate?negotiateVersion=1", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task HarvestHubNegotiate_Unauthenticated_ReturnsUnauthorized()
    {
        using HttpClient anon = _factory.CreateClient();
        HttpResponseMessage response = await anon.PostAsync("/hubs/harvest/negotiate?negotiateVersion=1", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task SlicerRegistryHubNegotiate_Unauthenticated_ReturnsUnauthorized()
    {
        using HttpClient anon = _factory.CreateClient();
        HttpResponseMessage response = await anon.PostAsync("/hubs/slicer-registry/negotiate?negotiateVersion=1", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task SlicerProgressHubNegotiate_Unauthenticated_ReturnsUnauthorized()
    {
        using HttpClient anon = _factory.CreateClient();
        HttpResponseMessage response = await anon.PostAsync("/hubs/slicers/negotiate?negotiateVersion=1", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Metrics_Unauthenticated_ReturnsOk()
    {
        using HttpClient anon = _factory.CreateClient();
        HttpResponseMessage response = await anon.GetAsync("/metrics");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task SpaFallback_Unauthenticated_ReturnsIndexHtml()
    {
        string webRoot = ResolveReactAppWebRoot();
        using WebApplicationFactory<Program> monolithFactory = _factory.WithWebHostBuilder(builder =>
        {
            _ = builder.UseWebRoot(webRoot);
        });
        using HttpClient anon = monolithFactory.CreateClient();

        HttpResponseMessage response = await anon.GetAsync("/");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/html");
    }

    private static string ResolveReactAppWebRoot()
    {
        string path = Path.GetFullPath(Path.Join(AppContext.BaseDirectory, "../../../../../Web/ReactApp"));
        Directory.Exists(path).Should().BeTrue("the test host needs an existing index.html to exercise MapFallbackToFile");
        File.Exists(Path.Join(path, "index.html")).Should().BeTrue("the SPA fallback serves index.html");
        return path;
    }
}
