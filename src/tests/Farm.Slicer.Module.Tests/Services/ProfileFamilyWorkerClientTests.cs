using System.Net;
using System.Text.Json;
using Farm.Slicer.Module.Api.Services;
using Farm.Slicer.Module.Dtos;
using Farm.Slicer.Module.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Slicer.Module.Tests.Services;

/// <summary>
/// Pins the slicer-host client to the worker's custom-bundle mutation contract.
/// </summary>
public sealed class ProfileFamilyWorkerClientTests
{
    [Fact]
    public async Task WriteBundleAsync_UsesAtomicWorkerBundleContract()
    {
        Guid familyId = Guid.NewGuid();
        var handler = new RecordingHandler();
        using HttpClient httpClient = new(handler);
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WorkerAuth:SharedKey"] = "test-worker-key"
            })
            .Build();
        var client = new ProfileFamilyWorkerClient(
            httpClient,
            new Mock<ISlicersService>(MockBehavior.Strict).Object,
            configuration,
            NullLogger<ProfileFamilyWorkerClient>.Instance);
        var bundle = new ProfileFamilyBundleDto(
            familyId,
            "Farm Test",
            """{"name":"Custom","machine_list":[]}""",
            [
                new RenderedProfileFileDto(
                    "machine/family/profile.json",
                    """{"name":"Farm Test 0.6 nozzle","type":"machine"}""")
            ]);

        await client.WriteBundleAsync(
            new ProfileFamilyWorkerTarget("http://worker:5100", "2.3.0"),
            bundle,
            CancellationToken.None);

        handler.Method.Should().Be(HttpMethod.Put);
        handler.Uri.Should().Be(
            new Uri($"http://worker:5100/api/profiles/custom-bundles/PrintFarmer-{familyId:N}"));
        handler.SharedKey.Should().Be("test-worker-key");
        using JsonDocument body = JsonDocument.Parse(handler.Body!);
        body.RootElement.GetProperty("manifest").GetProperty("name").GetString()
            .Should().Be("Custom");
        JsonElement file = body.RootElement.GetProperty("files")[0];
        file.GetProperty("relativePath").GetString().Should().Be("machine/family/profile.json");
        file.GetProperty("familyName").GetString().Should().Be("Farm Test");
        file.GetProperty("document").GetProperty("name").GetString()
            .Should().Be("Farm Test 0.6 nozzle");
    }

    [Fact]
    public async Task WriteBundleAsync_IncompleteInheritance_PreservesWorkerFailureDetails()
    {
        Guid familyId = Guid.NewGuid();
        var handler = new RecordingHandler(
            HttpStatusCode.UnprocessableEntity,
            """
            {
              "operation": "installed",
              "bundleName": "PrintFarmer-worker-bundle",
              "machineCount": 0,
              "filamentCount": 0,
              "processCount": 0,
              "failures": [
                {
                  "bundleName": "Custom",
                  "familyName": "Farm Test",
                  "profileName": "Farm Test 0.6 nozzle",
                  "missingParent": "Stock 0.6 nozzle"
                }
              ]
            }
            """);
        using HttpClient httpClient = new(handler);
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WorkerAuth:SharedKey"] = "test-worker-key"
            })
            .Build();
        var client = new ProfileFamilyWorkerClient(
            httpClient,
            new Mock<ISlicersService>(MockBehavior.Strict).Object,
            configuration,
            NullLogger<ProfileFamilyWorkerClient>.Instance);
        var bundle = new ProfileFamilyBundleDto(
            familyId,
            "Farm Test",
            """{"name":"Custom","machine_list":[]}""",
            []);

        Func<Task> act = () => client.WriteBundleAsync(
            new ProfileFamilyWorkerTarget("http://worker:5100", "2.3.0"),
            bundle,
            CancellationToken.None);

        ProfileFamilySourceException exception = (await act.Should()
            .ThrowAsync<ProfileFamilySourceException>()).Which;
        exception.Message.Should().Contain("bundle 'Custom'");
        exception.Message.Should().Contain("family 'Farm Test'");
        exception.Message.Should().Contain("profile 'Farm Test 0.6 nozzle'");
        exception.Message.Should().Contain("missing parent 'Stock 0.6 nozzle'");
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly string? _responseBody;

        public RecordingHandler(
            HttpStatusCode statusCode = HttpStatusCode.OK,
            string? responseBody = null)
        {
            _statusCode = statusCode;
            _responseBody = responseBody;
        }

        public HttpMethod? Method { get; private set; }

        public Uri? Uri { get; private set; }

        public string? SharedKey { get; private set; }

        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Method = request.Method;
            Uri = request.RequestUri;
            SharedKey = request.Headers.GetValues("X-Slicer-Api-Key").Single();
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(_statusCode)
            {
                Content = _responseBody is null
                    ? null
                    : new StringContent(_responseBody)
            };
        }
    }
}
