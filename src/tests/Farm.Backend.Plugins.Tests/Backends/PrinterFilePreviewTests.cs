using System.Net;
using System.Text;
using Farm.Backend.Plugin.Moonraker;
using Farm.Backend.Plugin.OctoPrint;
using Farm.Backend.Plugin.PrusaLink;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace Farm.Backend.Plugins.Tests.Backends;

public sealed class PrinterFilePreviewTests
{
    [Fact]
    public async Task MoonrakerPreviewAsync_UsesOnlyAuthenticatedMetadataAndImage()
    {
        List<string> requests = [];
        using Handler handler = new(request =>
        {
            Assert.Equal("secret", Assert.Single(request.Headers.GetValues("X-Api-Key")));
            requests.Add(request.RequestUri!.PathAndQuery);
            return request.RequestUri.AbsolutePath.EndsWith("/metadata", StringComparison.Ordinal)
                ? Json("""
                    {"result":{"filename":"folder/part.gcode","size":4,"modified":1,
                     "estimated_time":123,"layer_height":0.2,
                     "thumbnails":[{"relative_path":".thumbs/part.png","size":8}]}}
                    """)
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
                };
        });
        using HttpClient http = new(handler);
        PrinterFilePreviewContent result = await Moonraker(http).GetFilePreviewAsync(
            "http://printer", new("gcodes", "folder/part.gcode", 4, ModifiedValue: "1"),
            PrinterCredential.FromApiKey("secret"), CancellationToken.None);
        Assert.Equal(123, result.PrintTimeSeconds);
        Assert.Equal(0.2, result.LayerHeightMm);
        Assert.Equal("image/png", result.ContentType);
        Assert.Equal(3, requests.Count);
        Assert.Equal("/server/files/gcodes/folder/.thumbs/part.png", requests[1]);
        Assert.Equal(requests[0], requests[2]);
        Assert.DoesNotContain("/server/files/gcodes/folder/part.gcode", requests);
    }

    [Fact]
    public async Task OctoPrintPreviewAsync_UsesNativeAnalysisAndExplicitlyAbsentImageAndLayer()
    {
        using Handler handler = new(request =>
        {
            Assert.Equal("/api/files/local/folder/part.gcode", request.RequestUri!.AbsolutePath);
            Assert.Equal("secret", Assert.Single(request.Headers.GetValues("X-Api-Key")));
            return Json("""
                {"path":"folder/part.gcode","size":4,"date":1,"hash":"abc",
                 "gcodeAnalysis":{"estimatedPrintTime":123}}
                """);
        });
        using HttpClient http = new(handler);
        PrinterFilePreviewContent result = await new OctoPrintClient(http).GetFilePreviewAsync(
            "http://printer", new("local", "folder/part.gcode", 4, ProviderHash: "abc", ModifiedValue: "1"),
            PrinterCredential.FromApiKey("secret"), CancellationToken.None);
        Assert.Equal(123, result.PrintTimeSeconds);
        Assert.Null(result.LayerHeightMm);
        Assert.Null(result.Image);
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task PreviewAsync_ChangedMetadataRejectsBeforeImage()
    {
        using Handler handler = new(_ => Json("""
            {"result":{"filename":"part.gcode","size":5,"modified":1,
             "thumbnails":[{"relative_path":".thumbs/part.png","size":8}]}}
            """));
        using HttpClient http = new(handler);
        Assert.Equal("source_changed", (await Assert.ThrowsAsync<PrinterFileCopyException>(() =>
            Moonraker(http).GetFilePreviewAsync("http://printer", new("gcodes", "part.gcode", 4, ModifiedValue: "1"),
                null, CancellationToken.None))).Reason);
        Assert.Equal(1, handler.Requests);
    }

    [Theory]
    [InlineData("../secret.png")]
    [InlineData("http://evil/secret.png")]
    [InlineData("%2e%2e/secret.png")]
    [InlineData(".thumbs/file.gcode")]
    public async Task PreviewAsync_UntrustedThumbnailReferenceNeverFetchesBody(string path)
    {
        using Handler handler = new(_ => Json($$$"""
            {"result":{"filename":"part.gcode","size":4,"modified":1,
             "thumbnails":[{"relative_path":"{{{path}}}","size":8}]}}
            """));
        using HttpClient http = new(handler);
        PrinterFilePreviewContent result = await Moonraker(http).GetFilePreviewAsync(
            "http://printer", new("gcodes", "part.gcode", 4, ModifiedValue: "1"), null, CancellationToken.None);
        Assert.Null(result.Image);
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task PreviewAsync_OversizedMetadataFailsBeforeParsing()
    {
        using Handler handler = new(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[PrinterFilePreviewTransport.MaximumMetadataBytes + 1]),
        });
        using HttpClient http = new(handler);
        Assert.Equal("size_limit", (await Assert.ThrowsAsync<PrinterFileCopyException>(() =>
            Moonraker(http).GetFilePreviewAsync("http://printer", new("gcodes", "part.gcode"), null,
                CancellationToken.None))).Reason);
    }

    [Fact]
    public async Task PreviewAsync_RedirectIsNotFollowedOrExposed()
    {
        using Handler handler = new(_ =>
        {
            HttpResponseMessage response = new(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri("http://private.invalid/?secret=secret");
            return response;
        });
        using HttpClient http = new(handler);
        PrinterFileCopyException error = await Assert.ThrowsAsync<PrinterFileCopyException>(() =>
            Moonraker(http).GetFilePreviewAsync("http://printer", new("gcodes", "part.gcode"), null,
                CancellationToken.None));
        Assert.Equal("redirect_refused", error.Reason);
        Assert.DoesNotContain("secret", error.Message);
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public void PrusaLink_DoesNotAdvertiseUncertifiedPreview()
    {
        using HttpClient http = new();
        Assert.False((object)new PrusaLinkClient(http) is ISupportsPrinterFilePreview);
    }

    private static MoonrakerClient Moonraker(HttpClient http) =>
        new(http, NullLogger<MoonrakerClient>.Instance, new BackendTimeoutSettings());

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests++;
            return Task.FromResult(respond(request));
        }
    }
}
