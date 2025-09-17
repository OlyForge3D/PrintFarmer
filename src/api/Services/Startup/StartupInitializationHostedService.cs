using Farm.Web.Api.Configuration;
using Farm.Web.Api.Data;
using Microsoft.Extensions.Options;
using System.Diagnostics.Metrics;

namespace Farm.Web.Api.Services.Startup;

/// <summary>
/// Hosted service responsible for performing startup initialization tasks (database initialization,
/// configuration validation and authentication seeding) asynchronously <em>after</em> Kestrel has begun
/// listening. This design allows liveness endpoints to respond quickly while heavier work proceeds
/// in the background. Readiness is exposed via <see cref="StartupStatus"/>.
/// </summary>
/// <remarks>
/// <para>
/// The service intentionally returns from <see cref="StartAsync"/> immediately (fire-and-forget pattern)
/// so that the ASP.NET Core host can finish binding to its ports without being blocked by database or
/// migration related work. A timeout (default 90s) guards against indefinite hangs; on timeout or any
/// unhandled exception the startup status is marked failed allowing readiness / health probes
/// to surface the failure.
/// </para>
/// <para>
/// Retry behaviour is delegated to <see cref="DatabaseInitializer"/> and is parameterised via the optional
/// environment variables <c>DB_CONNECTION_RETRY_COUNT</c> and <c>DB_CONNECTION_RETRY_DELAY</c> (seconds).
/// </para>
/// <para>
/// Authentication / role seeding is idempotent; repeated executions are safe. No further catalog / sample
/// data seeding is performed here to minimise startup latency.
/// </para>
/// <para>
/// Thread Safety: the service uses an internal linked <see cref="CancellationTokenSource"/> to signal
/// cancellation on host shutdown. No mutable shared state is exposed; readiness flags are written once on
/// success or failure paths.
/// </para>
/// </remarks>
public class StartupInitializationHostedService : IHostedService
{
    private readonly IServiceProvider _root;
    private readonly ILogger<StartupInitializationHostedService> _logger;
    private readonly StartupStatus _status;
    private readonly TimeSpan _overallTimeout = TimeSpan.FromSeconds(90);
    private readonly Histogram<double> _initDurationHistogram;
    private readonly Counter<long> _initSuccessCounter;
    private readonly Counter<long> _initFailureCounter;
    private readonly ObservableGauge<int> _phaseGauge; // referenced via observable delegate
    private CancellationTokenSource? _cts;

    /// <summary>
    /// Creates a new <see cref="StartupInitializationHostedService"/>.
    /// </summary>
    /// <param name="root">Root service provider used to create a scoped provider for initialization.</param>
    /// <param name="logger">Logger for diagnostic and progress events.</param>
    /// <param name="status">Shared status object updated with readiness / failure outcome.</param>
    /// <param name="meterFactory">Factory used to create OpenTelemetry metrics instruments.</param>
    /// <param name="env">Host environment (reserved for future conditional behavior).</param>
    public StartupInitializationHostedService(
        IServiceProvider root,
        ILogger<StartupInitializationHostedService> logger,
        StartupStatus status,
        IMeterFactory meterFactory,
        IHostEnvironment env)
    {
        _root = root;
        _logger = logger;
        _status = status;

        var meter = meterFactory.Create("Farm.Web.Api.Startup");
        _initDurationHistogram = meter.CreateHistogram<double>("startup.initialization.duration.ms", unit: "ms", description: "Duration of startup initialization");
        _initSuccessCounter = meter.CreateCounter<long>("startup.initialization.success.count", description: "Count of successful startup initializations");
        _initFailureCounter = meter.CreateCounter<long>("startup.initialization.failure.count", description: "Count of failed startup initializations");
        _phaseGauge = meter.CreateObservableGauge<int>("startup.phase", () => new Measurement<int>((int)_status.Phase), description: "Current startup phase enum value (0=Starting,1=Ready,2=Failed)");
    }

    /// <summary>
    /// Schedules background initialization work and returns immediately. Heavy work MUST NOT be awaited here
    /// to avoid delaying web host port binding.
    /// </summary>
    /// <param name="cancellationToken">Host shutdown token.</param>
    /// <returns>Completed task (work continues in background).</returns>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Fire-and-forget background init so host can bind immediately
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _cts.CancelAfter(_overallTimeout);
        var token = _cts.Token;
        _status.MarkInitializationStarted();
        _ = Task.Run(() => RunInitializationAsync(token), CancellationToken.None);
        _logger.LogInformation("[StartupInit] Background initialization scheduled (timeout {Timeout}s)", _overallTimeout.TotalSeconds);
        return Task.CompletedTask; // DO NOT await heavy work here
    }

    /// <summary>
    /// Executes the initialization workflow inside a DI scope. On success marks the status ready;
    /// on failure (exception or timeout / cancellation) marks the status failed if readiness
    /// was not already achieved.
    /// </summary>
    /// <param name="token">Cancellation token incorporating both host shutdown and service timeout.</param>
    private async Task RunInitializationAsync(CancellationToken token)
    {
        var start = DateTime.UtcNow;
        try
        {
            using var scope = _root.CreateScope();
            var services = scope.ServiceProvider;

            _logger.LogInformation("[StartupInit] Initialization started (async)");
            var configurationValidator = services.GetRequiredService<ConfigurationValidator>();
            var dbInitializer = services.GetRequiredService<DatabaseInitializer>();
            var dbSettings = services.GetRequiredService<IOptions<DatabaseSettings>>().Value;

            var retryCount = int.TryParse(Environment.GetEnvironmentVariable("DB_CONNECTION_RETRY_COUNT"), out var rc) ? rc : 3;
            var retryDelay = int.TryParse(Environment.GetEnvironmentVariable("DB_CONNECTION_RETRY_DELAY"), out var rd) ? rd : 2;

            await dbInitializer.InitializeAsync(dbSettings.Provider, retryCount, retryDelay);
            configurationValidator.ValidateConfiguration();

            var db = services.GetRequiredService<AppDbContext>();
            await Data.Seed.AuthenticationDataSeeder.SeedAsync(db);

            _status.MarkReady();
            var elapsedMs = (DateTime.UtcNow - start).TotalMilliseconds;
            _initDurationHistogram.Record(elapsedMs, KeyValuePair.Create<string, object?>("outcome", "success"));
            _initSuccessCounter.Add(1);
            _logger.LogInformation("[StartupInit] Initialization succeeded in {Elapsed} ms", (DateTime.UtcNow - start).TotalMilliseconds);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (!_status.IsReady)
            {
                _status.MarkFailed();
                _initFailureCounter.Add(1);
                if (_status.InitializationDuration is TimeSpan d1)
                {
                    _initDurationHistogram.Record(d1.TotalMilliseconds, KeyValuePair.Create<string, object?>("outcome", "canceled"));
                }
                _logger.LogCritical("[StartupInit] Initialization canceled or timed out after {Timeout}s", _overallTimeout.TotalSeconds);
            }
        }
        catch (Exception ex)
        {
            _status.MarkFailed(ex);
            _initFailureCounter.Add(1);
            if (_status.InitializationDuration is TimeSpan d2)
            {
                _initDurationHistogram.Record(d2.TotalMilliseconds, KeyValuePair.Create<string, object?>("outcome", "failed"));
            }
            _logger.LogCritical(ex, "[StartupInit] Initialization failed: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Signals cancellation to any in‑flight initialization task (asynchronously) and disposes internal resources.
    /// </summary>
    /// <param name="cancellationToken">Host shutdown token (best-effort; cancellation is immediate).</param>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cts != null && !_cts.IsCancellationRequested)
        {
            try
            {
                await _cts.CancelAsync();
            }
            catch { /* ignore */ }
        }
        _cts?.Dispose();
    }
}
