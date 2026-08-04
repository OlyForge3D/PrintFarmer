using System.Net;
using System.Text.Json;
using Farm.OrcaSlicer.Worker.Services;
using Farm.Slicer.Worker.Core;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Farm.OrcaSlicer.Worker.Tests;

public sealed class SlicerRegistrationClientTests
{
    [Fact]
    public void ResolveRegistrationApiKey_CanonicalKeyConfigured_ReturnsKey()
    {
        IConfiguration configuration = CreateConfiguration(
            new KeyValuePair<string, string?>("WorkerAuth:SharedKey", "the-key"));

        string? apiKey = SlicerRegistrationClient.ResolveRegistrationApiKey(configuration);

        _ = apiKey.Should().Be("the-key");
    }

    [Theory]
    [InlineData("WorkerAuth:SharedApiKey")]
    [InlineData("Worker:SharedKey")]
    [InlineData("SlicerRegistry:ApiKey")]
    [InlineData("WORKER_SHARED_API_KEY")]
    [InlineData("SLICER_REGISTRATION_KEY")]
    public void ResolveRegistrationApiKey_LegacyAliasOnly_ReturnsNull(string legacyPath)
    {
        IConfiguration configuration = CreateConfiguration(
            new KeyValuePair<string, string?>(legacyPath, "legacy-key"));

        string? apiKey = SlicerRegistrationClient.ResolveRegistrationApiKey(configuration);

        _ = apiKey.Should().BeNull();
    }

    [Fact]
    public async Task RegisterAsync_CanonicalKeyConfigured_SendsRegistrationKeyHeader()
    {
        string? sentApiKey = null;
        CapturingHandler handler = new CapturingHandler(request =>
        {
            sentApiKey = request.Headers.TryGetValues("X-Slicer-Api-Key", out IEnumerable<string>? values)
                ? values.SingleOrDefault()
                : null;

            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent("""{"id":"11111111-1111-1111-1111-111111111111","apiKey":"registered-key"}""")
            };
        });
        using HttpClient httpClient = new HttpClient(handler);
        IConfiguration configuration = CreateConfiguration(
            new KeyValuePair<string, string?>("SlicerApi:BaseUrl", "http://api:5245"),
            new KeyValuePair<string, string?>("WorkerAuth:SharedKey", "the-key"));
        SlicerRegistrationClient client = new SlicerRegistrationClient(
            httpClient,
            configuration,
            new StubBinaryDetector(),
            NullLogger<SlicerRegistrationClient>.Instance,
            new WorkerCapabilityProvider(configuration));

        (Guid serviceId, string apiKey) = await client.RegisterAsync();

        _ = serviceId.Should().Be(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        _ = apiKey.Should().Be("registered-key");
        _ = sentApiKey.Should().Be("the-key");
    }

    [Fact]
    public async Task RegisterAsync_AdvertisesUpstreamDistributionCapability()
    {
        string? requestBody = null;
        CapturingHandler handler = new CapturingHandler(request =>
        {
            requestBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent("""{"id":"11111111-1111-1111-1111-111111111111","apiKey":"registered-key"}""")
            };
        });
        using HttpClient httpClient = new HttpClient(handler);
        IConfiguration configuration = CreateConfiguration(
            new KeyValuePair<string, string?>("SlicerApi:BaseUrl", "http://api:5245"),
            new KeyValuePair<string, string?>("WorkerAuth:SharedKey", "test-registration-key"),
            new KeyValuePair<string, string?>("Worker:EngineVersion", "2.4.0"));
        SlicerRegistrationClient client = new SlicerRegistrationClient(
            httpClient,
            configuration,
            new StubBinaryDetector(),
            NullLogger<SlicerRegistrationClient>.Instance,
            new WorkerCapabilityProvider(configuration));

        _ = await client.RegisterAsync();

        using JsonDocument registration = JsonDocument.Parse(requestBody!);
        string capabilitiesJson = registration.RootElement.GetProperty("CapabilitiesJson").GetString()!;
        using JsonDocument capabilities = JsonDocument.Parse(capabilitiesJson);
        string[] advertisedCapabilities = capabilities.RootElement
            .GetProperty("capabilities")
            .EnumerateArray()
            .Select(value => value.GetString()!)
            .ToArray();
        _ = advertisedCapabilities.Should().Contain(WorkerConstants.UpstreamDistributionCapability);
        _ = advertisedCapabilities.Should().Contain("orcaslicer:2.4.0");
    }

    [Theory]
    [InlineData("worker-a", "worker-a")]
    [InlineData("  worker-b  ", "worker-b")]
    [InlineData(null, null)]
    [InlineData("", null)]
    public async Task RegisterAsync_UsesConfiguredOrProcessWorkerIdentity(
        string? configuredInstanceId,
        string? expectedInstanceId)
    {
        string? requestBody = null;
        CapturingHandler handler = new CapturingHandler(request =>
        {
            requestBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent("""{"id":"11111111-1111-1111-1111-111111111111","apiKey":"registered-key"}""")
            };
        });
        using HttpClient httpClient = new HttpClient(handler);
        IConfiguration configuration = CreateConfiguration(
            new KeyValuePair<string, string?>("SlicerApi:BaseUrl", "http://api:5245"),
            new KeyValuePair<string, string?>("WorkerAuth:SharedKey", "test-registration-key"),
            new KeyValuePair<string, string?>("Worker:InstanceId", configuredInstanceId));
        SlicerRegistrationClient client = new SlicerRegistrationClient(
            httpClient,
            configuration,
            new StubBinaryDetector(),
            NullLogger<SlicerRegistrationClient>.Instance,
            new WorkerCapabilityProvider(configuration));

        _ = await client.RegisterAsync();

        using JsonDocument registration = JsonDocument.Parse(requestBody!);
        string instanceId = registration.RootElement.GetProperty("InstanceId").GetString()!;
        if (expectedInstanceId is not null)
        {
            _ = instanceId.Should().Be(expectedInstanceId);
        }
        else
        {
            _ = Guid.TryParseExact(instanceId, "N", out _).Should().BeTrue();
        }
    }

    private static IConfiguration CreateConfiguration(params KeyValuePair<string, string?>[] values)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }

    private sealed class CapturingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(responder(request));
        }
    }

    /// <summary>
    /// Reports no installed binary, so registration resolves an unverified identity and these
    /// tests stay focused on API-key fallback rather than binary attestation.
    /// </summary>
    private sealed class StubBinaryDetector : IOrcaBinaryDetector
    {
        public bool IsRealBinaryPresent() => false;

        public Task<string?> GetVersionAsync() => Task.FromResult<string?>(null);
    }
}
