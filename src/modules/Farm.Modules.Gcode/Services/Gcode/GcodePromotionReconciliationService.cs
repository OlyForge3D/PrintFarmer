using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Farm.Web.Api.Services.Gcode;

/// <summary>
/// Shared health of the promotion reconciler.
/// </summary>
/// <remarks>
/// Registration alone proves the reconciler is wired; consecutive failures prove it cannot currently
/// resolve unknown outcomes, which must make the promotion capability false rather than optimistic.
/// </remarks>
public sealed class GcodePromotionReconcilerState
{
    private const int UnhealthyFailureThreshold = 3;

    private int _consecutiveFailures;

    /// <summary>Gets whether the reconciler can currently resolve outstanding promotions.</summary>
    public bool IsHealthy => Volatile.Read(ref _consecutiveFailures) < UnhealthyFailureThreshold;

    /// <summary>Gets the UTC timestamp of the last completed reconciliation pass.</summary>
    public DateTime? LastRunAtUtc { get; private set; }

    /// <summary>Records a successful reconciliation pass.</summary>
    /// <param name="completedAtUtc">The UTC completion timestamp.</param>
    public void RecordSuccess(DateTime completedAtUtc)
    {
        LastRunAtUtc = completedAtUtc;
        _ = Interlocked.Exchange(ref _consecutiveFailures, 0);
    }

    /// <summary>Records a failed reconciliation pass.</summary>
    public void RecordFailure() => _ = Interlocked.Increment(ref _consecutiveFailures);
}

/// <summary>
/// Resolves promotion checkpoints whose outcome was unknown when the process stopped, and re-attempts
/// source acknowledgements that never landed.
/// </summary>
public sealed class GcodePromotionReconciliationService(
    IServiceScopeFactory scopeFactory,
    GcodePromotionReconcilerState state,
    ILogger<GcodePromotionReconciliationService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan FailureBackoff = TimeSpan.FromSeconds(30);

    /// <summary>Lets database initialization finish before the first reconciliation pass.</summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(15);

    private const int MaxCheckpointsPerPass = 50;

    private readonly IServiceScopeFactory _scopeFactory =
        scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));

    private readonly GcodePromotionReconcilerState _state = state ?? throw new ArgumentNullException(nameof(state));

    private readonly ILogger<GcodePromotionReconciliationService> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        TimeSpan nextInterval = StartupDelay;
        while (!stoppingToken.IsCancellationRequested)
        {
            if (nextInterval > TimeSpan.Zero)
            {
                await Task.Delay(nextInterval, stoppingToken);
            }

            try
            {
                using IServiceScope scope = _scopeFactory.CreateScope();
                IGcodeArtifactPromoter promoter = scope.ServiceProvider
                    .GetRequiredService<IGcodeArtifactPromoter>();
                int resolved = await promoter.ReconcilePendingAsync(MaxCheckpointsPerPass, stoppingToken);
                if (resolved > 0)
                {
                    _logger.LogInformation(
                        "Resolved {PromotionCount} outstanding G-code promotions.",
                        resolved);
                }

                _state.RecordSuccess(DateTime.UtcNow);
                nextInterval = PollInterval;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _state.RecordFailure();
                _logger.LogWarning(
                    exception,
                    "G-code promotion reconciliation failed and will be retried.");
                nextInterval = FailureBackoff;
            }
        }
    }
}
