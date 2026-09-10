using System.Text.Json;
using Farm.Infrastructure;
using Farm.Infrastructure.Contracts.Printers;
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
    IBackendCapabilityFactory capabilityFactory,
    IBackendClientFactory? backendClientFactory = null,
    IPrinterVerifiedSafetyCache? verifiedSafetyCache = null) : IPrinterBackendCapabilitiesService
{
    private readonly IPrintersRepository _repo = repo ?? throw new ArgumentNullException(nameof(repo));
    private readonly IBackendCapabilityFactory _capabilityFactory = capabilityFactory ?? throw new ArgumentNullException(nameof(capabilityFactory));
    private readonly IBackendClientFactory? _backendClientFactory = backendClientFactory;
    private readonly IPrinterVerifiedSafetyCache? _verifiedSafetyCache = verifiedSafetyCache;

    public async Task<PrinterBackendCapabilitiesDto?> GetByPrinterIdAsync(Guid printerId, CancellationToken ct)
    {
        Printer? printer = await _repo.FindByIdAsync(printerId, ct);
        return printer == null ? null : await CreateCapabilitiesDtoAsync(printer, ct);
    }

    public async Task<IEnumerable<PrinterBackendCapabilitiesDto>> GetAllAsync(CancellationToken ct)
    {
        List<Printer> printers = await _repo.GetAllAsync(ct);
        return await Task.WhenAll(printers.Select(printer => CreateCapabilitiesDtoAsync(printer, ct)));
    }

    public async Task<IEnumerable<PrinterBackendCapabilitiesDto>> GetByIdsAsync(Guid[] printerIds, CancellationToken ct)
    {
        if (printerIds == null || printerIds.Length == 0)
        {
            return Enumerable.Empty<PrinterBackendCapabilitiesDto>();
        }

        List<Printer> printers = await _repo.GetAllAsync(ct);
        Printer[] selected = printers
            .Where(p => printerIds.Contains(p.Id))
            .ToArray();

        return await Task.WhenAll(selected.Select(printer => CreateCapabilitiesDtoAsync(printer, ct)));
    }

    /// <inheritdoc />
    public void InvalidateVerifiedSafety(Guid printerId)
    {
        _verifiedSafetyCache?.Invalidate(printerId);
    }

    /// <summary>
    /// Converts a printer entity to backend capabilities DTO by checking
    /// which interfaces the backend client implements.
    /// </summary>
    private async Task<PrinterBackendCapabilitiesDto> CreateCapabilitiesDtoAsync(
        Printer printer,
        CancellationToken ct)
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

        PrinterVerifiedSafetyDto verifiedSafety =
            await GetVerifiedSafetyAsync(printer, backend, ct);

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
            // Relative jog still lacks a verified per-printer safety contract.
            // Absolute movement is projected only from authoritative discovery.
            SupportsRelativeMovement = false,
            SupportsAbsoluteMovement =
                verifiedSafety.Operations.AbsoluteMovement.Support ==
                VerifiedSafetySupport.Supported,

            // Only Moonraker's current service route preserves its backend URL contract.
            SupportsDisableMotors = moonraker && gcode,
            SupportsExtrusion = moonraker && gcode,
            SupportsZOffset = true,

            // SAVE_CONFIG does not persist SET_GCODE_OFFSET; M851/M500 is not universal
            // on OctoPrint firmware. Transport support alone cannot prove persistence.
            SupportsZOffsetFirmwareSave =
                verifiedSafety.Operations.FirmwareZOffsetSave.Support ==
                VerifiedSafetySupport.Supported,
            SupportsHoming = homing,
            SupportsHomingXY = homing,
            SupportsHomingZ = homing && (moonraker || backendPortMatches),
            SupportsHotendTemperature = heater,
            SupportsBedTemperature = heater,

            // Moonraker calls configurable LOAD_FILAMENT/UNLOAD_FILAMENT/M600 macros.
            // No per-printer macro discovery exists here; preserve the legacy broad flag
            // without presenting it as evidence those individual commands are configured.
            SupportsFilamentLoad =
                verifiedSafety.Operations.FilamentLoad.Support ==
                VerifiedSafetySupport.Supported,
            SupportsFilamentUnload =
                verifiedSafety.Operations.FilamentUnload.Support ==
                VerifiedSafetySupport.Supported,
            SupportsFilamentChange =
                verifiedSafety.Operations.FilamentChange.Support ==
                VerifiedSafetySupport.Supported,
            SupportedAxes = homing ? ["x", "y", "z"] : [],
            VerifiedSafety = verifiedSafety,
        };
    }

    private async Task<PrinterVerifiedSafetyDto> GetVerifiedSafetyAsync(
        Printer printer,
        PrinterBackend backend,
        CancellationToken ct)
    {
        string sourceRevision = printer.ConfigurationRevision.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        string dispatchUrl =
            PrinterBackendEndpointResolver.ResolveDispatchUrl(printer);
        var key = new VerifiedSafetyCacheKey(
            printer.Id,
            backend,
            dispatchUrl,
            printer.Revision,
            printer.ConfigurationRevision);
        long cacheGeneration =
            _verifiedSafetyCache?.GetGeneration(printer.Id) ?? 0;

        if (_verifiedSafetyCache?.TryGet(
                printer.Id,
                key,
                out PrinterVerifiedSafetyDto? cached) == true &&
            cached is not null)
        {
            return cached;
        }

        IBackendClient? backendClient;
        try
        {
            backendClient = _backendClientFactory?.GetClient(backend);
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException)
        {
            return PrinterVerifiedSafetyDto.Unknown(
                source: "backend.discovery.client-unavailable",
                sourceRevision: sourceRevision);
        }

        if (backendClient is not ISupportsVerifiedSafetyDiscovery discovery)
        {
            return PrinterVerifiedSafetyDto.Unknown(
                source: "backend.discovery.not-implemented",
                sourceRevision: sourceRevision);
        }

        PrinterVerifiedSafetyDto result;
        try
        {
            result = await discovery.DiscoverVerifiedSafetyAsync(
                dispatchUrl,
                printer.Credential,
                sourceRevision,
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            result = CreateUnavailableSafety(sourceRevision);
        }
        catch (HttpRequestException)
        {
            result = CreateUnavailableSafety(sourceRevision);
        }
        catch (JsonException)
        {
            result = CreateUnavailableSafety(sourceRevision);
        }
        catch (TimeoutException)
        {
            result = CreateUnavailableSafety(sourceRevision);
        }
        catch (InvalidOperationException)
        {
            result = CreateUnavailableSafety(sourceRevision);
        }
        catch (UriFormatException)
        {
            result = CreateUnavailableSafety(sourceRevision);
        }

        TimeSpan cacheDuration = TimeSpan.FromSeconds(15);
        if (_verifiedSafetyCache?.Set(
                printer.Id,
                key,
                result,
                cacheDuration,
                cacheGeneration) == false)
        {
            return PrinterVerifiedSafetyDto.Unknown(
                source: "backend.discovery.invalidated-during-probe",
                observedAtUtc: DateTime.UtcNow,
                sourceRevision: sourceRevision);
        }

        return result;
    }

    private static PrinterVerifiedSafetyDto CreateUnavailableSafety(
        string sourceRevision) =>
        PrinterVerifiedSafetyDto.Unknown(
            source: "backend.discovery.failed",
            observedAtUtc: DateTime.UtcNow,
            sourceRevision: sourceRevision);

    private sealed record VerifiedSafetyCacheKey(
        Guid PrinterId,
        PrinterBackend Backend,
        string BackendUrl,
        long Revision,
        long ConfigurationRevision);
}
