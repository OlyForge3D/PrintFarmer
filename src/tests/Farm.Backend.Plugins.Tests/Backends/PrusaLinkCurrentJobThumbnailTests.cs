using System.Net;
using System.Text.Json;
using Farm.Backend.Plugin.PrusaLink;
using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Farm.Backend.Plugins.Tests.Backends;

public sealed class PrusaLinkCurrentJobThumbnailTests
{
    [Fact]
    public async Task JobControlCapability_UsesProviderJobAndFileIdentity()
    {
        using var handler = new JobHandler();
        using var http = new HttpClient(handler);
        var client = new PrusaLinkClient(http, NullLogger<PrusaLinkClient>.Instance);
        ISupportsJobControl capability = Assert.IsAssignableFrom<ISupportsJobControl>(client);
        PrinterCredential credential = PrinterCredential.FromApiKey("prusa-key");

        PrinterJob firstJob = Assert.IsType<PrinterJob>(
            await capability.GetJobAsync("http://prusalink-thumbnail.invalid/", credential));
        PrinterJob secondJob = Assert.IsType<PrinterJob>(
            await capability.GetJobAsync("http://prusalink-thumbnail.invalid/", credential));

        Assert.Equal("same-name.gcode", firstJob.JobName);
        Assert.Equal(firstJob.ThumbnailUrl, secondJob.ThumbnailUrl);
        Assert.NotEqual(firstJob.ThumbnailCacheIdentity, secondJob.ThumbnailCacheIdentity);
        Assert.Equal(2, handler.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.NoContent)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task CurrentJobThumbnail_EmptyUpstreamResponse_ReturnsNull(HttpStatusCode statusCode)
    {
        using var handler = new ThumbnailHandler(_ => new HttpResponseMessage(statusCode));
        using var http = new HttpClient(handler);
        var client = new PrusaLinkApiClient(http, NullLogger<PrusaLinkApiClient>.Instance);

        HistoryThumbnailContent? result = await client.GetCurrentJobThumbnailAsync(
            "http://prusalink-thumbnail.invalid/",
            "http://prusalink-thumbnail.invalid/thumbs/current.png");

        Assert.Null(result);
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task CurrentJobThumbnail_RejectsOtherOriginAndInvalidImageContent()
    {
        using var handler = new ThumbnailHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([0x00, 0x01, 0x02])
            {
                Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png") },
            },
        });
        using var http = new HttpClient(handler);
        var client = new PrusaLinkApiClient(http, NullLogger<PrusaLinkApiClient>.Instance);

        await Assert.ThrowsAsync<InvalidDataException>(() => client.GetCurrentJobThumbnailAsync(
            "http://prusalink-thumbnail.invalid/",
            "http://other-printer.invalid/thumbs/current.png"));
        await Assert.ThrowsAsync<InvalidDataException>(() => client.GetCurrentJobThumbnailAsync(
            "http://prusalink-thumbnail.invalid/",
            "http://prusalink-thumbnail.invalid/thumbs/current.png"));
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task CurrentJobThumbnail_RejectsOversizedImage()
    {
        using var handler = new ThumbnailHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[10 * 1024 * 1024 + 1])
            {
                Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png") },
            },
        });
        using var http = new HttpClient(handler);
        var client = new PrusaLinkApiClient(http, NullLogger<PrusaLinkApiClient>.Instance);

        await Assert.ThrowsAsync<InvalidDataException>(() => client.GetCurrentJobThumbnailAsync(
            "http://prusalink-thumbnail.invalid/",
            "http://prusalink-thumbnail.invalid/thumbs/current.png"));
    }

    private sealed class ThumbnailHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(responseFactory(request));
        }
    }

    private sealed class JobHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.Equal("/api/v1/job", request.RequestUri!.AbsolutePath);
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    id = Requests,
                    state = "PRINTING",
                    progress = 0.25,
                    file = new
                    {
                        name = "same-name.gcode",
                        size = 100,
                        m_timestamp = 1_700_000_000,
                        refs = new { thumbnail = "/thumbs/same.png" },
                    },
                }), System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }
}
