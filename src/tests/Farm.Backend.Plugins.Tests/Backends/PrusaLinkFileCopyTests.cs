using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Farm.Backend.Plugin.PrusaLink;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;

namespace Farm.Backend.Plugins.Tests.Backends;

public sealed class PrusaLinkFileCopyTests
{
    private const string BaseUrl = "http://printer";
    private const string ShortPath = "CABLE~1.GCO";
    private const string DetailsPath = "/api/v1/files/usb/CABLE~1.GCO";
    private const string DownloadPath = "/usb/CABLE~1.GCO";
    private static readonly byte[] Content = Encoding.UTF8.GetBytes("G28\nG1 X1\n");
    private static readonly PrinterCredential Credential = PrinterCredential.FromApiKey("secret");

    [Theory]
    [InlineData("/usb/CABLE~1.GCO")]
    [InlineData("http://printer/usb/CABLE~1.GCO")]
    public async Task CopyFileToAsync_UsesConfirmedShortPathAndAuthenticatedTransport(string downloadReference)
    {
        using Handler handler = new(request => request.RequestUri!.AbsolutePath switch
        {
            DetailsPath => Json(Details(downloadReference)),
            DownloadPath => Bytes(Content),
            _ => throw new InvalidOperationException("Unexpected PrusaLink request."),
        });
        using HttpClient http = new(handler);
        await using MemoryStream destination = new();

        PrinterFileCopyBytes result = await CopyClient(http).CopyFileToAsync(
            BaseUrl, "usb", ShortPath, Credential, destination, 1024, CancellationToken.None);

        Assert.Equal(Content, destination.ToArray());
        Assert.Equal(Content.Length, result.SizeBytes);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Content)), result.Sha256);
        Assert.Equal([DetailsPath, DownloadPath], handler.Paths);
        Assert.All(handler.ApiKeys, apiKey => Assert.Equal("secret", apiKey));
    }

    [Theory]
    [InlineData("http://attacker.invalid/usb/CABLE~1.GCO")]
    [InlineData("/usb/other.gcode")]
    public async Task CopyFileToAsync_RejectsUntrustedDownloadReferenceBeforeTransfer(string reference)
    {
        using Handler handler = new(request => request.RequestUri!.AbsolutePath == DetailsPath
            ? Json(Details(reference))
            : throw new InvalidOperationException("The file request must not be sent."));
        using HttpClient http = new(handler);
        await using MemoryStream destination = new();

        PrinterFileCopyException error = await Assert.ThrowsAsync<PrinterFileCopyException>(() =>
            CopyClient(http).CopyFileToAsync(
                BaseUrl, "usb", ShortPath, Credential, destination, 1024, CancellationToken.None));

        Assert.Equal("unsupported_download_reference", error.Reason);
        Assert.Equal([DetailsPath], handler.Paths);
        Assert.Equal(0, destination.Length);
    }

    [Fact]
    public async Task CopyFileToAsync_UsesDigestAuthenticationForDetailsAndFile()
    {
        using Handler handler = new(request =>
        {
            if (request.Headers.Authorization?.Scheme != "Digest")
            {
                HttpResponseMessage challenge = new(HttpStatusCode.Unauthorized);
                challenge.Headers.WwwAuthenticate.ParseAdd(
                    """Digest realm="printer", nonce="test-nonce", qop="auth" """);
                return challenge;
            }

            return request.RequestUri!.AbsolutePath switch
            {
                DetailsPath => Json(Details("/usb/CABLE~1.GCO")),
                DownloadPath => Bytes(Content),
                _ => throw new InvalidOperationException("Unexpected PrusaLink request."),
            };
        });
        using HttpClient http = new(handler);
        await using MemoryStream destination = new();

        await CopyClient(http).CopyFileToAsync(
            BaseUrl, "usb", ShortPath, Credential, destination, 1024, CancellationToken.None);

        Assert.Equal(2, handler.DigestAuthenticatedRequests);
        Assert.Equal([DetailsPath, DetailsPath, DownloadPath], handler.Paths);
    }

    [Fact]
    public async Task CopyFileToAsync_RejectsDetailsForDifferentShortName()
    {
        using Handler handler = new(request => Json(Details("/usb/CABLE~1.GCO", name: "Long display name.gcode")));
        using HttpClient http = new(handler);
        await using MemoryStream destination = new();

        PrinterFileCopyException error = await Assert.ThrowsAsync<PrinterFileCopyException>(() =>
            CopyClient(http).CopyFileToAsync(
                BaseUrl, "usb", ShortPath, Credential, destination, 1024, CancellationToken.None));

        Assert.Equal("source_changed", error.Reason);
        Assert.Equal([DetailsPath], handler.Paths);
    }

    [Fact]
    public async Task CopyFileToAsync_RefusesRedirectBeforeFollowingTarget()
    {
        using Handler handler = new(request =>
        {
            if (request.RequestUri!.AbsolutePath == DetailsPath)
            {
                return Json(Details("/usb/CABLE~1.GCO"));
            }

            HttpResponseMessage response = new(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri("http://attacker.invalid/file.gcode");
            return response;
        });
        using HttpClient http = new(handler);
        await using MemoryStream destination = new();

        PrinterFileCopyException error = await Assert.ThrowsAsync<PrinterFileCopyException>(() =>
            CopyClient(http).CopyFileToAsync(
                BaseUrl, "usb", ShortPath, Credential, destination, 1024, CancellationToken.None));

        Assert.Equal("redirect_refused", error.Reason);
        Assert.Equal([DetailsPath, DownloadPath], handler.Paths);
    }

    [Fact]
    public async Task CopyFileToAsync_EnforcesActualByteLimit()
    {
        using Handler handler = new(request => request.RequestUri!.AbsolutePath == DetailsPath
            ? Json(Details("/usb/CABLE~1.GCO"))
            : Bytes(Content));
        using HttpClient http = new(handler);
        await using MemoryStream destination = new();

        PrinterFileCopyException error = await Assert.ThrowsAsync<PrinterFileCopyException>(() =>
            CopyClient(http).CopyFileToAsync(
                BaseUrl, "usb", ShortPath, Credential, destination, 4, CancellationToken.None));

        Assert.Equal("size_limit", error.Reason);
        Assert.True(destination.Length <= 4);
    }

    private static ISupportsPrinterFileCopy CopyClient(HttpClient http) =>
        Assert.IsAssignableFrom<ISupportsPrinterFileCopy>(new PrusaLinkClient(http));

    private static string Details(string reference, string name = ShortPath) =>
        JsonSerializer.Serialize(new
        {
            type = "PRINT_FILE",
            name,
            display_name = "Cable holder, final long filename.gcode",
            refs = new { download = reference },
        });

    private static HttpResponseMessage Json(string content) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(content, Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage Bytes(byte[] content) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(content),
    };

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];
        public List<string?> ApiKeys { get; } = [];
        public int DigestAuthenticatedRequests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Paths.Add(request.RequestUri!.AbsolutePath);
            if (request.Headers.Authorization?.Scheme == "Digest")
            {
                DigestAuthenticatedRequests++;
            }

            ApiKeys.Add(request.Headers.TryGetValues("X-Api-Key", out IEnumerable<string>? values)
                ? Assert.Single(values)
                : null);
            return Task.FromResult(send(request));
        }
    }
}
