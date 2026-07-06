using System.Net;
using Farm.OrcaSlicer.Worker.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Farm.OrcaSlicer.Worker.Tests;

public sealed class SlicerRegistrationClientTests
{
    [Fact]
    public void ResolveRegistrationApiKey_BlankSlicerRegistryFallsThroughToWorkerSharedApiKey_ReturnsSharedApiKey()
    {
        IConfiguration configuration = CreateConfiguration(
            new KeyValuePair<string, string?>("SlicerRegistry:ApiKey", string.Empty),
            new KeyValuePair<string, string?>("WorkerAuth:SharedApiKey", "the-key"));

        string? apiKey = SlicerRegistrationClient.ResolveRegistrationApiKey(configuration);

        _ = apiKey.Should().Be("the-key");
    }

    [Fact]
    public async Task RegisterAsync_BlankSlicerRegistryFallsThroughToWorkerSharedApiKey_SendsSharedApiKeyHeader()
    {
        string? sentApiKey = null;
        CapturingHandler handler = new CapturingHandler(request =>
        {
            sentApiKey = request.Headers.TryGetValues("X-Slicer-ApiKey", out IEnumerable<string>? values)
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
            new KeyValuePair<string, string?>("SlicerRegistry:ApiKey", string.Empty),
            new KeyValuePair<string, string?>("WorkerAuth:SharedApiKey", "the-key"));
        SlicerRegistrationClient client = new SlicerRegistrationClient(
            httpClient,
            configuration,
            NullLogger<SlicerRegistrationClient>.Instance);

        (Guid serviceId, string apiKey) = await client.RegisterAsync();

        _ = serviceId.Should().Be(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        _ = apiKey.Should().Be("registered-key");
        _ = sentApiKey.Should().Be("the-key");
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
}
