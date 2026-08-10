using System.Net;
using System.Text.Json;
using Farm.Infrastructure.OrcaSlicer;
using Farm.Infrastructure.PrinterCalibration;
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
    private readonly string _workingDirectory =
        Path.Join(Path.GetTempPath(), $"printfarmer-worker-version-{Guid.NewGuid():N}");
    private readonly WorkerVersionApplicationFactory _factory;

    public WorkerVersionEndpointTests()
    {
        _factory = new WorkerVersionApplicationFactory(_workingDirectory);
    }

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

    [Fact]
    public void VersionContracts_LatestWorkerAndCalibrationStayAligned()
    {
        WorkerConstants.SlicerVersion.Should().Be(OrcaSlicerVersionConstants.LatestSupported);
        CalibrationContractConstants.SlicerVersion.Should().Be(WorkerConstants.SlicerVersion);
        WorkerConstants.SlicerVersion.Should().Be("2.4.2");
    }

    public void Dispose()
    {
        _factory.Dispose();
        if (Directory.Exists(_workingDirectory))
        {
            Directory.Delete(_workingDirectory, recursive: true);
        }
    }

    private sealed class WorkerVersionApplicationFactory(string workingDirectory)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            _ = builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["WorkerAuth:SharedKey"] = "test-registration-key",
                    ["Worker:EngineVersion"] = ConfiguredEngineVersion,
                    ["Worker:VerifyBinaryVersion"] = "true",
                    ["Worker:WorkingDirectory"] = workingDirectory,
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
