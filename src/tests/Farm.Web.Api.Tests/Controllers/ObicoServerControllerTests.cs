using System.Net;
using System.Net.Http;
using System.Text;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Network;
using Farm.Modules.Observability.Controllers;
using Farm.Web.Api.Controllers;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Controllers;

public class ObicoServerControllerTests
{
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
        IEgressGuard? egressGuard = null)
    {
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
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
        ObicoServerController controller = new(dbContext, httpClientFactory.Object, egressGuard, logger.Object);
        return (controller, dbContext);
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
