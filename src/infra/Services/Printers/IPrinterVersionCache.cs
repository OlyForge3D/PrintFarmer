using Farm.Infrastructure;

namespace Farm.Infrastructure.Services.Printers;

public interface IPrinterVersionCache
{
    Task<PrinterVersionInfoDto?> GetAsync(Guid printerId, CancellationToken ct);
}
