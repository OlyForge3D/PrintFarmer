using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Printers;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Telemetry;

namespace Farm.Infrastructure.Services.Printers;

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
        bool movement = _capabilityFactory.TryGetMovementClientTyped(backend, out ISupportsMovement? movementClient)
            && movementClient is not null;
        bool temperature = _capabilityFactory.TryGetTemperatureControlClientTyped(backend, out ISupportsTemperatureControl? temperatureClient)
            && temperatureClient is not null;
        bool gcode = _capabilityFactory.TryGetGcodeExecutionClientTyped(backend, out ISupportsGcodeExecution? gcodeClient)
            && gcodeClient is not null;

        // These are shared-route guarantees, not the broad interfaces' sometimes-stubbed methods.
        // Home/Z and generic temperature use BuildMoonrakerUrl even for non-Moonraker
        // printers. Only advertise those routes when that port matches the backend endpoint.
        bool backendPortMatches = printer.ServerUri is { } serverUri
            && printer.BackendPort == (printer.FrontendPort ?? (serverUri.Scheme == "https" ? 443 : 80));
        bool homing = movement && backend is PrinterBackend.Moonraker or PrinterBackend.OctoPrint or PrinterBackend.PrusaLink;
        bool moonraker = backend == PrinterBackend.Moonraker;
        bool heater = (moonraker && temperature)
            || (backend == PrinterBackend.OctoPrint && temperatureClient is ISupportsOctoPrintTemperature)
            || (backendPortMatches && temperature && backend is PrinterBackend.PrusaLink or PrinterBackend.FlashForge);

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
            SupportsHistory: supportsHistory,
            SupportsFilamentControl: (capabilities & BackendCapabilities.FilamentControl) == BackendCapabilities.FilamentControl,
            SupportsObjectExclusion: (capabilities & BackendCapabilities.ObjectExclusion) == BackendCapabilities.ObjectExclusion)
        {
            // Moonraker currently emits "G91 G0 ..." / "G90 G0 ..." on one line, not
            // separate mode and move commands. Other MoveTo implementations are stubs;
            // relative jog also drops OctoPrint/PrusaLink credentials in PrintersService.
            SupportsRelativeMovement = false,
            SupportsAbsoluteMovement = false,

            // Only Moonraker's current service route preserves its backend URL contract.
            SupportsDisableMotors = moonraker && gcode,
            SupportsExtrusion = moonraker && gcode,
            SupportsZOffset = true,

            // SAVE_CONFIG does not persist SET_GCODE_OFFSET; M851/M500 is not universal
            // on OctoPrint firmware. Transport support alone cannot prove persistence.
            SupportsZOffsetFirmwareSave = false,
            SupportsHoming = homing,
            SupportsHomingXY = homing,
            SupportsHomingZ = homing && (moonraker || backendPortMatches),
            SupportsHotendTemperature = heater,
            SupportsBedTemperature = heater,

            // Moonraker calls configurable LOAD_FILAMENT/UNLOAD_FILAMENT/M600 macros.
            // No per-printer macro discovery exists here; preserve the legacy broad flag
            // without presenting it as evidence those individual commands are configured.
            SupportsFilamentLoad = false,
            SupportsFilamentUnload = false,
            SupportsFilamentChange = false,
            SupportedAxes = homing ? ["x", "y", "z"] : [],
        };
    }
}
