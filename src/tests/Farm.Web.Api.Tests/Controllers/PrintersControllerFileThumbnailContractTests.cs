// <copyright file="PrintersControllerFileThumbnailContractTests.cs" company="OlyForge3D">
// Copyright (c) OlyForge3D. All rights reserved.
// </copyright>

using System.Net;
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
public sealed class PrintersControllerFileThumbnailContractTests : IAsyncLifetime
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
    [InlineData("../../etc/passwd")]
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
