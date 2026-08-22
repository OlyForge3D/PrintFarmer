using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.PrinterCalibration;
using Farm.Slicer.Module.Services.Configuration;
using Farm.Web.Api.Services.Calibration;
using Farm.Web.Api.Services.Calibration.Generation;
using Farm.Web.Api.Tests.TestHelpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Farm.Web.Api.Tests.Calibration;

/// <summary>
/// Exercises <see cref="SlicerHostCapabilityClient"/>'s authenticated hop to the peer slicer host
/// (issue #1848). The client must never throw — every failure degrades to
/// <see cref="WorkerCompatibilitySnapshotDto.Empty"/>, which is a valid answer from the probe's
/// perspective.
/// </summary>
public sealed class SlicerHostCapabilityClientTests
{
    private static readonly Uri BaseUrl = new("http://slicer-host:5246/");

    [Fact]
    public async Task GetWorkerCompatibilityAsync_SuccessResponse_ReturnsDeserializedSnapshot()
    {
        WorkerCompatibilitySnapshotDto expected = new(
            new WorkerCompatibilityPinnedIdentityDto(
                "2.4.2",
                "upstream",
                "sha256:container",
                "sha256:binary",
                Guid.NewGuid()),
            ["2.4.2"],
            true);
        HttpRequestMessage? capturedRequest = null;
        FakeHttpMessageHandler handler = new(request =>
        {
            capturedRequest = request;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    System.Text.Json.JsonSerializer.Serialize(
                        expected,
                        WorkerCompatibilityContract.SerializerOptions),
                    System.Text.Encoding.UTF8,
                    "application/json"),
            };
        });

        ISlicerHostCapabilityClient client = CreateClient(handler, sharedKey: "test-shared-key");

        WorkerCompatibilitySnapshotDto snapshot =
            await client.GetWorkerCompatibilityAsync("2.4.2", CancellationToken.None);

        _ = snapshot.Should().BeEquivalentTo(expected);
        _ = capturedRequest.Should().NotBeNull();
        _ = capturedRequest!.Headers.Contains(WorkerCompatibilityContract.ApiKeyHeaderName).Should().BeTrue();
        _ = capturedRequest.RequestUri!.PathAndQuery.Should()
            .Contain(WorkerCompatibilityContract.WorkerCompatibilityRelativeRoute)
            .And.Contain("requiredSlicerVersion=2.4.2");
    }

    [Fact]
    public async Task GetWorkerCompatibilityAsync_MissingSharedKey_ReturnsEmptyWithoutRequest()
    {
        bool requestSent = false;
        FakeHttpMessageHandler handler = new(_ =>
        {
            requestSent = true;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        ISlicerHostCapabilityClient client = CreateClient(handler, sharedKey: null);

        WorkerCompatibilitySnapshotDto snapshot =
            await client.GetWorkerCompatibilityAsync(null, CancellationToken.None);

        _ = snapshot.Should().Be(WorkerCompatibilitySnapshotDto.Empty);
        _ = requestSent.Should().BeFalse();
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task GetWorkerCompatibilityAsync_NonSuccessStatus_ReturnsEmpty(HttpStatusCode statusCode)
    {
        FakeHttpMessageHandler handler = new(_ => new HttpResponseMessage(statusCode));
        ISlicerHostCapabilityClient client = CreateClient(handler, sharedKey: "test-shared-key");

        WorkerCompatibilitySnapshotDto snapshot =
            await client.GetWorkerCompatibilityAsync(null, CancellationToken.None);

        _ = snapshot.Should().Be(WorkerCompatibilitySnapshotDto.Empty);
    }

    [Fact]
    public async Task GetWorkerCompatibilityAsync_UnexpectedMediaType_ReturnsEmpty()
    {
        FakeHttpMessageHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("not json", System.Text.Encoding.UTF8, "text/plain"),
        });
        ISlicerHostCapabilityClient client = CreateClient(handler, sharedKey: "test-shared-key");

        WorkerCompatibilitySnapshotDto snapshot =
            await client.GetWorkerCompatibilityAsync(null, CancellationToken.None);

        _ = snapshot.Should().Be(WorkerCompatibilitySnapshotDto.Empty);
    }

    [Fact]
    public async Task GetWorkerCompatibilityAsync_MalformedJson_ReturnsEmpty()
    {
        FakeHttpMessageHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{not-valid-json", System.Text.Encoding.UTF8, "application/json"),
        });
        ISlicerHostCapabilityClient client = CreateClient(handler, sharedKey: "test-shared-key");

        WorkerCompatibilitySnapshotDto snapshot =
            await client.GetWorkerCompatibilityAsync(null, CancellationToken.None);

        _ = snapshot.Should().Be(WorkerCompatibilitySnapshotDto.Empty);
    }

    [Fact]
    public async Task GetWorkerCompatibilityAsync_TransportFailure_ReturnsEmpty()
    {
        FakeHttpMessageHandler handler = new(_ => throw new HttpRequestException("connection refused"));
        ISlicerHostCapabilityClient client = CreateClient(handler, sharedKey: "test-shared-key");

        WorkerCompatibilitySnapshotDto snapshot =
            await client.GetWorkerCompatibilityAsync(null, CancellationToken.None);

        _ = snapshot.Should().Be(WorkerCompatibilitySnapshotDto.Empty);
    }

    [Fact]
    public async Task GetWorkerCompatibilityAsync_NoRequiredVersion_OmitsQueryParam()
    {
        HttpRequestMessage? capturedRequest = null;
        FakeHttpMessageHandler handler = new(request =>
        {
            capturedRequest = request;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    System.Text.Json.JsonSerializer.Serialize(
                        WorkerCompatibilitySnapshotDto.Empty,
                        WorkerCompatibilityContract.SerializerOptions),
                    System.Text.Encoding.UTF8,
                    "application/json"),
            };
        });
        ISlicerHostCapabilityClient client = CreateClient(handler, sharedKey: "test-shared-key");

        _ = await client.GetWorkerCompatibilityAsync(null, CancellationToken.None);

        _ = capturedRequest!.RequestUri!.Query.Should().BeEmpty();
    }

    private static ISlicerHostCapabilityClient CreateClient(HttpMessageHandler handler, string? sharedKey)
    {
        HttpClient httpClient = new(handler) { BaseAddress = BaseUrl };
        System.Collections.Generic.Dictionary<string, string?> settings = new();
        if (sharedKey is not null)
        {
            settings[WorkerAuthConfiguration.SharedKeyPath] = sharedKey;
        }

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();
        SlicerHostCalibrationResolverOptions options = new() { BaseUrl = BaseUrl };

        return new SlicerHostCapabilityClient(
            httpClient,
            configuration,
            options,
            NullLogger<SlicerHostCapabilityClient>.Instance);
    }
}
