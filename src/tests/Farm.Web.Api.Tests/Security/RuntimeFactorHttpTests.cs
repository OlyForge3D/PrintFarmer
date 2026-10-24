using System.Net;
using System.Text;
using Farm.Infrastructure.Services.Printers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Farm.Web.Api.Tests.Security;

public sealed class RuntimeFactorHttpTests : IAsyncLifetime, IDisposable
{
    private readonly FactorFactory _factory = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    public void Dispose() => _factory.Dispose();

    [Theory]
    [InlineData("speed-factor")]
    [InlineData("flow-factor")]
    public async Task SetFactor_AuthenticatedWithoutQueueStart_RejectsBeforeService(string route)
    {
        using HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Id", Guid.NewGuid().ToString());
        client.DefaultRequestHeaders.Add("X-Test-Roles", "operator");
        using var body = new StringContent("""{"percent":125}""", Encoding.UTF8, "application/json");

        using HttpResponseMessage response = await client.PostAsync($"/api/printers/{Guid.NewGuid()}/{route}", body);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        _factory.Printers.Verify(s => s.SetSpeedFactorAsync(It.IsAny<Guid>(), It.IsAny<double>(), It.IsAny<CancellationToken>()), Times.Never);
        _factory.Printers.Verify(s => s.SetFlowFactorAsync(It.IsAny<Guid>(), It.IsAny<double>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("speed-factor", "{}")]
    [InlineData("flow-factor", """{"percent":"NaN"}""")]
    [InlineData("speed-factor", """{"percent":NaN}""")]
    [InlineData("flow-factor", """{"percent":1e309}""")]
    [InlineData("speed-factor", """{"percent":200.01}""")]
    [InlineData("flow-factor", """{"percent":1.001}""")]
    [InlineData("speed-factor", "null")]
    public async Task SetFactor_InvalidJsonOrValue_RejectsBeforeService(string route, string json)
    {
        using HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Id", Guid.NewGuid().ToString());
        client.DefaultRequestHeaders.Add("X-Test-Roles", "farm_admin");
        using var body = new StringContent(json, Encoding.UTF8, "application/json");

        using HttpResponseMessage response = await client.PostAsync($"/api/printers/{Guid.NewGuid()}/{route}", body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        _factory.Printers.Verify(s => s.SetSpeedFactorAsync(It.IsAny<Guid>(), It.IsAny<double>(), It.IsAny<CancellationToken>()), Times.Never);
        _factory.Printers.Verify(s => s.SetFlowFactorAsync(It.IsAny<Guid>(), It.IsAny<double>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private sealed class FactorFactory() : CustomWebApplicationFactory(new Dictionary<string, string?>
    {
        ["Testing:UseTestAuthentication"] = "true",
        ["Security:DevModeBypassAuth"] = "false",
    })
    {
        public Mock<IPrintersService> Printers { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services => services.AddScoped(_ => Printers.Object));
        }
    }
}
