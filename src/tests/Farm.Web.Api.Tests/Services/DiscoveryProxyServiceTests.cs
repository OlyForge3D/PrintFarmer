using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Farm.Infrastructure;
using Farm.Infrastructure.Services;
using Farm.Infrastructure.Services.SignalR;
using Farm.Infrastructure.Settings;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Services;
using Farm.Web.Api.Services.Interfaces;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services;

public class DiscoveryProxyServiceTests
{
    [Fact]
    public async Task StartDiscoveryStreamAsync_ForwardsRequestAndCachesInitialProgress()
    {
        Mock<IDiscoveryProgressCache> progressCache = new Mock<IDiscoveryProgressCache>();
        Mock<IHubContext<PrinterHub>> hubContext = CreateHubContextMock();
        Mock<ISettingsService> settingsService = new Mock<ISettingsService>();
        settingsService.Setup(s => s.Get<NetworkDiscoverySettings>())
            .Returns(new NetworkDiscoverySettings
            {
                DiscoverySubnets = new List<string> { "192.168.1.0/24" },
                ClientTimeoutMs = 500,
                MaxConcurrentRequests = 5
            });

        RecordingHandler handler = new RecordingHandler(request =>
        {
            HttpResponseMessage response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { sessionId = "session-123", message = "ok" })
            };
            return Task.FromResult(response);
        });

        Mock<IHttpClientFactory> httpClientFactory = new Mock<IHttpClientFactory>();
        httpClientFactory.Setup(f => f.CreateClient("PrinterDiscovery")).Returns(new HttpClient(handler));

        DiscoveryProxyService service = CreateService(httpClientFactory, hubContext, progressCache, settingsService);

        DiscoveryStreamResponse result = await service.StartDiscoveryStreamAsync(new[] { PrinterBackend.Moonraker }, autoRegister: true, cancellationToken: CancellationToken.None);

        Assert.Equal("session-123", result.SessionId);
        Assert.Equal("ok", result.Message);

        progressCache.Verify(c => c.Set("session-123", It.Is<DiscoveryProgressDto>(p => p.Status == DiscoveryStatus.Starting)), Times.Once);

        Assert.NotNull(handler.LastRequest);
        Assert.Equal("/api/discovery/stream", handler.LastRequest!.RequestUri!.AbsolutePath);

        string body = handler.LastRequest.Content == null ? string.Empty : await handler.LastRequest.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.GetProperty("autoRegister").GetBoolean());
        Assert.Equal(500, doc.RootElement.GetProperty("probeTimeoutMs").GetInt32());
        Assert.Equal(5, doc.RootElement.GetProperty("maxConcurrentProbes").GetInt32());
        Assert.Equal((int)PrinterBackend.Moonraker, doc.RootElement.GetProperty("backends")[0].GetInt32());
        Assert.Equal("192.168.1.0/24", doc.RootElement.GetProperty("subnets")[0].GetString());
    }

    [Fact]
    public async Task StartDiscoveryStreamAsync_WhenRequestFails_ThrowsInvalidOperation()
    {
        Mock<IDiscoveryProgressCache> progressCache = new Mock<IDiscoveryProgressCache>();
        Mock<IHubContext<PrinterHub>> hubContext = CreateHubContextMock();
        Mock<ISettingsService> settingsService = new Mock<ISettingsService>();
        settingsService.Setup(s => s.Get<NetworkDiscoverySettings>()).Returns(new NetworkDiscoverySettings());

        ThrowingHandler handler = new ThrowingHandler(new HttpRequestException("network down"));

        Mock<IHttpClientFactory> httpClientFactory = new Mock<IHttpClientFactory>();
        httpClientFactory.Setup(f => f.CreateClient("PrinterDiscovery")).Returns(new HttpClient(handler));

        DiscoveryProxyService service = CreateService(httpClientFactory, hubContext, progressCache, settingsService);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartDiscoveryStreamAsync());
    }

    [Fact]
    public async Task CancelDiscoveryStreamAsync_OnFailure_UpdatesCacheAndPublishesEvents()
    {
        Mock<IDiscoveryProgressCache> progressCache = new Mock<IDiscoveryProgressCache>();

        Mock<IHubContext<PrinterHub>> hubContext = CreateHubContextMock(out Mock<IHubClients> clients, out Mock<IClientProxy> proxy);
        List<(string Method, object?[] Args)> sentMessages = new();
        proxy.Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object?[], CancellationToken>((method, args, _) => sentMessages.Add((method, args)))
            .Returns(Task.CompletedTask);

        Mock<ISettingsService> settingsService = new Mock<ISettingsService>();
        settingsService.Setup(s => s.Get<NetworkDiscoverySettings>()).Returns(new NetworkDiscoverySettings());

        RecordingHandler handler = new RecordingHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)));

        Mock<IHttpClientFactory> httpClientFactory = new Mock<IHttpClientFactory>();
        httpClientFactory.Setup(f => f.CreateClient("PrinterDiscovery")).Returns(new HttpClient(handler));

        DiscoveryProxyService service = CreateService(httpClientFactory, hubContext, progressCache, settingsService);

        DiscoveryCancelResponse response = await service.CancelDiscoveryStreamAsync("sess-1", CancellationToken.None);

        Assert.Equal("Discovery session cancelled", response.Message);

        progressCache.Verify(c => c.Set("sess-1", It.Is<DiscoveryProgressDto>(p => p.Status == DiscoveryStatus.Cancelled)), Times.Once);

        clients.Verify(c => c.Group("discovery-sess-1"), Times.Exactly(2));
        Assert.Equal(2, sentMessages.Count);
        Assert.Contains(sentMessages, call => call.Method == "discoveryprogress" && call.Args.Length == 1 && call.Args[0] is DiscoveryProgressDto dto && dto.Status == DiscoveryStatus.Cancelled && dto.SessionId == "sess-1");
        Assert.Contains(sentMessages, call => call.Method == "discoverycompleted" && call.Args.Length == 1 && call.Args[0] is DiscoveryCompletedDto dto && dto.WasCancelled && dto.SessionId == "sess-1");
    }

    private static DiscoveryProxyService CreateService(
        Mock<IHttpClientFactory> httpClientFactory,
        Mock<IHubContext<PrinterHub>> hubContext,
        Mock<IDiscoveryProgressCache> progressCache,
        Mock<ISettingsService> settingsService)
    {
        Mock<IUnifiedLoggingService> logger = new Mock<IUnifiedLoggingService>();

        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { { "PRINTER_DISCOVERY_URL", "http://discovery.local" } })
            .Build();

        return new DiscoveryProxyService(
            httpClientFactory.Object,
            hubContext.Object,
            progressCache.Object,
            settingsService.Object,
            logger.Object,
            config);
    }

    private static Mock<IHubContext<PrinterHub>> CreateHubContextMock()
    {
        return CreateHubContextMock(out _, out _);
    }

    private static Mock<IHubContext<PrinterHub>> CreateHubContextMock(out Mock<IHubClients> clientsMock, out Mock<IClientProxy> proxyMock)
    {
        clientsMock = new Mock<IHubClients>();
        proxyMock = new Mock<IClientProxy>();

        clientsMock.Setup(c => c.Group(It.IsAny<string>())).Returns(proxyMock.Object);

        Mock<IHubContext<PrinterHub>> hubContext = new Mock<IHubContext<PrinterHub>>();
        hubContext.Setup(h => h.Clients).Returns(clientsMock.Object);
        return hubContext;
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _handler = handler;
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return await _handler(request).ConfigureAwait(false);
        }
    }

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        private readonly Exception _exception = exception;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromException<HttpResponseMessage>(_exception);
        }
    }
}
