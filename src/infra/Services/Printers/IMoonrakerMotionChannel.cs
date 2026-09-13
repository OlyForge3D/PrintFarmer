using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Services.Printers;

/// <summary>A dedicated correlated command connection; disposal aborts and joins all I/O.</summary>
public interface IMoonrakerMotionChannel : IAsyncDisposable
{
    Task<bool> IsIdleAsync(CancellationToken ct);

    /// <summary>Reads current G-code position, homing and origin offset in one controller response.</summary>
    Task<PrinterStatusDto> ReadMotionStateAsync(Guid printerId, CancellationToken ct);

    Task ExecuteAsync(Guid correlationId, string script, CancellationToken ct);
}

public interface IMoonrakerMotionChannelFactory
{
    Task<IMoonrakerMotionChannel> ConnectAsync(Printer printer, CancellationToken ct);
}
