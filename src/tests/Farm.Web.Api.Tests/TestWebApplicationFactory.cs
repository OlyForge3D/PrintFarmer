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
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authentication;

// PRESUBMIT: SKIP-DBHEAVY - This is a test factory class, not a test class itself
namespace Farm.Web.Api.Tests;

public sealed class CustomWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _dbPath;
    private SqliteConnection? _inMemorySqliteConnection;
    // Optional shared connection used as a lightweight fixture across factory instances
    private static SqliteConnection? _sharedSqliteConnection;
    private static readonly object _sharedConnLock = new object();
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

        // Determine whether tests request an in-memory SQLite database or a shared
        // shared in-memory SQLite fixture. If either is requested, force the host
        // environment to Development so the startup path uses EnsureCreated instead
        // of Migrate which simplifies in-memory schema creation for tests.
        var useInMemorySqlite = string.Equals(Environment.GetEnvironmentVariable("TEST_USE_SQLITE_INMEMORY"), "true", StringComparison.OrdinalIgnoreCase);
        var useSharedSqlite = string.Equals(Environment.GetEnvironmentVariable("TEST_USE_SHARED_SQLITE"), "true", StringComparison.OrdinalIgnoreCase);
        // If a shared fixture already prepared a global SqliteConnection, mark the
        // startup to skip its own DB initialization as early as possible so the
        // application won't race with the fixture's pre-seed.
        try
        {
            if (useSharedSqlite && Farm.Web.Api.Tests.TestInfrastructure.SharedSqliteFixture.GlobalConnection != null)
            {
                Environment.SetEnvironmentVariable("TEST_SKIP_STARTUP_DB_INIT", "true");
            }
        }
        catch { }
        if (useInMemorySqlite || useSharedSqlite)
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Development");
            builder.UseEnvironment("Development");
        }
        else
        {
            builder.UseEnvironment("Testing");
        }
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
            // Early: if tests requested a shared in-memory sqlite, ensure a
            // SqliteConnection singleton is present before any DbContextOptions
            // are configured. Some DI/resolution paths may attempt to resolve
            // the connection while building the service provider, so we must
            // register it as soon as ConfigureServices starts executing.
            try
            {
                if (string.Equals(Environment.GetEnvironmentVariable("TEST_USE_SHARED_SQLITE"), "true", StringComparison.OrdinalIgnoreCase))
                {
                    // Prefer fixture-provided connection when available
                    var global = Farm.Web.Api.Tests.TestInfrastructure.SharedSqliteFixture.GlobalConnection;
                    SqliteConnection earlyConn;
                    if (global != null)
                    {
                        earlyConn = global;
                    }
                    else
                    {
                        var exported = Environment.GetEnvironmentVariable("TEST_SHARED_SQLITE_CONN");
                        if (!string.IsNullOrEmpty(exported))
                        {
                            earlyConn = new SqliteConnection(exported);
                        }
                        else
                        {
                            var sharedName = $"early_shared_unittest_{Guid.NewGuid():N}";
                            var connStr = $"Data Source=file:{sharedName}?mode=memory&cache=shared";
                            earlyConn = new SqliteConnection(connStr);
                        }

                        if (earlyConn.State != System.Data.ConnectionState.Open)
                        {
                            earlyConn.Open();
                        }
                    }

                    // Ensure the env flag so startup doesn't double-seed
                    Environment.SetEnvironmentVariable("TEST_SKIP_STARTUP_DB_INIT", "true");

                    // Register into DI if not already present
                    try
                    {
                        services.AddSingleton<Microsoft.Data.Sqlite.SqliteConnection>(earlyConn);
                        services.AddSingleton<System.Data.Common.DbConnection>(earlyConn);
                    }
                    catch { }
                }
            }
            catch { }

            // If using a shared in-memory sqlite for tests, always skip the
            // application's heavy startup DB initialization. Tests will
            // pre-seed or ensure schema themselves to avoid races.
            if (string.Equals(Environment.GetEnvironmentVariable("TEST_USE_SHARED_SQLITE"), "true", StringComparison.OrdinalIgnoreCase))
            {
                Environment.SetEnvironmentVariable("TEST_SKIP_STARTUP_DB_INIT", "true");
            }
            // Test authentication: provide a deterministic authenticated user so tests don't get 401
            services.AddAuthentication("Test")
                .AddScheme<AuthenticationSchemeOptions, Farm.Web.Api.Tests.TestInfrastructure.TestAuthHandler>("Test", options => { });

            services.AddAuthorization(options =>
            {
                options.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
                    .AddAuthenticationSchemes("Test")
                    .RequireAuthenticatedUser()
                    .Build();
            });
            // Allow tests to opt into using EF Core's InMemory provider instead of SQLite.
            // This is useful to isolate tests from SQLite file/in-memory semantics when
            // table creation timing causes flakiness. Enable with TEST_USE_EF_INMEMORY=true.
            var useEfInMemory = string.Equals(Environment.GetEnvironmentVariable("TEST_USE_EF_INMEMORY"), "true", StringComparison.OrdinalIgnoreCase);
            if (useEfInMemory)
            {
                // Replace AppDbContext registration with InMemory provider pointing to a unique DB name
                try
                {
                    // Remove any registration that may reference AppDbContext or its DbContextOptions
                    var descriptors = services.Where(d =>
                        (d.ServiceType != null && d.ServiceType.FullName != null && (d.ServiceType.FullName.Contains("AppDbContext") || d.ServiceType.FullName.Contains("DbContextOptions"))) ||
                        (d.ImplementationType != null && d.ImplementationType.FullName != null && d.ImplementationType.FullName.Contains("AppDbContext"))
                    ).ToList();
                    foreach (var d in descriptors)
                        services.Remove(d);
                    services.AddDbContext<Farm.Infrastructure.Data.AppDbContext>(opts =>
                    {
                        opts.UseInMemoryDatabase($"unittest_inmemory_{Guid.NewGuid():N}");
                    });

                    // Optionally replace DatabaseInitializer with a no-op implementation to avoid heavy seeding in InMemory tests.
                    var dbInitDesc = services.SingleOrDefault(d => d.ServiceType == typeof(Farm.Web.Api.Services.DatabaseInitializer));
                    if (dbInitDesc != null)
                    {
                        services.Remove(dbInitDesc);
                        services.AddScoped<Farm.Web.Api.Services.DatabaseInitializer, Farm.Web.Api.Tests.TestInfrastructure.NoOpDatabaseInitializer>();
                    }
                }
                catch
                {
                    // best-effort
                }
            }

            // If requested, replace the AppDbContext registration with a SQLite in-memory
            // connection which preserves relational semantics for tests. Enable by setting
            // the environment variable TEST_USE_SQLITE_INMEMORY=true in the test process.
            // If the environment decision was made above, reuse that value here.
            // This avoids re-reading environment variables whose values we may have
            // just adjusted for the host builder.
            // NOTE: keep this variable in sync with the check above.
            // (It will be re-evaluated only if not previously set.)
            var useInMemorySqliteLocal = useInMemorySqlite || string.Equals(Environment.GetEnvironmentVariable("TEST_USE_SQLITE_INMEMORY"), "true", StringComparison.OrdinalIgnoreCase);

            if (useInMemorySqliteLocal)
            {
                // Use a shared in-memory SQLite database by using a file: URI with shared cache.
                // Keep one SqliteConnection open for the lifetime of the factory so the
                // in-memory database is preserved across connections opened by EF Core.
                var memDbName = $"unittest_{Guid.NewGuid():N}";
                var memConnString = $"Data Source=file:{memDbName}?mode=memory&cache=shared";

                // Override connection strings so Program.cs registrations will use the in-memory DB
                Environment.SetEnvironmentVariable("ConnectionStrings__Default", memConnString);
                Environment.SetEnvironmentVariable("ConnectionStrings__Sqlite", memConnString);

                _inMemorySqliteConnection = new SqliteConnection(memConnString);
                _inMemorySqliteConnection.Open();
                // Re-register AppDbContext to use the opened connection so the host will
                // use the exact same in-memory database instance. This guarantees
                // that EnsureCreated and any DbContext resolved later (e.g. in
                // SettingsService) operate on the same database.
                try
                {
                    // Remove any existing descriptors that reference AppDbContext or DbContextOptions so
                    // we can replace the registration reliably.
                    var descriptorsToRemove = services.Where(d =>
                        (d.ServiceType != null && d.ServiceType.FullName != null && (d.ServiceType.FullName.Contains("AppDbContext") || d.ServiceType.FullName.Contains("DbContextOptions"))) ||
                        (d.ImplementationType != null && d.ImplementationType.FullName != null && d.ImplementationType.FullName.Contains("AppDbContext"))
                    ).ToList();

                    foreach (var d in descriptorsToRemove)
                    {
                        services.Remove(d);
                    }

                    // Register AppDbContext to use the opened SqliteConnection instance
                    services.AddDbContext<Farm.Infrastructure.Data.AppDbContext>(opts =>
                    {
                        opts.UseSqlite(_inMemorySqliteConnection);
                    });
                }
                catch
                {
                    // Best-effort; don't throw in test registration path
                }
            }

            // Support a shared fixture-like SQLite connection across factories to avoid EnsureCreated races.
            var useSharedSqlite = string.Equals(Environment.GetEnvironmentVariable("TEST_USE_SHARED_SQLITE"), "true", StringComparison.OrdinalIgnoreCase);
            if (useSharedSqlite)
            {
                try
                {
                    lock (_sharedConnLock)
                    {
                        if (_sharedSqliteConnection == null)
                        {
                            // Prefer the in-memory SqliteConnection instance published by a fixture
                            if (Farm.Web.Api.Tests.TestInfrastructure.SharedSqliteFixture.GlobalConnection != null)
                            {
                                _sharedSqliteConnection = Farm.Web.Api.Tests.TestInfrastructure.SharedSqliteFixture.GlobalConnection;
                            }
                            else
                            {
                                var exported = Environment.GetEnvironmentVariable("TEST_SHARED_SQLITE_CONN");
                                if (!string.IsNullOrEmpty(exported))
                                {
                                    _sharedSqliteConnection = new SqliteConnection(exported);
                                }
                                else
                                {
                                    var sharedName = $"shared_unittest_{Guid.NewGuid():N}";
                                    var connStr = $"Data Source=file:{sharedName}?mode=memory&cache=shared";
                                    _sharedSqliteConnection = new SqliteConnection(connStr);
                                }
                            }

                            // Only open if not already open
                            if (_sharedSqliteConnection.State != System.Data.ConnectionState.Open)
                            {
                                _sharedSqliteConnection.Open();
                            }

                            // If a shared fixture prepared and seeded the DB, instruct
                            // application startup to skip its own initialization/seed.
                            Environment.SetEnvironmentVariable("TEST_SKIP_STARTUP_DB_INIT", "true");
                        }

                        // Register the shared connection instance into DI so the app and tests
                        // use the exact same SqliteConnection object instance. Do this every
                        // time so each factory's IServiceCollection can resolve the singleton
                        // even when the static connection was created by a different factory.
                        try
                        {
                            services.AddSingleton<Microsoft.Data.Sqlite.SqliteConnection>(_sharedSqliteConnection!);
                            services.AddSingleton<System.Data.Common.DbConnection>(_sharedSqliteConnection!);
                        }
                        catch { }
                    }

                    // Remove existing AppDbContext registrations so we can replace with the shared connection
                    var descriptors = services.Where(d =>
                        (d.ServiceType != null && d.ServiceType.FullName != null && (d.ServiceType.FullName.Contains("AppDbContext") || d.ServiceType.FullName.Contains("DbContextOptions"))) ||
                        (d.ImplementationType != null && d.ImplementationType.FullName != null && d.ImplementationType.FullName.Contains("AppDbContext"))
                    ).ToList();
                    foreach (var d in descriptors)
                        services.Remove(d);

                    // Register AppDbContext to pick up the SqliteConnection from DI so every context uses the
                    // same open connection instance. Use factory overload so we can resolve the connection.
                    services.AddDbContext<Farm.Infrastructure.Data.AppDbContext>((sp, options) =>
                    {
                        var conn = sp.GetRequiredService<Microsoft.Data.Sqlite.SqliteConnection>();
                        options.UseSqlite(conn);
                    });

                    // Best-effort: build a temporary provider now to ensure schema exists and run
                    // the real DatabaseInitializer (InitializeAsync + SeedAllAsync) before the
                    // real host starts. This prevents startup races where services (e.g.
                    // SettingsService) are constructed during host build and query tables
                    // that haven't been created yet. We call async methods synchronously
                    // here as ConfigureServices is not async.
                    try
                    {
                        var tempProvider = services.BuildServiceProvider();
                        using (var scope = tempProvider.CreateScope())
                        {
                            var tempDb = scope.ServiceProvider.GetService<Farm.Infrastructure.Data.AppDbContext>();
                            if (tempDb != null)
                            {
                                tempDb.Database.EnsureCreated();
                            }

                            var initializer = scope.ServiceProvider.GetService<Farm.Web.Api.Services.DatabaseInitializer>();
                            if (initializer != null)
                            {
                                try
                                {
                                    // Use default provider name "sqlite" for this pre-seed phase
                                    initializer.InitializeAsync("sqlite", 3, 2).GetAwaiter().GetResult();
                                    initializer.SeedAllAsync().GetAwaiter().GetResult();
                                }
                                catch
                                {
                                    // Best-effort; if seeding fails here, host startup will report the error.
                                }
                            }
                        }
                    }
                    catch
                    {
                        // Swallow - best-effort pre-seed
                    }
                }
                catch
                {
                    // best-effort
                }
            }
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
        // Before building the host, ensure the database schema exists on the exact
        // provider/connection we configured above. This prevents a race where the
        // SettingsService or other DB-reading services are constructed during host
        // build and query tables that don't exist yet. We do a best-effort pre-create
        // using a temporary AppDbContext configured to the same provider/connection.

        try
        {
            // If we created a per-factory in-memory Sqlite connection, create a temporary
            // DbContext using that connection and ensure schema is created now.
            if (_inMemorySqliteConnection != null)
            {
                var opts = new DbContextOptionsBuilder<Farm.Infrastructure.Data.AppDbContext>()
                    .UseSqlite(_inMemorySqliteConnection)
                    .Options;
                using var temp = new Farm.Infrastructure.Data.AppDbContext(opts);
                temp.Database.EnsureCreated();
            }

            // If a shared SQLite connection was prepared, ensure its schema exists as well
            if (_sharedSqliteConnection != null)
            {
                var opts = new DbContextOptionsBuilder<Farm.Infrastructure.Data.AppDbContext>()
                    .UseSqlite(_sharedSqliteConnection)
                    .Options;
                using var temp = new Farm.Infrastructure.Data.AppDbContext(opts);
                temp.Database.EnsureCreated();
            }

            // If tests opted to use EF InMemory, ensure a DB instance with the same name is created.
            var useEfInMemory = string.Equals(Environment.GetEnvironmentVariable("TEST_USE_EF_INMEMORY"), "true", StringComparison.OrdinalIgnoreCase);
            if (useEfInMemory)
            {
                // We used a unique name when registering the provider. Reconstruct a minimal
                // in-memory options with the same name pattern and call EnsureCreated.
                var inmemoryName = Environment.GetEnvironmentVariable("TEST_EF_INMEMORY_DBNAME") ?? null;
                if (string.IsNullOrEmpty(inmemoryName))
                {
                    // Fall back to a deterministic name when not provided; the registration used a GUID,
                    // but ensuring here is a best-effort safety; many tests will re-seed as needed.
                    inmemoryName = "unittest_inmemory";
                    // When using a shared pre-seeded DB from a fixture, instruct
                    // the application startup to skip its own initialization/seed
                    // to avoid double-seeding and provider lock races.
                    Environment.SetEnvironmentVariable("TEST_SKIP_STARTUP_DB_INIT", "true");
                }
                var opts = new DbContextOptionsBuilder<Farm.Infrastructure.Data.AppDbContext>()
                    .UseInMemoryDatabase(inmemoryName)
                    .Options;
                using var temp = new Farm.Infrastructure.Data.AppDbContext(opts);
                temp.Database.EnsureCreated();
            }
        }
        catch
        {
            // Best-effort; if EnsureCreated fails here, host build will likely fail as well and tests will report.
        }

        // Build the real host. After constructing the host (but before starting it),
        // we still attempt a host-scoped EnsureCreated as a final safety net.
        var host = base.CreateHost(builder);

        try
        {
            using var scope = host.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<Farm.Infrastructure.Data.AppDbContext>();
            db.Database.EnsureCreated();
        }
        catch
        {
            // Best-effort; let host startup handle actual errors
        }

        return host;
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
                return [.. list];
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
        try
        {
            // Dispose in-memory connection if used
            if (_inMemorySqliteConnection != null)
            {
                _inMemorySqliteConnection.Close();
                _inMemorySqliteConnection.Dispose();
                _inMemorySqliteConnection = null;
            }
        }
        catch { }

        TryDelete();
    }
}
