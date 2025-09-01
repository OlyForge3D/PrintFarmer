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
                // Avoid running EF Core Migrate() in tests when using ad-hoc SQLite files; rely on startup safety + EnsureCreated
                ["DISABLE_EF_MIGRATIONS"] = "true"
            };
            config.AddInMemoryCollection(dict!);
        });

        builder.ConfigureServices(services =>
        {
            // Remove existing service registrations
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
        { if (File.Exists(_dbPath))
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
