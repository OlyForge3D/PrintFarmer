using System.Diagnostics.Metrics;
using Farm.Web.Api.Configuration;
using Farm.Infrastructure.Data;
using Microsoft.Extensions.Options;
using Farm.Infrastructure.Telemetry;

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
    private readonly IServiceScopeFactory _scopeFactory;
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
    /// <param name="scopeFactory">Scope factory for creating DI scopes during initialization.</param>
    /// <param name="status">Shared status object updated with readiness / failure outcome.</param>
    /// <param name="meterFactory">Factory used to create OpenTelemetry metrics instruments.</param>
    /// <param name="env">Host environment (reserved for future conditional behavior).</param>
    public StartupInitializationHostedService(
        IServiceProvider root,
        IServiceScopeFactory scopeFactory,
        StartupStatus status,
        IMeterFactory meterFactory,
        IHostEnvironment env)
    {
        _root = root;
        _scopeFactory = scopeFactory;
        _status = status;

        Meter meter = meterFactory.Create("Farm.Web.Api.Startup");
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
        CancellationToken token = _cts.Token;
        _status.MarkInitializationStarted();
        _ = Task.Run(() => RunInitializationAsync(token), CancellationToken.None);
        using (var scope = _scopeFactory.CreateScope())
        {
            var logger = scope.ServiceProvider.GetRequiredService<IUnifiedLoggingService>();
            logger.LogInformation($"[StartupInit] Background initialization scheduled (timeout {_overallTimeout.TotalSeconds}s)");
        }
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
        DateTime start = DateTime.UtcNow;
        try
        {
            using IServiceScope scope = _root.CreateScope();
            IServiceProvider services = scope.ServiceProvider;

            var logger = services.GetRequiredService<IUnifiedLoggingService>();
            logger.LogInformation("[StartupInit] Initialization started (async)");
            ConfigurationValidator configurationValidator = services.GetRequiredService<ConfigurationValidator>();
            DatabaseInitializer dbInitializer = services.GetRequiredService<DatabaseInitializer>();
            DatabaseSettings dbSettings = services.GetRequiredService<IOptions<DatabaseSettings>>().Value;

            int retryCount = int.TryParse(Environment.GetEnvironmentVariable("DB_CONNECTION_RETRY_COUNT"), out int rc) ? rc : 3;
            int retryDelay = int.TryParse(Environment.GetEnvironmentVariable("DB_CONNECTION_RETRY_DELAY"), out int rd) ? rd : 2;

            await dbInitializer.InitializeAsync(dbSettings.Provider, retryCount, retryDelay);
            configurationValidator.ValidateConfiguration();

            AppDbContext db = services.GetRequiredService<AppDbContext>();
            await Farm.Web.Api.Data.Seed.AuthenticationDataSeeder.SeedAsync(db);

            _status.MarkReady();
            double elapsedMs = (DateTime.UtcNow - start).TotalMilliseconds;
            _initDurationHistogram.Record(elapsedMs, KeyValuePair.Create<string, object?>("outcome", "success"));
            _initSuccessCounter.Add(1);
            var successLogger = services.GetRequiredService<IUnifiedLoggingService>();
            successLogger.LogInformation($"[StartupInit] Initialization succeeded in {elapsedMs} ms");
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
                using (var scope = _scopeFactory.CreateScope())
                {
                    var logger = scope.ServiceProvider.GetRequiredService<IUnifiedLoggingService>();
                    logger.LogCritical($"[StartupInit] Initialization canceled or timed out after {_overallTimeout.TotalSeconds}s");
                }
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
            using (var scope = _scopeFactory.CreateScope())
            {
                var logger = scope.ServiceProvider.GetRequiredService<IUnifiedLoggingService>();
                logger.LogCritical(ex, $"[StartupInit] Initialization failed: {ex.Message}");
            }
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
