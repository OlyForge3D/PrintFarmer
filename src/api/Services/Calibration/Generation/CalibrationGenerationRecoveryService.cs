namespace Farm.Web.Api.Services.Calibration.Generation;

/// <summary>
/// Resumes calibration generation runs that were interrupted by a restart, a transient outage or a
/// worker that has not reported yet.
/// </summary>
/// <remarks>
/// The loop only touches rows whose next attempt is due and whose lease has lapsed, and every pass goes
/// through the same durable saga the request path uses. Two hosts running this service therefore cannot
/// process the same orchestration at once: the first lease write wins on the orchestration's optimistic
/// concurrency token and the second host skips the row.
/// </remarks>
public sealed class CalibrationGenerationRecoveryService(
    IServiceScopeFactory scopeFactory,
    CalibrationGenerationRecoveryState state,
    ILogger<CalibrationGenerationRecoveryService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FailureBackoff = TimeSpan.FromSeconds(30);

    /// <summary>Lets database initialization finish before the first recovery pass.</summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(10);

    private const int MaxOrchestrationsPerPass = 25;

    private readonly IServiceScopeFactory _scopeFactory =
        scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));

    private readonly CalibrationGenerationRecoveryState _state =
        state ?? throw new ArgumentNullException(nameof(state));

    private readonly ILogger<CalibrationGenerationRecoveryService> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc/>
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
                ICalibrationGenerationSaga saga = scope.ServiceProvider
                    .GetRequiredService<ICalibrationGenerationSaga>();
                int advanced = await saga.RecoverDueAsync(MaxOrchestrationsPerPass, stoppingToken);
                if (advanced > 0)
                {
                    _logger.LogInformation(
                        "Advanced {OrchestrationCount} calibration generation orchestrations.",
                        advanced);
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
                    "Calibration generation recovery failed and will be retried.");
                nextInterval = FailureBackoff;
            }
        }
    }
}
