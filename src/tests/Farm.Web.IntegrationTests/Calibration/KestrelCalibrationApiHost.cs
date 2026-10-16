using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Farm.Infrastructure.Data;
using Farm.Slicer.Module.Data;
using Farm.Testing.Shared;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Farm.Web.IntegrationTests.Calibration;

/// <summary>
/// Hosts the production API on a real Kestrel listener so a container can reach it over the loopback
/// interface it shares with the runner.
/// </summary>
/// <remarks>
/// <para>
/// Uses the factory's native Kestrel support to run one entry point on a real TCP port.
/// Both database contexts are migrated before building that entry point: a deferred host's Build
/// returns before Program finishes its explicit database initialization.
/// </para>
/// <para>
/// Nothing about the application pipeline is replaced. The only test-specific configuration is the
/// repository's own explicit test authentication for the human caller; the worker still has to present
/// production registration, worker and lease headers.
/// </para>
/// </remarks>
internal sealed class KestrelCalibrationApiHost : WebApplicationFactory<Program>
{
    private readonly SqliteConnection _keepAliveConnection;
    private readonly IReadOnlyDictionary<string, string?> _hostConfiguration;
    private readonly Action<SqliteConnection>? _beforeHostBuild;
    private int _disposed;

    private KestrelCalibrationApiHost(
        SqliteConnection keepAliveConnection,
        string workerSharedKey,
        string storageRoot,
        int listenPort,
        Action<SqliteConnection>? beforeHostBuild)
    {
        _keepAliveConnection = keepAliveConnection;
        _beforeHostBuild = beforeHostBuild;
        WorkerSharedKey = workerSharedKey;
        StorageRoot = storageRoot;
        ListenPort = listenPort;
        _hostConfiguration = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = keepAliveConnection.ConnectionString,
            ["ConnectionStrings:Sqlite"] = keepAliveConnection.ConnectionString,
            ["ConnectionStrings:DefaultConnection"] = keepAliveConnection.ConnectionString,
            ["ConnectionStrings:SlicerDatabase"] = keepAliveConnection.ConnectionString,
            ["TEST_USE_SQLITE_INMEMORY"] = "true",
            ["TEST_DISABLE_BACKGROUND_SERVICES"] = "true",
            ["DISABLE_TELEMETRY"] = "true",
            ["Testing:UseTestAuthentication"] = "true",
            ["Slicer:Enabled"] = "true",

            // The pinned worker registers with this shared key over the production registration route.
            ["WorkerAuth:SharedKey"] = workerSharedKey,
            ["STORAGE_PATHS:UPLOADS"] = Path.Join(storageRoot, "models"),
            ["STORAGE_PATHS:GCODE"] = Path.Join(storageRoot, "gcode"),

            // Kestrel binds loopback only. The worker container shares the runner's network namespace,
            // so the same address resolves inside the container without exposing anything externally.
            ["ASPNETCORE_URLS"] = string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{listenPort}"),
            ["urls"] = string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{listenPort}"),
        };
    }

    /// <summary>Gets the shared registration key the pinned worker must present.</summary>
    public string WorkerSharedKey { get; }

    /// <summary>Gets the isolated storage root that backs model and G-code storage.</summary>
    public string StorageRoot { get; }

    /// <summary>Gets the loopback TCP port the production pipeline listens on.</summary>
    public int ListenPort { get; }

    /// <summary>Gets the loopback base address a container on the runner's network can dial.</summary>
    public string BaseAddress => string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{ListenPort}");

    /// <summary>Gets the service provider of the host bound to <see cref="BaseAddress"/>.</summary>
    public IServiceProvider ListeningServices => Services;

    /// <summary>
    /// Starts the production pipeline on a free loopback port.
    /// </summary>
    /// <param name="storageRoot">Directory that backs model and G-code storage for this run.</param>
    /// <param name="beforeHostBuild">Optional assertion at the deferred entry-point build boundary.</param>
    /// <returns>The started host.</returns>
    public static KestrelCalibrationApiHost Start(
        string storageRoot,
        Action<SqliteConnection>? beforeHostBuild = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageRoot);
        _ = Directory.CreateDirectory(Path.Join(storageRoot, "models"));
        _ = Directory.CreateDirectory(Path.Join(storageRoot, "gcode"));

        SqliteConnection keepAlive = new(
            $"Data Source=file:orca_smoke_{Guid.NewGuid():N}?mode=memory&cache=shared;Pooling=False");
        keepAlive.Open();
        EnsureDatabaseMigrated(keepAlive);

        KestrelCalibrationApiHost host = new(
            keepAlive,
            $"orca-smoke-{Guid.NewGuid():N}",
            storageRoot,
            ReserveLoopbackPort(),
            beforeHostBuild);

        host.UseKestrel(options => options.Listen(IPAddress.Loopback, host.ListenPort));
        _ = host.Services;
        host.VerifyListening();
        return host;
    }

    /// <summary>Creates an HTTP client that dials the real listener rather than an in-memory server.</summary>
    /// <returns>A client bound to <see cref="BaseAddress"/>.</returns>
    public HttpClient CreateListeningClient() => new()
    {
        BaseAddress = new Uri(BaseAddress),
        Timeout = TimeSpan.FromMinutes(5),
    };

    /// <summary>
    /// Waits until the production pipeline answers its basic health probe on the real listener.
    /// </summary>
    /// <param name="timeout">How long to wait.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the listener is healthy.</returns>
    /// <exception cref="TimeoutException">Thrown when the listener never answered.</exception>
    public async Task WaitUntilHealthyAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using HttpClient client = CreateListeningClient();
        client.Timeout = TimeSpan.FromSeconds(10);
        DateTime deadline = DateTime.UtcNow + timeout;
        string lastFailure = "(never attempted)";
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using HttpResponseMessage response = await client.GetAsync(
                    new Uri("/healthz", UriKind.Relative),
                    cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }

                lastFailure = ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture);
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
                lastFailure = exception.GetType().Name;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }

        throw new TimeoutException(
            $"The calibration smoke host never became healthy on {BaseAddress} (last result: {lastFailure}).");
    }

    /// <inheritdoc/>
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        base.ConfigureWebHost(builder);

        // The human caller authenticates through the repository's own explicit test authentication
        // infrastructure. Worker authentication is untouched: the container still has to present the
        // production registration, worker and lease headers.
        _ = builder.ConfigureServices(services => services
            .AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                options.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                options.DefaultForbidScheme = TestAuthHandler.SchemeName;
            })
            .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
                TestAuthHandler.SchemeName,
                _ => { }));
    }

    /// <inheritdoc/>
    protected override IHost CreateHost(IHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Mvc.Testing's deferred builder turns host configuration into entry-point arguments, which is
        // the supported way to reach WebApplication.CreateBuilder(args) before Program decides anything.
        _ = builder.ConfigureHostConfiguration(config =>
            config.AddInMemoryCollection(_hostConfiguration));
        _ = builder.UseEnvironment("Testing");

        _beforeHostBuild?.Invoke(_keepAliveConnection);
        return base.CreateHost(builder);
    }

    /// <inheritdoc/>
    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            await base.DisposeAsync();
        }
        finally
        {
            await _keepAliveConnection.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }

    private void VerifyListening()
    {
        IServer server = ListeningServices.GetRequiredService<IServer>();
        if (server is Microsoft.AspNetCore.TestHost.TestServer)
        {
            throw new InvalidOperationException(
                "The calibration smoke host resolved an in-memory test server, which no container can " +
                "reach. Kestrel must be the registered server for the listening host.");
        }

        using TcpClient probe = new();
        SocketException? lastFailure = null;
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using TcpClient attempt = new();
                attempt.Connect(IPAddress.Loopback, ListenPort);
                return;
            }
            catch (SocketException exception)
            {
                lastFailure = exception;
                Thread.Sleep(250);
            }
        }

        throw new InvalidOperationException(
            $"The calibration smoke host is not accepting connections on {BaseAddress}.",
            lastFailure);
    }

    private static void EnsureDatabaseMigrated(SqliteConnection connection)
    {
        DbContextOptions<AppDbContext> coreOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection, options => options.MigrationsAssembly("Farm.Migrations.Sqlite"))
            .Options;
        using AppDbContext core = new(coreOptions);
        core.Database.Migrate();

        // Do not resolve host services here: Build already releases Program to validate the schema.
        DbContextOptions<SlicerDbContext> slicerOptions = new DbContextOptionsBuilder<SlicerDbContext>()
            .UseSqlite(connection, options => options.MigrationsAssembly("Farm.Slicer.Migrations.Sqlite"))
            .Options;
        using SlicerDbContext slicer = new(slicerOptions);
        slicer.Database.Migrate();
    }

    /// <summary>
    /// Reserves a free loopback port by binding and releasing it.
    /// </summary>
    /// <returns>A port number that was free a moment ago.</returns>
    private static int ReserveLoopbackPort()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        try
        {
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
            listener.Dispose();
        }
    }
}
