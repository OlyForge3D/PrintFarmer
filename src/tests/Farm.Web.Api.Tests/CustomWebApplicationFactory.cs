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
        // "Default Timeout" sets SQLite's busy_timeout: this factory opens several independent
        // connections against the same shared in-memory database during startup (a throwaway
        // ServiceProvider that runs EnsureCreated/CreateTables here, and the real host's own
        // SlicerDbInitializationHostedService migration once it starts). Under the heavy CPU
        // contention this suite now runs under with the parallelism cap lifted, those can be
        // slow enough to genuinely overlap, and SQLite's in-memory journal takes an exclusive
        // write lock — without a busy timeout, a second writer fails immediately with
        // "SQLite Error 5: database is locked" instead of waiting the (very short) real time it
        // takes for the first writer's transaction to complete.
        // "Pooling=False" disables Microsoft.Data.Sqlite's internal connection pool for this
        // connection string. AppDbContext and SlicerDbContext are both configured with the exact
        // same connection string, so with pooling enabled they draw from the same pool key; a
        // pooled connection handed back to one context while a statement from the other context
        // is still active (e.g. mid-migration) can make SQLite reject a subsequent collation
        // registration with "SQLite Error 5: unable to delete/modify collation sequence due to
        // active statements". Disabling pooling forces every Open() to create a genuinely
        // distinct native connection handle (all still attached to the same shared in-memory
        // database via cache=shared), which removes that cross-context reuse hazard.
        _keepAliveConnection = new SqliteConnection(
            $"Data Source=file:farm_test_{dbId}?mode=memory&cache=shared;Default Timeout=30;Pooling=False");
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
    //
    // Permit count is 1 (fully serial host builds), not tuned upward for wall-clock: raising it
    // was tried and reverted. BackendPluginExtensions.DiscoverAndLoadPlugins (product code, out
    // of scope for this test-only change) wraps Assembly.GetTypes() in a catch that silently
    // swallows ReflectionTypeLoadException rather than salvaging the types that DID load, so a
    // partial-load race under >1 concurrent callers can silently drop plugin registrations
    // instead of failing loudly — there is no way to prove that is safe for any N > 1 without
    // reading or changing that product code, which this PR must not do. Passing empirically
    // under a higher permit count for a bounded number of runs is not proof of safety for a race
    // like this one; only permit=1 removes the race entirely. This does cost wall-clock (measured
    // ~12 minutes vs. the ~11 minute target) but correctness takes priority — do not raise this
    // without first eliminating the underlying product-code race.
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

        // Tables backing the singleton config/state rows normally provided by EF's HasData
        // model seeding (applied once, at EnsureCreatedAsync/migration time). These are
        // excluded from the blanket DELETE below and reset in place instead — see
        // ResetSingletonModelDataAsync for why.
        await ClearAllTablesAsync(context.Database, excludedTables: SingletonTableNames);

        // The ~20 hosted services this factory keeps alive across an entire test class
        // (AutoDispatchBackgroundService, CatalogUpdateDetectionService, etc. — see the
        // BackgroundServiceExceptionBehavior.Ignore comment in ConfigureWebHost) query
        // singleton config rows like DispatchSettings on every poll cycle, often with no
        // defensive try/catch around a plain FirstAsync()/SingleAsync() (e.g.
        // AutoDispatchBackgroundService.ReconcileStartupEligiblePrintersAsync). An earlier
        // version of this method deleted every row (including these singletons) via
        // ClearAllTablesAsync and then re-inserted them via a separate SaveChangesAsync call,
        // leaving a genuine window where such a query could observe an empty table and throw
        // — an exception that BackgroundServiceExceptionBehavior.Ignore then treats as fatal
        // to that ONE hosted service for the rest of the shared host's lifetime, silently
        // degrading every later test in the class. Wrapping that delete-then-reinsert in a
        // single transaction was tried and rejected: SQLite's shared-cache locking (this
        // factory's databases all use `cache=shared`) does not always retry lock conflicts
        // via the busy-timeout handler the way file-level BUSY locks do, so holding one
        // connection's write lock across every table for the whole clear+reseed measurably
        // increased 500s from concurrent requests/background services hitting SQLITE_LOCKED.
        // Excluding these tables from the DELETE and resetting each row to its default values
        // in place (UPDATE, or INSERT only on the very first call before any row exists)
        // removes the window entirely — the row is simply never absent — without holding any
        // lock longer than a single-row write.
        await ResetSingletonModelDataAsync(context);

        // Slicer module may be disabled for this factory (no SlicerDbContext registered).
        // Both contexts point at the SAME physical shared-cache SQLite database (see the
        // `cache=shared` connection string in the constructor), so sqlite_master — and
        // therefore ClearAllTablesAsync's table enumeration — sees every table regardless of
        // which DbContext's connection queries it. The singleton tables must be excluded here
        // too, or this call silently re-deletes the very rows ResetSingletonModelDataAsync
        // already reset above.
        Farm.Slicer.Module.Data.SlicerDbContext? slicerContext =
            scope.ServiceProvider.GetService<Farm.Slicer.Module.Data.SlicerDbContext>();
        if (slicerContext != null)
        {
            await ClearAllTablesAsync(slicerContext.Database, excludedTables: SingletonTableNames);
        }

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
    /// Table names backing singleton config/state rows established once by EF's HasData model
    /// seeding. <see cref="ClearAllTablesAsync"/> excludes these from its blanket per-table
    /// DELETE for <see cref="AppDbContext"/>; <see cref="ResetSingletonModelDataAsync"/> resets
    /// each row to its default values in place instead of deleting and re-inserting it, so no
    /// concurrent reader (e.g. a hosted background service) ever observes the table empty. See
    /// the comment in <see cref="ResetDataAsync"/> for the concurrency hazard this avoids.
    /// </summary>
    private static readonly IReadOnlySet<string> SingletonTableNames = new HashSet<string>(
        StringComparer.OrdinalIgnoreCase)
    {
        "OutboxSequenceStates",
        "PasswordPolicies",
        "DispatchSettings",
        "CalibrationChangeFeedStates",
        "MutationCounters",
    };

    /// <summary>
    /// Deletes every row from every user table on the given SQLite database facade (except any
    /// named in <paramref name="excludedTables"/>), leaving the schema intact. Table names come
    /// from <c>sqlite_master</c> (our own schema), not external input, so building the DELETE
    /// statements via interpolation is safe here.
    /// </summary>
    private static async Task ClearAllTablesAsync(
        Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade database,
        IReadOnlySet<string>? excludedTables = null)
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
                string table = reader.GetString(0);
                if (excludedTables == null || !excludedTables.Contains(table))
                {
                    tables.Add(table);
                }
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
    /// Resets the singleton rows that <c>AppDbContext.OnModelCreating</c> establishes via EF's
    /// <c>HasData</c> model seeding (<see cref="OutboxSequenceState"/>,
    /// <see cref="PasswordPolicyEntity"/>, <see cref="DispatchSettings"/>,
    /// <see cref="CalibrationChangeFeedState"/>, and <see cref="MutationCounter"/>) to their
    /// default values, in place. <see cref="ClearAllTablesAsync"/> excludes each of these
    /// tables (see <see cref="SingletonTableNames"/>) from its blanket DELETE specifically so
    /// this method can reset the existing row via UPDATE rather than delete-then-reinsert — see
    /// <see cref="ResetDataAsync"/> for why an empty-table window here is a real hazard, not a
    /// theoretical one. A row is only ever inserted here on the very first call against a fresh
    /// database, before <c>HasData</c>'s own seeding would otherwise apply.
    /// </summary>
    private static async Task ResetSingletonModelDataAsync(AppDbContext context)
    {
        await ResetSingletonRowAsync(context, new OutboxSequenceState { Id = 1, NextSequence = 0 });
        await ResetSingletonRowAsync(context, new PasswordPolicyEntity
        {
            Id = 1,
            MinLength = 8,
            RequireUppercase = false,
            RequireLowercase = false,
            RequireDigit = false,
            RequireSymbol = false,
            UpdatedAt = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        });
        await ResetSingletonRowAsync(context, new DispatchSettings());
        await ResetSingletonRowAsync(context, new CalibrationChangeFeedState { Id = 1, LastSequence = 0 });
        await ResetSingletonRowAsync(context, new MutationCounter());

        await context.SaveChangesAsync();

        // RevisionConcurrency.Advance() (invoked unconditionally from AppDbContext.SaveChangesAsync
        // above, product code) forces every Modified IRevisionedEntity's Revision to its true
        // persisted OriginalValue + 1, overwriting whatever value SetValues assigned above — so
        // without this follow-up, DispatchSettings/OutboxSequenceState's Revision would climb by
        // one on every reset instead of returning to the intended baseline (1). Forcing
        // OriginalValue instead was tried and reverted: EF also uses a concurrency token's
        // OriginalValue as the UPDATE's WHERE-clause match against the real row, so overriding it
        // to anything other than the row's true prior value makes the WHERE clause miss and throws
        // DbUpdateConcurrencyException ("expected to affect 1 row(s), but actually affected 0").
        // ExecuteUpdateAsync issues a direct SQL UPDATE that bypasses change tracking (and
        // therefore Advance() and the concurrency check) entirely, so it can force Revision back
        // to baseline without fighting the automatic +1 or requiring any original-value match.
        await ResetRevisionAsync<DispatchSettings>(context, 1);
        await ResetRevisionAsync<OutboxSequenceState>(context, 1);
    }

    /// <summary>
    /// Forces every row of <typeparamref name="TEntity"/> to <paramref name="revision"/> via a
    /// direct SQL UPDATE (EF Core's <c>ExecuteUpdateAsync</c>), bypassing change tracking so
    /// <see cref="RevisionConcurrency.Advance"/> never sees — and therefore never overwrites —
    /// this write. Safe to call unconditionally for the two revisioned singleton tables: each has
    /// exactly one row at this point in <see cref="ResetSingletonModelDataAsync"/>.
    /// </summary>
    private static Task ResetRevisionAsync<TEntity>(AppDbContext context, long revision)
        where TEntity : class, IRevisionedEntity
        => context.Set<TEntity>().ExecuteUpdateAsync(setters => setters.SetProperty(e => e.Revision, revision));

    /// <summary>
    /// Resets the single row of a singleton entity type to <paramref name="defaults"/>: updates
    /// every scalar property of the existing tracked row in place if one exists, or inserts
    /// <paramref name="defaults"/> as a new row otherwise. Using <c>CurrentValues.SetValues</c>
    /// (rather than restating each property assignment by hand) keeps this in sync automatically
    /// as each entity type gains or loses properties. See <see cref="ResetSingletonModelDataAsync"/>
    /// for why <see cref="IRevisionedEntity"/> rows need an additional follow-up step beyond this
    /// method to actually land on their intended baseline <c>Revision</c>.
    /// </summary>
    private static async Task ResetSingletonRowAsync<TEntity>(AppDbContext context, TEntity defaults)
        where TEntity : class
    {
        TEntity? existing = await context.Set<TEntity>().FirstOrDefaultAsync();
        if (existing == null)
        {
            context.Set<TEntity>().Add(defaults);
        }
        else
        {
            context.Entry(existing).CurrentValues.SetValues(defaults);
        }
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
