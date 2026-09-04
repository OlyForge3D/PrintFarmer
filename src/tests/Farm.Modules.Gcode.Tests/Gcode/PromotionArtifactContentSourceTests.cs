using System.Net;
using System.Text;
using Farm.Infrastructure;
using Farm.Slicer.Module.Api.Controllers;
using Farm.Slicer.Module.Api.Services;
using Farm.Slicer.Module.Data.Repositories;
using Farm.Slicer.Module.Domain;
using Farm.Slicer.Module.Services;
using Farm.Web.Api.Services.SlicerHost;
using Farm.Web.Api.Startup;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;

namespace Farm.Modules.Gcode.Tests.Gcode;

/// <summary>Tests local and split-host promotion content boundaries.</summary>
public sealed class PromotionArtifactContentSourceTests
{
    private const string SharedKey = "promotion-test-key";

    [Fact]
    public async Task LocalSource_OpenReadAsync_ReturnsExactContentAndDisposesLease()
    {
        Guid artifactId = Guid.NewGuid();
        byte[] bytes = [1, 2, 3, 4];
        var stream = new TrackingMemoryStream(bytes);
        var artifacts = new Mock<IArtifactsService>();
        artifacts
            .Setup(service => service.OpenReadStreamAsync(
                artifactId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(ArtifactContentStream.Open(
                new Artifact { Id = artifactId },
                () => stream));
        var source = new LocalPromotionArtifactContentSource(artifacts.Object);

        await using (PromotionArtifactContent? content = await source.OpenReadAsync(
            artifactId,
            "operation-key",
            bytes.LongLength,
            CancellationToken.None))
        {
            content.Should().NotBeNull();
            using var destination = new MemoryStream();
            await content!.Content.CopyToAsync(destination);
            destination.ToArray().Should().Equal(bytes);
        }

        stream.WasDisposed.Should().BeTrue();
    }

    [Fact]
    public async Task LocalSource_WhenStorageOpenThrowsIOException_ThrowsRetryableTransportError()
    {
        var artifacts = new Mock<IArtifactsService>();
        artifacts
            .Setup(service => service.OpenReadStreamAsync(
                It.IsAny<Guid>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("storage unavailable"));
        var source = new LocalPromotionArtifactContentSource(artifacts.Object);

        Func<Task> action = async () => _ = await source.OpenReadAsync(
            Guid.NewGuid(),
            "operation-key",
            4,
            CancellationToken.None);

        await action.Should().ThrowAsync<PromotionSourceTransportException>();
    }

    [Fact]
    public async Task HttpSource_OpenReadAsync_SendsDedicatedAuthenticationAndPinHeaders()
    {
        Guid artifactId = Guid.NewGuid();
        byte[] bytes = [5, 6, 7];
        var inner = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes),
        });
        using HttpClient client = BuildAuthenticatedClient(inner);
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "must-not-forward");
        var source = new SlicerHostPromotionArtifactContentSource(
            client,
            new SlicerHostPromotionOptions(client.BaseAddress!, TimeSpan.FromSeconds(10)));

        await using PromotionArtifactContent? content = await source.OpenReadAsync(
            artifactId,
            "scoped-operation-key",
            bytes.LongLength,
            CancellationToken.None);
        using var destination = new MemoryStream();
        await content!.Content.CopyToAsync(destination);

        destination.ToArray().Should().Equal(bytes);
        inner.Request.Should().NotBeNull();
        inner.Request!.RequestUri!.PathAndQuery.Should().Be(
            $"/{SlicerPromotionContract.ArtifactContentPath(artifactId)}");
        inner.Request.Headers.GetValues(SlicerPromotionContract.ApiKeyHeaderName)
            .Should().ContainSingle().Which.Should().Be(SharedKey);
        inner.Request.Headers.GetValues(SlicerPromotionContract.OperationKeyHeaderName)
            .Should().ContainSingle().Which.Should().Be("scoped-operation-key");
        inner.Request.Headers.Authorization.Should().BeNull();
    }

    [Fact]
    public async Task HttpSource_OpenReadAsync_TransfersResponseLifetimeToReturnedContent()
    {
        byte[] bytes = [5, 6, 7];
        var responseStream = new TrackingMemoryStream(bytes);
        var inner = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(responseStream),
        });
        using HttpClient client = BuildAuthenticatedClient(inner);
        var source = new SlicerHostPromotionArtifactContentSource(
            client,
            new SlicerHostPromotionOptions(client.BaseAddress!, TimeSpan.FromSeconds(10)));

        PromotionArtifactContent content = (await source.OpenReadAsync(
            Guid.NewGuid(),
            "scoped-operation-key",
            bytes.LongLength,
            CancellationToken.None))!;

        responseStream.WasDisposed.Should().BeFalse();
        await content.DisposeAsync();
        responseStream.WasDisposed.Should().BeTrue();
    }

    [Fact]
    public async Task HttpSource_WhenBodyIsTruncated_ThrowsRetryableTransportError()
    {
        var inner = new RecordingHandler(_ =>
        {
            var content = new StreamContent(new MemoryStream([1, 2], writable: false));
            content.Headers.ContentLength = 3;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        using HttpClient client = BuildAuthenticatedClient(inner);
        var source = new SlicerHostPromotionArtifactContentSource(
            client,
            new SlicerHostPromotionOptions(client.BaseAddress!, TimeSpan.FromSeconds(10)));
        await using PromotionArtifactContent? content = await source.OpenReadAsync(
            Guid.NewGuid(),
            "scoped-operation-key",
            3,
            CancellationToken.None);

        Func<Task> action = async () =>
        {
            using var destination = new MemoryStream();
            await content!.Content.CopyToAsync(destination);
        };

        await action.Should().ThrowAsync<PromotionSourceTransportException>();
    }

    [Fact]
    public async Task HttpSource_WhenArtifactIsMissing_ReturnsMissingContent()
    {
        var responseStream = new TrackingMemoryStream([]);
        var inner = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StreamContent(responseStream),
        });
        using HttpClient client = BuildAuthenticatedClient(inner);
        var source = new SlicerHostPromotionArtifactContentSource(
            client,
            new SlicerHostPromotionOptions(client.BaseAddress!, TimeSpan.FromSeconds(10)));

        PromotionArtifactContent? content = await source.OpenReadAsync(
            Guid.NewGuid(),
            "scoped-operation-key",
            3,
            CancellationToken.None);

        content.Should().BeNull();
        responseStream.WasDisposed.Should().BeTrue();
    }

    [Fact]
    public async Task HttpSource_WhenPromotionPinMismatches_ThrowsRaceSignal()
    {
        var inner = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent(
                "{\"code\":\"promotion_pin_mismatch\"}",
                Encoding.UTF8,
                "application/problem+json"),
        });
        using HttpClient client = BuildAuthenticatedClient(inner);
        var source = new SlicerHostPromotionArtifactContentSource(
            client,
            new SlicerHostPromotionOptions(client.BaseAddress!, TimeSpan.FromSeconds(10)));

        Func<Task> action = async () => _ = await source.OpenReadAsync(
            Guid.NewGuid(),
            "scoped-operation-key",
            3,
            CancellationToken.None);

        await action.Should().ThrowAsync<PromotionSourcePinMismatchException>();
    }

    [Fact]
    public async Task InternalEndpoint_RequiresSharedKeyAndMatchingActivePin()
    {
        Guid artifactId = Guid.NewGuid();
        var repository = new Mock<IArtifactsRepository>();
        var artifacts = new Mock<IArtifactsService>();
        Artifact artifact = PinnedArtifact(artifactId, "active-operation");
        repository
            .Setup(value => value.GetByIdAsync(artifactId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(artifact);
        artifacts
            .Setup(value => value.OpenReadStreamAsync(artifactId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ArtifactContentStream.Open(
                artifact,
                () => new MemoryStream([1, 2, 3], writable: false)));

        InternalPromotionArtifactController wrongPin = BuildInternalController(
            repository.Object,
            artifacts.Object,
            apiKey: SharedKey);
        IActionResult denied = await wrongPin.GetContentAsync(
            artifactId,
            "wrong-operation",
            CancellationToken.None);

        denied.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);

        InternalPromotionArtifactController authorized = BuildInternalController(
            repository.Object,
            artifacts.Object,
            apiKey: SharedKey);
        IActionResult allowed = await authorized.GetContentAsync(
            artifactId,
            "active-operation",
            CancellationToken.None);

        allowed.Should().BeOfType<FileStreamResult>();
        authorized.Response.ContentLength.Should().Be(artifact.SizeBytes);
    }

    [Fact]
    public async Task InternalEndpoint_WhenSharedKeyIsMissing_DoesNotResolveArtifact()
    {
        var repository = new Mock<IArtifactsRepository>();
        var artifacts = new Mock<IArtifactsService>();
        InternalPromotionArtifactController controller = BuildInternalController(
            repository.Object,
            artifacts.Object,
            apiKey: null);

        IActionResult result = await controller.GetContentAsync(
            Guid.NewGuid(),
            "active-operation",
            CancellationToken.None);

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        repository.Verify(value => value.GetByIdAsync(
            It.IsAny<Guid>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void SplitRegistration_AddsHttpSourceWithoutHostedServiceOrApplicationPart()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DEPLOYMENT_MODE"] = "microservices",
                ["SlicerHost:BaseUrl"] = "http://slicer-host:5246",
                [SlicerPromotionContract.SharedKeyPath] = SharedKey,
                [$"{SlicerPromotionContract.SectionName}:StreamTimeoutSeconds"] = "240",
            })
            .Build();
        var services = new ServiceCollection();

        _ = services.AddSlicerPromotionDependencies(configuration);

        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IPromotionArtifactContentSource));
        services.Should().NotContain(descriptor =>
            descriptor.ServiceType == typeof(IHostedService));
        services.Should().NotContain(descriptor =>
            descriptor.ServiceType.FullName == "Microsoft.AspNetCore.Mvc.ApplicationParts.ApplicationPartManager");
    }

    private static HttpClient BuildAuthenticatedClient(HttpMessageHandler inner)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [SlicerPromotionContract.SharedKeyPath] = SharedKey,
            })
            .Build();
        var authentication = new SlicerPromotionAuthenticationHandler(configuration)
        {
            InnerHandler = inner,
        };
        return new HttpClient(authentication)
        {
            BaseAddress = new Uri("http://slicer-host/"),
        };
    }

    private static InternalPromotionArtifactController BuildInternalController(
        IArtifactsRepository repository,
        IArtifactsService artifacts,
        string? apiKey)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [SlicerPromotionContract.SharedKeyPath] = SharedKey,
            })
            .Build();
        var controller = new InternalPromotionArtifactController(
            new SlicerPromotionServiceAuthenticator(configuration),
            repository,
            artifacts)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext(),
            },
        };
        if (apiKey is not null)
        {
            controller.Request.Headers[SlicerPromotionContract.ApiKeyHeaderName] = apiKey;
        }

        return controller;
    }

    private static Artifact PinnedArtifact(Guid id, string operationKey) => new()
    {
        Id = id,
        JobId = Guid.NewGuid(),
        Kind = SlicerArtifactKinds.Gcode,
        FileName = "output.gcode",
        RelativePath = "internal/output.gcode",
        ContentType = "application/octet-stream",
        SizeBytes = 3,
        Sha256 = new string('A', 64),
        CreatedAt = DateTime.UtcNow,
        PromotionCheckpointId = Guid.NewGuid(),
        PromotionOperationKey = operationKey,
        PromotionStartedAtUtc = DateTime.UtcNow,
    };

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Request = request;
            return Task.FromResult(responseFactory(request));
        }
    }

    private sealed class TrackingMemoryStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        public bool WasDisposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            base.Dispose(disposing);
        }
    }
}
