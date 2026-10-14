using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using Farm.Infrastructure.Network;
using Farm.Infrastructure.Services.FailureDetection;
using Farm.Infrastructure.Settings;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Infrastructure.Tests.Services.FailureDetection;

public partial class ObicoFailureDetectionServiceTests
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
                "VettedEgress" => new HttpClient(snapshotHandler, disposeHandler: false),
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
        snapshotRequests[0].AbsoluteUri.Should().Be("http://192.168.1.50/webcam/?action=snapshot");
        snapshotRequests[0].Host.Should().Be("printer.local");
    }

    [Fact(DisplayName = "AnalyzeImageFromUrlAsync falls back when Obico cannot reach the snapshot URL")]
    public async Task AnalyzeImageFromUrlAsync_WhenObicoCannotReachSnapshotUrl_FallsBackToLegacyUpload()
    {
        const string snapshotUrl = "http://printer.local:8080/webcam/?action=snapshot";
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
                "VettedEgress" => new HttpClient(snapshotHandler, disposeHandler: false),
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
        obicoRequests.Should().HaveCount(2);
        obicoRequests[1].Method.Should().Be(HttpMethod.Post);
        obicoRequests[1].ContentType.Should().StartWith("multipart/form-data");
        snapshotRequests.Should().ContainSingle();
        snapshotRequests[0].Method.Should().Be(HttpMethod.Get);
        snapshotRequests[0].AbsoluteUri.Should().Be("http://192.168.1.50:8080/webcam/?action=snapshot");
        snapshotRequests[0].Host.Should().Be("printer.local:8080");
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
                "VettedEgress" => new HttpClient(snapshotHandler, disposeHandler: false),
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
        snapshotRequests[0].AbsoluteUri.Should().Be("http://192.168.1.50/webcam/?action=snapshot");
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
                "VettedEgress" => new HttpClient(snapshotHandler, disposeHandler: false),
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
        snapshotRequests[0].AbsoluteUri.Should().Be("http://192.168.1.50/webcam/?action=snapshot");
    }

    [Fact]
    public async Task AnalyzeImageFromUrlAsync_WhenObicoCannotReachSnapshotUrlAndLegacyFallbackIsUnavailable_ReturnsReachabilitySpecificError()
    {
        const string snapshotUrl = "http://printer.local/webcam/?action=snapshot";
        List<CapturedRequest> obicoRequests = [];
        List<CapturedRequest> snapshotRequests = [];

        using RecordingHandler obicoHandler = new(request =>
        {
            obicoRequests.Add(CapturedRequest.From(request));
            if (request.Method == HttpMethod.Get)
            {
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent(
                        "{\"error\":\"Obico could not fetch the provided snapshot URL from its network.\"}",
                        Encoding.UTF8,
                        "application/json")
                };
            }

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
                "VettedEgress" => new HttpClient(snapshotHandler, disposeHandler: false),
                _ => throw new InvalidOperationException($"Unexpected client request: {name}")
            });

        ObicoFailureDetectionService service = CreateService(httpClientFactory);

        FailureDetectionResult result = await service.AnalyzeImageFromUrlAsync(snapshotUrl, "http://obico.local", null, CancellationToken.None);

        result.ErrorMessage.Should().Be(
            "Obico could not reach the saved snapshot URL from its network, and this server does not accept uploaded fallback snapshots (legacy POST /p/ returned HTTP 405). " +
            "Make the camera snapshot URL reachable from the Obico host, or use an Obico ML API build that supports uploaded snapshot fallback.");
        result.IsFailureDetected.Should().BeFalse();
        result.Confidence.Should().Be(0m);

        obicoRequests.Should().HaveCount(2);
        obicoRequests[0].Method.Should().Be(HttpMethod.Get);
        obicoRequests[0].PathAndQuery.Should().StartWith("/p/?img=");
        Uri.UnescapeDataString(obicoRequests[0].PathAndQuery.Split("img=", 2)[1]).Should().Be(snapshotUrl);
        obicoRequests[1].Method.Should().Be(HttpMethod.Post);
        obicoRequests[1].PathAndQuery.Should().Be("/p/");

        snapshotRequests.Should().ContainSingle();
        snapshotRequests[0].Method.Should().Be(HttpMethod.Get);
        snapshotRequests[0].AbsoluteUri.Should().Be("http://192.168.1.50/webcam/?action=snapshot");
    }

    public static IEnumerable<object[]> BlockedFallbackCases()
    {
        string[] blockedUrls =
        [
            "file:///snapshot.jpg",
            "ftp://camera.example/snapshot.jpg",
            "not-a-url",
            "http://127.0.0.1/snapshot.jpg",
            "http://[::1]/snapshot.jpg",
            "http://169.254.169.254/latest/meta-data/",
            "http://[fe80::1]/snapshot.jpg",
            "http://[::ffff:127.0.0.1]/snapshot.jpg",
            "http://blocked.example/snapshot.jpg",
            "http://mixed.example/snapshot.jpg",
            "http://unresolved.example/snapshot.jpg"
        ];

        foreach (HttpStatusCode status in FallbackStatuses)
        {
            foreach (string url in blockedUrls)
            {
                yield return [status, url];
            }
        }
    }

    [Theory]
    [MemberData(nameof(BlockedFallbackCases))]
    public async Task AnalyzeImageFromUrlAsync_WhenFallbackDestinationIsDenied_NeverFetchesOrUploads(
        HttpStatusCode upstreamStatus,
        string snapshotUrl)
    {
        List<CapturedRequest> obicoRequests = [];
        List<CapturedRequest> snapshotRequests = [];
        using RecordingHandler obicoHandler = new(request =>
        {
            obicoRequests.Add(CapturedRequest.From(request));
            return CreateFallbackResponse(upstreamStatus);
        });
        using RecordingHandler snapshotHandler = new(request =>
        {
            snapshotRequests.Add(CapturedRequest.From(request));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
        });
        Mock<IHttpClientFactory> factory = CreateFallbackClientFactory(obicoHandler, snapshotHandler);
        EgressGuard guard = CreateEgressGuard((host, _) => host switch
        {
            "blocked.example" => Task.FromResult(new[] { IPAddress.Loopback }),
            "mixed.example" => Task.FromResult(new[] { IPAddress.Parse("192.168.1.50"), IPAddress.Loopback }),
            "unresolved.example" => throw new SocketException((int)SocketError.HostNotFound),
            _ => throw new InvalidOperationException($"Unexpected DNS lookup: {host}")
        });
        ObicoFailureDetectionService service = CreateService(factory, guard);

        FailureDetectionResult result = await service.AnalyzeImageFromUrlAsync(snapshotUrl, "http://obico.local");

        using var scope = new AssertionScope();
        snapshotRequests.Should().BeEmpty();
        obicoRequests.Should().ContainSingle().Which.Method.Should().Be(HttpMethod.Get);
        factory.Verify(clientFactory => clientFactory.CreateClient("VettedEgress"), Times.Never);
        result.ErrorMessage.Should().StartWith("Snapshot URL is not allowed for local fallback:");
        result.Confidence.Should().Be(0m);
        result.IsFailureDetected.Should().BeFalse();
    }

    public static IEnumerable<object[]> PermittedFallbackCases()
    {
        foreach (HttpStatusCode status in FallbackStatuses)
        {
            yield return [status, "http://192.168.1.50:8080/snapshot.jpg?camera=1", "192.168.1.50"];
            yield return [status, "http://camera.example:8080/snapshot.jpg?camera=1", "93.184.216.34"];
            yield return [status, "https://camera.example:8443/snapshot.jpg?camera=1", "2001:4860:4860::8888"];
        }
    }

    [Theory]
    [MemberData(nameof(PermittedFallbackCases))]
    public async Task AnalyzeImageFromUrlAsync_WhenFallbackDestinationIsAllowed_FetchesPinnedAddressAndUploadsImage(
        HttpStatusCode upstreamStatus,
        string snapshotUrl,
        string resolvedAddress)
    {
        List<CapturedRequest> obicoRequests = [];
        List<CapturedRequest> snapshotRequests = [];
        using RecordingHandler obicoHandler = new(request =>
        {
            obicoRequests.Add(CapturedRequest.From(request));
            return request.Method == HttpMethod.Get
                ? CreateFallbackResponse(upstreamStatus)
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"result\":{\"p\":0.82}}", Encoding.UTF8, "application/json")
                };
        });
        using RecordingHandler snapshotHandler = new(request =>
        {
            snapshotRequests.Add(CapturedRequest.From(request));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes("camera-image"))
            };
        });
        Mock<IHttpClientFactory> factory = CreateFallbackClientFactory(obicoHandler, snapshotHandler);
        int dnsCalls = 0;
        IPAddress vettedAddress = IPAddress.Parse(resolvedAddress);
        EgressGuard guard = CreateEgressGuard((_, _) =>
        {
            dnsCalls++;
            return Task.FromResult(new[] { dnsCalls == 1 ? vettedAddress : IPAddress.Loopback });
        });
        ObicoFailureDetectionService service = CreateService(factory, guard);

        FailureDetectionResult result = await service.AnalyzeImageFromUrlAsync(snapshotUrl, "http://obico.local");

        result.ErrorMessage.Should().BeNull();
        result.Confidence.Should().Be(0.82m);
        var originalUri = new Uri(snapshotUrl);
        snapshotRequests.Should().ContainSingle().Which.AbsoluteUri.Should().Be(
            EgressGuard.CreatePinnedUri(originalUri, vettedAddress).AbsoluteUri);
        snapshotRequests[0].Host.Should().Be(originalUri.Authority);
        snapshotRequests[0].Method.Should().Be(HttpMethod.Get);
        dnsCalls.Should().Be(IPAddress.TryParse(originalUri.Host, out _) ? 0 : 1);
        factory.Verify(clientFactory => clientFactory.CreateClient("VettedEgress"), Times.Once);
        obicoRequests.Should().HaveCount(2);
        obicoRequests[1].Method.Should().Be(HttpMethod.Post);
        obicoRequests[1].ContentType.Should().StartWith("multipart/form-data");
        obicoRequests[1].Body.Should().Contain("camera-image");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnalyzeImageFromUrlAsync_WhenGuardCannotProvidePinnedDestination_NeverFetchesOrUploads(bool hasUri)
    {
        const string snapshotUrl = "http://camera.example/snapshot.jpg";
        List<CapturedRequest> obicoRequests = [];
        using RecordingHandler obicoHandler = new(request =>
        {
            obicoRequests.Add(CapturedRequest.From(request));
            return CreateFallbackResponse(HttpStatusCode.MethodNotAllowed);
        });
        Mock<IHttpClientFactory> factory = new(MockBehavior.Strict);
        factory.Setup(clientFactory => clientFactory.CreateClient("ObicoML"))
            .Returns(() => new HttpClient(obicoHandler, disposeHandler: false));
        Mock<IEgressGuard> guard = new(MockBehavior.Strict);
        guard.Setup(egress => egress.CheckAsync(snapshotUrl, CancellationToken.None))
            .ReturnsAsync(new EgressCheckResult(
                true,
                hasUri ? new Uri(snapshotUrl) : null,
                null,
                hasUri ? null : IPAddress.Parse("93.184.216.34")));
        ObicoFailureDetectionService service = CreateService(factory, guard.Object);

        FailureDetectionResult result = await service.AnalyzeImageFromUrlAsync(snapshotUrl, "http://obico.local");

        result.ErrorMessage.Should().Be(
            "Snapshot URL is not allowed for local fallback: Destination could not be pinned to a vetted address");
        result.IsFailureDetected.Should().BeFalse();
        obicoRequests.Should().ContainSingle().Which.Method.Should().Be(HttpMethod.Get);
        factory.Verify(clientFactory => clientFactory.CreateClient("VettedEgress"), Times.Never);
        guard.Verify(egress => egress.CheckAsync(snapshotUrl, CancellationToken.None), Times.Once);
    }

    [Theory]
    [InlineData(HttpStatusCode.MovedPermanently)]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.SeeOther)]
    [InlineData(HttpStatusCode.TemporaryRedirect)]
    [InlineData(HttpStatusCode.PermanentRedirect)]
    public async Task AnalyzeImageFromUrlAsync_WhenSnapshotRedirects_DoesNotFollowOrUpload(HttpStatusCode redirectStatus)
    {
        List<CapturedRequest> obicoRequests = [];
        List<CapturedRequest> snapshotRequests = [];
        using RecordingHandler obicoHandler = new(request =>
        {
            obicoRequests.Add(CapturedRequest.From(request));
            return CreateFallbackResponse(HttpStatusCode.MethodNotAllowed);
        });
        using RecordingHandler snapshotHandler = new(request =>
        {
            snapshotRequests.Add(CapturedRequest.From(request));
            var response = new HttpResponseMessage(redirectStatus);
            response.Headers.Location = new Uri("http://169.254.169.254/latest/meta-data/");
            return response;
        });
        Mock<IHttpClientFactory> factory = CreateFallbackClientFactory(obicoHandler, snapshotHandler);
        ObicoFailureDetectionService service = CreateService(factory);

        FailureDetectionResult result = await service.AnalyzeImageFromUrlAsync(
            "http://camera.example/snapshot.jpg", "http://obico.local");

        result.ErrorMessage.Should().Be($"Failed to fetch snapshot: HTTP {(int)redirectStatus}");
        snapshotRequests.Should().ContainSingle().Which.AbsoluteUri.Should().Be("http://192.168.1.50/snapshot.jpg");
        obicoRequests.Should().ContainSingle().Which.Method.Should().Be(HttpMethod.Get);
        factory.Verify(clientFactory => clientFactory.CreateClient("VettedEgress"), Times.Once);
    }

    private static readonly HttpStatusCode[] FallbackStatuses =
    [
        HttpStatusCode.NotFound,
        HttpStatusCode.MethodNotAllowed,
        HttpStatusCode.UnsupportedMediaType,
        HttpStatusCode.BadRequest,
        HttpStatusCode.OK
    ];

    private static HttpResponseMessage CreateFallbackResponse(HttpStatusCode status)
    {
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(
                status == HttpStatusCode.BadRequest
                    ? "{\"error\":\"Obico could not fetch the provided snapshot URL from its network.\"}"
                    : "{}",
                Encoding.UTF8,
                "application/json")
        };
    }

    private static Mock<IHttpClientFactory> CreateFallbackClientFactory(HttpMessageHandler obicoHandler, HttpMessageHandler snapshotHandler)
    {
        Mock<IHttpClientFactory> factory = new(MockBehavior.Strict);
        factory.Setup(clientFactory => clientFactory.CreateClient(It.IsAny<string>()))
            .Returns((string name) => name switch
            {
                "ObicoML" => new HttpClient(obicoHandler, disposeHandler: false),
                "VettedEgress" => new HttpClient(snapshotHandler, disposeHandler: false),
                // Record the old unnamed transport too, so denial tests detect an actual fetch
                // regression rather than just an unexpected factory invocation.
                "" => new HttpClient(snapshotHandler, disposeHandler: false),
                _ => throw new InvalidOperationException($"Unexpected client request: {name}")
            });
        return factory;
    }

    private static ObicoFailureDetectionService CreateService(Mock<IHttpClientFactory> httpClientFactory, IEgressGuard? egressGuard = null)
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
        return new ObicoFailureDetectionService(
            httpClientFactory.Object,
            settingsService.Object,
            logger.Object,
            egressGuard ?? CreateEgressGuard((_, _) => Task.FromResult(new[] { IPAddress.Parse("192.168.1.50") })));
    }

    private static EgressGuard CreateEgressGuard(Func<string, CancellationToken, Task<IPAddress[]>> resolveHostAsync)
    {
        return new EgressGuard(
            new ConfigurationBuilder().Build(),
            NullLogger<EgressGuard>.Instance,
            resolveHostAsync);
    }

    private sealed record CapturedRequest(HttpMethod Method, string AbsoluteUri, string PathAndQuery, string? ContentType, string Body, string? Host)
    {
        public static CapturedRequest From(HttpRequestMessage request)
        {
            // The upstream JSON-style requests (GetAsync) carry no content at all -- request.Content
            // is null and the empty-body branch below is taken. The only content-bearing request is
            // the legacy image-upload contract, which sends a MultipartFormDataContent wrapping a
            // ByteArrayContent built synchronously from data already fully buffered in memory --
            // never a network/file stream. So ReadAsStream() performs no I/O and is safe to read
            // synchronously here — unlike ReadAsStringAsync().GetAwaiter().GetResult(), which
            // VSTHRD002 (rightly) flags as a problematic synchronous wait on a task that could, in
            // general, block on real I/O.
            string body;
            if (request.Content == null)
            {
                body = string.Empty;
            }
            else
            {
                using StreamReader reader = new(request.Content.ReadAsStream(), Encoding.UTF8);
                body = reader.ReadToEnd();
            }

            return new CapturedRequest(
                request.Method,
                request.RequestUri?.AbsoluteUri ?? string.Empty,
                request.RequestUri?.PathAndQuery ?? string.Empty,
                request.Content?.Headers.ContentType?.ToString(),
                body,
                request.Headers.Host);
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
