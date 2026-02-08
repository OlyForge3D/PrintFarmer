using Farm.Infrastructure;

namespace Farm.Web.Api.Infrastructure.Caching;

public interface IPrinterVersionCache
{
    Task<PrinterVersionInfoDto?> GetAsync(Guid printerId, CancellationToken ct);
}
