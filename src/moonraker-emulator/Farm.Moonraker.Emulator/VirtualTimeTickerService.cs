using Farm.Moonraker.Emulator.Options;

namespace Farm.Moonraker.Emulator;

/// <summary>
/// When <see cref="EmulatorOptions.TimeScale"/> is greater than zero, periodically
/// advances this printer's virtual clock and broadcasts the resulting status update so
/// demo/dev stacks see live progress without polling the control API. Left inactive
/// (default TimeScale = 0) contract tests never observe unsolicited time movement.
/// </summary>
public sealed class VirtualTimeTickerService(
    VirtualTimeCoordinator coordinator,
    Microsoft.Extensions.Options.IOptions<EmulatorOptions> options,
    ILogger<VirtualTimeTickerService> logger) : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        double scale = options.Value.TimeScale;
        if (scale <= 0)
        {
            return;
        }

        logger.LogInformation("Virtual time auto-advance enabled at {Scale}x real time.", scale);
        using var timer = new PeriodicTimer(TickInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await coordinator.AutoAdvanceAsync(
                TickInterval.TotalSeconds * scale,
                stoppingToken);
        }
    }
}
