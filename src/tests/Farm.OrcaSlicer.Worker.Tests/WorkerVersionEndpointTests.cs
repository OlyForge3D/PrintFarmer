using System.Net;
using System.Text.Json;
using Farm.OrcaSlicer.Worker.Services;
using Farm.Slicer.Worker.Core;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Farm.OrcaSlicer.Worker.Tests;

public sealed class WorkerVersionEndpointTests : IDisposable
{
    private const string ConfiguredEngineVersion = "9.8.7";
    private readonly WorkerVersionApplicationFactory _factory = new();

    [Fact]
    public async Task GetVersionEndpoints_ConfiguredEngineVersion_ReturnExpectedVersion()
    {
        using HttpClient client = _factory.CreateClient();
        (string Path, string Property)[] endpoints =
        [
            ("/", "version"),
            ("/version", "workerVersion"),
            ("/api/system/version", "workerVersion"),
        ];

        foreach ((string path, string property) in endpoints)
        {
            using HttpResponseMessage response = await client.GetAsync(path);
            using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            body.RootElement.GetProperty(property).GetString().Should().Be(ConfiguredEngineVersion);
        }
    }

    public void Dispose()
    {
        _factory.Dispose();
    }

    private sealed class WorkerVersionApplicationFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            _ = builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Worker:EngineVersion"] = ConfiguredEngineVersion,
                    ["Worker:VerifyBinaryVersion"] = "true",
                }));

            _ = builder.ConfigureServices(services =>
            {
                services.RemoveAll<IOrcaBinaryDetector>();
                _ = services.AddSingleton<IOrcaBinaryDetector>(
                    new StubBinaryDetector(ConfiguredEngineVersion));
                services.RemoveAll<IProfilePreloadService>();
                _ = services.AddSingleton<IProfilePreloadService, NoOpProfilePreloadService>();

                Type[] workerHostedServiceTypes =
                [
                    typeof(GracefulShutdownService),
                    typeof(QueueConsumerService),
                    typeof(RegistrationBackgroundService),
                ];
                ServiceDescriptor[] workerHostedServices = services
                    .Where(descriptor =>
                        descriptor.ServiceType == typeof(IHostedService) &&
                        descriptor.ImplementationType is not null &&
                        workerHostedServiceTypes.Contains(descriptor.ImplementationType))
                    .ToArray();
                foreach (ServiceDescriptor descriptor in workerHostedServices)
                {
                    _ = services.Remove(descriptor);
                }
            });
        }
    }

    private sealed class StubBinaryDetector(string version) : IOrcaBinaryDetector
    {
        public bool IsRealBinaryPresent() => true;

        public Task<string?> GetVersionAsync() => Task.FromResult<string?>(version);
    }

    private sealed class NoOpProfilePreloadService : IProfilePreloadService
    {
        public Task PreloadProfilesAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
