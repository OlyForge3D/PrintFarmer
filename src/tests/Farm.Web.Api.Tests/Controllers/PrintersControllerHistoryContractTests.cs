// <copyright file="PrintersControllerHistoryContractTests.cs" company="OlyForge3D">
// Copyright (c) OlyForge3D. All rights reserved.
// </copyright>

using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Farm.Backend.Plugin.OctoPrint;
using Farm.Backend.Plugin.Sdcp;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Printers;
using Farm.Infrastructure.Repositories.UnitOfWork;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Settings;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
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
        _factory = new HistoryContractFactory(_printers.Object);
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

    [Fact]
    public async Task HistoryThumbnail_ValidImage_ReturnsSameOriginContent()
    {
        Guid printerId = Guid.NewGuid();
        _printers.Setup(service => service.GetHistoryThumbnailAsync(
                printerId,
                "job-1",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HistoryThumbnailContent([1, 2, 3], "image/png"));

        HttpResponseMessage response = await _client.GetAsync(
            $"/api/printers/{printerId}/history/job-1/thumbnail");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("image/png");
        response.Headers.GetValues("X-Content-Type-Options")
            .Should().ContainSingle("nosniff");
        (await response.Content.ReadAsByteArrayAsync()).Should().Equal(1, 2, 3);
    }

    [Theory]
    [InlineData(typeof(KeyNotFoundException), HttpStatusCode.NotFound)]
    [InlineData(typeof(InvalidDataException), HttpStatusCode.BadGateway)]
    [InlineData(typeof(HttpRequestException), HttpStatusCode.BadGateway)]
    [InlineData(typeof(SocketException), HttpStatusCode.BadGateway)]
    [InlineData(typeof(IOException), HttpStatusCode.BadGateway)]
    [InlineData(typeof(TimeoutException), HttpStatusCode.RequestTimeout)]
    public async Task HistoryThumbnail_ProviderFailure_ReturnsExplicitStatus(
        Type exceptionType,
        HttpStatusCode expectedStatus)
    {
        Guid printerId = Guid.NewGuid();
        _printers.Setup(service => service.GetHistoryThumbnailAsync(
                printerId,
                "job-1",
                It.IsAny<CancellationToken>()))
            .ThrowsAsync((Exception)Activator.CreateInstance(exceptionType)!);

        HttpResponseMessage response = await _client.GetAsync(
            $"/api/printers/{printerId}/history/job-1/thumbnail");

        response.StatusCode.Should().Be(expectedStatus);
    }

    [Fact]
    public async Task HistoryThumbnail_HttpClientTimeout_ReturnsRequestTimeout()
    {
        Guid printerId = Guid.NewGuid();
        _printers.Setup(service => service.GetHistoryThumbnailAsync(
                printerId,
                "job-1",
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TaskCanceledException(
                "timed out",
                new TimeoutException("HttpClient timeout")));

        HttpResponseMessage response = await _client.GetAsync(
            $"/api/printers/{printerId}/history/job-1/thumbnail");

        response.StatusCode.Should().Be(HttpStatusCode.RequestTimeout);
    }

    [Fact]
    public async Task HistoryThumbnail_InaccessiblePrinter_ReturnsNotFound()
    {
        var authorization =
            new Mock<Farm.Infrastructure.Services.Queue.IQueueResourceAuthorizationService>();
        authorization
            .Setup(service => service.CanAccessPrinterAsync(
                It.IsAny<System.Security.Claims.ClaimsPrincipal>(),
                It.IsAny<Guid>(),
                PrinterGroupAccessLevel.View,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        await using var factory =
            new HistoryContractFactory(_printers.Object, authorization.Object);
        using HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(
            "X-Test-User-Id",
            Guid.NewGuid().ToString());

        HttpResponseMessage response = await client.GetAsync(
            $"/api/printers/{Guid.NewGuid()}/history/job-1/thumbnail");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        _printers.Verify(
            service => service.GetHistoryThumbnailAsync(
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
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

    [Fact]
    public async Task HistoryList_RealServiceAndOctoPrint_Limit50Returns50Rows()
    {
        var entries = Enumerable.Range(0, 250)
            .Select(index => new
            {
                name = $"job-{index:D3}.gcode",
                success = true,
                timestamp = 1700000000 + index,
            })
            .ToArray();
        using var handler = new InlineHandler(request =>
        {
            int start = ReadQueryInt(request, "start");
            int limit = ReadQueryInt(request, "limit");
            return JsonResponse(new
            {
                success = true,
                count = entries.Length,
                results = entries.Skip(start).Take(Math.Min(limit, 100)),
            });
        });
        using var adapterHttp = new HttpClient(handler);
        var adapter = new OctoPrintClient(
            adapterHttp,
            NullLogger<OctoPrintClient>.Instance,
            new BackendTimeoutSettings());
        await using AppDbContext db = CreateHistoryDbContext();
        Printer printer = CreateOctoPrintPrinter();
        PrintersService service = CreateConcreteHistoryService(
            db,
            printer,
            adapter);
        await using var factory = new HistoryContractFactory(service);
        using HttpClient client = CreateAuthenticatedClient(factory);

        HttpResponseMessage response = await client.GetAsync(
            $"/api/printers/{printer.Id}/history?limit=50");
        HistoryListResponse? body =
            await response.Content.ReadFromJsonAsync<HistoryListResponse>();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().NotBeNull();
        body!.Count.Should().Be(250);
        body.Jobs.Should().HaveCount(50);
    }

    [Fact]
    public async Task HistoryList_RealServiceAndAdapterNonAuthority_ReturnsBadGateway()
    {
        using var handler = new InlineHandler(_ =>
            JsonResponse(new
            {
                success = true,
                count = 250,
                results = Array.Empty<object>(),
            }));
        using var adapterHttp = new HttpClient(handler);
        var adapter = new OctoPrintClient(
            adapterHttp,
            NullLogger<OctoPrintClient>.Instance,
            new BackendTimeoutSettings());
        await using AppDbContext db = CreateHistoryDbContext();
        Printer printer = CreateOctoPrintPrinter();
        PrintersService service = CreateConcreteHistoryService(
            db,
            printer,
            adapter);
        await using var factory = new HistoryContractFactory(service);
        using HttpClient client = CreateAuthenticatedClient(factory);

        HttpResponseMessage response = await client.GetAsync(
            $"/api/printers/{printer.Id}/history?limit=50");
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        body.Should().Contain("history_completeness_unproven");
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
                DateTime? before,
                string? order,
                CancellationToken ct) =>
                await adapter.GetHistoryListAsync(
                    baseUrl,
                    limit,
                    start,
                    since,
                    before,
                    order,
                    credential: null,
                    ct: ct) ??
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

    private static HttpClient CreateAuthenticatedClient(
        HistoryContractFactory factory)
    {
        HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(
            "X-Test-User-Id",
            Guid.NewGuid().ToString());
        return client;
    }

    private static HttpResponseMessage JsonResponse(object payload) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json"),
        };

    private static int ReadQueryInt(HttpRequestMessage request, string name)
    {
        string value = request.RequestUri!.Query
            .TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .Single(parts => string.Equals(parts[0], name, StringComparison.Ordinal))[1];
        return int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static AppDbContext CreateHistoryDbContext()
    {
        DbContextOptions<AppDbContext> options =
            new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(
                    $"PrintersControllerHistoryContractTests_{Guid.NewGuid():N}")
                .Options;
        return new AppDbContext(options);
    }

    private static Printer CreateOctoPrintPrinter() => new()
    {
        Id = Guid.NewGuid(),
        Name = "OctoPrint history controller",
        ServerUrl = "http://octoprint.local",
        BackendPort = 80,
        Backend = (int)PrinterBackend.OctoPrint,
        Credential = new PrinterCredential { ApiKey = "test-key" },
    };

    private static PrintersService CreateConcreteHistoryService(
        AppDbContext db,
        Printer printer,
        ISupportsHistory historyClient)
    {
        var printersRepository = new Mock<IPrintersRepository>();
        printersRepository
            .Setup(repository => repository.FindByIdAsync(
                printer.Id,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(printer);
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(work => work.Printers)
            .Returns(printersRepository.Object);
        var capabilityFactory = new Mock<IBackendCapabilityFactory>();
        ISupportsHistory? supported = historyClient;
        capabilityFactory
            .Setup(factory => factory.TryGetHistoryClientTyped(
                PrinterBackend.OctoPrint,
                out supported))
            .Returns(true);

        return new PrintersService(
            unitOfWork.Object,
            db,
            Mock.Of<IBackendClientFactory>(),
            capabilityFactory.Object,
            Mock.Of<Farm.Infrastructure.Services.Catalog.ICatalogService>(),
            Mock.Of<IHttpClientFactory>(),
            NullLogger<PrintersService>.Instance,
            Mock.Of<IPrinterStatusBroadcaster>(),
            Mock.Of<IMultiPrinterStatusCoordinator>(),
            Mock.Of<IPrinterStatusClientFactory>(),
            Mock.Of<IPrinterStatusCacheReader>(),
            Mock.Of<Farm.Infrastructure.Services.Locations.ILocationService>(),
            Mock.Of<Farm.Infrastructure.Services.Security.ISensitiveDataProtector>(),
            Mock.Of<Farm.Infrastructure.Services.Interfaces.ISpoolmanService>(),
            Mock.Of<Farm.Infrastructure.Services.Cameras.IGo2RtcService>(),
            Mock.Of<Farm.Infrastructure.Services.StorageManagement.IStoragePathService>(),
            Mock.Of<Farm.Infrastructure.Services.Spoolman.IFilamentCoverageSpoolResolver>());
    }

    private sealed class InlineHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }

    private sealed class HistoryContractFactory(
        IPrintersService printers,
        Farm.Infrastructure.Services.Queue.IQueueResourceAuthorizationService? authorization = null)
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
                services.AddSingleton(printers);
                if (authorization is not null)
                {
                    services.RemoveAll<Farm.Infrastructure.Services.Queue.IQueueResourceAuthorizationService>();
                    services.AddSingleton(authorization);
                }
            });
        }
    }
}
