using System.Data;
using System.Net;
using System.Net.Http;
using System.Text;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Network;
using Farm.Infrastructure.Services.FailureDetection;
using Farm.Modules.Observability.Controllers;
using Farm.Web.Api.Controllers;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Controllers;

public class ObicoServerControllerTests
{
    private const string ExpectedProbeFailure = """
        {"detections":[],"message":"Failed to get image ImmutableMultiDict([('img', 'http://printfarmer.local/obico-health-probe.jpg')]) - Connection refused"}
        """;

    [Fact]
    public async Task UpdateServerAsync_WhenBundledServerCannotFetchSyntheticProbe_PersistsUrlWithoutLegacyPost()
    {
        List<CapturedRequest> requests = [];
        (ObicoServerController controller, AppDbContext dbContext) = CreateController(request =>
        {
            requests.Add(CapturedRequest.From(request));
            return new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(ExpectedProbeFailure, Encoding.UTF8, "application/json")
            };
        });
        ObicoServer server = new()
        {
            Id = Guid.NewGuid(),
            Name = "Existing Obico",
            Url = "http://retired-obico.local:3333",
            IsEnabled = true,
            MaxConcurrentAnalyses = 5,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow.AddDays(-1),
        };
        dbContext.ObicoServers.Add(server);
        await dbContext.SaveChangesAsync();

        ActionResult<ObicoServerDto> result = await controller.UpdateServerAsync(
            server.Id, new UpdateObicoServerDto { Url = "http://obico-ml-api:3333" }, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        ObicoServer persisted = await dbContext.ObicoServers.SingleAsync();
        persisted.Url.Should().Be("http://obico-ml-api:3333");
        persisted.Name.Should().Be("Existing Obico");
        persisted.IsEnabled.Should().BeTrue();
        persisted.MaxConcurrentAnalyses.Should().Be(5);
        persisted.ApiKey.Should().BeNull();
        requests.Should().ContainSingle();
        requests[0].Method.Should().Be(HttpMethod.Get);
        requests[0].PathAndQuery.Should().Be("/p/?img=http%3A%2F%2Fprintfarmer.local%2Fobico-health-probe.jpg");
    }

    [Theory]
    [InlineData(400, "{\"detections\":[],\"message\":\"Invalid request params\"}")]
    [InlineData(400, "{\"detections\":[],\"message\":\"Failed to get image http://different.local/frame.jpg\"}")]
    [InlineData(400, "{\"detections\":[{}],\"message\":\"Failed to get image http://printfarmer.local/obico-health-probe.jpg\"}")]
    [InlineData(400, "{\"message\":\"Failed to get image http://printfarmer.local/obico-health-probe.jpg\"}")]
    [InlineData(400, "{\"detections\":[],\"message\":null}")]
    [InlineData(400, "{\"detections\":[],\"message\":\"Internal error for http://printfarmer.local/obico-health-probe.jpg\"}")]
    [InlineData(400, "Failed to get image http://printfarmer.local/obico-health-probe.jpg")]
    [InlineData(400, "[]")]
    [InlineData(401, ExpectedProbeFailure)]
    [InlineData(403, ExpectedProbeFailure)]
    [InlineData(500, ExpectedProbeFailure)]
    [InlineData(503, ExpectedProbeFailure)]
    public async Task CreateServerAsync_WhenProbeFailureIsNotExpectedSyntheticResponse_RejectsServer(
        int statusCode, string body)
    {
        List<CapturedRequest> requests = [];
        (ObicoServerController controller, AppDbContext dbContext) = CreateController(request =>
        {
            requests.Add(CapturedRequest.From(request));
            return new HttpResponseMessage((HttpStatusCode)statusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        });

        ActionResult<ObicoServerDto> result = await controller.CreateServerAsync(
            new CreateObicoServerDto { Name = "Rejected Obico", Url = "http://obico.local:3333", IsEnabled = true },
            CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        dbContext.ObicoServers.Should().BeEmpty();
        requests.Should().NotBeEmpty();
        requests.Should().OnlyContain(request => request.Method == HttpMethod.Get,
            "an unrelated upstream failure must never become a successful legacy probe");
    }

    [Fact]
    public async Task CreateServerAsync_WhenPredictionEndpointIsUnreachable_RejectsServer()
    {
        int calls = 0;
        (ObicoServerController controller, AppDbContext dbContext) = CreateController(_ =>
        {
            calls++;
            throw new HttpRequestException("Synthetic network failure");
        });

        ActionResult<ObicoServerDto> result = await controller.CreateServerAsync(
            new CreateObicoServerDto { Name = "Unreachable Obico", Url = "http://obico.local:3333", IsEnabled = true },
            CancellationToken.None);

        BadRequestObjectResult badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.IsType<string>(badRequest.Value).Should().Contain("Cannot reach prediction endpoint");
        dbContext.ObicoServers.Should().BeEmpty();
        calls.Should().Be(1);
    }

    [Fact]
    public async Task GetAllServersAsync_ActiveAndLegacyAssignments_CountsAllHeldReservations()
    {
        (ObicoServerController controller, AppDbContext db) = CreateController(_ => throw new InvalidOperationException("No network expected."));
        ObicoServer server = new() { Id = Guid.NewGuid(), Name = "Pool", Url = "http://pool.invalid" };
        db.ObicoServers.Add(server);
        db.PrinterServiceStates.AddRange(
            new PrinterServiceState { PrinterId = Guid.NewGuid(), ObicoServerId = server.Id, ObicoPrintKey = "external:print" },
            new PrinterServiceState { PrinterId = Guid.NewGuid(), ObicoServerId = server.Id });
        await db.SaveChangesAsync();

        ActionResult<List<ObicoServerDto>> result = await controller.GetAllServersAsync(CancellationToken.None);

        var servers = Assert.IsType<List<ObicoServerDto>>(Assert.IsType<OkObjectResult>(result.Result).Value);
        servers.Should().ContainSingle().Which.ActivePrintCount.Should().Be(2);
        ObicoServerDto detail = Assert.IsType<ObicoServerDto>(
            Assert.IsType<OkObjectResult>((await controller.GetServerAsync(server.Id, CancellationToken.None)).Result).Value);
        detail.ActivePrintCount.Should().Be(2);
    }

    [Theory]
    [InlineData("external:print")]
    [InlineData(null)]
    public async Task DeleteServerAsync_ActiveOrLegacyReservation_RejectsDeletionWithoutReassigning(string? printKey)
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        (ObicoServerController controller, AppDbContext db) = CreateController(
            _ => throw new InvalidOperationException("No network expected."), databaseOptions: options);
        await using var ownedDb = db;
        await db.Database.EnsureCreatedAsync();
        ObicoServer server = new() { Id = Guid.NewGuid(), Name = "Pool", Url = "http://pool.invalid" };
        Manufacturer manufacturer = new() { Id = Guid.NewGuid(), Name = "Manufacturer" };
        PrinterModel model = new() { Id = Guid.NewGuid(), Name = "Model", Manufacturer = manufacturer };
        Printer printer = new()
        {
            Id = Guid.NewGuid(),
            Name = "Printer",
            ServerUrl = "http://printer.invalid",
            Manufacturer = manufacturer,
            Model = model
        };
        db.Printers.Add(printer);
        db.ObicoServers.Add(server);
        db.PrinterServiceStates.Add(new PrinterServiceState
        {
            PrinterId = printer.Id,
            ObicoServerId = server.Id,
            ObicoPrintKey = printKey
        });
        await db.SaveChangesAsync();

        IActionResult result = await controller.DeleteServerAsync(server.Id, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result).Value.Should().BeOfType<string>()
            .Which.Should().Contain("held print reservation")
            .And.Contain("restore printer connectivity")
            .And.Contain("explicitly opt out of monitoring")
            .And.Contain("next monitoring scan");
        (await db.ObicoServers.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task DeleteServerAsync_UnreservedServer_HoldsSerializableTransactionThroughDeletion()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var interceptor = new ServerDeletionTransactionInterceptor();
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection).AddInterceptors(interceptor).Options;
        (ObicoServerController controller, AppDbContext db) = CreateController(
            _ => throw new InvalidOperationException("No network expected."), databaseOptions: options);
        await using var ownedDb = db;
        await db.Database.EnsureCreatedAsync();
        ObicoServer server = new() { Id = Guid.NewGuid(), Name = "Pool", Url = "http://pool.invalid" };
        db.ObicoServers.Add(server);
        await db.SaveChangesAsync();

        IActionResult result = await controller.DeleteServerAsync(server.Id, CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        interceptor.DeletionObserved.Should().BeTrue();
        (await db.ObicoServers.CountAsync()).Should().Be(0);
        db.Database.CurrentTransaction.Should().BeNull();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task CreateServerAsync_InvalidPrintCapacity_RejectsWithoutProbe(int capacity)
    {
        (ObicoServerController controller, _) = CreateController(_ => throw new InvalidOperationException("No network expected."));

        ActionResult<ObicoServerDto> result = await controller.CreateServerAsync(
            new CreateObicoServerDto { Name = "Pool", Url = "http://pool.invalid", MaxConcurrentAnalyses = capacity }, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result).Value.Should().Be("Concurrent print capacity must be greater than zero.");
    }

    [Fact]
    public async Task CreateServerAsync_WhenUpstreamContractIsAvailable_PersistsServer()
    {
        List<CapturedRequest> requests = [];
        (ObicoServerController controller, AppDbContext dbContext) = CreateController(request =>
        {
            requests.Add(CapturedRequest.From(request));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"detections\":[]}", Encoding.UTF8, "application/json")
            };
        });

        CreateObicoServerDto dto = new()
        {
            Name = "Local Obico",
            Url = "http://obico.local:3333",
            IsEnabled = true,
            MaxConcurrentAnalyses = 2,
        };

        ActionResult<ObicoServerDto> result = await controller.CreateServerAsync(dto, CancellationToken.None);

        CreatedAtActionResult createdResult = Assert.IsType<CreatedAtActionResult>(result.Result);
        ObicoServerDto createdServer = Assert.IsType<ObicoServerDto>(createdResult.Value);
        createdServer.Name.Should().Be("Local Obico");
        createdServer.HasEndpoint.Should().BeTrue();

        dbContext.ObicoServers.Should().ContainSingle(server => server.Name == "Local Obico");
        requests.Should().ContainSingle();
        requests[0].Method.Should().Be(HttpMethod.Get);
        requests[0].PathAndQuery.Should().StartWith("/p/?img=");
    }

    [Fact]
    public async Task TestServerHealthAsync_WhenUpstreamContractRequiresFallback_UsesLegacyProbe()
    {
        List<CapturedRequest> requests = [];
        (ObicoServerController controller, AppDbContext dbContext) = CreateController(request =>
        {
            requests.Add(CapturedRequest.From(request));
            if (request.Method == HttpMethod.Get)
            {
                return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
            }

            return new HttpResponseMessage(HttpStatusCode.UnsupportedMediaType);
        });

        ObicoServer server = new()
        {
            Id = Guid.NewGuid(),
            Name = "Legacy Obico",
            Url = "http://obico.local:3333",
            IsEnabled = true,
            MaxConcurrentAnalyses = 4,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        dbContext.ObicoServers.Add(server);
        await dbContext.SaveChangesAsync();

        ActionResult<ObicoServerHealthDto> result = await controller.TestServerHealthAsync(server.Id, CancellationToken.None);

        OkObjectResult okResult = Assert.IsType<OkObjectResult>(result.Result);
        ObicoServerHealthDto health = Assert.IsType<ObicoServerHealthDto>(okResult.Value);
        health.Healthy.Should().BeTrue();
        health.ErrorMessage.Should().BeNull();

        requests.Should().HaveCount(2);
        requests[0].Method.Should().Be(HttpMethod.Get);
        requests[1].Method.Should().Be(HttpMethod.Post);
        requests[0].PathAndQuery.Should().StartWith("/p/?img=");
        requests[1].PathAndQuery.Should().Be("/p/");
    }

    [Fact]
    public async Task TestServerHealthAsync_WhenEgressGuardResolvesAnAddress_ConnectionTargetsThePinnedIpNotTheHostname()
    {
        // The real outbound connection must reuse the exact IP the egress guard vetted rather
        // than letting HttpClient re-resolve "obico.local" independently — otherwise a
        // DNS-rebinding attacker could swap the record between the check and this connection.
        List<HttpRequestMessage> requests = [];
        Mock<IEgressGuard> pinningEgressGuard = new(MockBehavior.Strict);
        pinningEgressGuard
            .Setup(guard => guard.CheckAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string url, CancellationToken _) =>
                EgressCheckResult.Allow(new Uri(url), IPAddress.Parse("203.0.113.5")));

        (ObicoServerController controller, AppDbContext dbContext) = CreateController(
            request =>
            {
                requests.Add(request);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"detections\":[]}", Encoding.UTF8, "application/json")
                };
            },
            pinningEgressGuard.Object);

        ObicoServer server = new()
        {
            Id = Guid.NewGuid(),
            Name = "Pinned Obico",
            Url = "http://obico.local:3333",
            IsEnabled = true,
            MaxConcurrentAnalyses = 4,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        dbContext.ObicoServers.Add(server);
        await dbContext.SaveChangesAsync();

        ActionResult<ObicoServerHealthDto> result = await controller.TestServerHealthAsync(server.Id, CancellationToken.None);

        OkObjectResult okResult = Assert.IsType<OkObjectResult>(result.Result);
        ObicoServerHealthDto health = Assert.IsType<ObicoServerHealthDto>(okResult.Value);
        health.Healthy.Should().BeTrue();

        requests.Should().NotBeEmpty();
        foreach (HttpRequestMessage request in requests)
        {
            request.RequestUri.Should().NotBeNull();
            request.RequestUri!.Host.Should().Be("203.0.113.5", "the connection must target the vetted IP, not re-resolve the hostname");
            request.Headers.Host.Should().Be("obico.local:3333", "the original hostname must be preserved via the Host header for virtual-hosting/SNI");
        }
    }

    [Fact]
    public async Task CreateServerAsync_WhenUpstreamServerCannotReachProbeSnapshot_PersistsCompatibleServer()
    {
        List<CapturedRequest> requests = [];
        bool isFirstRequest = true;
        (ObicoServerController controller, AppDbContext dbContext) = CreateController(request =>
        {
            requests.Add(CapturedRequest.From(request));
            if (isFirstRequest)
            {
                isFirstRequest = false;
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent(
                        "{\"error\":\"The prediction service could not fetch the supplied snapshot URL.\"}",
                        Encoding.UTF8,
                        "application/json")
                };
            }

            if (request.Method == HttpMethod.Post)
            {
                return new HttpResponseMessage(HttpStatusCode.UnsupportedMediaType);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"detections\":[]}", Encoding.UTF8, "application/json")
            };
        });

        CreateObicoServerDto dto = new()
        {
            Name = "Reachable Obico",
            Url = "http://obico.local:3333",
            IsEnabled = true,
            MaxConcurrentAnalyses = 2,
        };

        ActionResult<ObicoServerDto> result = await controller.CreateServerAsync(dto, CancellationToken.None);

        CreatedAtActionResult createdResult = Assert.IsType<CreatedAtActionResult>(result.Result);
        ObicoServerDto createdServer = Assert.IsType<ObicoServerDto>(createdResult.Value);
        createdServer.Name.Should().Be("Reachable Obico");
        dbContext.ObicoServers.Should().ContainSingle(server => server.Name == "Reachable Obico");

        requests.Should().NotBeEmpty();
        requests[0].Method.Should().Be(HttpMethod.Get);
        requests[0].PathAndQuery.Should().StartWith("/p/?img=");
    }

    [Fact]
    public async Task CreateServerAsync_WhenLegacyProbeAlsoReturnsMethodNotAllowed_RejectsServer()
    {
        List<CapturedRequest> requests = [];
        (ObicoServerController controller, AppDbContext dbContext) = CreateController(request =>
        {
            requests.Add(CapturedRequest.From(request));
            return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
        });

        CreateObicoServerDto dto = new()
        {
            Name = "Incompatible Obico",
            Url = "http://obico.local:3333",
            IsEnabled = true,
            MaxConcurrentAnalyses = 2,
        };

        ActionResult<ObicoServerDto> result = await controller.CreateServerAsync(dto, CancellationToken.None);

        BadRequestObjectResult badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        string message = Assert.IsType<string>(badRequest.Value);
        message.Should().Contain("Obico server validation failed");
        message.Should().Contain("HTTP 405");
        dbContext.ObicoServers.Should().BeEmpty();

        requests.Should().HaveCount(2);
        requests[0].Method.Should().Be(HttpMethod.Get);
        requests[1].Method.Should().Be(HttpMethod.Post);
        requests[0].PathAndQuery.Should().StartWith("/p/?img=");
        requests[1].PathAndQuery.Should().Be("/p/");
    }

    [Fact]
    public async Task CreateServerAsync_WhenUpstreamContractReturnsBadRequest_RejectsServerWithoutLegacyProbe()
    {
        List<CapturedRequest> requests = [];
        (ObicoServerController controller, AppDbContext dbContext) = CreateController(request =>
        {
            requests.Add(CapturedRequest.From(request));
            return new HttpResponseMessage(HttpStatusCode.BadRequest);
        });

        CreateObicoServerDto dto = new()
        {
            Name = "Unreachable Snapshot Obico",
            Url = "http://obico.local:3333",
            IsEnabled = true,
            MaxConcurrentAnalyses = 2,
        };

        ActionResult<ObicoServerDto> result = await controller.CreateServerAsync(dto, CancellationToken.None);

        BadRequestObjectResult badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        string message = Assert.IsType<string>(badRequest.Value);
        message.Should().Contain("Obico server validation failed");
        message.Should().Contain("HTTP 400");
        dbContext.ObicoServers.Should().BeEmpty();

        requests.Should().ContainSingle();
        requests[0].Method.Should().Be(HttpMethod.Get);
        requests[0].PathAndQuery.Should().StartWith("/p/?img=");
    }

    [Fact]
    public async Task CreateServerAsync_WhenEgressGuardDeniesDestination_RejectsServerWithoutOutboundCall()
    {
        int callCount = 0;
        (ObicoServerController controller, AppDbContext dbContext) = CreateController(
            request =>
            {
                callCount++;
                return new HttpResponseMessage(HttpStatusCode.OK);
            },
            egressGuard: DenyingEgressGuard("Destination resolves to a loopback, link-local, or multicast address"));

        CreateObicoServerDto dto = new()
        {
            Name = "Loopback Obico",
            Url = "http://127.0.0.1:3333",
            IsEnabled = true,
            MaxConcurrentAnalyses = 2,
        };

        ActionResult<ObicoServerDto> result = await controller.CreateServerAsync(dto, CancellationToken.None);

        BadRequestObjectResult badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        string message = Assert.IsType<string>(badRequest.Value);
        message.Should().Contain("Obico server validation failed");
        message.Should().Contain("loopback");
        dbContext.ObicoServers.Should().BeEmpty();
        callCount.Should().Be(0, "no outbound HTTP call should be made once the egress guard denies the destination");
    }

    [Fact]
    public async Task TestServerHealthAsync_WhenEgressGuardDeniesDestination_ReturnsUnhealthyWithoutOutboundCall()
    {
        int callCount = 0;
        (ObicoServerController controller, AppDbContext dbContext) = CreateController(
            request =>
            {
                callCount++;
                return new HttpResponseMessage(HttpStatusCode.OK);
            },
            egressGuard: DenyingEgressGuard("Destination resolves to a loopback, link-local, or multicast address"));

        ObicoServer server = new()
        {
            Id = Guid.NewGuid(),
            Name = "Loopback Obico",
            Url = "http://169.254.169.254/",
            IsEnabled = true,
            MaxConcurrentAnalyses = 4,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        dbContext.ObicoServers.Add(server);
        await dbContext.SaveChangesAsync();

        ActionResult<ObicoServerHealthDto> result = await controller.TestServerHealthAsync(server.Id, CancellationToken.None);

        OkObjectResult okResult = Assert.IsType<OkObjectResult>(result.Result);
        ObicoServerHealthDto health = Assert.IsType<ObicoServerHealthDto>(okResult.Value);
        health.Healthy.Should().BeFalse();
        health.ErrorMessage.Should().Contain("loopback");
        callCount.Should().Be(0, "no outbound HTTP call should be made once the egress guard denies the destination");
    }

    [Fact]
    public async Task UpdateServerAsync_WhenEnabledServerUrlChangesToBlockedDestination_RejectsUpdateWithoutOutboundCallOrMutation()
    {
        int callCount = 0;
        (ObicoServerController controller, AppDbContext dbContext) = CreateController(
            request =>
            {
                callCount++;
                return new HttpResponseMessage(HttpStatusCode.OK);
            },
            egressGuard: DenyingEgressGuard("Destination resolves to a loopback, link-local, or multicast address"));

        ObicoServer server = new()
        {
            Id = Guid.NewGuid(),
            Name = "Already Enabled Obico",
            Url = "http://obico.local:3333",
            ApiKey = "original-key",
            IsEnabled = true,
            MaxConcurrentAnalyses = 4,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        dbContext.ObicoServers.Add(server);
        await dbContext.SaveChangesAsync();

        UpdateObicoServerDto dto = new()
        {
            Url = "http://127.0.0.1:3333",
        };

        ActionResult<ObicoServerDto> result = await controller.UpdateServerAsync(server.Id, dto, CancellationToken.None);

        BadRequestObjectResult badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        string message = Assert.IsType<string>(badRequest.Value);
        message.Should().Contain("Obico server validation failed");
        message.Should().Contain("loopback");
        callCount.Should().Be(0, "no outbound HTTP call should be made once the egress guard denies the destination");

        ObicoServer persisted = await dbContext.ObicoServers.SingleAsync(s => s.Id == server.Id);
        persisted.Url.Should().Be("http://obico.local:3333", "the blocked URL must not be persisted");
        persisted.ApiKey.Should().Be("original-key");
    }

    [Fact]
    public async Task UpdateServerAsync_WhenEnabledServerApiKeyChangesAndDestinationBlocked_RejectsUpdateWithoutOutboundCallOrMutation()
    {
        int callCount = 0;
        (ObicoServerController controller, AppDbContext dbContext) = CreateController(
            request =>
            {
                callCount++;
                return new HttpResponseMessage(HttpStatusCode.OK);
            },
            egressGuard: DenyingEgressGuard("Destination resolves to a loopback, link-local, or multicast address"));

        ObicoServer server = new()
        {
            Id = Guid.NewGuid(),
            Name = "Already Enabled Obico With Blocked Url",
            Url = "http://127.0.0.1:3333",
            ApiKey = "original-key",
            IsEnabled = true,
            MaxConcurrentAnalyses = 4,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        dbContext.ObicoServers.Add(server);
        await dbContext.SaveChangesAsync();

        UpdateObicoServerDto dto = new()
        {
            ApiKey = "rotated-key",
        };

        ActionResult<ObicoServerDto> result = await controller.UpdateServerAsync(server.Id, dto, CancellationToken.None);

        _ = Assert.IsType<BadRequestObjectResult>(result.Result);
        callCount.Should().Be(0, "no outbound HTTP call should be made once the egress guard denies the destination");

        ObicoServer persisted = await dbContext.ObicoServers.SingleAsync(s => s.Id == server.Id);
        persisted.ApiKey.Should().Be("original-key", "the API key must not be rotated when revalidation fails");
    }

    [Fact]
    public async Task UpdateServerAsync_WhenEnablingDisabledServerWithUnchangedBlockedDestination_RejectsUpdateWithoutOutboundCallOrMutation()
    {
        int callCount = 0;
        (ObicoServerController controller, AppDbContext dbContext) = CreateController(
            request =>
            {
                callCount++;
                return new HttpResponseMessage(HttpStatusCode.OK);
            },
            egressGuard: DenyingEgressGuard("Destination resolves to a loopback, link-local, or multicast address"));

        ObicoServer server = new()
        {
            Id = Guid.NewGuid(),
            Name = "Disabled Obico With Blocked Url",
            Url = "http://127.0.0.1:3333",
            ApiKey = "original-key",
            IsEnabled = false,
            MaxConcurrentAnalyses = 4,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        dbContext.ObicoServers.Add(server);
        await dbContext.SaveChangesAsync();

        // Neither Url nor ApiKey is changing — only the disabled -> enabled transition.
        UpdateObicoServerDto dto = new()
        {
            IsEnabled = true,
        };

        ActionResult<ObicoServerDto> result = await controller.UpdateServerAsync(server.Id, dto, CancellationToken.None);

        BadRequestObjectResult badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        string message = Assert.IsType<string>(badRequest.Value);
        message.Should().Contain("loopback");
        callCount.Should().Be(0, "no outbound HTTP call should be made once the egress guard denies the destination");

        ObicoServer persisted = await dbContext.ObicoServers.SingleAsync(s => s.Id == server.Id);
        persisted.IsEnabled.Should().BeFalse("the server must not be enabled when connectivity revalidation fails");
    }

    private static IEgressGuard DenyingEgressGuard(string reason)
    {
        Mock<IEgressGuard> egressGuard = new(MockBehavior.Strict);
        egressGuard
            .Setup(guard => guard.CheckAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string url, CancellationToken _) => EgressCheckResult.Deny(reason, new Uri(url)));
        return egressGuard.Object;
    }

    private static (ObicoServerController Controller, AppDbContext DbContext) CreateController(
        Func<HttpRequestMessage, HttpResponseMessage> responder,
        IEgressGuard? egressGuard = null,
        DbContextOptions<AppDbContext>? databaseOptions = null)
    {
        DbContextOptions<AppDbContext> options = databaseOptions ?? new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"ObicoServerControllerTests_{Guid.NewGuid()}")
            .Options;

        AppDbContext dbContext = new(options);
        Mock<IHttpClientFactory> httpClientFactory = new(MockBehavior.Strict);
        httpClientFactory
            .Setup(factory => factory.CreateClient(It.IsAny<string>()))
            .Returns(new HttpClient(new RecordingHandler(responder)));

        if (egressGuard == null)
        {
            Mock<IEgressGuard> allowingEgressGuard = new(MockBehavior.Strict);
            allowingEgressGuard
                .Setup(guard => guard.CheckAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string url, CancellationToken _) => EgressCheckResult.Allow(new Uri(url)));
            egressGuard = allowingEgressGuard.Object;
        }

        Mock<ILogger<ObicoServerController>> logger = new();
        var probe = new ObicoConnectivityProbe(httpClientFactory.Object, egressGuard,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ObicoConnectivityProbe>.Instance);
        ObicoServerController controller = new(dbContext, probe, logger.Object);
        return (controller, dbContext);
    }

    private sealed class ServerDeletionTransactionInterceptor : SaveChangesInterceptor
    {
        public bool DeletionObserved { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context is AppDbContext db
                && db.ChangeTracker.Entries<ObicoServer>().Any(entry => entry.State == EntityState.Deleted))
            {
                IDbContextTransaction transaction = Assert.IsAssignableFrom<IDbContextTransaction>(db.Database.CurrentTransaction);
                transaction.GetDbTransaction().IsolationLevel.Should().Be(IsolationLevel.Serializable);
                DeletionObserved = true;
            }

            return new(result);
        }
    }

    private sealed record CapturedRequest(HttpMethod Method, string PathAndQuery)
    {
        public static CapturedRequest From(HttpRequestMessage request)
        {
            return new CapturedRequest(
                request.Method,
                request.RequestUri?.PathAndQuery ?? string.Empty);
        }
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_responder(request));
        }
    }
}
