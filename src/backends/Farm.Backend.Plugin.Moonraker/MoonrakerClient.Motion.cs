using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;

namespace Farm.Backend.Plugin.Moonraker;

public partial class MoonrakerClient : ISupportsDurableMotion
{
    private static readonly IReadOnlyCollection<PrinterControlKind> MotionKinds = Array.AsReadOnly(
        new[] { PrinterControlKind.HomeAll, PrinterControlKind.HomeXY, PrinterControlKind.HomeZ, PrinterControlKind.Jog, PrinterControlKind.MoveTo });

    /// <inheritdoc />
    public IReadOnlyCollection<PrinterControlKind> SupportedMotionKinds =>
        _motionChannels is null ? Array.Empty<PrinterControlKind>() : MotionKinds;

    /// <inheritdoc />
    public Task<IPrinterMotionChannel> ConnectAsync(Printer printer, CancellationToken ct) =>
        (_motionChannels ?? throw new PrinterControlException(
            422, "printer_operation_unsupported", "The Moonraker motion transport is unavailable."))
        .ConnectAsync(printer, ct);
}
