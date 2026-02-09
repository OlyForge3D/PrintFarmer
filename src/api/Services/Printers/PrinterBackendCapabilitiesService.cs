using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Printers;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Telemetry;

namespace Farm.Web.Api.Services.Printers;

/// <summary>
/// Implementation of IPrinterBackendCapabilitiesService.
/// Converts backend plugin capability flags into user-friendly DTOs for the UI.
/// </summary>
public class PrinterBackendCapabilitiesService(
    IPrintersRepository repo,
    IBackendCapabilityFactory capabilityFactory) : IPrinterBackendCapabilitiesService
{
    private readonly IPrintersRepository _repo = repo ?? throw new ArgumentNullException(nameof(repo));
    private readonly IBackendCapabilityFactory _capabilityFactory = capabilityFactory ?? throw new ArgumentNullException(nameof(capabilityFactory));

    public async Task<PrinterBackendCapabilitiesDto?> GetByPrinterIdAsync(Guid printerId, CancellationToken ct)
    {
        Printer? printer = await _repo.FindByIdAsync(printerId, ct);
        return printer == null ? null : CreateCapabilitiesDto(printer);
    }

    public async Task<IEnumerable<PrinterBackendCapabilitiesDto>> GetAllAsync(CancellationToken ct)
    {
        List<Printer> printers = await _repo.GetAllAsync(ct);
        return printers.Select(CreateCapabilitiesDto);
    }

    public async Task<IEnumerable<PrinterBackendCapabilitiesDto>> GetByIdsAsync(Guid[] printerIds, CancellationToken ct)
    {
        if (printerIds == null || printerIds.Length == 0)
        {
            return Enumerable.Empty<PrinterBackendCapabilitiesDto>();
        }

        List<Printer> printers = await _repo.GetAllAsync(ct);
        var result = printers
            .Where(p => printerIds.Contains(p.Id))
            .Select(CreateCapabilitiesDto)
            .ToList();

        return result;
    }

    /// <summary>
    /// Converts a printer entity to backend capabilities DTO by checking
    /// which interfaces the backend client implements.
    /// </summary>
    private PrinterBackendCapabilitiesDto CreateCapabilitiesDto(Printer printer)
    {
        var backend = (PrinterBackend)printer.Backend;
        BackendCapabilities capabilities = _capabilityFactory.GetSupportedCapabilities(backend);
        bool supportsHistory = _capabilityFactory.TryGetHistoryClientTyped(backend, out _);

        return new PrinterBackendCapabilitiesDto(
            PrinterId: printer.Id,
            PrinterName: printer.Name,
            Backend: backend,
            SupportsCamera: (capabilities & BackendCapabilities.Camera) == BackendCapabilities.Camera,
            SupportsFileDownload: (capabilities & BackendCapabilities.FileDownload) == BackendCapabilities.FileDownload,
            SupportsFileList: (capabilities & BackendCapabilities.FileList) == BackendCapabilities.FileList,
            SupportsFileUpload: (capabilities & BackendCapabilities.FileUpload) == BackendCapabilities.FileUpload,
            SupportsStartPrint: (capabilities & BackendCapabilities.StartPrint) == BackendCapabilities.StartPrint,
            SupportsControlOperations: (capabilities & BackendCapabilities.ControlOperations) == BackendCapabilities.ControlOperations,
            SupportsFileMetadata: (capabilities & BackendCapabilities.FileMetadata) == BackendCapabilities.FileMetadata,
            SupportsMovement: (capabilities & BackendCapabilities.Movement) == BackendCapabilities.Movement,
            SupportsTemperatureControl: (capabilities & BackendCapabilities.TemperatureControl) == BackendCapabilities.TemperatureControl,
            SupportsPrinterInformation: (capabilities & BackendCapabilities.PrinterInformation) == BackendCapabilities.PrinterInformation,
            SupportsHistory: supportsHistory);
    }
}
