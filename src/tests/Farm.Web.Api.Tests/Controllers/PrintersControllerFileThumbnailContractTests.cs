// <copyright file="PrintersControllerFileThumbnailContractTests.cs" company="OlyForge3D">
// Copyright (c) OlyForge3D. All rights reserved.
// </copyright>

using System.Net;
using System.Text.Json;
using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace Farm.Web.Api.Tests.Controllers;

/// <summary>
/// Contract tests for the same-origin, authenticated printer file thumbnail proxy endpoint
/// added for issue #1650 (Moonraker file thumbnails previously leaked the backend's internal
/// base URL directly to the browser).
/// </summary>
public sealed class PrintersControllerFileThumbnailContractTests : IAsyncLifetime, IDisposable
{
    private readonly Mock<IPrintersService> _printers = new(MockBehavior.Strict);
    private readonly FileThumbnailContractFactory _factory;
    private readonly HttpClient _client;

    public PrintersControllerFileThumbnailContractTests()
    {
        _factory = new FileThumbnailContractFactory(_printers.Object);
        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.Add("X-Test-User-Id", Guid.NewGuid().ToString());
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    public void Dispose()
    {
        _client?.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task FileThumbnail_AnonymousRequest_IsUnauthorized()
    {
        await using var productionAuthFactory =
            new CustomWebApplicationFactory(
                new Dictionary<string, string?>
                {
                    ["Security:DevModeBypassAuth"] = "false",
                });
        using HttpClient anonymousClient = productionAuthFactory.CreateClient();

        HttpResponseMessage response = await anonymousClient.GetAsync(
            $"/api/printers/{Guid.NewGuid()}/files/thumbnail?filename=thumbs%2Fbenchy-300x300.png");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task CurrentJobThumbnail_AnonymousRequest_IsUnauthorizedAsync()
    {
        await using var productionAuthFactory =
            new CustomWebApplicationFactory(
                new Dictionary<string, string?>
                {
                    ["Security:DevModeBypassAuth"] = "false",
                });
        using HttpClient anonymousClient = productionAuthFactory.CreateClient();

        HttpResponseMessage response = await anonymousClient.GetAsync(
            $"/api/printers/{Guid.NewGuid()}/current-job/thumbnail");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task CurrentJobThumbnail_IdlePrinter_ReturnsNotFoundAsync()
    {
        Guid printerId = Guid.NewGuid();
        _printers.Setup(service => service.GetCurrentJobThumbnailAsync(
                printerId,
                null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((HistoryThumbnailContent?)null);

        HttpResponseMessage response = await _client.GetAsync(
            $"/api/printers/{printerId}/current-job/thumbnail");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task CurrentJobThumbnail_ListUrlRoundTrip_RotatesAndClearsAsync()
    {
        Guid printerId = Guid.NewGuid();
        const string privateTarget = "http://printer.internal/thumb.png";
        byte[] image = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aGZkAAAAASUVORK5CYII=");
        var printer = new CompletePrinterDto(
            Id: printerId, Name: "Card fixture", Notes: null,
            ManufacturerId: null, ManufacturerName: null, ModelId: null, ModelName: null,
            MotionType: null, Backend: PrinterBackend.Moonraker, ApiKey: null,
            OriginalServerUrl: null, BackendPort: 7125, FrontendPort: null,
            InMaintenance: false, IsEnabled: true, IsOnline: true, State: "printing",
            Progress: 50, JobName: "card.gcode", FileName: "card.gcode",
            ThumbnailUrl: privateTarget, CameraStreamUrl: null,
            X: null, Y: null, Z: null, HotendTemp: 210, BedTemp: 60,
            HotendTarget: 210, BedTarget: 60, HomedAxes: null, SpoolInfo: null);
        _printers.Setup(service => service.GetAllCompleteDtosAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => [printer]);

        string? previousPath = null;
        foreach (string identity in new[] { "file-revision-1", "file-revision-2" })
        {
            string cacheToken = PrinterThumbnailUrl.GetCacheToken(
                printer.JobName!, privateTarget, identity);
            printer = printer with
            {
                CurrentJobThumbnailUrl = PrinterThumbnailUrl.Create(
                    printerId, printer.State, printer.JobName, privateTarget, identity),
            };
            _printers.Setup(service => service.GetCurrentJobThumbnailAsync(
                    printerId, cacheToken, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new HistoryThumbnailContent(image, "image/png"));

            using HttpResponseMessage list = await _client.GetAsync("/api/printers");
            list.StatusCode.Should().Be(HttpStatusCode.OK);
            string json = await list.Content.ReadAsStringAsync();
            json.Should().NotContain(privateTarget);
            using JsonDocument document = JsonDocument.Parse(json);
            string path = document.RootElement[0].GetProperty("currentJobThumbnailUrl").GetString()!;
            path.Should().Be(printer.CurrentJobThumbnailUrl);
            path.Should().NotBe(previousPath);
            previousPath = path;

            using HttpResponseMessage thumbnail = await _client.GetAsync(path);
            thumbnail.StatusCode.Should().Be(HttpStatusCode.OK);
            thumbnail.Content.Headers.ContentType!.MediaType.Should().Be("image/png");
            thumbnail.Headers.ETag!.Tag.Should().Be($"\"{cacheToken}\"");
            thumbnail.Headers.CacheControl!.Private.Should().BeTrue();
            thumbnail.Headers.CacheControl.MaxAge.Should().Be(TimeSpan.FromSeconds(300));
            thumbnail.Headers.GetValues("X-Content-Type-Options").Should().ContainSingle("nosniff");
            (await thumbnail.Content.ReadAsByteArrayAsync()).Should().Equal(image);
        }

        printer = printer with { State = "idle", JobName = null, CurrentJobThumbnailUrl = null };
        using HttpResponseMessage idleList = await _client.GetAsync("/api/printers");
        idleList.StatusCode.Should().Be(HttpStatusCode.OK);
        using JsonDocument idleDocument = JsonDocument.Parse(await idleList.Content.ReadAsStringAsync());
        bool present = idleDocument.RootElement[0].TryGetProperty("currentJobThumbnailUrl", out JsonElement value);
        (present && value.ValueKind != JsonValueKind.Null).Should().BeFalse();
    }

    [Fact]
    public async Task CurrentJobThumbnail_VersionedImage_ReturnsPrivateCachedContentAsync()
    {
        Guid printerId = Guid.NewGuid();
        const string cacheToken = "0123456789abcdef";
        _printers.Setup(service => service.GetCurrentJobThumbnailAsync(
                printerId,
                cacheToken,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HistoryThumbnailContent([1, 2, 3], "image/png"));

        HttpResponseMessage response = await _client.GetAsync(
            $"/api/printers/{printerId}/current-job/thumbnail?v={cacheToken}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("image/png");
        response.Headers.CacheControl!.Private.Should().BeTrue();
        response.Headers.CacheControl.MaxAge.Should().Be(TimeSpan.FromSeconds(300));
        response.Headers.ETag!.Tag.Should().Be($"\"{cacheToken}\"");
        response.Headers.GetValues("X-Content-Type-Options")
            .Should().ContainSingle("nosniff");
        (await response.Content.ReadAsByteArrayAsync()).Should().Equal(1, 2, 3);
    }

    [Fact]
    public async Task FileThumbnail_ValidImage_ReturnsSameOriginContent()
    {
        Guid printerId = Guid.NewGuid();
        _printers.Setup(service => service.DownloadPrinterFileAsync(
                printerId,
                "thumbs/benchy-300x300.png",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([1, 2, 3]);

        HttpResponseMessage response = await _client.GetAsync(
            $"/api/printers/{printerId}/files/thumbnail?filename={Uri.EscapeDataString("thumbs/benchy-300x300.png")}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("image/png");
        response.Headers.GetValues("X-Content-Type-Options")
            .Should().ContainSingle("nosniff");
        (await response.Content.ReadAsByteArrayAsync()).Should().Equal(1, 2, 3);
    }

    [Theory]
    [InlineData("thumbs/a.jpg", "image/jpeg")]
    [InlineData("thumbs/a.jpeg", "image/jpeg")]
    [InlineData("thumbs/a.gif", "image/gif")]
    [InlineData("thumbs/a.webp", "image/webp")]
    public async Task FileThumbnail_SupportedImageExtensions_ReturnExpectedContentType(
        string filename,
        string expectedContentType)
    {
        Guid printerId = Guid.NewGuid();
        _printers.Setup(service => service.DownloadPrinterFileAsync(
                printerId,
                filename,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([9]);

        HttpResponseMessage response = await _client.GetAsync(
            $"/api/printers/{printerId}/files/thumbnail?filename={Uri.EscapeDataString(filename)}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be(expectedContentType);
    }

    [Fact]
    public async Task FileThumbnail_MissingFilename_ReturnsBadRequestWithoutService()
    {
        HttpResponseMessage response = await _client.GetAsync(
            $"/api/printers/{Guid.NewGuid()}/files/thumbnail");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _printers.Verify(
            service => service.DownloadPrinterFileAsync(
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Theory]
    [InlineData("thumbs/benchy.gcode")]
    [InlineData("thumbs/benchy")]
    public async Task FileThumbnail_NonImageExtension_ReturnsBadRequestWithoutService(
        string filename)
    {
        HttpResponseMessage response = await _client.GetAsync(
            $"/api/printers/{Guid.NewGuid()}/files/thumbnail?filename={Uri.EscapeDataString(filename)}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _printers.Verify(
            service => service.DownloadPrinterFileAsync(
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("../../etc/passwd.png")]
    [InlineData("thumbs/../../secret.png")]
    [InlineData("./thumbs/benchy.png")]
    [InlineData("/etc/passwd.png")]
    [InlineData(@"\etc\passwd.png")]
    [InlineData(@"\\server\share\thumb.png")]
    [InlineData(@"C:\thumbs\evil.png")]
    [InlineData("C:/thumbs/evil.png")]
    public async Task FileThumbnail_PathTraversalShapedFilename_ReturnsBadRequestWithoutService(
        string filename)
    {
        // A traversal-shaped filename must be rejected even when it ends in an allowed image
        // extension - the extension allowlist alone does not stop a segment like ".." from
        // escaping the printer backend's intended files subtree (see issue #1654 review).
        // The Windows-rooted/UNC/drive-letter shapes above must be rejected regardless of the
        // host OS running the test (Path.IsPathRooted's notion of "rooted" is OS-dependent and
        // would miss these on Linux, so the check is implemented with explicit string patterns
        // instead - see the Bishop re-review finding on PR #1654).
        HttpResponseMessage response = await _client.GetAsync(
            $"/api/printers/{Guid.NewGuid()}/files/thumbnail?filename={Uri.EscapeDataString(filename)}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _printers.Verify(
            service => service.DownloadPrinterFileAsync(
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task FileThumbnail_BackendReturnsNoContent_ReturnsNotFound()
    {
        Guid printerId = Guid.NewGuid();
        _printers.Setup(service => service.DownloadPrinterFileAsync(
                printerId,
                "thumbs/missing.png",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((byte[]?)null);

        HttpResponseMessage response = await _client.GetAsync(
            $"/api/printers/{printerId}/files/thumbnail?filename={Uri.EscapeDataString("thumbs/missing.png")}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task FileThumbnail_ServiceThrows_ReturnsServiceUnavailable()
    {
        Guid printerId = Guid.NewGuid();
        _printers.Setup(service => service.DownloadPrinterFileAsync(
                printerId,
                "thumbs/benchy-300x300.png",
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("upstream failed"));

        HttpResponseMessage response = await _client.GetAsync(
            $"/api/printers/{printerId}/files/thumbnail?filename={Uri.EscapeDataString("thumbs/benchy-300x300.png")}");

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task FileThumbnail_InaccessiblePrinter_ReturnsNotFoundWithoutService()
    {
        var authorization =
            new Mock<Farm.Infrastructure.Services.Queue.IQueueResourceAuthorizationService>();
        authorization
            .Setup(service => service.CanAccessPrinterAsync(
                It.IsAny<System.Security.Claims.ClaimsPrincipal>(),
                It.IsAny<Guid>(),
                PrinterGroupAccessLevel.View,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        await using var factory =
            new FileThumbnailContractFactory(_printers.Object, authorization.Object);
        using HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(
            "X-Test-User-Id",
            Guid.NewGuid().ToString());

        HttpResponseMessage response = await client.GetAsync(
            $"/api/printers/{Guid.NewGuid()}/files/thumbnail?filename={Uri.EscapeDataString("thumbs/benchy-300x300.png")}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        _printers.Verify(
            service => service.DownloadPrinterFileAsync(
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private sealed class FileThumbnailContractFactory(
        IPrintersService printers,
        Farm.Infrastructure.Services.Queue.IQueueResourceAuthorizationService? authorization = null)
        : CustomWebApplicationFactory(
            new Dictionary<string, string?>
            {
                ["Testing:UseTestAuthentication"] = "true",
                ["Security:DevModeBypassAuth"] = "false",
            })
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IPrintersService>();
                services.AddSingleton(printers);
                if (authorization is not null)
                {
                    services.RemoveAll<Farm.Infrastructure.Services.Queue.IQueueResourceAuthorizationService>();
                    services.AddSingleton(authorization);
                }
            });
        }
    }
}
