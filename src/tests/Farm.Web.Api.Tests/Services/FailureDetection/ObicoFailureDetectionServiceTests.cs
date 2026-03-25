using System.Net;
using System.Net.Http;
using System.Text;
using Farm.Infrastructure.Services.FailureDetection;
using Farm.Infrastructure.Settings;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services.FailureDetection;

public class ObicoFailureDetectionServiceTests
{
    [Fact]
    public async Task AnalyzeImageFromUrlAsync_WhenUpstreamContractReturnsDetections_UsesSnapshotQueryContract()
    {
        const string snapshotUrl = "http://printer.local/webcam/?action=snapshot";
        List<CapturedRequest> obicoRequests = [];
        using RecordingHandler obicoHandler = new(request =>
        {
            obicoRequests.Add(CapturedRequest.From(request));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"detections\":[[\"spaghetti\",0.91,[1,2,3,4]],[\"blob\",0.42,[4,5,6,7]]]}", Encoding.UTF8, "application/json")
            };
        });

        Mock<IHttpClientFactory> httpClientFactory = new(MockBehavior.Strict);
        httpClientFactory
            .Setup(factory => factory.CreateClient(It.IsAny<string>()))
            .Returns((string name) => name == "ObicoML"
                ? new HttpClient(obicoHandler, disposeHandler: false)
                : throw new InvalidOperationException($"Unexpected client request: {name}"));

        ObicoFailureDetectionService service = CreateService(httpClientFactory);

        FailureDetectionResult result = await service.AnalyzeImageFromUrlAsync(snapshotUrl, "http://obico.local", null, CancellationToken.None);

        result.ErrorMessage.Should().BeNull();
        result.Confidence.Should().Be(0.91m);
        result.IsFailureDetected.Should().BeTrue();

        obicoRequests.Should().ContainSingle();
        obicoRequests[0].Method.Should().Be(HttpMethod.Get);
        obicoRequests[0].PathAndQuery.Should().StartWith("/p/?img=");
        Uri.UnescapeDataString(obicoRequests[0].PathAndQuery.Split("img=", 2)[1]).Should().Be(snapshotUrl);
    }

    [Fact]
    public async Task AnalyzeImageFromUrlAsync_WhenUpstreamContractIsUnavailable_FallsBackToLegacyUpload()
    {
        const string snapshotUrl = "http://printer.local/webcam/?action=snapshot";
        List<CapturedRequest> obicoRequests = [];
        List<CapturedRequest> snapshotRequests = [];

        using RecordingHandler obicoHandler = new(request =>
        {
            obicoRequests.Add(CapturedRequest.From(request));
            if (request.Method == HttpMethod.Get)
            {
                return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"result\":{\"p\":0.82}}", Encoding.UTF8, "application/json")
            };
        });

        using RecordingHandler snapshotHandler = new(request =>
        {
            snapshotRequests.Add(CapturedRequest.From(request));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2, 3, 4, 5])
            };
        });

        Mock<IHttpClientFactory> httpClientFactory = new(MockBehavior.Strict);
        httpClientFactory
            .Setup(factory => factory.CreateClient(It.IsAny<string>()))
            .Returns((string name) => name switch
            {
                "ObicoML" => new HttpClient(obicoHandler, disposeHandler: false),
                "" => new HttpClient(snapshotHandler, disposeHandler: false),
                _ => throw new InvalidOperationException($"Unexpected client request: {name}")
            });

        ObicoFailureDetectionService service = CreateService(httpClientFactory);

        FailureDetectionResult result = await service.AnalyzeImageFromUrlAsync(snapshotUrl, "http://obico.local", null, CancellationToken.None);

        result.ErrorMessage.Should().BeNull();
        result.Confidence.Should().Be(0.82m);
        result.IsFailureDetected.Should().BeTrue();

        obicoRequests.Should().HaveCount(2);
        obicoRequests[0].Method.Should().Be(HttpMethod.Get);
        obicoRequests[0].PathAndQuery.Should().StartWith("/p/?img=");
        Uri.UnescapeDataString(obicoRequests[0].PathAndQuery.Split("img=", 2)[1]).Should().Be(snapshotUrl);
        obicoRequests[1].Method.Should().Be(HttpMethod.Post);
        obicoRequests[1].PathAndQuery.Should().Be("/p/");
        obicoRequests[1].ContentType.Should().StartWith("multipart/form-data");
        obicoRequests[1].Body.Should().Contain("name=img");

        snapshotRequests.Should().ContainSingle();
        snapshotRequests[0].Method.Should().Be(HttpMethod.Get);
        snapshotRequests[0].AbsoluteUri.Should().Be(snapshotUrl);
    }

    [Fact(DisplayName = "AnalyzeImageFromUrlAsync falls back when Obico cannot reach the snapshot URL")]
    public async Task AnalyzeImageFromUrlAsync_WhenObicoCannotReachSnapshotUrl_FallsBackToLegacyUpload()
    {
        const string snapshotUrl = "http://127.0.0.1:8080/webcam/?action=snapshot";
        List<CapturedRequest> obicoRequests = [];
        List<CapturedRequest> snapshotRequests = [];
        bool isFirstObicoRequest = true;

        using RecordingHandler obicoHandler = new(request =>
        {
            obicoRequests.Add(CapturedRequest.From(request));
            if (isFirstObicoRequest)
            {
                isFirstObicoRequest = false;
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent(
                        "{\"error\":\"Obico could not fetch the provided snapshot URL from its network.\"}",
                        Encoding.UTF8,
                        "application/json")
                };
            }

            if (request.Method == HttpMethod.Post)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"result\":{\"p\":0.88}}", Encoding.UTF8, "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"detections\":[[\"spaghetti\",0.88,[1,2,3,4]]]}", Encoding.UTF8, "application/json")
            };
        });

        using RecordingHandler snapshotHandler = new(request =>
        {
            snapshotRequests.Add(CapturedRequest.From(request));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([9, 8, 7, 6, 5])
            };
        });

        Mock<IHttpClientFactory> httpClientFactory = new(MockBehavior.Strict);
        httpClientFactory
            .Setup(factory => factory.CreateClient(It.IsAny<string>()))
            .Returns((string name) => name switch
            {
                "ObicoML" => new HttpClient(obicoHandler, disposeHandler: false),
                "" => new HttpClient(snapshotHandler, disposeHandler: false),
                _ => throw new InvalidOperationException($"Unexpected client request: {name}")
            });

        ObicoFailureDetectionService service = CreateService(httpClientFactory);

        FailureDetectionResult result = await service.AnalyzeImageFromUrlAsync(snapshotUrl, "http://obico.local", null, CancellationToken.None);

        result.ErrorMessage.Should().BeNull("reachability-only GET failures should use the recovery path instead of surfacing a raw HTTP 400");
        result.Confidence.Should().Be(0.88m);
        result.IsFailureDetected.Should().BeTrue();

        obicoRequests.Should().NotBeEmpty();
        obicoRequests[0].Method.Should().Be(HttpMethod.Get);
        obicoRequests[0].PathAndQuery.Should().StartWith("/p/?img=");
        Uri.UnescapeDataString(obicoRequests[0].PathAndQuery.Split("img=", 2)[1]).Should().Be(snapshotUrl);
        obicoRequests.Count.Should().BeGreaterThan(1, "a private or loopback snapshot URL needs a fallback/proxy/local-upload recovery path");

        if (snapshotRequests.Count > 0)
        {
            snapshotRequests.Should().ContainSingle();
            snapshotRequests[0].Method.Should().Be(HttpMethod.Get);
            snapshotRequests[0].AbsoluteUri.Should().Be(snapshotUrl);
        }
    }

    [Fact]
    public async Task AnalyzeImageFromUrlAsync_WhenUpstreamContractReturnsBadRequestWithoutReachabilitySignal_ReturnsActionableSnapshotUrlError()
    {
        const string snapshotUrl = "http://printer.local/webcam/?action=snapshot";
        List<CapturedRequest> obicoRequests = [];

        using RecordingHandler obicoHandler = new(request =>
        {
            obicoRequests.Add(CapturedRequest.From(request));
            return new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("{\"error\":\"invalid snapshot url\"}", Encoding.UTF8, "application/json")
            };
        });

        Mock<IHttpClientFactory> httpClientFactory = new(MockBehavior.Strict);
        httpClientFactory
            .Setup(factory => factory.CreateClient(It.IsAny<string>()))
            .Returns((string name) => name == "ObicoML"
                ? new HttpClient(obicoHandler, disposeHandler: false)
                : throw new InvalidOperationException($"Unexpected client request: {name}"));

        ObicoFailureDetectionService service = CreateService(httpClientFactory);

        FailureDetectionResult result = await service.AnalyzeImageFromUrlAsync(snapshotUrl, "http://obico.local", null, CancellationToken.None);

        result.ErrorMessage.Should().Be(
            "Obico rejected the snapshot URL request (HTTP 400). Check whether the saved snapshot URL is reachable from the Obico server network and still returns an image.");
        result.IsFailureDetected.Should().BeFalse();
        result.Confidence.Should().Be(0m);

        obicoRequests.Should().ContainSingle();
        obicoRequests[0].Method.Should().Be(HttpMethod.Get);
        obicoRequests[0].PathAndQuery.Should().StartWith("/p/?img=");
    }

    [Fact]
    public async Task AnalyzeImageFromUrlAsync_WhenSnapshotUrlAnalysisTimesOut_ReturnsActionableTimeoutMessage()
    {
        const string snapshotUrl = "http://printer.local/webcam/?action=snapshot";
        List<CapturedRequest> obicoRequests = [];

        using ThrowingHandler obicoHandler = new(request =>
        {
            obicoRequests.Add(CapturedRequest.From(request));
            return new TaskCanceledException("timed out");
        });

        Mock<IHttpClientFactory> httpClientFactory = new(MockBehavior.Strict);
        httpClientFactory
            .Setup(factory => factory.CreateClient(It.IsAny<string>()))
            .Returns((string name) => name == "ObicoML"
                ? new HttpClient(obicoHandler, disposeHandler: false)
                : throw new InvalidOperationException($"Unexpected client request: {name}"));

        ObicoFailureDetectionService service = CreateService(httpClientFactory);

        FailureDetectionResult result = await service.AnalyzeImageFromUrlAsync(snapshotUrl, "http://obico.local", null, CancellationToken.None);

        result.ErrorMessage.Should().Be(
            "Obico analysis timed out while fetching the snapshot URL. Check the Obico server load and whether the camera feed is reachable from that server.");
        result.IsFailureDetected.Should().BeFalse();
        result.Confidence.Should().Be(0m);

        obicoRequests.Should().ContainSingle();
        obicoRequests[0].Method.Should().Be(HttpMethod.Get);
        obicoRequests[0].PathAndQuery.Should().StartWith("/p/?img=");
    }

    [Fact]
    public async Task AnalyzeImageFromUrlAsync_WhenLocalSnapshotFetchTimesOut_ReturnsActionableSnapshotFetchTimeout()
    {
        const string snapshotUrl = "http://printer.local/webcam/?action=snapshot";
        List<CapturedRequest> obicoRequests = [];
        List<CapturedRequest> snapshotRequests = [];

        using RecordingHandler obicoHandler = new(request =>
        {
            obicoRequests.Add(CapturedRequest.From(request));
            return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
        });

        using ThrowingHandler snapshotHandler = new(request =>
        {
            snapshotRequests.Add(CapturedRequest.From(request));
            return new TaskCanceledException("timed out");
        });

        Mock<IHttpClientFactory> httpClientFactory = new(MockBehavior.Strict);
        httpClientFactory
            .Setup(factory => factory.CreateClient(It.IsAny<string>()))
            .Returns((string name) => name switch
            {
                "ObicoML" => new HttpClient(obicoHandler, disposeHandler: false),
                "" => new HttpClient(snapshotHandler, disposeHandler: false),
                _ => throw new InvalidOperationException($"Unexpected client request: {name}")
            });

        ObicoFailureDetectionService service = CreateService(httpClientFactory);

        FailureDetectionResult result = await service.AnalyzeImageFromUrlAsync(snapshotUrl, "http://obico.local", null, CancellationToken.None);

        result.ErrorMessage.Should().Be("Snapshot fetch timeout. PrintFarmer could not download the camera snapshot in time.");
        result.IsFailureDetected.Should().BeFalse();
        result.Confidence.Should().Be(0m);

        obicoRequests.Should().ContainSingle();
        obicoRequests[0].Method.Should().Be(HttpMethod.Get);
        obicoRequests[0].PathAndQuery.Should().StartWith("/p/?img=");

        snapshotRequests.Should().ContainSingle();
        snapshotRequests[0].Method.Should().Be(HttpMethod.Get);
        snapshotRequests[0].AbsoluteUri.Should().Be(snapshotUrl);
    }

    [Fact]
    public async Task AnalyzeImageFromUrlAsync_WhenLegacyFallbackRouteAlsoReturnsMethodNotAllowed_ReturnsActionableCompatibilityError()
    {
        const string snapshotUrl = "http://printer.local/webcam/?action=snapshot";
        List<CapturedRequest> obicoRequests = [];
        List<CapturedRequest> snapshotRequests = [];

        using RecordingHandler obicoHandler = new(request =>
        {
            obicoRequests.Add(CapturedRequest.From(request));
            return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
        });

        using RecordingHandler snapshotHandler = new(request =>
        {
            snapshotRequests.Add(CapturedRequest.From(request));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2, 3, 4, 5])
            };
        });

        Mock<IHttpClientFactory> httpClientFactory = new(MockBehavior.Strict);
        httpClientFactory
            .Setup(factory => factory.CreateClient(It.IsAny<string>()))
            .Returns((string name) => name switch
            {
                "ObicoML" => new HttpClient(obicoHandler, disposeHandler: false),
                "" => new HttpClient(snapshotHandler, disposeHandler: false),
                _ => throw new InvalidOperationException($"Unexpected client request: {name}")
            });

        ObicoFailureDetectionService service = CreateService(httpClientFactory);

        FailureDetectionResult result = await service.AnalyzeImageFromUrlAsync(snapshotUrl, "http://obico.local", null, CancellationToken.None);

        result.ErrorMessage.Should().Be(
            "Configured Obico server is not exposing a supported prediction route (legacy POST /p/ returned HTTP 405). " +
            "Check that the URL points to the Obico ML API root that supports upstream GET /p/?img=... or legacy POST /p/.");
        result.IsFailureDetected.Should().BeFalse();
        result.Confidence.Should().Be(0m);

        obicoRequests.Should().HaveCount(2);
        obicoRequests[0].Method.Should().Be(HttpMethod.Get);
        obicoRequests[0].PathAndQuery.Should().StartWith("/p/?img=");
        Uri.UnescapeDataString(obicoRequests[0].PathAndQuery.Split("img=", 2)[1]).Should().Be(snapshotUrl);
        obicoRequests[1].Method.Should().Be(HttpMethod.Post);
        obicoRequests[1].PathAndQuery.Should().Be("/p/");
        obicoRequests[1].ContentType.Should().StartWith("multipart/form-data");

        snapshotRequests.Should().ContainSingle();
        snapshotRequests[0].Method.Should().Be(HttpMethod.Get);
        snapshotRequests[0].AbsoluteUri.Should().Be(snapshotUrl);
    }

    private static ObicoFailureDetectionService CreateService(Mock<IHttpClientFactory> httpClientFactory)
    {
        Mock<ISettingsService> settingsService = new(MockBehavior.Strict);
        settingsService
            .Setup(settings => settings.Get<ObicoSettings>())
            .Returns(new ObicoSettings
            {
                ObicoApiUrl = "http://obico.default",
                ConfidenceThreshold = 0.7m,
            });

        Mock<ILogger<ObicoFailureDetectionService>> logger = new();
        return new ObicoFailureDetectionService(httpClientFactory.Object, settingsService.Object, logger.Object);
    }

    private sealed record CapturedRequest(HttpMethod Method, string AbsoluteUri, string PathAndQuery, string? ContentType, string Body)
    {
        public static CapturedRequest From(HttpRequestMessage request)
        {
            string body = request.Content == null
                ? string.Empty
                : request.Content.ReadAsStringAsync().GetAwaiter().GetResult();

            return new CapturedRequest(
                request.Method,
                request.RequestUri?.AbsoluteUri ?? string.Empty,
                request.RequestUri?.PathAndQuery ?? string.Empty,
                request.Content?.Headers.ContentType?.ToString(),
                body);
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

    private sealed class ThrowingHandler(Func<HttpRequestMessage, Exception> exceptionFactory) : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Exception> _exceptionFactory = exceptionFactory;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            throw _exceptionFactory(request);
        }
    }
}
