using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;

namespace Farm.Web.Api.Services.Printers
{
    public interface IPrintersService
    {
        Task<List<Printer>> GetAllAsync(CancellationToken ct);
        Task<List<Printer>> GetAllWithIncludesAsync(CancellationToken ct);
        Task<Printer?> FindByIdAsync(Guid id, CancellationToken ct);
        Task<Printer?> FindByIdWithIncludesAsync(Guid id, CancellationToken ct);
        Task AddAsync(Printer p, CancellationToken ct);
        Task RemoveAsync(Printer p, CancellationToken ct);
        Task SaveChangesAsync(CancellationToken ct);
        Task<Dictionary<Guid, Farm.Infrastructure.Domain.PrinterCapabilities>> GetCapabilitiesDictionaryAsync(Guid[]? ids, CancellationToken ct);
        Task<List<Farm.Infrastructure.Domain.PrinterCapabilities>> GetCapabilitiesListAsync(Guid[]? ids, CancellationToken ct);
        Task<Farm.Infrastructure.Domain.PrinterCapabilities?> GetCapabilitiesByPrinterIdAsync(Guid id, CancellationToken ct);
        // Higher-level orchestration methods that encapsulate external client calls and status aggregation
        Task<Farm.Web.Shared.PrinterDto[]> GetAllWithStatusDtosAsync(CancellationToken ct);
        Task<Farm.Web.Shared.PrinterStatusDto> GetStatusDtoAsync(Guid id, CancellationToken ct);
        Task<Farm.Web.Shared.PrinterDto> GetPrinterDtoAsync(Guid id, CancellationToken ct);
        Task<Farm.Web.Shared.PrinterCameraUrlsDto[]> GetCameraUrlsAsync(CancellationToken ct);
        Task<Farm.Web.Shared.PrinterFastDto[]> GetAllFastDtosAsync(CancellationToken ct);
    }
}
