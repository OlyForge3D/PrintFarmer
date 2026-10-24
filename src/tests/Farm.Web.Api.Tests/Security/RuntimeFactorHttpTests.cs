using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Farm.Backend.Plugin.Moonraker;
using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.Queue;
using Farm.Infrastructure.Settings;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
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

    [Theory]
    [InlineData(true, "idle")]
    [InlineData(false, "idle")]
    [InlineData(true, "printing")]
    [InlineData(false, "printing")]
    public async Task SetFactor_AdapterOwnedTimeout_Returns503UnknownAndSettlesLeaseOnce(bool speed, string state)
    {
        _factory.ConfigureOnlinePrinter(state);
        using var handler = new PendingControlHandler();
        using var backendHttp = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var backend = new MoonrakerClient(backendHttp, NullLogger<MoonrakerClient>.Instance,
            new BackendTimeoutSettings { CommandTimeoutSeconds = 1 });
        CancellationToken requestCancellation = default;
        async Task<bool> SendAsync(Guid id, double percent, CancellationToken token)
        {
            Assert.Equal(_factory.PrinterId, id);
            requestCancellation = token;
            return speed
                ? await backend.SetSpeedFactorAsync("http://timeout.invalid", percent, null, token)
                : await backend.SetFlowFactorAsync("http://timeout.invalid", percent, null, token);
        }

        if (speed)
        {
            _factory.Printers.Setup(service => service.SetSpeedFactorAsync(_factory.PrinterId, 125, It.IsAny<CancellationToken>()))
                .Returns(SendAsync);
        }
        else
        {
            _factory.Printers.Setup(service => service.SetFlowFactorAsync(_factory.PrinterId, 125, It.IsAny<CancellationToken>()))
                .Returns(SendAsync);
        }

        using HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Id", Guid.NewGuid().ToString());
        client.DefaultRequestHeaders.Add("X-Test-Roles", "farm_admin");
        string route = speed ? "speed-factor" : "flow-factor";
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/api/printers/{_factory.PrinterId}/{route}", new { percent = 125 });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        CommandResult result = Assert.IsType<CommandResult>(await response.Content.ReadFromJsonAsync<CommandResult>());
        Assert.False(result.Success);
        Assert.Equal(state == "idle"
            ? "The backend did not prove whether the physical command was applied; reconciliation is required."
            : "The runtime override outcome is unknown; read status before retrying.", result.Message);
        Assert.False(requestCancellation.IsCancellationRequested);
        Assert.True(handler.CommandCancellation.IsCancellationRequested);
        Assert.Equal(1, handler.Requests);
        Assert.Equal(speed ? "M220 S125" : "M221 S125", handler.Script);
        _factory.Actuation.Verify(service => service.MarkDirectUnknownAsync(
            It.IsAny<PrinterActuationLease>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        _factory.Actuation.Verify(service => service.CompleteDirectAsync(
            It.IsAny<PrinterActuationLease>(), It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        _factory.Printers.Verify(service => service.SetSpeedFactorAsync(
            _factory.PrinterId, 125, It.IsAny<CancellationToken>()), speed ? Times.Once() : Times.Never());
        _factory.Printers.Verify(service => service.SetFlowFactorAsync(
            _factory.PrinterId, 125, It.IsAny<CancellationToken>()), speed ? Times.Never() : Times.Once());
        _factory.Actuation.Verify(service => service.AcquireDirectAsync(
            _factory.PrinterId, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            state == "idle" ? Times.Once() : Times.Never());
        _factory.Actuation.Verify(service => service.AcquireActiveAsync(
            _factory.PrinterId, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            state == "printing" ? Times.Once() : Times.Never());
    }

    private sealed class PendingControlHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }

        public string? Script { get; private set; }

        public CancellationToken CommandCancellation { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            CommandCancellation = cancellationToken;
            using JsonDocument body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Script = body.RootElement.GetProperty("script").GetString();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The pending command must end through cancellation.");
        }
    }

    private sealed class FactorFactory() : CustomWebApplicationFactory(new Dictionary<string, string?>
    {
        ["Testing:UseTestAuthentication"] = "true",
        ["Security:DevModeBypassAuth"] = "false",
    })
    {
        public Mock<IPrintersService> Printers { get; } = new();

        public Guid PrinterId { get; } = Guid.NewGuid();

        public Mock<IPrinterPhysicalActuationService> Actuation { get; } = new();

        private readonly Mock<IPrinterBackendCapabilitiesService> _capabilities = new();
        private readonly Mock<IQueueResourceAuthorizationService> _authorization = new();

        public void ConfigureOnlinePrinter(string state)
        {
            Printers.Setup(service => service.FindByIdAsync(PrinterId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new Printer { Id = PrinterId });
            Printers.Setup(service => service.GetStatusDtoAsync(PrinterId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PrinterStatusDto(PrinterId, true, state));
            _capabilities.Setup(service => service.GetByPrinterIdAsync(PrinterId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PrinterBackendCapabilitiesDto(PrinterId, "fixture", PrinterBackend.Moonraker)
                {
                    SupportsSpeedFactorControl = true,
                    SupportsFlowFactorControl = true,
                });
            _authorization.Setup(service => service.CanAccessPrinterAsync(
                    It.IsAny<System.Security.Claims.ClaimsPrincipal>(), PrinterId,
                    PrinterGroupAccessLevel.Submit, It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
            Actuation.Setup(service => service.AcquireDirectAsync(
                    PrinterId, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Guid id, string actor, string operation, CancellationToken _) =>
                    new PrinterActuationResult(PrinterActuationResultCode.Accepted,
                        new PrinterActuationLease(Guid.NewGuid(), id, null, operation, actor)));
            Actuation.Setup(service => service.AcquireActiveAsync(
                    PrinterId, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Guid id, string actor, string operation, CancellationToken _) =>
                    new PrinterActuationResult(PrinterActuationResultCode.Accepted,
                        new PrinterActuationLease(Guid.NewGuid(), id, Guid.NewGuid(), operation, actor)));
            Actuation.Setup(service => service.RevalidateDirectAsync(
                    It.IsAny<PrinterActuationLease>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((PrinterActuationLease lease, CancellationToken _) =>
                    new PrinterActuationResult(PrinterActuationResultCode.Accepted, lease, lease.CommandId));
            Actuation.Setup(service => service.MarkDirectUnknownAsync(
                    It.IsAny<PrinterActuationLease>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                services.AddScoped(_ => Printers.Object);
                services.AddScoped(_ => _capabilities.Object);
                services.AddScoped(_ => _authorization.Object);
                services.AddScoped(_ => Actuation.Object);
            });
        }
    }
}
