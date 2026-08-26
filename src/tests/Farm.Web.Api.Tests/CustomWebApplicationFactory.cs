using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Authentication;
using Farm.Infrastructure.Services.Interfaces;
using Farm.Infrastructure.Services.RateLimiting;
using Farm.Slicer.Module.Domain;
using Farm.Web.Api.Services.Authentication;
using Farm.Web.Api.Tests.TestInfrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moq;

namespace Farm.Web.Api.Tests;

// Provides isolated in-memory SQLite database for each test instance.
// Uses SQLite in-memory with shared cache so each factory instance gets its own isolated database.
public class CustomWebApplicationFactory : WebApplicationFactory<Program>, IAsyncDisposable
{
    // Each test gets a unique in-memory database using named connection
    private readonly string _connectionString;
    private readonly string _modelStoragePath;
    private readonly string _gcodeStoragePath;
    private readonly SqliteConnection _keepAliveConnection;
    private readonly Dictionary<string, string?>? _configOverrides;
    private static int _databaseCounter = 0;

    public CustomWebApplicationFactory() : this(configOverrides: null) { }

    internal CustomWebApplicationFactory(Dictionary<string, string?>? configOverrides)
    {
        _configOverrides = configOverrides;

        // Create a unique in-memory database per factory instance
        // Using auto-increment ID ensures complete isolation between tests
        int dbId = System.Threading.Interlocked.Increment(ref _databaseCounter);
        // Use a named shared in-memory database and keep one connection open for the factory lifetime.
        // This prevents SQLite from treating the string as a file path and avoids intermittent IO errors.
        _keepAliveConnection = new SqliteConnection($"Data Source=file:farm_test_{dbId}?mode=memory&cache=shared");
        _keepAliveConnection.Open();
        _connectionString = _keepAliveConnection.ConnectionString;

        // Create temp directories for file storage (isolated per test)
        string tempDir = Path.Join(Path.GetTempPath(), $"farm_test_{Guid.NewGuid()}");
        _modelStoragePath = Path.Join(tempDir, "models");
        _gcodeStoragePath = Path.Join(tempDir, "gcode");

        // Create the directories
        Directory.CreateDirectory(_modelStoragePath);
        Directory.CreateDirectory(_gcodeStoragePath);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Program.cs registers process-wide logging providers (Windows EventLog, the
        // SystemLog DB-table provider, console, etc.) that are unnecessary in tests and,
        // for EventLog specifically, rely on shared native OS handles that are not safe
        // to open/close concurrently across the many WebApplicationFactory hosts this
        // suite now builds and tears down in parallel. Strip every provider Program.cs
        // added and rely on xUnit's own test output instead.
        builder.ConfigureLogging(logging => logging.ClearProviders());

        // Configure worker auth shared key and storage paths for testing
        builder.ConfigureAppConfiguration((context, config) =>
        {
            Dictionary<string, string?> testConfig = new()
            {
                ["WorkerAuth:SharedKey"] = "test-worker-key",
                ["STORAGE_PATHS:UPLOADS"] = _modelStoragePath,
                ["STORAGE_PATHS:GCODE"] = _gcodeStoragePath
            };

            // Merge any caller-supplied config overrides (e.g., "Slicer:Enabled" = "false")
            if (_configOverrides != null)
            {
                foreach (KeyValuePair<string, string?> kvp in _configOverrides)
                {
                    testConfig[kvp.Key] = kvp.Value;
                }
            }

            config.AddInMemoryCollection(testConfig);
        });

        builder.ConfigureServices((context, services) =>
        {
            // Program.cs registers ~10 real IHostedService workers (maintenance pollers,
            // catalog update detection, power-monitor polling, queue/history/audit
            // background tasks, etc.), and at least one of them (PrintFailureMonitorService)
            // is a genuine test dependency: FailureDetectionControllerTests asserts on state
            // that only that background loop publishes. So these services must keep running
            // — but with up to ProcessorCount hosts now alive concurrently, each running ~10
            // pollers against a database that per-test ResetDataAsync() calls wipe mid-cycle,
            // a poller occasionally hits a table another test just cleared and throws. .NET's
            // default HostOptions.BackgroundServiceExceptionBehavior is StopHost, so that one
            // unhandled exception tears down the ENTIRE host — which is exactly what surfaced
            // as sporadic, single-class-wide ObjectDisposedException("IServiceProvider")
            // failures once full parallelism was enabled. Switch to Ignore: a poller iteration
            // that throws is logged and that background task stops, but the host (and every
            // other service in it, including the DI container tests still need) survives.
            services.Configure<HostOptions>(options =>
                options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);

            if (_configOverrides?.TryGetValue("Testing:UseTestAuthentication", out string? useTestAuth) == true &&
                bool.TryParse(useTestAuth, out bool enabled) &&
                enabled)
            {
                _ = services
                    .AddAuthentication(options =>
                    {
                        options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                        options.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                        options.DefaultForbidScheme = TestAuthHandler.SchemeName;
                    })
                    .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, TestAuthHandler>(
                        TestAuthHandler.SchemeName,
                        _ => { });
            }

            foreach (ServiceDescriptor descriptor in services
                .Where(d => d.ServiceType == typeof(AppDbContext)
                    || d.ServiceType == typeof(DbContextOptions<AppDbContext>)
                    || d.ServiceType == typeof(Microsoft.EntityFrameworkCore.Infrastructure.IDbContextOptionsConfiguration<AppDbContext>)
                    || d.ServiceType == typeof(IDbContextFactory<AppDbContext>))
                .ToList())
            {
                services.Remove(descriptor);
            }

            // Register in-memory SQLite database
            services.AddDbContext<AppDbContext>(options =>
            {
                options.UseSqlite(
                    _connectionString,
                    sqlite => sqlite.MigrationsAssembly("Farm.Migrations.Sqlite"));
            });

            // Re-register DbContextFactory with the test SQLite connection (same pattern as production)
            DbContextOptionsBuilder<AppDbContext> optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();
            optionsBuilder.UseSqlite(
                _connectionString,
                sqlite => sqlite.MigrationsAssembly("Farm.Migrations.Sqlite"));
            services.AddSingleton(optionsBuilder.Options);
            services.AddDbContextFactory<AppDbContext>();

            // Ensure database is created after all services are registered
            ServiceProvider sp = services.BuildServiceProvider();
            using (IServiceScope scope = sp.CreateScope())
            {
                AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                db.Database.EnsureCreated();
            }

            // Re-configure SlicerDbContext to use the same test SQLite database.
            // AddSlicerModule registered it with production defaults; override here.
            // Skip when slicer is disabled (no SlicerDbContext will be registered).
            //
            // Determinism guard: AddSlicerIntegration discovers the slicer module via a runtime
            // assembly scan (SlicerIntegrationExtensions). Under parallel host builds that scan can
            // transiently fail — concurrent Assembly.GetTypes() throws ReflectionTypeLoadException,
            // which the discovery code swallows — leaving the slicer module (and SlicerDbContext)
            // unregistered for that host. This produced intermittent
            // "No service for type 'SlicerDbContext' has been registered" failures. Re-run the
            // idempotent AddSlicerModule so registration is deterministic unless the slicer is
            // explicitly disabled for this factory.
            bool slicerDisabled = string.Equals(
                context.Configuration["Slicer:Enabled"], "false", StringComparison.OrdinalIgnoreCase);
            if (!slicerDisabled && !services.Any(d =>
                d.ServiceType == typeof(DbContextOptions<Farm.Slicer.Module.Data.SlicerDbContext>)))
            {
                Farm.Slicer.Module.SlicerModuleExtensions.AddSlicerModule(services, context.Configuration);
            }

            // Deterministically (re)register SlicerDbContext against the test SQLite database.
            // This intentionally does NOT depend on whether discovery / AddSlicerModule already
            // registered it. Two independent races could otherwise leave it unregistered:
            //   1. The runtime assembly scan in AddSlicerIntegration can transiently miss the slicer
            //      module under parallel host builds (ReflectionTypeLoadException), so nothing is
            //      registered.
            //   2. AddSlicerModule is idempotent on a SlicerModuleMarker and skips the DbContext in
            //      microservices DEPLOYMENT_MODE — so the safety-net AddSlicerModule call above can be
            //      a no-op that leaves SlicerDbContext unregistered while the marker is present.
            // Either case previously fell through the old `if (slicerRegistered)` gate, causing
            // ResetDatabaseAsync to throw "No service for type 'SlicerDbContext' has been registered".
            // Registering unconditionally (unless slicer is explicitly disabled for this factory)
            // makes the test host deterministic. The Remove calls below are null-safe when nothing
            // was registered, so a fresh registration is added in that case.
            if (!slicerDisabled)
            {
                ServiceDescriptor? slicerDbDescriptor = services.FirstOrDefault(d =>
                    d.ServiceType == typeof(DbContextOptions<Farm.Slicer.Module.Data.SlicerDbContext>));
                if (slicerDbDescriptor != null)
                {
                    services.Remove(slicerDbDescriptor);
                }

                ServiceDescriptor? slicerFactoryDescriptor = services.FirstOrDefault(d =>
                    d.ServiceType == typeof(IDbContextFactory<Farm.Slicer.Module.Data.SlicerDbContext>));
                if (slicerFactoryDescriptor != null)
                {
                    services.Remove(slicerFactoryDescriptor);
                }

                ServiceDescriptor? slicerSingletonOpts = services.FirstOrDefault(d =>
                    d.ServiceType == typeof(DbContextOptions<Farm.Slicer.Module.Data.SlicerDbContext>)
                    && d.Lifetime == ServiceLifetime.Singleton);
                if (slicerSingletonOpts != null)
                {
                    services.Remove(slicerSingletonOpts);
                }

                services.AddDbContext<Farm.Slicer.Module.Data.SlicerDbContext>(options =>
                {
                    options.UseSqlite(
                        _connectionString,
                        sqlite => sqlite.MigrationsAssembly("Farm.Slicer.Migrations.Sqlite"));
                });

                DbContextOptionsBuilder<Farm.Slicer.Module.Data.SlicerDbContext> slicerOptionsBuilder = new();
                slicerOptionsBuilder.UseSqlite(
                    _connectionString,
                    sqlite => sqlite.MigrationsAssembly("Farm.Slicer.Migrations.Sqlite"));
                services.AddSingleton(slicerOptionsBuilder.Options);
                services.AddDbContextFactory<Farm.Slicer.Module.Data.SlicerDbContext>();

                // Create SlicerDbContext tables after reconfiguration
                ServiceProvider sp2 = services.BuildServiceProvider();
                using (IServiceScope scope2 = sp2.CreateScope())
                {
                    // EnsureCreated on a second context is a no-op if the DB already exists.
                    // Use CreateTables() to add SlicerDbContext tables to the shared DB.
                    Farm.Slicer.Module.Data.SlicerDbContext slicerDb = scope2.ServiceProvider.GetRequiredService<Farm.Slicer.Module.Data.SlicerDbContext>();
                    var creator1 = ((Microsoft.EntityFrameworkCore.Infrastructure.IInfrastructure<IServiceProvider>)slicerDb).Instance
                        .GetRequiredService<Microsoft.EntityFrameworkCore.Storage.IRelationalDatabaseCreator>();
                    try
                    { creator1.CreateTables(); }
                    catch (Microsoft.Data.Sqlite.SqliteException) { /* tables may already exist */ }
                }
            }
        });
    }

    // WebApplicationFactory<Program> resolves the minimal-hosting entry point via reflection
    // (HostFactoryResolver) and Program.cs's own startup performs reflection-based type scanning
    // (backend plugin discovery — see BackendPluginExtensions.DiscoverAndLoadPlugins). Building
    // many hosts concurrently — now that the assembly-level parallelism cap is lifted — makes
    // this scan race (e.g. transient ReflectionTypeLoadException), which previously surfaced as
    // sporadic missing DbContext registrations and, in the worst case, a faulted host whose
    // IServiceProvider gets disposed before the test ever runs. A pre-load of the plugin
    // assemblies ahead of the first host build was tried and rejected: it did not remove the
    // race (still triggered widespread ObjectDisposedException/AggregateException failures) and
    // did not improve wall-clock time either, since BackendPluginExtensions re-scans on every
    // host build regardless of whether the assemblies are already resident. Serializing only the
    // build step (not test execution) removes the race while keeping full cross-class
    // parallelism: once a host is built, all of its requests/tests still run concurrently with
    // every other class's.
    private static readonly SemaphoreSlim HostBuildLock = new(1, 1);

    protected override IHost CreateHost(IHostBuilder builder)
    {
        HostBuildLock.Wait();
        try
        {
            return base.CreateHost(builder);
        }
        finally
        {
            HostBuildLock.Release();
        }
    }

    public static CustomWebApplicationFactory CreateWithIsolatedDatabase(bool useInMemorySqlite = true)
    {
        _ = useInMemorySqlite;

        // Tests expect a factory instance configured for an isolated DB.
        return new CustomWebApplicationFactory();
    }

    /// <summary>
    /// Connection string of this factory's isolated in-memory SQLite database. Split-deployment
    /// tests need it so a separate slicer-host test server can read the same profile tables.
    /// </summary>
    internal string TestConnectionString => _connectionString;

    /// <summary>
    /// Cleans up temporary directories created during test setup.
    /// </summary>
    public override async ValueTask DisposeAsync()
    {
        // Clean up temporary storage directories
        try
        {
            string? tempDir = Path.GetDirectoryName(_modelStoragePath);
            if (!string.IsNullOrEmpty(tempDir) && Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup errors (files might be locked)
        }

        try
        {
            _keepAliveConnection.Close();
            _keepAliveConnection.Dispose();
        }
        catch
        {
            // Ignore connection cleanup errors
        }

        await base.DisposeAsync();
    }

    /// <summary>
    /// Creates an authenticated HTTP client with a valid JWT bearer token.
    /// This should be used for testing endpoints that require [Authorize].
    /// </summary>
    public async Task<HttpClient> CreateAuthenticatedClientAsync(
        string username = "test-admin",
        string email = "test@example.com",
        string password = "TestPassword123!")
    {
        // Create test user
        using (AsyncServiceScope scope = Services.CreateAsyncScope())
        {
            AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            IPasswordHashingService passwordHasher = scope.ServiceProvider.GetRequiredService<IPasswordHashingService>();

            User? existingUser = await context.Users.FirstOrDefaultAsync(u => u.Username == username);
            if (existingUser == null)
            {
                var user = new User
                {
                    Id = Guid.NewGuid(),
                    Username = username,
                    Email = email,
                    PasswordHash = passwordHasher.HashPassword(password),
                    FirstName = "Test",
                    LastName = "Admin",
                    IsActive = true,
                    EmailConfirmed = true,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };
                context.Users.Add(user);
                await context.SaveChangesAsync();
            }
        }

        // Get token from authentication service
        using (AsyncServiceScope scope = Services.CreateAsyncScope())
        {
            IAuthenticationService authService = scope.ServiceProvider.GetRequiredService<IAuthenticationService>();
            AuthenticationResult result = await authService.AuthenticateAsync(username, password);

            HttpClient client = CreateClient();
            if (result.Success && !string.IsNullOrEmpty(result.Token))
            {
                client.DefaultRequestHeaders.Add("Authorization", $"Bearer {result.Token}");
            }
            return client;
        }
    }

    /// <summary>
    /// Clears all row data (but keeps the schema) across both <see cref="AppDbContext"/> and,
    /// when registered, <see cref="Farm.Slicer.Module.Data.SlicerDbContext"/>, then reseeds the
    /// baseline root folders. Intended for per-test isolation when a factory instance (and its
    /// host/schema) is shared across every test in a class via <c>IClassFixture</c> — unlike the
    /// old <c>ResetDatabaseAsync</c>, this does not drop/recreate the schema, so it is cheap
    /// enough to call before every test.
    /// </summary>
    public async Task ResetDataAsync()
    {
        await using AsyncServiceScope scope = Services.CreateAsyncScope();

        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await ClearAllTablesAsync(context.Database);

        // Slicer module may be disabled for this factory (no SlicerDbContext registered).
        Farm.Slicer.Module.Data.SlicerDbContext? slicerContext =
            scope.ServiceProvider.GetService<Farm.Slicer.Module.Data.SlicerDbContext>();
        if (slicerContext != null)
        {
            await ClearAllTablesAsync(slicerContext.Database);
        }

        // Re-establish the singleton rows normally provided by EF's HasData model seeding
        // (applied only once, at EnsureCreatedAsync/migration time) before re-seeding the
        // root folders — ClearAllTablesAsync wipes these rows just like any other data.
        await SeedSingletonModelDataAsync(context);

        // ClearAllTablesAsync also wipes the imperative reference/catalog data that
        // DatabaseInitializer.SeedAllAsync() establishes once at host startup (Resources,
        // Roles, RolePermissions, UserActions, Manufacturers, FilamentTypes, etc. — see
        // DatabaseInitializer.cs). Re-run it here so every test observes the same baseline
        // reference data a freshly-built host would have. Every Seed*Async step checks for
        // existing rows before inserting, so calling this repeatedly is safe.
        IDatabaseInitializer dbInitializer = scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>();
        await dbInitializer.SeedAllAsync();

        // Seed root folders for gcode and models to match production behavior
        await SeedRootFoldersAsync(context);
    }

    /// <summary>
    /// Deletes every row from every user table on the given SQLite database facade, leaving the
    /// schema intact. Table names come from <c>sqlite_master</c> (our own schema), not external
    /// input, so building the DELETE statements via interpolation is safe here.
    /// </summary>
    private static async Task ClearAllTablesAsync(Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade database)
    {
        System.Data.Common.DbConnection connection = database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        List<string> tables = new();
        using (System.Data.Common.DbCommand listCmd = connection.CreateCommand())
        {
            listCmd.CommandText =
                "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' AND name <> '__EFMigrationsHistory';";
            using System.Data.Common.DbDataReader reader = await listCmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                tables.Add(reader.GetString(0));
            }
        }

        if (tables.Count == 0)
        {
            return;
        }

        await database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;");
        try
        {
            foreach (string table in tables)
            {
                // Table names are read back from sqlite_master above (our own schema), never from
                // external/user input, and EF's parameterized ExecuteSqlAsync cannot bind a table
                // identifier as a parameter (it would quote it as a value, producing invalid SQL).
                // Raw interpolation into the DELETE statement is safe in this specific context.
#pragma warning disable EF1002
                await database.ExecuteSqlRawAsync($"DELETE FROM \"{table}\";");
#pragma warning restore EF1002
            }

            await database.ExecuteSqlRawAsync("DELETE FROM sqlite_sequence;");
        }
        finally
        {
            await database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON;");
        }
    }

    private async Task SeedRootFoldersAsync(AppDbContext context)
    {
        try
        {
            // Ensure root "/" folder exists for "gcode" category
            FolderNode? existingGcodeRoot = await context.Set<FolderNode>().AsNoTracking().FirstOrDefaultAsync(f => f.Path == "/" && f.FolderType == "gcode");
            if (existingGcodeRoot == null)
            {
                context.Set<FolderNode>().Add(new FolderNode
                {
                    Id = Guid.NewGuid(),
                    Path = "/",
                    FolderType = "gcode",
                    CreatedAt = DateTime.UtcNow
                });
            }

            // Ensure root "/" folder exists for "models" category
            FolderNode? existingModelsRoot = await context.Set<FolderNode>().AsNoTracking().FirstOrDefaultAsync(f => f.Path == "/" && f.FolderType == "models");
            if (existingModelsRoot == null)
            {
                context.Set<FolderNode>().Add(new FolderNode
                {
                    Id = Guid.NewGuid(),
                    Path = "/",
                    FolderType = "models",
                    CreatedAt = DateTime.UtcNow
                });
            }

            await context.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("UNIQUE constraint") == true)
        {
            // Folders already exist - this is fine, just continue
            context.ChangeTracker.Clear();
        }
    }

    /// <summary>
    /// Re-inserts the singleton rows that <c>AppDbContext.OnModelCreating</c> establishes via
    /// EF's <c>HasData</c> model seeding (<see cref="OutboxSequenceState"/>,
    /// <see cref="PasswordPolicyEntity"/>, <see cref="DispatchSettings"/>,
    /// <see cref="CalibrationChangeFeedState"/>, and <see cref="MutationCounter"/>).
    /// <c>HasData</c> seeding only runs once, at <c>EnsureCreatedAsync</c>/migration time, so
    /// <see cref="ClearAllTablesAsync"/> — which deletes every row, including these — must
    /// reseed them explicitly, matching each row's <c>HasData</c> values exactly.
    /// </summary>
    private async Task SeedSingletonModelDataAsync(AppDbContext context)
    {
        if (!await context.Set<OutboxSequenceState>().AsNoTracking().AnyAsync())
        {
            context.Set<OutboxSequenceState>().Add(new OutboxSequenceState { Id = 1, NextSequence = 0 });
        }

        if (!await context.Set<PasswordPolicyEntity>().AsNoTracking().AnyAsync())
        {
            context.Set<PasswordPolicyEntity>().Add(new PasswordPolicyEntity
            {
                Id = 1,
                MinLength = 8,
                RequireUppercase = false,
                RequireLowercase = false,
                RequireDigit = false,
                RequireSymbol = false,
                UpdatedAt = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            });
        }

        if (!await context.Set<DispatchSettings>().AsNoTracking().AnyAsync())
        {
            context.Set<DispatchSettings>().Add(new DispatchSettings());
        }

        if (!await context.Set<CalibrationChangeFeedState>().AsNoTracking().AnyAsync())
        {
            context.Set<CalibrationChangeFeedState>().Add(new CalibrationChangeFeedState { Id = 1, LastSequence = 0 });
        }

        if (!await context.Set<MutationCounter>().AsNoTracking().AnyAsync())
        {
            context.Set<MutationCounter>().Add(new MutationCounter());
        }

        await context.SaveChangesAsync();
    }

    // Generic mock helpers: use generic type parameter so callers with
    // Action<Mock<T>> lambdas will type-infer T correctly.
    public CustomWebApplicationFactory MockNetworkDiscoveryService<T>(Action<Mock<T>>? setup = null)
        where T : class
    {
        setup?.Invoke(new Mock<T>());
        return this;
    }

    public CustomWebApplicationFactory MockMoonrakerClient<T>(Action<Mock<T>>? setup = null)
        where T : class
    {
        setup?.Invoke(new Mock<T>());
        return this;
    }

    public CustomWebApplicationFactory MockPrusaLinkClient<T>(Action<Mock<T>>? setup = null)
        where T : class
    {
        setup?.Invoke(new Mock<T>());
        return this;
    }

    public CustomWebApplicationFactory MockSdcpClient<T>(Action<Mock<T>>? setup = null)
        where T : class
    {
        setup?.Invoke(new Mock<T>());
        return this;
    }

    public CustomWebApplicationFactory MockSlicerJobQueue<T>(Action<Mock<T>>? setup = null)
        where T : class
    {
        setup?.Invoke(new Mock<T>());
        return this;
    }

    public CustomWebApplicationFactory MockSlicerFileStorage<T>(Action<Mock<T>>? setup = null)
        where T : class
    {
        setup?.Invoke(new Mock<T>());
        return this;
    }

    public CustomWebApplicationFactory MockSlicerProgressNotifier<T>(Action<Mock<T>>? setup = null)
        where T : class
    {
        setup?.Invoke(new Mock<T>());
        return this;
    }

    public CustomWebApplicationFactory MockModelAnalysisService<T>(Action<Mock<T>>? setup = null)
        where T : class
    {
        setup?.Invoke(new Mock<T>());
        return this;
    }

    /// <summary>
    /// Creates an authenticated HTTP client with farm_admin role.
    /// Use for testing endpoints that require admin-only [RequirePermission(resource, "admin")]
    /// gates — the farm_admin role satisfies every permission check via
    /// PrintFarmerPermissions.IsFarmAdmin, so this remains valid after issue #1467 removed the
    /// role-backed "farm_admin"/"RequireAdmin" policy aliases those endpoints used to reference.
    /// </summary>
    public async Task<HttpClient> CreateAdminClientAsync(
        string username = "test-admin",
        string email = "test@example.com",
        string password = "TestPassword123!")
    {
        // Create admin user with farm_admin role
        using (AsyncServiceScope scope = Services.CreateAsyncScope())
        {
            AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            IPasswordHashingService passwordHasher = scope.ServiceProvider.GetRequiredService<IPasswordHashingService>();

            User? existingUser = await context.Users.FirstOrDefaultAsync(u => u.Username == username);
            if (existingUser == null)
            {
                var user = new User
                {
                    Id = Guid.NewGuid(),
                    Username = username,
                    Email = email,
                    PasswordHash = passwordHasher.HashPassword(password),
                    FirstName = "Test",
                    LastName = "Admin",
                    IsActive = true,
                    EmailConfirmed = true,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };
                context.Users.Add(user);
                await context.SaveChangesAsync();

                // Assign farm_admin role
                Role? adminRole = await context.Roles.FirstOrDefaultAsync(r => r.Name == "farm_admin");
                if (adminRole == null)
                {
                    // Create the farm_admin role if it doesn't exist
                    adminRole = new Role
                    {
                        Id = Guid.NewGuid(),
                        Name = "farm_admin",
                        Description = "Farm administrator",
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow
                    };
                    context.Roles.Add(adminRole);
                    await context.SaveChangesAsync();
                }

                context.UserRoles.Add(new UserRole
                {
                    UserId = user.Id,
                    RoleId = adminRole.Id,
                    IsActive = true,
                    AssignedAt = DateTime.UtcNow
                });
                await context.SaveChangesAsync();
            }
        }

        // Get token and create authenticated client
        using (AsyncServiceScope scope = Services.CreateAsyncScope())
        {
            IAuthenticationService authService = scope.ServiceProvider.GetRequiredService<IAuthenticationService>();
            AuthenticationResult result = await authService.AuthenticateAsync(username, password);

            HttpClient client = CreateClient();
            if (result.Success && !string.IsNullOrEmpty(result.Token))
            {
                client.DefaultRequestHeaders.Add("Authorization", $"Bearer {result.Token}");
            }
            return client;
        }
    }

    /// <summary>
    /// Creates an authenticated HTTP client with a valid worker API key.
    /// Use for testing endpoints that require both [Authorize] and worker key validation.
    /// </summary>
    public async Task<HttpClient> CreateWorkerClientAsync(
        string workerKey = "test-worker-key",
        string workerName = "Test Worker",
        string username = "test-worker-user",
        string email = "worker@example.com",
        string password = "WorkerPassword123!")
    {
        // Create the worker in the database
        await RegisterWorkerAsync(workerKey, workerName);

        // Get authenticated client and add worker key header
        HttpClient client = await CreateAuthenticatedClientAsync(username, email, password);
        client.DefaultRequestHeaders.Add("X-Worker-Key", workerKey);
        return client;
    }

    /// <summary>
    /// Registers a worker in the database with the given API key.
    /// Use this for tests that need a valid worker key but don't want the header set automatically.
    /// </summary>
    public async Task RegisterWorkerAsync(
        string workerKey = "test-worker-key",
        string workerName = "Test Worker")
    {
        using (AsyncServiceScope scope = Services.CreateAsyncScope())
        {
            AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            Worker? existingWorker = await context.Set<Worker>().FirstOrDefaultAsync(w => w.ApiKey == workerKey);
            if (existingWorker == null)
            {
                var worker = new Worker
                {
                    Id = Guid.NewGuid(),
                    ServiceId = $"worker-{Guid.NewGuid():N}",
                    Name = workerName,
                    EndpointUrl = "http://localhost:8080",
                    CapabilitiesJson = "[\"orcaslicer\"]",
                    Status = "online",
                    ApiKey = workerKey,
                    TotalSlots = 4,
                    ActiveJobs = 0,
                    LastHeartbeat = DateTime.UtcNow
                };
                context.Set<Worker>().Add(worker);
                await context.SaveChangesAsync();
            }
        }
    }
}
