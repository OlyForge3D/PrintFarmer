using System.Net;
using Farm.Web.Api.Tests.TestInfrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Farm.Web.Api.Tests.Integration;

[Collection(IntegrationTestCollection.Name)]
public class MappedEndpointAnonymousAccessTests : IAsyncLifetime
{
    private CustomWebApplicationFactory _factory = null!;

    public async Task InitializeAsync()
    {
        _factory = new CustomWebApplicationFactory(new Dictionary<string, string?>
        {
            ["Security:DevModeBypassAuth"] = "false",
            ["DEPLOYMENT_MODE"] = "monolith"
        });
        await _factory.ResetDatabaseAsync();
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task PrinterHubNegotiate_Unauthenticated_ReturnsOk()
    {
        using HttpClient anon = _factory.CreateClient();
        HttpResponseMessage response = await anon.PostAsync("/hubs/printers/negotiate?negotiateVersion=1", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task HarvestHubNegotiate_Unauthenticated_ReturnsOk()
    {
        using HttpClient anon = _factory.CreateClient();
        HttpResponseMessage response = await anon.PostAsync("/hubs/harvest/negotiate?negotiateVersion=1", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
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
        string path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../Web/ReactApp"));
        Directory.Exists(path).Should().BeTrue("the test host needs an existing index.html to exercise MapFallbackToFile");
        File.Exists(Path.Combine(path, "index.html")).Should().BeTrue("the SPA fallback serves index.html");
        return path;
    }
}
