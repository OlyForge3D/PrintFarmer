using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Services.Printers;

/// <summary>A dedicated correlated command connection; disposal aborts and joins all I/O.</summary>
public interface IPrinterMotionChannel : IAsyncDisposable
{
    Task<bool> IsIdleAsync(CancellationToken ct);

    /// <summary>Reads current G-code position, homing and origin offset in one controller response.</summary>
    Task<PrinterStatusDto> ReadMotionStateAsync(Guid printerId, CancellationToken ct);

    /// <summary>Executes once and completes only on correlated physical completion, never submission acknowledgement.</summary>
    Task ExecuteAsync(Guid correlationId, PrinterControlRequest request, CancellationToken ct);
}

/// <summary>Plugin-owned durable motion protocol and supported semantic intents.</summary>
public interface ISupportsDurableMotion
{
    IReadOnlyCollection<PrinterControlKind> SupportedMotionKinds { get; }

    /// <summary>Opens an isolated, credential-aware connection to the printer's configured backend endpoint.</summary>
    Task<IPrinterMotionChannel> ConnectAsync(Printer printer, CancellationToken ct);
}
