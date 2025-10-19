using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Repositories.PrinterCapabilities;

public interface IPrinterCapabilitiesRepository
{
    Task<List<Farm.Infrastructure.Domain.PrinterCapabilities>> GetAllWithPrinterAsync(CancellationToken ct = default);
    Task<Farm.Infrastructure.Domain.PrinterCapabilities?> GetByPrinterIdAsync(Guid printerId, CancellationToken ct = default);
    Task<bool> ExistsByPrinterIdAsync(Guid printerId, CancellationToken ct = default);
    Task AddAsync(Farm.Infrastructure.Domain.PrinterCapabilities capabilities, CancellationToken ct = default);
    Task UpdateAsync(Farm.Infrastructure.Domain.PrinterCapabilities capabilities, CancellationToken ct = default);
    Task RemoveAsync(Farm.Infrastructure.Domain.PrinterCapabilities capabilities, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
    Task<List<Farm.Infrastructure.Domain.PrinterCapabilities>> GetStaleCapabilitiesAsync(DateTime threshold, int limit, CancellationToken ct = default);
    Task LoadPrinterReferenceAsync(Farm.Infrastructure.Domain.PrinterCapabilities capabilities, CancellationToken ct = default);
    Task<Printer?> FindPrinterAsync(Guid printerId, CancellationToken ct = default);
    Task<Printer?> GetPrinterWithModelAndManufacturerAsync(Guid printerId, CancellationToken ct = default);
    Task<GcodeFile?> FindGcodeFileAsync(Guid id, CancellationToken ct = default);
    Task<List<Farm.Infrastructure.Domain.PrinterCapabilities>> GetAvailableWithPrinterAsync(CancellationToken ct = default);
}
