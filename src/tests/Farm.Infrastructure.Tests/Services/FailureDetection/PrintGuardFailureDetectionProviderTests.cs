using System.Net;
using System.Text;
using Farm.Infrastructure.Services.FailureDetection;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Farm.Infrastructure.Tests.Services.FailureDetection;

public class PrintGuardFailureDetectionProviderTests
{
    [Fact]
    public async Task AnalyzeAsync_WhenResponseContainsDefectScore_PostsFrameAndReturnsScore()
    {
        byte[] imageData = [0xFF, 0xD8, 0xFF, 0x00];
        CapturedRequest? captured = null;
        using var handler = new RecordingHandler(async (request, ct) =>
        {
            captured = new CapturedRequest(
                request.Method,
                request.RequestUri,
                request.Headers.Authorization?.ToString(),
                request.Content?.Headers.ContentType?.MediaType,
                await request.Content!.ReadAsByteArrayAsync(ct));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"prediction\":\"failure\",\"defect_score\":0.81,\"margin\":1.2}",
                    Encoding.UTF8,
                    "application/json")
            };
        });
        PrintGuardFailureDetectionProvider provider = CreateProvider(handler);

        FailureDetectionProviderResult result = await provider.AnalyzeAsync(
            new FailureDetectionProviderRequest(
                imageData,
                "http://printguard.local/api/v1/classify",
                "test-token"));

        result.ErrorMessage.Should().BeNull();
        result.Score.Should().Be(0.81m);
        result.IsFailureDetected.Should().BeNull();
        captured.Should().NotBeNull();
        captured!.Method.Should().Be(HttpMethod.Post);
        captured.Endpoint!.AbsolutePath.Should().Be("/api/v1/classify");
        captured.Authorization.Should().Be("Bearer test-token");
        captured.ContentType.Should().Be("image/jpeg");
        captured.ImageData.Should().Equal(imageData);
    }

    [Fact]
    public async Task AnalyzeAsync_WhenImageIsNotJpeg_ReturnsErrorWithoutSendingRequest()
    {
        bool requestSent = false;
        using var handler = new RecordingHandler((_, _) =>
        {
            requestSent = true;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });
        PrintGuardFailureDetectionProvider provider = CreateProvider(handler);

        FailureDetectionProviderResult result = await provider.AnalyzeAsync(
            new FailureDetectionProviderRequest(Encoding.ASCII.GetBytes("not-a-jpeg"), "http://printguard.local/api/v1/classify"));

        result.ErrorMessage.Should().NotBeNullOrWhiteSpace();
        requestSent.Should().BeFalse();
    }

    [Theory]
    [InlineData("{\"prediction\":\"failure\"}")]
    [InlineData("{\"defect_score\":-0.1}")]
    [InlineData("{\"defect_score\":1.1}")]
    [InlineData("{\"defect_score\":\"0.5\"}")]
    public async Task AnalyzeAsync_WhenResponseHasInvalidDefectScore_ReturnsError(string responseBody)
    {
        using var handler = new RecordingHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
        }));
        PrintGuardFailureDetectionProvider provider = CreateProvider(handler);

        FailureDetectionProviderResult result = await provider.AnalyzeAsync(
            new FailureDetectionProviderRequest([0xFF, 0xD8, 0xFF, 0x00], "http://printguard.local/api/v1/classify"));

        result.ErrorMessage.Should().NotBeNullOrWhiteSpace();
        result.IsFailureDetected.Should().BeNull();
    }

    [Fact]
    public async Task AnalyzeAsync_WhenResponseExceedsSizeLimit_ReturnsError()
    {
        string oversizedResponse = $"{{\"defect_score\":0.5,\"padding\":\"{new string('x', 64 * 1024)}\"}}";
        using var handler = new RecordingHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new NonSeekableMemoryStream(Encoding.UTF8.GetBytes(oversizedResponse)))
        }));
        PrintGuardFailureDetectionProvider provider = CreateProvider(handler);

        FailureDetectionProviderResult result = await provider.AnalyzeAsync(
            new FailureDetectionProviderRequest([0xFF, 0xD8, 0xFF, 0x00], "http://printguard.local/api/v1/classify"));

        result.ErrorMessage.Should().Contain("64 KB");
        result.IsFailureDetected.Should().BeNull();
    }

    [Fact]
    public async Task BoundedHttpContentReader_WhenChunkedContentExceedsLimit_StopsAfterLimit()
    {
        using var content = new StreamContent(new NonSeekableMemoryStream([1, 2, 3, 4]));

        Func<Task> read = async () => await BoundedHttpContentReader.ReadAsByteArrayAsync(content, 3, CancellationToken.None);

        await read.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task AnalyzeAsync_WhenEndpointReturnsServiceUnavailable_ReturnsErrorWithoutThrowing()
    {
        using var handler = new RecordingHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        PrintGuardFailureDetectionProvider provider = CreateProvider(handler);

        FailureDetectionProviderResult result = await provider.AnalyzeAsync(
            new FailureDetectionProviderRequest([0xFF, 0xD8, 0xFF, 0x00], "http://printguard.local/api/v1/classify"));

        result.ErrorMessage.Should().Contain("503");
    }

    [Fact]
    public void TryGetClassificationEndpoint_WhenBaseUrlIsConfigured_AppendsDocumentedRoute()
    {
        var settings = new Farm.Infrastructure.Settings.PrintGuardShadowSettings
        {
            ApiUrl = "http://printguard.local:8000/",
        };

        settings.TryGetClassificationEndpoint(out Uri? endpoint).Should().BeTrue();
        endpoint!.AbsoluteUri.Should().Be("http://printguard.local:8000/api/v1/classify");
    }

    [Fact]
    public void TryGetClassificationEndpoint_WhenUrlContainsCredentialsOrQuery_IsRejected()
    {
        var settings = new Farm.Infrastructure.Settings.PrintGuardShadowSettings
        {
            ApiUrl = new UriBuilder("http", "printguard.local") { UserName = "user", Password = "password", Query = "token=secret" }.Uri.AbsoluteUri,
        };

        settings.TryGetClassificationEndpoint(out Uri? endpoint).Should().BeFalse();
        endpoint.Should().BeNull();
    }

    private static PrintGuardFailureDetectionProvider CreateProvider(HttpMessageHandler handler)
    {
        Mock<IHttpClientFactory> factory = new(MockBehavior.Strict);
        factory.Setup(item => item.CreateClient("PrintGuard"))
            .Returns(new HttpClient(handler, disposeHandler: false));
        return new PrintGuardFailureDetectionProvider(
            factory.Object,
            NullLogger<PrintGuardFailureDetectionProvider>.Instance);
    }

    private sealed record CapturedRequest(
        HttpMethod Method,
        Uri? Endpoint,
        string? Authorization,
        string? ContentType,
        byte[] ImageData);

    private sealed class NonSeekableMemoryStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return responder(request, cancellationToken);
        }
    }
}
