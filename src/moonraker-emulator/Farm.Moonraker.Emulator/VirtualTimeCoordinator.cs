using Farm.Moonraker.Emulator.Domain;
using Farm.Moonraker.Emulator.Endpoints;

namespace Farm.Moonraker.Emulator;

public sealed record VirtualTimeSnapshot(
    string PrinterId,
    DateTimeOffset VirtualTime,
    string PrintState);

/// <summary>
/// Serializes virtual-time mutations through status publication so control requests
/// cannot race the optional background ticker.
/// </summary>
public sealed class VirtualTimeCoordinator(PrinterRegistry registry) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public Task<VirtualTimeSnapshot> AdvanceAsync(TimeSpan delta, CancellationToken cancellationToken = default) =>
        MutateAndPublishAsync(printer => printer.AdvanceTime(delta), cancellationToken);

    public Task<VirtualTimeSnapshot> AutoAdvanceAsync(double seconds, CancellationToken cancellationToken = default) =>
        MutateAndPublishAsync(printer => printer.AutoAdvanceTime(seconds), cancellationToken);

    public Task<VirtualTimeSnapshot> ResetAsync(CancellationToken cancellationToken = default) =>
        MutateAndPublishAsync(printer => printer.ResetTime(), cancellationToken);

    public async Task<TResult> ExecuteExclusiveAsync<TResult>(
        Func<Task<TResult>> operation,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await operation();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<VirtualTimeSnapshot> MutateAndPublishAsync(
        Action<PrinterAggregate> mutation,
        CancellationToken cancellationToken)
    {
        return await ExecuteExclusiveAsync(
            async () =>
            {
                PrinterAggregate printer = registry.Printer;
                mutation(printer);
                VirtualTimeSnapshot snapshot = new(
                    printer.Id,
                    printer.Clock.UtcNow,
                    printer.PrintState);
                await BroadcastService.NotifyStatusUpdateAsync(printer);
                return snapshot;
            },
            cancellationToken);
    }

    public void Dispose() => _gate.Dispose();
}
