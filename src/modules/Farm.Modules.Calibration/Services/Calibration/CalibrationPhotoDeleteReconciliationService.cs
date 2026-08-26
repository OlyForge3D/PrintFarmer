using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Farm.Web.Api.Services.Calibration;

/// <summary>
/// Retries durable calibration photo-delete requests that could not remove their
/// private blob during the original two-phase delete.
/// </summary>
public sealed class CalibrationPhotoDeleteReconciliationService(
    IServiceScopeFactory scopeFactory,
    ILogger<CalibrationPhotoDeleteReconciliationService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan FailureBackoff = TimeSpan.FromMinutes(1);

    private readonly IServiceScopeFactory _scopeFactory =
        scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));

    private readonly ILogger<CalibrationPhotoDeleteReconciliationService> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        TimeSpan nextInterval = TimeSpan.Zero;
        while (!stoppingToken.IsCancellationRequested)
        {
            if (nextInterval > TimeSpan.Zero)
            {
                await Task.Delay(nextInterval, stoppingToken);
            }

            try
            {
                using IServiceScope scope = _scopeFactory.CreateScope();
                ICalibrationProjectService service = scope.ServiceProvider
                    .GetRequiredService<ICalibrationProjectService>();
                int reconciled = await service.ReconcilePendingPhotoDeletesAsync(stoppingToken);
                if (reconciled > 0)
                {
                    _logger.LogInformation(
                        "Reconciled {PhotoCount} pending calibration photo deletes.",
                        reconciled);
                }

                nextInterval = PollInterval;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "Calibration photo-delete reconciliation failed and will be retried.");
                nextInterval = FailureBackoff;
            }
        }
    }
}
