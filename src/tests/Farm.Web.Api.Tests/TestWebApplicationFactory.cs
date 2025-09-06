using Farm.Web.Api.Services;
using Farm.Web.Api.Services.Interfaces;
using Farm.Web.Shared;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;

namespace Farm.Web.Api.Tests;

public sealed class CustomWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _dbPath;
    public Mock<INetworkDiscoveryService> MockNetworkDiscoveryService { get; private set; }
    public Mock<IMoonrakerClient> MockMoonrakerClient { get; private set; }
    public Mock<IPrusaLinkClient> MockPrusaLinkClient { get; private set; }
    public Mock<ISdcpClient> MockSdcpClient { get; private set; }

    public CustomWebApplicationFactory()
    {
        var dbFile = $"farm_test_{Guid.NewGuid():N}.db";
        _dbPath = Path.Combine(Path.GetTempPath(), dbFile);
        TryDelete();

    // Set environment variables EARLY so Program.cs picks up the test-specific database path
    // Minimal hosting reads configuration very early; relying only on ConfigureAppConfiguration
    // meant the connection string was resolved before our in-memory override, causing usage of the default farm.db.
    // Using environment variables guarantees the correct ephemeral file is used for each test factory instance.
    Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={_dbPath}");
    Environment.SetEnvironmentVariable("ConnectionStrings__Sqlite", $"Data Source={_dbPath}");
    Environment.SetEnvironmentVariable("DISABLE_EF_MIGRATIONS", "true");

        // Initialize mocks
        MockNetworkDiscoveryService = new Mock<INetworkDiscoveryService>();
        MockMoonrakerClient = new Mock<IMoonrakerClient>();
        MockPrusaLinkClient = new Mock<IPrusaLinkClient>();
        MockSdcpClient = new Mock<ISdcpClient>();

        // Set up default mock behaviors
        SetupDefaultMockBehaviors();
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((context, config) =>
        {
            var dict = new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = $"Data Source={_dbPath}",
                ["ConnectionStrings:Sqlite"] = $"Data Source={_dbPath}",
                // Avoid running EF Core Migrate() in tests when using ad-hoc SQLite files; rely on startup safety + EnsureCreated
                ["DISABLE_EF_MIGRATIONS"] = "true"
            };
            config.AddInMemoryCollection(dict!);
        });

        builder.ConfigureServices(services =>
        {
            // Remove existing service registrations
            // Disable background hosted services that would talk to external systems during tests
            var moonrakerHosted = services.SingleOrDefault(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(MoonrakerSubscriptionService));
            if (moonrakerHosted != null)
            {
                services.Remove(moonrakerHosted);
            }

            var harvestWorkerHosted = services.SingleOrDefault(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType?.Name == "HarvestWorkerService");
            if (harvestWorkerHosted != null)
            {
                services.Remove(harvestWorkerHosted);
            }

            var harvestCompletionHosted = services.SingleOrDefault(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType?.Name == "HarvestCompletionService");
            if (harvestCompletionHosted != null)
            {
                services.Remove(harvestCompletionHosted);
            }

            var gracefulShutdownHosted = services.SingleOrDefault(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType?.Name == "GracefulShutdownService");
            if (gracefulShutdownHosted != null)
            {
                services.Remove(gracefulShutdownHosted);
            }

            var networkDiscoveryDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(INetworkDiscoveryService));
            if (networkDiscoveryDescriptor != null)
            {
                services.Remove(networkDiscoveryDescriptor);
            }

            var moonrakerDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IMoonrakerClient));
            if (moonrakerDescriptor != null)
            {
                services.Remove(moonrakerDescriptor);
            }

            var prusaDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IPrusaLinkClient));
            if (prusaDescriptor != null)
            {
                services.Remove(prusaDescriptor);
            }

            var sdcpDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(ISdcpClient));
            if (sdcpDescriptor != null)
            {
                services.Remove(sdcpDescriptor);
            }

            // Register mocked services
            services.AddSingleton(MockNetworkDiscoveryService.Object);
            services.AddSingleton(MockMoonrakerClient.Object);
            services.AddSingleton(MockPrusaLinkClient.Object);
            services.AddSingleton(MockSdcpClient.Object);
        });

        // Set up default mock behaviors
        MockPrusaLinkClient.Setup(x => x.GetCompositeStatusAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Farm.Web.Api.Services.PrusaCompositeStatus(
                IsOnline: true,
                State: "Idle",
                Progress: 0,
                JobName: null,
                ThumbnailUrl: null,
                CameraStreamUrl: null,
                CameraSnapshotUrl: null
            ));

        MockMoonrakerClient.Setup(x => x.GetCompositeStatusAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Farm.Web.Api.Services.PrinterCompositeStatus(
                IsOnline: true,
                State: "Idle",
                Progress: 0,
                JobName: null,
                ThumbnailUrl: null,
                CameraStreamUrl: null,
                CameraSnapshotUrl: null
            ));

        MockSdcpClient.Setup(x => x.GetCompositeStatusAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Farm.Web.Api.Services.PrinterCompositeStatus(
                IsOnline: true,
                State: "Idle",
                Progress: 0,
                JobName: null,
                ThumbnailUrl: null,
                CameraStreamUrl: null,
                CameraSnapshotUrl: null
            ));
        return base.CreateHost(builder);
    }

    private void SetupDefaultMockBehaviors()
    {
        // Set up default discovery behavior to return empty list
        MockNetworkDiscoveryService
            .Setup(x => x.DiscoverPrintersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DiscoveredPrinterDto>());
    }

    private void TryDelete()
    {
        try
        {
            if (File.Exists(_dbPath))
            {
                File.Delete(_dbPath);
            }
        }
        catch { }
    }

    public new void Dispose()
    {
        base.Dispose();
        TryDelete();
    }
}
