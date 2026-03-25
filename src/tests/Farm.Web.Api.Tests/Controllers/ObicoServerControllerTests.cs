using System.Net;
using System.Net.Http;
using System.Text;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
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
        createdServer.Url.Should().Be("http://obico.local:3333");

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

    private static (ObicoServerController Controller, AppDbContext DbContext) CreateController(
        Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"ObicoServerControllerTests_{Guid.NewGuid()}")
            .Options;

        AppDbContext dbContext = new(options);
        Mock<IHttpClientFactory> httpClientFactory = new(MockBehavior.Strict);
        httpClientFactory
            .Setup(factory => factory.CreateClient(It.IsAny<string>()))
            .Returns(new HttpClient(new RecordingHandler(responder)));

        Mock<ILogger<ObicoServerController>> logger = new();
        ObicoServerController controller = new(dbContext, httpClientFactory.Object, logger.Object);
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
