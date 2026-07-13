using System.Data.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.Idempotency;

/// <summary>
/// Background service that periodically prunes expired
/// <see cref="Farm.Infrastructure.Domain.IdempotencyRecord"/> rows so the
/// idempotency table does not grow unbounded (issue #715).
///
/// <para>
/// Runs the prune under a fresh <c>IServiceScope</c> per tick so it does not
/// hold a scoped <c>AppDbContext</c> for the process lifetime. Failures are
/// logged and swallowed — a transient DB outage must not tear down the host —
/// and the next tick tries again. The store's prune predicate is a single
/// bulk DELETE, safe under concurrent instances (kubernetes replicas / dev + prod).
/// </para>
///
/// <para>
/// The interval is intentionally coarse (1 hour) because the 7-day retention
/// window is large; the read path already ignores expired rows so freshness of
/// deletion is a housekeeping concern, not a correctness one.
/// </para>
/// </summary>
public sealed class IdempotencyRecordCleanupService : BackgroundService
{
    /// <summary>Default interval between prune sweeps.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromHours(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<IdempotencyRecordCleanupService> _logger;
    private readonly TimeSpan _interval;

    /// <summary>Constructs the cleanup service with the default interval.</summary>
    public IdempotencyRecordCleanupService(
        IServiceScopeFactory scopeFactory,
        ILogger<IdempotencyRecordCleanupService> logger)
        : this(scopeFactory, logger, DefaultInterval)
    {
    }

    /// <summary>Constructs the cleanup service with an explicit interval (for tests).</summary>
    public IdempotencyRecordCleanupService(
        IServiceScopeFactory scopeFactory,
        ILogger<IdempotencyRecordCleanupService> logger,
        TimeSpan interval)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _interval = interval > TimeSpan.Zero ? interval : DefaultInterval;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "IdempotencyRecordCleanupService starting (interval={Interval}).",
            _interval);

        // Small startup delay so the app can finish booting before the first sweep.
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            await RunOnceAsync(stoppingToken);

            try
            {
                await Task.Delay(_interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Executes a single prune pass under a fresh scope. Exposed for tests so
    /// they do not need to wait on the timer.
    /// </summary>
    internal async Task RunOnceAsync(CancellationToken ct)
    {
        try
        {
            await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
            IIdempotencyStore store = scope.ServiceProvider.GetRequiredService<IIdempotencyStore>();
            int removed = await store.PruneExpiredAsync(DateTime.UtcNow, ct);
            if (removed > 0)
            {
                _logger.LogInformation("Idempotency prune removed {Count} expired records.", removed);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Graceful shutdown; suppress.
        }
        catch (DbException ex)
        {
            // Transient database failures (outage, timeout, transient connection
            // error) must not tear down the host — log and retry on the next tick.
            // Non-DbException failures are deliberately NOT caught here so genuine
            // programmer errors surface via the BackgroundService pipeline instead
            // of being silently swallowed every hour.
            _logger.LogError(ex, "Idempotency prune sweep failed; will retry on the next tick.");
        }
    }
}
