using System.Collections.Concurrent;
using Farm.Web.Api.Services;
using Farm.Web.Api.Services.Interfaces;
using Farm.Web.Api.Services.SlicerServices;
using Farm.Web.Shared;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;

// PRESUBMIT: SKIP-DBHEAVY - This is a test factory class, not a test class itself
namespace Farm.Web.Api.Tests;

public sealed class CustomWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _dbPath;
    public Mock<INetworkDiscoveryService> MockNetworkDiscoveryService { get; private set; }
    public Mock<IMoonrakerClient> MockMoonrakerClient { get; private set; }
    public Mock<IPrusaLinkClient> MockPrusaLinkClient { get; private set; }
    public Mock<ISdcpClient> MockSdcpClient { get; private set; }
    public Mock<ISlicerJobQueue> MockSlicerJobQueue { get; private set; } = null!;
    public Mock<ISlicerFileStorage> MockSlicerFileStorage { get; private set; } = null!;
    public Mock<ISlicerProgressNotifier> MockSlicerProgressNotifier { get; private set; } = null!;
    public Mock<IModelAnalysisService> MockModelAnalysisService { get; private set; } = null!;

    private readonly ConcurrentDictionary<Guid, DistributedSlicingJob> _slicerJobs = new();

    public CustomWebApplicationFactory()
    {
        var dbFile = $"farm_test_{Guid.NewGuid():N}.db"; // repository-local temp db file
        var tempDir = Farm.Web.Api.Tests.TestInfrastructure.TestPaths.GetUniqueTempDirectory();
        _dbPath = Path.Combine(tempDir, dbFile);
        TryDelete();

        // Ensure Program.cs picks up the test-specific database path early
        // Minimal hosting reads configuration very early; environment variables are safest for overrides
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={_dbPath}");
        Environment.SetEnvironmentVariable("ConnectionStrings__Sqlite", $"Data Source={_dbPath}");
        Environment.SetEnvironmentVariable("DISABLE_EF_MIGRATIONS", "true");

        // Ensure JWT config is present and consistent in tests
        Environment.SetEnvironmentVariable("Jwt__Key", "PrintFarmerTestSigningKey_0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ");
        Environment.SetEnvironmentVariable("Jwt__Issuer", "PrintFarmer");
        Environment.SetEnvironmentVariable("Jwt__Audience", "PrintFarmer");

        // Initialize mocks
        MockNetworkDiscoveryService = new Mock<INetworkDiscoveryService>();
        MockMoonrakerClient = new Mock<IMoonrakerClient>();
        MockPrusaLinkClient = new Mock<IPrusaLinkClient>();
        MockSdcpClient = new Mock<ISdcpClient>();
        MockSlicerJobQueue = new Mock<ISlicerJobQueue>();
        MockSlicerFileStorage = new Mock<ISlicerFileStorage>();
        MockSlicerProgressNotifier = new Mock<ISlicerProgressNotifier>();
        MockModelAnalysisService = new Mock<IModelAnalysisService>();

        // Set up default mock behaviors
        SetupDefaultMockBehaviors();

        SetupSlicerServiceMocks();
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

            // Provide a no-op ModelAnalysisService for tests to avoid DI activation failures when
            // the real analysis service is not desirable in unit/integration tests.
            services.AddSingleton<IModelAnalysisService>(MockModelAnalysisService.Object);

            // Replace temp path provider with test-specific implementation confined to repo
            var existingTemp = services.SingleOrDefault(d => d.ServiceType == typeof(Farm.Web.Api.Infrastructure.Temp.ITempPathProvider));
            if (existingTemp != null)
            {
                services.Remove(existingTemp);
            }
            services.AddSingleton<Farm.Web.Api.Infrastructure.Temp.ITempPathProvider>(new TestInfrastructure.TestTempPathProvider());

            // Slicer service registrations: in-process engines removed; only orchestrator + queue abstractions used.
            // File storage is fully mocked; still register options for any resolution paths
            services.Configure<LocalFileStorageOptions>(o =>
            {
                o.BasePath = Path.Combine(Farm.Web.Api.Tests.TestInfrastructure.TestPaths.RepoTempRoot, "slicer-test-storage");
            });

            services.AddSingleton(MockSlicerJobQueue.Object);
            services.AddSingleton(MockSlicerFileStorage.Object);
            services.AddSingleton(MockSlicerProgressNotifier.Object);
            services.AddScoped<ISlicerOrchestrator, SlicerOrchestrator>();
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

        // Default analysis behavior: return null (analysis optional) to keep tests deterministic
        MockModelAnalysisService.Setup(x => x.AnalyzeModelAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ModelAnalysisResult?)null);
        return base.CreateHost(builder);
    }

    private void SetupDefaultMockBehaviors()
    {
        // Set up default discovery behavior to return empty list
        MockNetworkDiscoveryService
            .Setup(x => x.DiscoverPrintersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DiscoveredPrinterDto>());
    }

    private void SetupSlicerServiceMocks()
    {
        // Basic deterministic queue stats
        MockSlicerJobQueue.Setup(q => q.GetQueueStatsAsync(It.IsAny<SlicerEngineType?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SlicerEngineType? engine, CancellationToken _) => new SlicerQueueStats
            {
                Engine = engine ?? SlicerEngineType.OrcaSlicer,
                QueuedJobs = 0,
                ProcessingJobs = 0,
                CompletedJobs = 0,
                FailedJobs = 0,
                ActiveWorkers = 0,
                AverageProcessingTimeSeconds = 10,
                EstimatedWaitTime = TimeSpan.Zero,
                LastUpdated = DateTime.UtcNow
            });

        // Capture jobs enqueued so status queries work
        MockSlicerJobQueue.Setup(q => q.EnqueueAsync(It.IsAny<DistributedSlicingJob>(), It.IsAny<CancellationToken>()))
            .Callback<DistributedSlicingJob, CancellationToken>((job, _) =>
            {
                job.Status = SlicingJobStatus.Queued;
                _slicerJobs[job.Id] = job;
            })
            .Returns(Task.CompletedTask);

        MockSlicerJobQueue.Setup(q => q.GetJobAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, CancellationToken _) => _slicerJobs.TryGetValue(id, out var job) ? job : null);

        MockSlicerJobQueue.Setup(q => q.FindExistingJobAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid cid, string checksum, CancellationToken _) => _slicerJobs.Values.FirstOrDefault(j => j.CorrelationId == cid && j.Checksum == checksum));

        MockSlicerJobQueue.Setup(q => q.GetUserJobsAsync(It.IsAny<Guid>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid userId, int? limit, CancellationToken _) =>
            {
                var list = _slicerJobs.Values.Where(j => j.UserId == userId).Take(limit ?? 50);
                return list.ToList();
            });

        // File storage mocks (accept any path & simulate existence)
        MockSlicerFileStorage.Setup(fs => fs.FileExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        MockSlicerFileStorage.Setup(fs => fs.GetFileMetadataAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SlicerFileMetadata { SizeBytes = 1024, ContentType = "application/octet-stream" });
        MockSlicerFileStorage.Setup(fs => fs.DownloadFileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(new byte[128]));
        MockSlicerFileStorage.Setup(fs => fs.DownloadFileBytesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new byte[128]);
        MockSlicerFileStorage.Setup(fs => fs.UploadFileAsync(It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string key, Stream _, string _, CancellationToken _) => $"memory://{key}");
        MockSlicerFileStorage.Setup(fs => fs.UploadFileAsync(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string key, byte[] _, string _, CancellationToken _) => $"memory://{key}");
        MockSlicerFileStorage.Setup(fs => fs.GenerateSignedUrlAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string key, TimeSpan _, CancellationToken _) => $"memory://{key}?sig=dev-test");

        // Progress notifier no-ops
        MockSlicerProgressNotifier.Setup(p => p.NotifyProgressAsync(It.IsAny<SlicingProgressUpdate>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        MockSlicerProgressNotifier.Setup(p => p.NotifyCompletionAsync(It.IsAny<DistributedSlicingJob>(), It.IsAny<SlicingResult>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        MockSlicerProgressNotifier.Setup(p => p.NotifyFailureAsync(It.IsAny<DistributedSlicingJob>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        MockSlicerProgressNotifier.Setup(p => p.SubscribeToJobAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        MockSlicerProgressNotifier.Setup(p => p.UnsubscribeFromJobAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
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
