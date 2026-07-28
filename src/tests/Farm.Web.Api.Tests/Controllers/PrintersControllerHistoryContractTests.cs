// <copyright file="PrintersControllerHistoryContractTests.cs" company="OlyForge3D">
// Copyright (c) OlyForge3D. All rights reserved.
// </copyright>

using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using Farm.Backend.Plugin.Sdcp;
using Farm.Infrastructure;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Settings;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Farm.Web.Api.Tests.Controllers;

public sealed class PrintersControllerHistoryContractTests : IAsyncLifetime
{
    private readonly Mock<IPrintersService> _printers = new(MockBehavior.Strict);
    private readonly HistoryContractFactory _factory;
    private readonly HttpClient _client;

    public PrintersControllerHistoryContractTests()
    {
        _factory = new HistoryContractFactory(_printers);
        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.Add("X-Test-User-Id", Guid.NewGuid().ToString());
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task HistoryRoutes_UnsupportedBackend_ReturnBadRequest()
    {
        Guid printerId = Guid.NewGuid();
        _printers.Setup(service => service.GetHistoryListAsync(
                printerId,
                It.IsAny<int?>(),
                It.IsAny<int?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new NotSupportedException("unsupported"));
        _printers.Setup(service => service.GetHistoryJobAsync(
                printerId,
                "job-1",
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new NotSupportedException("unsupported"));

        HttpResponseMessage list = await _client.GetAsync(
            $"/api/printers/{printerId}/history");
        HttpResponseMessage detail = await _client.GetAsync(
            $"/api/printers/{printerId}/history/job-1");

        list.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        detail.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData(false, typeof(HttpRequestException), HttpStatusCode.BadGateway)]
    [InlineData(false, typeof(SocketException), HttpStatusCode.BadGateway)]
    [InlineData(false, typeof(TimeoutException), HttpStatusCode.RequestTimeout)]
    [InlineData(true, typeof(HttpRequestException), HttpStatusCode.BadGateway)]
    [InlineData(true, typeof(SocketException), HttpStatusCode.BadGateway)]
    [InlineData(true, typeof(TimeoutException), HttpStatusCode.RequestTimeout)]
    public async Task HistoryRoutes_ClassifiedProviderFailure_ReturnExpectedHttpStatus(
        bool detailRoute,
        Type exceptionType,
        HttpStatusCode expectedStatus)
    {
        Guid printerId = Guid.NewGuid();
        Exception exception = (Exception)Activator.CreateInstance(exceptionType)!;
        if (detailRoute)
        {
            _printers.Setup(service => service.GetHistoryJobAsync(
                    printerId,
                    "job-1",
                    It.IsAny<CancellationToken>()))
                .ThrowsAsync(exception);
        }
        else
        {
            _printers.Setup(service => service.GetHistoryListAsync(
                    printerId,
                    It.IsAny<int?>(),
                    It.IsAny<int?>(),
                    It.IsAny<DateTime?>(),
                    It.IsAny<DateTime?>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()))
                .ThrowsAsync(exception);
        }

        string path = detailRoute
            ? $"/api/printers/{printerId}/history/job-1"
            : $"/api/printers/{printerId}/history";
        HttpResponseMessage response = await _client.GetAsync(path);

        response.StatusCode.Should().Be(expectedStatus);
    }

    [Fact]
    public async Task HistoryList_RealSdcpRejectedUpgrade_ReturnsBadGateway()
    {
        (WebApplication app, string baseUrl) =
            await CreateSdcpEndpointAsync(silent: false);
        await using (app)
        using (var http = new HttpClient())
        {
            var adapter = new SdcpClient(
                http,
                NullLogger<SdcpClient>.Instance,
                new BackendTimeoutSettings());
            SetupHistoryListDelegate(adapter, baseUrl);

            HttpResponseMessage response = await _client.GetAsync(
                $"/api/printers/{Guid.NewGuid()}/history");

            response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        }
    }

    [Fact]
    public async Task HistoryList_RealSdcpSilentSocket_ReturnsRequestTimeout()
    {
        (WebApplication app, string baseUrl) =
            await CreateSdcpEndpointAsync(silent: true);
        await using (app)
        using (var http = new HttpClient())
        {
            var adapter = new SdcpClient(
                http,
                NullLogger<SdcpClient>.Instance,
                new BackendTimeoutSettings { CommandTimeoutSeconds = 1 });
            SetupHistoryListDelegate(adapter, baseUrl);

            HttpResponseMessage response = await _client.GetAsync(
                $"/api/printers/{Guid.NewGuid()}/history");

            response.StatusCode.Should().Be(HttpStatusCode.RequestTimeout);
        }
    }

    private void SetupHistoryListDelegate(
        ISupportsHistory adapter,
        string baseUrl)
    {
        _printers.Setup(service => service.GetHistoryListAsync(
                It.IsAny<Guid>(),
                It.IsAny<int?>(),
                It.IsAny<int?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Returns(async (
                Guid _,
                int? limit,
                int? start,
                DateTime? since,
                DateTime? _,
                string? _,
                CancellationToken ct) =>
                await adapter.GetHistoryListAsync(
                    baseUrl,
                    limit,
                    start,
                    since,
                    credential: null,
                    ct) ??
                throw new InvalidDataException("SDCP returned no history."));
    }

    private static async Task<(WebApplication App, string BaseUrl)>
        CreateSdcpEndpointAsync(bool silent)
    {
        int port = GetFreeTcpPort();
        WebApplicationBuilder builder = WebApplication.CreateBuilder(
            new WebApplicationOptions
            {
                EnvironmentName = Environments.Development,
            });
        builder.WebHost.ConfigureKestrel(options => options.ListenLocalhost(port));
        WebApplication app = builder.Build();
        if (silent)
        {
            app.UseWebSockets();
        }

        app.Map("/websocket", async context =>
        {
            if (!silent)
            {
                context.Response.StatusCode =
                    (int)HttpStatusCode.ServiceUnavailable;
                return;
            }

            using WebSocket ws = await context.WebSockets.AcceptWebSocketAsync();
            byte[] buffer = new byte[8192];
            await ws.ReceiveAsync(buffer, context.RequestAborted);
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(10), context.RequestAborted);
            }
            catch (OperationCanceledException)
            {
                // The client timeout closes the test socket.
            }
        });
        await app.StartAsync();
        return (app, $"http://127.0.0.1:{port}");
    }

    private static int GetFreeTcpPort()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class HistoryContractFactory(Mock<IPrintersService> printers)
        : CustomWebApplicationFactory(
            new Dictionary<string, string?>
            {
                ["Testing:UseTestAuthentication"] = "true",
                ["Security:DevModeBypassAuth"] = "false",
            })
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IPrintersService>();
                services.AddSingleton(printers.Object);
            });
        }
    }
}
