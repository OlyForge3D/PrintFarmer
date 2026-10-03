using System.Net;
using System.Text;
using System.Text.Json;
using Farm.Backend.Plugin.Moonraker;
using Farm.Infrastructure;
using Farm.Infrastructure.Contracts.Printers;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Farm.Backend.Plugins.Tests.Backends;

public sealed class MoonrakerCurrentJobThumbnailTests
{
    private const string BaseUrl = "http://moonraker-thumbnail.invalid/";
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    [Fact]
    public void ThumbnailCacheIdentity_UsesStableStartAndFileMetadataWithoutClockFallback()
    {
        string? subscriptionIdentity = MoonrakerThumbnailCacheIdentity.Create(
            startTime: null,
            fileSize: 100,
            modified: 1_700_000_000);
        string? directPollIdentity = MoonrakerThumbnailCacheIdentity.Create(
            startTime: null,
            fileSize: 100,
            modified: 1_700_000_000);
        string? replacedFileIdentity = MoonrakerThumbnailCacheIdentity.Create(
            startTime: null,
            fileSize: 101,
            modified: 1_700_000_001);

        Assert.Equal("file:100:1700000000", subscriptionIdentity);
        Assert.Equal(subscriptionIdentity, directPollIdentity);
        Assert.NotEqual(subscriptionIdentity, replacedFileIdentity);
        Assert.Null(MoonrakerThumbnailCacheIdentity.Create(null, null, null));
    }

    [Fact]
    public async Task CurrentJobThumbnail_UsesStableFileMetadataIdentityAndRetrievesImage()
    {
        using var handler = new ActiveJobHandler();
        using var http = new HttpClient(handler);
        var client = new MoonrakerClient(http, NullLogger<MoonrakerClient>.Instance, new BackendTimeoutSettings());
        IBackendClient backend = client;
        ISupportsCurrentJobThumbnail thumbnail = Assert.IsAssignableFrom<ISupportsCurrentJobThumbnail>(backend);
        Assert.IsNotAssignableFrom<ISupportsJobControl>(backend);
        PrinterCredential credential = PrinterCredential.FromApiKey("thumbnail-test-key");

        PrinterJob firstJob = Assert.IsType<PrinterJob>(
            await thumbnail.GetCurrentJobAsync(BaseUrl, credential, CancellationToken.None));
        PrinterJob secondJob = Assert.IsType<PrinterJob>(
            await thumbnail.GetCurrentJobAsync(BaseUrl, credential, CancellationToken.None));

        Assert.Equal("same-name.gcode", firstJob.JobName);
        Assert.Equal(
            "http://moonraker-thumbnail.invalid/server/files/gcodes/thumbnails/thumbnail.png",
            firstJob.ThumbnailUrl);
        Assert.Equal(firstJob.ThumbnailUrl, secondJob.ThumbnailUrl);
        Assert.Equal(firstJob.ThumbnailCacheIdentity, secondJob.ThumbnailCacheIdentity);
        Assert.Equal(
            PrinterThumbnailUrl.GetCacheToken(firstJob.JobName!, firstJob.ThumbnailUrl!, firstJob.ThumbnailCacheIdentity),
            PrinterThumbnailUrl.GetCacheToken(secondJob.JobName!, secondJob.ThumbnailUrl!, secondJob.ThumbnailCacheIdentity));
        handler.ReplaceFile = true;
        PrinterJob replacedJob = Assert.IsType<PrinterJob>(
            await thumbnail.GetCurrentJobAsync(BaseUrl, credential, CancellationToken.None));
        Assert.NotEqual(firstJob.ThumbnailCacheIdentity, replacedJob.ThumbnailCacheIdentity);

        HistoryThumbnailContent? content = await thumbnail.GetCurrentJobThumbnailAsync(
            BaseUrl,
            firstJob.ThumbnailUrl!,
            credential,
            CancellationToken.None);

        Assert.Equal("image/png", Assert.IsType<HistoryThumbnailContent>(content).ContentType);
        Assert.Equal(PngSignature, content.Content);
        Assert.All(handler.ApiKeyHeaders, value => Assert.Equal("thumbnail-test-key", value));
    }

    [Theory]
    [InlineData(HttpStatusCode.NoContent)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task GetCurrentJobThumbnailAsync_EmptyUpstreamResponse_ReturnsNull(HttpStatusCode statusCode)
    {
        using var handler = new ThumbnailResponseHandler(new HttpResponseMessage(statusCode));
        using var http = new HttpClient(handler);
        var client = CreateClient(http);

        HistoryThumbnailContent? content = await client.GetCurrentJobThumbnailAsync(
            BaseUrl,
            new Uri(new Uri(BaseUrl), "server/files/gcodes/thumbnail.png").ToString(),
            PrinterCredential.FromApiKey("thumbnail-test-key"),
            CancellationToken.None);

        Assert.Null(content);
        Assert.Equal("thumbnail-test-key", handler.ApiKey);
    }

    [Fact]
    public async Task GetCurrentJobThumbnailAsync_RejectsDifferentOriginAndInvalidImageSignature()
    {
        using var handler = new ThumbnailResponseHandler(ImageResponse([0x00, 0x01, 0x02]));
        using var http = new HttpClient(handler);
        var client = CreateClient(http);

        await Assert.ThrowsAsync<InvalidDataException>(() => client.GetCurrentJobThumbnailAsync(
            BaseUrl,
            "http://different-host.invalid/server/files/gcodes/thumbnail.png",
            PrinterCredential.FromApiKey("thumbnail-test-key"),
            CancellationToken.None));

        await Assert.ThrowsAsync<InvalidDataException>(() => client.GetCurrentJobThumbnailAsync(
            BaseUrl,
            new Uri(new Uri(BaseUrl), "server/files/gcodes/thumbnail.png").ToString(),
            PrinterCredential.FromApiKey("thumbnail-test-key"),
            CancellationToken.None));
        Assert.Equal("thumbnail-test-key", handler.ApiKey);
    }

    [Theory]
    [InlineData("http://moonraker-thumbnail.invalid/prefix/server/files/gcodes/thumb.png")]
    [InlineData("http://moonraker-thumbnail.invalid/server/files/gcodes/thumb.png?download=1")]
    [InlineData("http://moonraker-thumbnail.invalid/server/files/gcodes/thumb.png#fragment")]
    public async Task GetCurrentJobThumbnailAsync_RejectsUntrustedPathAndUriComponents(string thumbnailUrl)
    {
        using var handler = new ThumbnailResponseHandler(ImageResponse(PngSignature));
        using var http = new HttpClient(handler);
        var client = CreateClient(http);

        await Assert.ThrowsAsync<InvalidDataException>(() => client.GetCurrentJobThumbnailAsync(
            BaseUrl,
            thumbnailUrl,
            PrinterCredential.FromApiKey("thumbnail-test-key"),
            CancellationToken.None));

        Assert.Null(handler.ApiKey);
    }

    [Fact]
    public async Task GetCurrentJobThumbnailAsync_RedirectResponse_IsNotFollowed()
    {
        using var handler = new RedirectHandler();
        using var http = new HttpClient(handler);
        var client = CreateClient(http);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetCurrentJobThumbnailAsync(
            BaseUrl,
            new Uri(new Uri(BaseUrl), "server/files/gcodes/thumbnail.png").ToString(),
            PrinterCredential.FromApiKey("thumbnail-test-key"),
            CancellationToken.None));

        Assert.Equal(1, handler.Requests);
        Assert.Equal("thumbnail-test-key", handler.ApiKey);
    }

    [Fact]
    public async Task GetCurrentJobThumbnailAsync_RejectsOversizedResponse()
    {
        using var handler = new ThumbnailResponseHandler(ImageResponse(
            new byte[10 * 1024 * 1024 + 1]));
        using var http = new HttpClient(handler);
        var client = CreateClient(http);

        await Assert.ThrowsAsync<InvalidDataException>(() => client.GetCurrentJobThumbnailAsync(
            BaseUrl,
            new Uri(new Uri(BaseUrl), "server/files/gcodes/thumbnail.png").ToString(),
            PrinterCredential.FromApiKey("thumbnail-test-key"),
            CancellationToken.None));
    }

    [Fact]
    public void PrinterStatusUpdate_SerializesRelativeVersionedUrlWithoutBackendTarget()
    {
        PrinterStatusUpdate update = new(
            Guid.NewGuid(),
            true,
            "printing",
            50,
            "same-name.gcode",
            "http://printer.internal/server/files/gcodes/thumb.png",
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            ThumbnailCacheIdentity: "start:1700000000");

        using JsonDocument json = JsonDocument.Parse(JsonSerializer.Serialize(update, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        string url = json.RootElement.GetProperty("currentJobThumbnailUrl").GetString()!;
        Assert.StartsWith("/api/printers/", url, StringComparison.Ordinal);
        Assert.Contains("/current-job/thumbnail?v=", url, StringComparison.Ordinal);
        Assert.DoesNotContain("printer.internal", json.RootElement.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("start:1700000000", json.RootElement.GetRawText(), StringComparison.Ordinal);
    }

    private static MoonrakerClient CreateClient(HttpClient http) =>
        new(http, NullLogger<MoonrakerClient>.Instance, new BackendTimeoutSettings());

    private static HttpResponseMessage ImageResponse(byte[] bytes) =>
        new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes),
        };

    private sealed class ActiveJobHandler : HttpMessageHandler
    {
        public bool ReplaceFile { get; set; }

        public List<string> ApiKeyHeaders { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            ApiKeyHeaders.Add(Assert.Single(request.Headers.GetValues("X-Api-Key")));
            if (request.RequestUri!.AbsolutePath == "/printer/objects/query")
            {
                object payload = new
                {
                    result = new
                    {
                        status = new
                        {
                            display_status = new { progress = 0.5 },
                            print_stats = new
                            {
                                state = "printing",
                                filename = "same-name.gcode",
                                print_duration = 10,
                            },
                        },
                        job_queue = new { },
                    },
                };
                return Task.FromResult(JsonResponse(JsonSerializer.Serialize(payload)));
            }

            if (request.RequestUri.AbsolutePath == "/server/files/metadata")
            {
                return Task.FromResult(JsonResponse(JsonSerializer.Serialize(new
                {
                    result = new
                    {
                        size = ReplaceFile ? 101 : 100,
                        modified = ReplaceFile ? 1_700_000_001 : 1_700_000_000,
                        thumbnails = new[]
                        {
                            new { width = 32, height = 32, relative_path = "thumbnails/thumbnail.png" },
                        },
                    },
                })));
            }

            Assert.Equal("/server/files/gcodes/thumbnails/thumbnail.png", request.RequestUri.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(PngSignature),
            });
        }

        private static HttpResponseMessage JsonResponse(string json) =>
            new(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
    }

    private sealed class ThumbnailResponseHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public string? ApiKey { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            ApiKey = Assert.Single(request.Headers.GetValues("X-Api-Key"));
            return Task.FromResult(response);
        }
    }

    private sealed class RedirectHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }
        public string? ApiKey { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests++;
            ApiKey = Assert.Single(request.Headers.GetValues("X-Api-Key"));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect)
            {
                Headers = { Location = new Uri("http://attacker.invalid/collect") },
            });
        }
    }
}
