using System.Net;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Farm.Moonraker.Emulator.Tests;

public sealed class SpoolmanTests : IClassFixture<ReadyPrinterFactory>
{
    private readonly ReadyPrinterFactory _factory;

    public SpoolmanTests(ReadyPrinterFactory factory) => _factory = factory;

    [Fact]
    public async Task Status_ReturnsConnectedAndActiveSpool()
    {
        using HttpClient client = _factory.CreateClient();
        using HttpResponseMessage response = await client.GetAsync("/server/spoolman/status");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement result = doc.RootElement.GetProperty("result");
        result.GetProperty("spoolman_connected").GetBoolean().Should().BeTrue();
        result.GetProperty("spool_id").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task SetSpoolId_ThenGet_ReturnsUpdatedValue()
    {
        using HttpClient client = _factory.CreateClient();
        using HttpResponseMessage set = await client.PostAsync(
            "/server/spoolman/spool_id",
            TestRequests.Json("""{"spool_id":2}"""));
        set.StatusCode.Should().Be(HttpStatusCode.OK);

        using HttpResponseMessage get = await client.GetAsync("/server/spoolman/spool_id");
        using JsonDocument doc = JsonDocument.Parse(await get.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("result").GetProperty("spool_id").GetInt32().Should().Be(2);

        // Restore for other tests in this class.
        await client.PostAsync("/server/spoolman/spool_id", TestRequests.Json("""{"spool_id":1}"""));
    }

    [Fact]
    public async Task Proxy_ListSpools_ReturnsSeededSpools()
    {
        using HttpClient client = _factory.CreateClient();
        using HttpResponseMessage response = await client.PostAsync(
            "/server/spoolman/proxy",
            TestRequests.Json("""{"request_method":"GET","path":"/v1/spool"}"""));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("result").GetArrayLength().Should().BeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task Proxy_GetSingleSpool_ReturnsMatchingSpool()
    {
        using HttpClient client = _factory.CreateClient();
        using HttpResponseMessage response = await client.PostAsync(
            "/server/spoolman/proxy",
            TestRequests.Json("""{"request_method":"GET","path":"/v1/spool/1"}"""));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("result").GetProperty("id").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Proxy_UnknownSpool_Returns404()
    {
        using HttpClient client = _factory.CreateClient();
        using HttpResponseMessage response = await client.PostAsync(
            "/server/spoolman/proxy",
            TestRequests.Json("""{"request_method":"GET","path":"/v1/spool/999"}"""));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("error").GetString().Should().Be("WebRequestError");
        doc.RootElement.GetProperty("message").GetString().Should().Contain("Spool not found");
    }

    [Fact]
    public async Task Proxy_UnmodeledPath_ReturnsMoonrakerShaped404_NotFabricatedSuccess()
    {
        // Farm.Backend.Plugin.Moonraker only ever calls the proxy for "list spools" and
        // "get spool by id" (see GetSpoolmanSpoolsAsync/GetSpoolmanSpoolByIdAsync). Every
        // other real Spoolman proxy path (filaments, vendors, spool CRUD, info/health/...)
        // is unimplemented here and must fail loudly rather than fabricate a 200 success —
        // this is the exact contract the emulator promises for out-of-scope capabilities.
        using HttpClient client = _factory.CreateClient();
        using HttpResponseMessage response = await client.PostAsync(
            "/server/spoolman/proxy",
            TestRequests.Json("""{"request_method":"GET","path":"/v1/filament"}"""));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("error").GetString().Should().Be("WebRequestError");
        doc.RootElement.GetProperty("message").GetString().Should().Contain("Unsupported Spoolman proxy request");
        doc.RootElement.TryGetProperty("result", out _).Should().BeFalse("an unmodeled proxy path must never return a fabricated result envelope");
    }

    [Theory]
    [InlineData("POST", "/v1/spool")]
    [InlineData("PATCH", "/v1/spool/1")]
    [InlineData("DELETE", "/v1/spool/1")]
    [InlineData("GET", "/v1/vendor")]
    [InlineData("GET", "/v1/info")]
    [InlineData("GET", "/v1/health")]
    public async Task Proxy_UnmodeledMethodOrPath_Returns404(string requestMethod, string path)
    {
        using HttpClient client = _factory.CreateClient();
        using HttpResponseMessage response = await client.PostAsync(
            "/server/spoolman/proxy",
            TestRequests.Json($$"""{"request_method":"{{requestMethod}}","path":"{{path}}"}"""));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}

public sealed class WebcamTests : IClassFixture<ReadyPrinterFactory>
{
    private readonly ReadyPrinterFactory _factory;

    public WebcamTests(ReadyPrinterFactory factory) => _factory = factory;

    [Fact]
    public async Task List_ReturnsSeededWebcamWithStreamAndSnapshotUrls()
    {
        using HttpClient client = _factory.CreateClient();
        using HttpResponseMessage response = await client.GetAsync("/server/webcams/list");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement webcam = doc.RootElement.GetProperty("webcams")[0];
        webcam.GetProperty("name").GetString().Should().Be("Nozzle Cam");
        webcam.GetProperty("uid").GetString().Should().Be("nozzle-cam");
        webcam.GetProperty("stream_url").GetString().Should().Contain("/webcams/");
        webcam.GetProperty("snapshot_url").GetString().Should().Contain("/webcams/");
    }

    [Fact]
    public async Task WebcamTest_ByDeterministicUid_ResolvesSeededWebcam()
    {
        // The seeded webcam's uid is fixed ("nozzle-cam"), not a random GUID, so a
        // lookup by that exact value is reproducible across process restarts — this is
        // the same uid the real MoonrakerClient resolves via server/webcams/list and
        // then looks up via server/webcams/test?uid=....
        using HttpClient client = _factory.CreateClient();
        using HttpResponseMessage response = await client.PostAsync("/server/webcams/test?uid=nozzle-cam", content: null);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("result").GetProperty("stream_url").GetString().Should().Contain("/webcams/");
        doc.RootElement.GetProperty("result").GetProperty("snapshot_url").GetString().Should().Contain("/webcams/");
    }

    [Fact]
    public async Task Snapshot_ReturnsJpegBytes()
    {
        using HttpClient client = _factory.CreateClient();
        using HttpResponseMessage response = await client.GetAsync("/webcams/Nozzle%20Cam/snapshot");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("image/jpeg");

        byte[] bytes = await response.Content.ReadAsByteArrayAsync();
        bytes.Should().NotBeEmpty();
        AssertJpegMagicBytes(bytes);
    }

    [Fact]
    public async Task CameraMonitorSnapshot_SnapmakerRoute_ReturnsJpegBytes()
    {
        using HttpClient client = _factory.CreateClient();
        using HttpResponseMessage response = await client.GetAsync("/server/files/camera/monitor.jpg");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("image/jpeg");

        byte[] bytes = await response.Content.ReadAsByteArrayAsync();
        bytes.Should().NotBeEmpty();
        AssertJpegMagicBytes(bytes);
    }

    [Fact]
    public async Task Stream_ReturnsMultipartMjpegWithEmbeddedJpegFrame()
    {
        using HttpClient client = _factory.CreateClient();
        using HttpResponseMessage response = await client.GetAsync(
            "/webcams/Nozzle%20Cam/stream",
            HttpCompletionOption.ResponseHeadersRead);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("multipart/x-mixed-replace");

        byte[] body = await response.Content.ReadAsByteArrayAsync();
        string preamble = System.Text.Encoding.ASCII.GetString(body, 0, Math.Min(body.Length, 64));
        preamble.Should().Contain("--frame").And.Contain("Content-Type: image/jpeg");

        // The embedded frame must itself be a valid, real (SOI/EOI-bounded) JPEG payload, not a
        // mislabeled PNG — this is what makes the MJPEG stream actually renderable in a browser.
        int frameStart = IndexOf(body, [0xFF, 0xD8, 0xFF]);
        frameStart.Should().BeGreaterThanOrEqualTo(0, "the multipart body must contain an embedded JPEG SOI marker");
        byte[] frame = body[frameStart..];
        AssertJpegMagicBytes(frame);
    }

    /// <summary>
    /// Asserts <paramref name="bytes"/> starts with the JPEG SOI marker (<c>FF D8 FF</c>) and ends
    /// with the JPEG EOI marker (<c>FF D9</c>) — i.e. it is a real, decodable JPEG payload rather than
    /// bytes mislabeled with an <c>image/jpeg</c> content type (the historic bug this guards against).
    /// </summary>
    private static void AssertJpegMagicBytes(byte[] bytes)
    {
        bytes.Length.Should().BeGreaterThanOrEqualTo(5);
        bytes[0].Should().Be(0xFF);
        bytes[1].Should().Be(0xD8);
        bytes[2].Should().Be(0xFF);
        bytes[^2].Should().Be(0xFF);
        bytes[^1].Should().Be(0xD9);
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return i;
            }
        }

        return -1;
    }
}
