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
public class PrinterBackendCapabilitiesService : IPrinterBackendCapabilitiesService
{
    private readonly IPrintersRepository _repo;
    private readonly IBackendCapabilityFactory _capabilityFactory;
    private readonly IUnifiedLoggingService _logger;

    public PrinterBackendCapabilitiesService(
        IPrintersRepository repo,
        IBackendCapabilityFactory capabilityFactory,
        IUnifiedLoggingService logger)
    {
        _repo = repo ?? throw new ArgumentNullException(nameof(repo));
        _capabilityFactory = capabilityFactory ?? throw new ArgumentNullException(nameof(capabilityFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<PrinterBackendCapabilitiesDto?> GetByPrinterIdAsync(Guid printerId, CancellationToken ct)
    {
        var printer = await _repo.FindByIdAsync(printerId, ct);
        if (printer == null)
        {
            return null;
        }

        return CreateCapabilitiesDto(printer);
    }

    public async Task<IEnumerable<PrinterBackendCapabilitiesDto>> GetAllAsync(CancellationToken ct)
    {
        var printers = await _repo.GetAllAsync(ct);
        return printers.Select(CreateCapabilitiesDto);
    }

    public async Task<IEnumerable<PrinterBackendCapabilitiesDto>> GetByIdsAsync(Guid[] printerIds, CancellationToken ct)
    {
        if (printerIds == null || printerIds.Length == 0)
        {
            return Enumerable.Empty<PrinterBackendCapabilitiesDto>();
        }

        var printers = await _repo.GetAllAsync(ct);
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
        var capabilities = _capabilityFactory.GetSupportedCapabilities(backend);

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
            SupportsHistory: false // History is handled specially, set to false for now
        );
    }
}
