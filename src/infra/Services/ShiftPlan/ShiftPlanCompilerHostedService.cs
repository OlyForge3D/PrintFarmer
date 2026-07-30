using Farm.Infrastructure.Services.OperatorFeatures;
using Farm.Infrastructure.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.ShiftPlan;

/// <summary>
/// Runs <see cref="IShiftPlanCompiler.CompileAsync"/> on a fixed interval so
/// operator-facing shift-plan tasks stay fresh even when no domain event
/// drives an out-of-band recompile.
/// The service no-ops silently when <see cref="OperatorFeature.ShiftPlan"/> is
/// disabled via <see cref="IOperatorFeatureGate"/> — it does not shut down, so
/// the flag can be re-enabled at runtime without restarting the API.
/// </summary>
public sealed class ShiftPlanCompilerHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ShiftPlanCompilerHostedService> _logger;

    // Fix R3-6: owned once for the process lifetime and threaded through every
    // compile pass so suppression continuity survives across per-tick scopes (each
    // tick gets a fresh IShiftPlanCompiler/AppDbContext — see CompileAsync call below).
    private readonly ShiftPlanSuppressionState _suppressionState = new();

    public ShiftPlanCompilerHostedService(
        IServiceScopeFactory scopeFactory,
        ILogger<ShiftPlanCompilerHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            int intervalSeconds;
            try
            {
                await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();

                // Fix 7: check the feature gate each iteration; skip compile when disabled.
                IOperatorFeatureGate featureGate = scope.ServiceProvider.GetRequiredService<IOperatorFeatureGate>();
                if (!await featureGate.IsEnabledAsync(OperatorFeature.ShiftPlan, stoppingToken).ConfigureAwait(false))
                {
                    _logger.LogDebug("Shift-plan feature is disabled; skipping compile pass");
                    intervalSeconds = 60;
                }
                else
                {
                    ISettingsService settingsService = scope.ServiceProvider.GetRequiredService<ISettingsService>();
                    ShiftPlanSettings settings = settingsService.Get<ShiftPlanSettings>() ?? new ShiftPlanSettings();
                    intervalSeconds = Math.Max(15, settings.CompileIntervalSeconds);

                    IShiftPlanCompiler compiler = scope.ServiceProvider.GetRequiredService<IShiftPlanCompiler>();
                    await compiler.CompileAsync(_suppressionState, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Shift-plan compile pass failed");
                intervalSeconds = 60;
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
