using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Farm.Backend.Plugin.PrusaLink;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;

namespace Farm.Backend.Plugins.Tests.Backends;

public sealed class PrusaLinkFilePreviewTests
{
    private const string BaseUrl = "http://printer";
    private const string HttpScheme = "http://";
    private const string DetailsPath = "/api/v1/files/usb/folder/part.gcode";
    private const string ThumbnailPath = "/thumb/l/usb/folder/part.gcode";

    private static readonly byte[] Png = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 0x00];
    private static readonly byte[] Jpeg = [0xff, 0xd8, 0xff, 0xe0, 0x00, 0x10];
    private static readonly byte[] WebP = [0x52, 0x49, 0x46, 0x46, 0x04, 0x00, 0x00, 0x00, 0x57, 0x45, 0x42, 0x50];
    private static readonly PrinterFileInventoryEntry Observation = new("usb", "folder/part.gcode", 4, ModifiedValue: "1");
    private static readonly PrinterCredential Secret = PrinterCredential.FromApiKey("secret");

    [Fact]
    public void PrusaLinkClient_AdvertisesNativePreview()
    {
        using HttpClient http = new();
        Assert.IsAssignableFrom<ISupportsPrinterFilePreview>(new PrusaLinkClient(http));
    }

    [Theory]
    [InlineData("/thumb/l/usb/folder/part.gcode", "image/png")]
    [InlineData("/api/thumbnails/usb/folder/part.gcode.orig.png", "image/jpeg")]
    [InlineData("/thumb/l/usb/folder/part.gcode", "image/webp")]
    public async Task GetFilePreviewAsync_ReadsOnlyDetailsAndNativeThumbnail(string reference, string contentType)
    {
        byte[] image = contentType switch { "image/png" => Png, "image/jpeg" => Jpeg, _ => WebP };
        using Handler handler = new(Route(DetailsJson(details => details["refs"]!["thumbnail"] = reference), image, reference));
        using HttpClient http = new(handler);

        PrinterFilePreviewContent result = await PreviewAsync(http);

        Assert.Equal(123, result.PrintTimeSeconds);
        Assert.Equal(0.2, result.LayerHeightMm);
        Assert.Equal(image, result.Image);
        Assert.Equal(contentType, result.ContentType);
        Assert.Equal([DetailsPath, reference, DetailsPath], handler.Paths);
        Assert.All(handler.Calls, call =>
        {
            Assert.Equal(HttpMethod.Get, call.Method);
            Assert.Equal(BaseUrl, call.Origin);
            Assert.Equal("secret", call.ApiKey);
        });
    }

    [Fact]
    public async Task GetFilePreviewAsync_EscapesEverySegmentOfNativeReferences()
    {
        const string encoded = "/usb/My%20Folder/part%20%5B1%5D%20%26%20%C3%A9%2B.gcode";
        PrinterFileInventoryEntry observation = new("usb", "My Folder/part [1] & é+.gcode", 4, ModifiedValue: "1");
        string details = DetailsJson(change =>
        {
            change["name"] = "part [1] & é+.gcode";
            change["refs"]!["thumbnail"] = "/thumb/l" + encoded;
        });
        using Handler handler = new(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/v1/files" + encoded => Json(details),
            "/thumb/l" + encoded => Bytes(Png),
            _ => Unexpected(request),
        });
        using HttpClient http = new(handler);

        PrinterFilePreviewContent result = await PreviewAsync(http, observation);

        Assert.Equal(Png, result.Image);
        Assert.Equal(["/api/v1/files" + encoded, "/thumb/l" + encoded, "/api/v1/files" + encoded], handler.Paths);
    }

    [Fact]
    public async Task GetFilePreviewAsync_ImageTypeComesFromBytesNotResponseHeader()
    {
        using Handler handler = new(request => request.RequestUri!.AbsolutePath == DetailsPath
            ? Json(DetailsJson())
            : Bytes(Png, "image/svg+xml"));
        using HttpClient http = new(handler);

        PrinterFilePreviewContent result = await PreviewAsync(http);

        Assert.Equal("image/png", result.ContentType);
    }

    [Fact]
    public async Task GetFilePreviewAsync_WithoutCredential_SendsNoAuthenticationHeaders()
    {
        using Handler handler = new(Route(DetailsJson(), Png));
        using HttpClient http = new(handler);

        PrinterFilePreviewContent result = await Client(http).GetFilePreviewAsync(
            BaseUrl, Observation, null, CancellationToken.None);

        Assert.Equal(Png, result.Image);
        Assert.All(handler.Calls, call =>
        {
            Assert.Null(call.ApiKey);
            Assert.Null(call.Authorization);
        });
    }

    [Fact]
    public async Task GetFilePreviewAsync_DigestChallenge_AuthenticatesDetailsAndThumbnail()
    {
        Func<HttpRequestMessage, HttpResponseMessage> route = Route(DetailsJson(), Png);
        using Handler handler = new(request =>
        {
            if (request.Headers.Authorization?.Scheme == "Digest")
            {
                return route(request);
            }

            HttpResponseMessage challenge = new(HttpStatusCode.Unauthorized);
            challenge.Headers.WwwAuthenticate.ParseAdd("""Digest realm="printer", nonce="test-nonce", qop="auth" """);
            return challenge;
        });
        using HttpClient http = new(handler);

        PrinterFilePreviewContent result = await PreviewAsync(http);

        Assert.Equal(Png, result.Image);
        Assert.Equal(
            [DetailsPath, ThumbnailPath, DetailsPath],
            handler.Calls.Where(call => call.Authorization == "Digest").Select(call => call.PathAndQuery));
    }

    [Theory]
    [InlineData("""{"estimated_print_time":123,"layer_height":0.2}""", 123.0, 0.2)]
    [InlineData("""{"estimated printing time (normal mode)":"1d 2h 3m 4s"}""", 93784.0, null)]
    [InlineData("""{"estimated printing time (normal mode)":"2h 3m"}""", 7380.0, null)]
    [InlineData("""{"estimated printing time (normal mode)":"45s"}""", 45.0, null)]
    [InlineData("""{"estimated_print_time":123,"estimated printing time (normal mode)":"5s"}""", 123.0, null)]
    [InlineData("""{"estimated_print_time":-1,"estimated printing time (normal mode)":"5s"}""", 5.0, null)]
    [InlineData("""{"estimated_print_time":"123"}""", null, null)]
    [InlineData("""{"estimated printing time (normal mode)":"soon"}""", null, null)]
    [InlineData("""{"estimated printing time (normal mode)":"0s"}""", null, null)]
    [InlineData("""{"estimated printing time (normal mode)":"1h 2d"}""", null, null)]
    [InlineData("""{"estimated printing time (normal mode)":"99999999s"}""", null, null)]
    [InlineData("""{"estimated printing time (normal mode)":5}""", null, null)]
    [InlineData("""{"layer_height":0}""", null, null)]
    [InlineData("""{"layer_height":-0.2}""", null, null)]
    [InlineData("""{"layer_height":"0.2"}""", null, null)]
    [InlineData("null", null, null)]
    [InlineData("[]", null, null)]
    [InlineData("\"text\"", null, null)]
    [InlineData(null, null, null)]
    public async Task GetFilePreviewAsync_ReadsOnlyValidProviderMetadata(
        string? metaJson, double? printTime, double? layerHeight)
    {
        using Handler handler = new(Route(DetailsJson(change =>
        {
            change.Remove("refs");
            if (metaJson is null)
            {
                change.Remove("meta");
            }
            else
            {
                change["meta"] = JsonNode.Parse(metaJson);
            }
        })));
        using HttpClient http = new(handler);

        PrinterFilePreviewContent result = await PreviewAsync(http);

        Assert.Equal(printTime, result.PrintTimeSeconds);
        Assert.Equal(layerHeight, result.LayerHeightMm);
        Assert.Null(result.Image);
        Assert.Equal([DetailsPath], handler.Paths);
    }

    [Fact]
    public async Task GetFilePreviewAsync_ZeroNativeMeasurements_PreservesMetadataWithoutInvalidLegacyScalars()
    {
        using Handler handler = new(Route(DetailsJson(change =>
        {
            change.Remove("refs");
            change["meta"] = JsonNode.Parse("""{"estimated_print_time":0,"layer_height":0}""");
        })));
        using HttpClient http = new(handler);

        PrinterFilePreviewContent result = await PreviewAsync(http);

        Assert.NotNull(result.Metadata);
        Assert.Equal(0, result.Metadata.EstimatedPrintTimeSeconds);
        Assert.Equal(0, result.Metadata.LayerHeightMm);
        Assert.True(result.Metadata.IsValid());
        Assert.Null(result.PrintTimeSeconds);
        Assert.Null(result.LayerHeightMm);
        Assert.Equal([DetailsPath], handler.Paths);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("thumb/l/usb/folder/part.gcode")]
    [InlineData("../secret.png")]
    [InlineData("http://evil/thumb/l/usb/folder/part.gcode")]
    [InlineData("//evil/thumb/l/usb/folder/part.gcode")]
    [InlineData("/thumb")]
    [InlineData("/thumb/")]
    [InlineData("/thumb//x")]
    [InlineData("/thumb/./x")]
    [InlineData("/thumb/../api/files/usb/folder/part.gcode")]
    [InlineData("/thumb/%2e%2e/x")]
    [InlineData("/thumb/%2E%2E/x")]
    [InlineData("/thumb/a%2Fb")]
    [InlineData("/thumb/a%5Cb")]
    [InlineData("/thumb/a%255Cb")]
    [InlineData("/thumb/x%3Fy")]
    [InlineData("/thumb/x%23y")]
    [InlineData("/thumb/c%3Ax")]
    [InlineData("/thumb/x%00y")]
    [InlineData("/thumb/%")]
    [InlineData("/thumb/x?t=1")]
    [InlineData("/thumb/x#f")]
    [InlineData("/thumb/c:x")]
    [InlineData("/thumb/x\\y")]
    [InlineData("/thumb/x\u0001y")]
    [InlineData("/api/thumbnails")]
    [InlineData("/api/files/usb/folder/part.gcode/raw")]
    [InlineData("/api/v1/files/usb/folder/part.gcode")]
    [InlineData("/usb/folder/part.gcode")]
    public async Task GetFilePreviewAsync_UntrustedThumbnailReference_ReturnsMetadataWithoutImageRequest(string reference)
    {
        using Handler handler = new(Route(DetailsJson(change => change["refs"]!["thumbnail"] = reference)));
        using HttpClient http = new(handler);

        PrinterFilePreviewContent result = await PreviewAsync(http);

        Assert.Equal(123, result.PrintTimeSeconds);
        Assert.Null(result.Image);
        Assert.Equal([DetailsPath], handler.Paths);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("\"/thumb/l/usb/folder/part.gcode\"")]
    [InlineData("""{"thumbnail":null}""")]
    [InlineData("""{"thumbnail":42}""")]
    [InlineData("""{"thumbnail":{"href":"/thumb/l/usb/folder/part.gcode"}}""")]
    [InlineData("""{"icon":"/thumb/s/usb/folder/part.gcode"}""")]
    [InlineData("""{"download":"/usb/folder/part.gcode"}""")]
    [InlineData("""{"icon":"/thumb/s/usb/folder/part.gcode","download":"/usb/folder/part.gcode"}""")]
    public async Task GetFilePreviewAsync_WithoutNativeThumbnailReference_NeverFollowsIconOrDownload(string? refsJson)
    {
        using Handler handler = new(Route(DetailsJson(change =>
        {
            if (refsJson is null)
            {
                change.Remove("refs");
            }
            else
            {
                change["refs"] = JsonNode.Parse(refsJson);
            }
        })));
        using HttpClient http = new(handler);

        PrinterFilePreviewContent result = await PreviewAsync(http);

        Assert.Equal(123, result.PrintTimeSeconds);
        Assert.Null(result.Image);
        Assert.Equal([DetailsPath], handler.Paths);
    }

    [Theory]
    [InlineData(1024, true)]
    [InlineData(1025, false)]
    public async Task GetFilePreviewAsync_ThumbnailReferenceLengthIsBounded(int length, bool fetched)
    {
        string reference = "/thumb/" + new string('a', length - "/thumb/".Length);
        using Handler handler = new(Route(
            DetailsJson(change => change["refs"]!["thumbnail"] = reference), Png, reference));
        using HttpClient http = new(handler);

        PrinterFilePreviewContent result = await PreviewAsync(http);

        Assert.Equal(fetched, result.Image is not null);
        Assert.Equal(fetched ? [DetailsPath, reference, DetailsPath] : [DetailsPath], handler.Paths);
    }

    [Theory]
    [InlineData("size differs")]
    [InlineData("size missing")]
    [InlineData("timestamp differs")]
    [InlineData("timestamp missing")]
    [InlineData("timestamp not numeric")]
    [InlineData("type is folder")]
    [InlineData("type missing")]
    [InlineData("name differs")]
    [InlineData("name differs by case")]
    [InlineData("name missing")]
    [InlineData("name empty")]
    [InlineData("name not text")]
    [InlineData("name has separator")]
    public async Task GetFilePreviewAsync_DetailsDifferFromObservation_RejectsBeforeImage(string scenario)
    {
        using Handler handler = new(Route(DetailsJson(ChangeDetails(scenario)), Png));
        using HttpClient http = new(handler);

        await AssertFailsAsync("source_changed", http);

        Assert.Equal([DetailsPath], handler.Paths);
    }

    [Fact]
    public async Task GetFilePreviewAsync_FileChangesAfterThumbnail_RejectsCapturedImage()
    {
        int detailsReads = 0;
        using Handler handler = new(request => request.RequestUri!.AbsolutePath switch
        {
            DetailsPath => Json(DetailsJson(change =>
            {
                if (++detailsReads > 1)
                {
                    change["size"] = 5;
                }
            })),
            ThumbnailPath => Bytes(Png),
            _ => Unexpected(request),
        });
        using HttpClient http = new(handler);

        await AssertFailsAsync("source_changed", http);

        Assert.Equal([DetailsPath, ThumbnailPath, DetailsPath], handler.Paths);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.NoContent)]
    public async Task GetFilePreviewAsync_ThumbnailAbsent_ReturnsMetadataOnly(HttpStatusCode status)
    {
        using Handler handler = new(request => request.RequestUri!.AbsolutePath == DetailsPath
            ? Json(DetailsJson())
            : new HttpResponseMessage(status));
        using HttpClient http = new(handler);

        PrinterFilePreviewContent result = await PreviewAsync(http);

        Assert.Equal(123, result.PrintTimeSeconds);
        Assert.Null(result.Image);
        Assert.Null(result.ContentType);
        Assert.Equal([DetailsPath, ThumbnailPath], handler.Paths);
    }

    [Fact]
    public async Task GetFilePreviewAsync_QoiThumbnail_ReturnsMetadataOnly()
    {
        using Handler handler = new(Route(DetailsJson(), [0x71, 0x6f, 0x69, 0x66, 0x00, 0x00, 0x00, 0x01]));
        using HttpClient http = new(handler);

        PrinterFilePreviewContent result = await PreviewAsync(http);

        Assert.Equal(123, result.PrintTimeSeconds);
        Assert.Null(result.Image);
        Assert.Equal([DetailsPath, ThumbnailPath], handler.Paths);
    }

    [Theory]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>")]
    [InlineData("<!doctype html><title>captive portal</title>")]
    [InlineData("GIF89a\u0001\u0000\u0001\u0000")]
    [InlineData("\u0089PNG\r\n\u001a")]
    [InlineData("\u00ff\u00d8")]
    [InlineData("RIFF\0\0\0\0WAVE")]
    [InlineData("qoi")]
    public async Task GetFilePreviewAsync_NonRasterThumbnail_IsRejectedDespiteImageContentType(string payload)
    {
        using Handler handler = new(request => request.RequestUri!.AbsolutePath == DetailsPath
            ? Json(DetailsJson())
            : Bytes(Encoding.Latin1.GetBytes(payload), "image/png"));
        using HttpClient http = new(handler);

        await AssertFailsAsync("invalid_preview_image", http);

        Assert.Equal([DetailsPath, ThumbnailPath], handler.Paths);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "authentication_required")]
    [InlineData(HttpStatusCode.Forbidden, "authentication_required")]
    [InlineData(HttpStatusCode.InternalServerError, "transfer_failed")]
    [InlineData(HttpStatusCode.Redirect, "redirect_refused")]
    public async Task GetFilePreviewAsync_ThumbnailFailure_IsReportedWithoutFollowingRedirect(
        HttpStatusCode status, string reason)
    {
        using Handler handler = new(request =>
        {
            if (request.RequestUri!.AbsolutePath == DetailsPath)
            {
                return Json(DetailsJson());
            }

            HttpResponseMessage response = new(status);
            response.Headers.Location = new Uri("http://private.invalid/?secret=secret");
            return response;
        });
        using HttpClient http = new(handler);

        PrinterFileCopyException error = await Assert.ThrowsAsync<PrinterFileCopyException>(() => PreviewAsync(http));

        Assert.Equal(reason, error.Reason);
        Assert.DoesNotContain("secret", error.Message);
        Assert.Equal([DetailsPath, ThumbnailPath], handler.Paths);
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(false, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, false, false)]
    public async Task GetFilePreviewAsync_ThumbnailSizeLimitIsInclusive(bool oversized, bool declareLength, bool accepted)
    {
        byte[] image = [.. Png, .. new byte[PrinterFilePreviewTransport.MaximumImageBytes - Png.Length + (oversized ? 1 : 0)]];
        using Handler handler = new(request => request.RequestUri!.AbsolutePath == DetailsPath
            ? Json(DetailsJson())
            : Sized(image, declareLength));
        using HttpClient http = new(handler);

        if (accepted)
        {
            Assert.Equal(image, (await PreviewAsync(http)).Image);
        }
        else
        {
            await AssertFailsAsync("size_limit", http);
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "authentication_required")]
    [InlineData(HttpStatusCode.Forbidden, "authentication_required")]
    [InlineData(HttpStatusCode.NotFound, "source_missing")]
    [InlineData(HttpStatusCode.NoContent, "transfer_failed")]
    [InlineData(HttpStatusCode.InternalServerError, "transfer_failed")]
    [InlineData(HttpStatusCode.Redirect, "redirect_refused")]
    public async Task GetFilePreviewAsync_DetailsFailure_IsReportedWithoutFollowingRedirect(
        HttpStatusCode status, string reason)
    {
        using Handler handler = new(_ =>
        {
            HttpResponseMessage response = new(status);
            response.Headers.Location = new Uri("http://private.invalid/?secret=secret");
            return response;
        });
        using HttpClient http = new(handler);

        PrinterFileCopyException error = await Assert.ThrowsAsync<PrinterFileCopyException>(() => PreviewAsync(http));

        Assert.Equal(reason, error.Reason);
        Assert.DoesNotContain("secret", error.Message);
        Assert.Equal([DetailsPath], handler.Paths);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"text\"")]
    [InlineData("42")]
    [InlineData("true")]
    [InlineData("null")]
    public async Task GetFilePreviewAsync_NonObjectDetails_AreRejected(string json)
    {
        using Handler handler = new(_ => Json(json));
        using HttpClient http = new(handler);

        await AssertFailsAsync("invalid_preview_metadata", http);

        Assert.Equal([DetailsPath], handler.Paths);
    }

    [Theory]
    [InlineData("{\"name\":")]
    [InlineData("not json")]
    public async Task GetFilePreviewAsync_MalformedDetails_FailAsJsonError(string json)
    {
        using Handler handler = new(_ => Json(json));
        using HttpClient http = new(handler);

        await Assert.ThrowsAnyAsync<JsonException>(() => PreviewAsync(http));

        Assert.Equal([DetailsPath], handler.Paths);
    }

    [Fact]
    public async Task GetFilePreviewAsync_DeeplyNestedDetails_AreRefused()
    {
        using Handler handler = new(_ => Json(new string('[', 64) + new string(']', 64)));
        using HttpClient http = new(handler);

        await Assert.ThrowsAnyAsync<JsonException>(() => PreviewAsync(http));
    }

    [Fact]
    public async Task GetFilePreviewAsync_EmptyDetails_AreIncomplete()
    {
        using Handler handler = new(_ => Bytes([]));
        using HttpClient http = new(handler);

        await AssertFailsAsync("incomplete_response", http);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GetFilePreviewAsync_OversizedDetails_FailBeforeParsing(bool declareLength)
    {
        byte[] body = new byte[PrinterFilePreviewTransport.MaximumMetadataBytes + 1];
        using Handler handler = new(_ => Sized(body, declareLength));
        using HttpClient http = new(handler);

        await AssertFailsAsync("size_limit", http);

        Assert.Equal([DetailsPath], handler.Paths);
    }

    [Theory]
    [InlineData("/usb/", "part.gcode", BaseUrl)]
    [InlineData("us%62", "part.gcode", BaseUrl)]
    [InlineData("", "part.gcode", BaseUrl)]
    [InlineData("/", "part.gcode", BaseUrl)]
    [InlineData("usb", "", BaseUrl)]
    [InlineData("usb", "/part.gcode", BaseUrl)]
    [InlineData("usb", "a//b.gcode", BaseUrl)]
    [InlineData("usb", "../part.gcode", BaseUrl)]
    [InlineData("usb", "a/../part.gcode", BaseUrl)]
    [InlineData("usb", "a%2fb.gcode", BaseUrl)]
    [InlineData("usb", "%2e%2e/part.gcode", BaseUrl)]
    [InlineData("usb", "a\\b.gcode", BaseUrl)]
    [InlineData("usb", "a?b.gcode", BaseUrl)]
    [InlineData("usb", "a#b.gcode", BaseUrl)]
    [InlineData("usb", "part.gcode", "ftp://printer")]
    // Scheme is a constant so the committed source never holds a complete credential-bearing URL literal.
    [InlineData("usb", "part.gcode", HttpScheme + "user:pw@printer")]
    [InlineData("usb", "part.gcode", "http://printer/?token=x")]
    [InlineData("usb", "part.gcode", "http://printer/#fragment")]
    [InlineData("usb", "part.gcode", "not a url")]
    public async Task GetFilePreviewAsync_InvalidSource_IsRefusedWithoutAnyRequest(
        string root, string path, string baseUrl)
    {
        using Handler handler = new(request => Unexpected(request));
        using HttpClient http = new(handler);

        PrinterFileCopyException error = await Assert.ThrowsAsync<PrinterFileCopyException>(() =>
            Client(http).GetFilePreviewAsync(baseUrl, new(root, path, 4, ModifiedValue: "1"), Secret, CancellationToken.None));

        Assert.Equal("invalid_source_path", error.Reason);
        Assert.Empty(handler.Paths);
    }

    private static PrusaLinkClient Client(HttpClient http) => new(http);

    private static Task<PrinterFilePreviewContent> PreviewAsync(
        HttpClient http, PrinterFileInventoryEntry? observation = null) =>
        Client(http).GetFilePreviewAsync(BaseUrl, observation ?? Observation, Secret, CancellationToken.None);

    private static async Task AssertFailsAsync(string reason, HttpClient http)
    {
        PrinterFileCopyException error = await Assert.ThrowsAsync<PrinterFileCopyException>(() => PreviewAsync(http));
        Assert.Equal(reason, error.Reason);
    }

    private static string DetailsJson(Action<JsonObject>? change = null)
    {
        JsonObject details = new()
        {
            ["name"] = "part.gcode",
            ["type"] = "PRINT_FILE",
            ["size"] = 4,
            ["m_timestamp"] = 1,
            ["refs"] = new JsonObject
            {
                ["icon"] = "/thumb/s/usb/folder/part.gcode",
                ["thumbnail"] = ThumbnailPath,
                ["download"] = "/usb/folder/part.gcode",
            },
            ["meta"] = new JsonObject { ["estimated_print_time"] = 123, ["layer_height"] = 0.2 },
        };
        change?.Invoke(details);
        return details.ToJsonString();
    }

    private static Action<JsonObject> ChangeDetails(string scenario) => scenario switch
    {
        "size differs" => details => details["size"] = 5,
        "size missing" => details => details.Remove("size"),
        "timestamp differs" => details => details["m_timestamp"] = 2,
        "timestamp missing" => details => details.Remove("m_timestamp"),
        "timestamp not numeric" => details => details["m_timestamp"] = "1",
        "type is folder" => details => details["type"] = "FOLDER",
        "type missing" => details => details.Remove("type"),
        "name differs" => details => details["name"] = "other.gcode",
        "name differs by case" => details => details["name"] = "PART.gcode",
        "name missing" => details => details.Remove("name"),
        "name empty" => details => details["name"] = "",
        "name not text" => details => details["name"] = 42,
        "name has separator" => details => details["name"] = "folder/part.gcode",
        _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null),
    };

    // Any request outside the details document and its own thumbnail fails the test immediately.
    private static Func<HttpRequestMessage, HttpResponseMessage> Route(
        string details, byte[]? image = null, string thumbnailPath = ThumbnailPath) =>
        request => request.RequestUri!.AbsolutePath switch
        {
            DetailsPath => Json(details),
            var path when image is not null && path == thumbnailPath => Bytes(image),
            _ => Unexpected(request),
        };

    private static HttpResponseMessage Unexpected(HttpRequestMessage request) =>
        throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Bytes(byte[] content, string mediaType = "application/octet-stream")
    {
        ByteArrayContent body = new(content);
        body.Headers.ContentType = new(mediaType);
        return new(HttpStatusCode.OK) { Content = body };
    }

    private static HttpResponseMessage Sized(byte[] content, bool declareLength) =>
        new(HttpStatusCode.OK) { Content = declareLength ? new ByteArrayContent(content) : new UnknownLengthContent(content) };

    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(bytes).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed record Call(HttpMethod Method, string Origin, string PathAndQuery, string? ApiKey, string? Authorization);

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Call> Calls { get; } = [];

        public List<string> Paths => [.. Calls.Select(call => call.PathAndQuery)];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(new(
                request.Method,
                request.RequestUri!.GetLeftPart(UriPartial.Authority),
                request.RequestUri.PathAndQuery,
                request.Headers.TryGetValues("X-Api-Key", out IEnumerable<string>? keys) ? Assert.Single(keys) : null,
                request.Headers.Authorization?.Scheme));
            return Task.FromResult(respond(request));
        }
    }
}
