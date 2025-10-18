using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Web.Shared;

namespace Farm.Web.Api.Services.PrinterCapabilities
{
    public interface IPrinterCapabilitiesService
    {
        Task<IReadOnlyList<PrinterCapabilitiesDto>> GetAllAsync(CancellationToken ct = default);
        Task<PrinterCapabilitiesDto?> GetByPrinterIdAsync(Guid printerId, CancellationToken ct = default);
        Task<PrinterCapabilitiesDto?> CreateAsync(CreatePrinterCapabilitiesDto request, CancellationToken ct = default);
        Task<PrinterCapabilitiesDto?> CreateOrUpdateAsync(Guid printerId, UpdatePrinterCapabilitiesDto request, CancellationToken ct = default);
        Task<IReadOnlyList<PrinterDto>> GetCompatiblePrintersAsync(Guid gcodeFileId, CancellationToken ct = default);
        Task<bool> DeleteAsync(Guid printerId, CancellationToken ct = default);
        Task<(PrinterCapabilitiesDto? result, bool isNew)> DiscoverAsync(Guid printerId, CancellationToken ct = default);
        Task<Farm.Web.Api.Services.Interfaces.CapabilityValidationResult> ValidateAsync(Guid printerId, CancellationToken ct = default);
        Task<PrinterCapabilitiesDto?> GetModelDefaultsAsync(Guid printerId, CancellationToken ct = default);
    }
}
