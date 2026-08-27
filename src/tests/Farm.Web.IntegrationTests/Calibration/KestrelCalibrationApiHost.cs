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
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
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
/// <see cref="WebApplicationFactory{TEntryPoint}"/> always installs an in-memory <c>TestServer</c>,
/// whose address no container can dial. This host therefore uses the documented two-host arrangement:
/// the factory keeps its in-memory host so its own internals stay satisfied, while the host this class
/// exposes is a second, identically configured instance bound to a real TCP port. Both instances share
/// one SQLite database and one storage root, so the smoke asserts on exactly what the container talked to.
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
    private IHost? _kestrelHost;
    private int _disposed;

    private KestrelCalibrationApiHost(
        SqliteConnection keepAliveConnection,
        string workerSharedKey,
        string storageRoot,
        int listenPort)
    {
        _keepAliveConnection = keepAliveConnection;
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
    public IServiceProvider ListeningServices => _kestrelHost?.Services
        ?? throw new InvalidOperationException("The Kestrel host has not been started yet.");

    /// <summary>
    /// Starts the production pipeline on a free loopback port.
    /// </summary>
    /// <param name="storageRoot">Directory that backs model and G-code storage for this run.</param>
    /// <returns>The started host.</returns>
    public static KestrelCalibrationApiHost Start(string storageRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageRoot);
        _ = Directory.CreateDirectory(Path.Join(storageRoot, "models"));
        _ = Directory.CreateDirectory(Path.Join(storageRoot, "gcode"));

        SqliteConnection keepAlive = new(
            $"Data Source=file:orca_smoke_{Guid.NewGuid():N}?mode=memory&cache=shared;Pooling=False");
        keepAlive.Open();

        KestrelCalibrationApiHost host = new(
            keepAlive,
            $"orca-smoke-{Guid.NewGuid():N}",
            storageRoot,
            ReserveLoopbackPort());

        // Touching Services forces WebApplicationFactory to build both hosts.
        _ = host.Services;
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

        // Build the in-memory host first, while TestServer is still the registered server.
        IHost inMemoryHost = builder.Build();

        // Then add Kestrel and build the second host. The endpoint is bound explicitly so the listener
        // never depends on URL configuration reaching the entry point, and its IServer registration
        // wins because it is applied after the factory's TestServer registration.
        _ = builder.ConfigureWebHost(webHost => webHost
            .UseKestrel(options => options.Listen(IPAddress.Loopback, ListenPort)));
        _kestrelHost = builder.Build();
        _kestrelHost.Start();

        VerifyListening(_kestrelHost);
        EnsureDatabaseCreated(_kestrelHost);

        inMemoryHost.Start();
        return inMemoryHost;
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
            if (_kestrelHost is not null)
            {
                await _kestrelHost.StopAsync(TimeSpan.FromSeconds(15));
                _kestrelHost.Dispose();
            }
        }
        finally
        {
            await base.DisposeAsync();
            await _keepAliveConnection.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }

    private void VerifyListening(IHost host)
    {
        IServer server = host.Services.GetRequiredService<IServer>();
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

    private static void EnsureDatabaseCreated(IHost host)
    {
        using IServiceScope scope = host.Services.CreateScope();
        AppDbContext core = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        _ = core.Database.EnsureCreated();

        // Both contexts share one database, so EnsureCreated on the slicer context is a no-op once the
        // core tables exist. Creating the slicer tables explicitly keeps the artifact, worker and job
        // hops routable, which is exactly what the capability probe reports on.
        SlicerDbContext slicer = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();
        try
        {
            ((IInfrastructure<IServiceProvider>)slicer).Instance
                .GetRequiredService<IRelationalDatabaseCreator>()
                .CreateTables();
        }
        catch (SqliteException)
        {
            // The tables already exist because the sibling host created them first.
        }
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
