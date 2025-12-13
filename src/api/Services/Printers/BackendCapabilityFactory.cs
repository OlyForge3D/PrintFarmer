using System.Reflection;
using Farm.Infrastructure;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Services.Interfaces;

namespace Farm.Web.Api.Services.Printers;

/// <summary>
/// Implementation of IBackendCapabilityFactory.
/// Wraps IBackendClientFactory and adds capability-aware retrieval methods.
/// Uses reflection-based capability detection: clients that implement capability marker interfaces
/// (e.g., ISupportsFileDownload, ISupportsFileList) are automatically registered with those capabilities.
/// </summary>
public class BackendCapabilityFactory : IBackendCapabilityFactory
{
    private readonly IBackendClientFactory _clientFactory;
    private readonly IUnifiedLoggingService _logger;

    // Map of capability marker interfaces to their corresponding BackendCapabilities flags
    private static readonly Dictionary<Type, BackendCapabilities> CapabilityInterfaceMap = new()
    {
        { typeof(ISupportsFileDownload), BackendCapabilities.FileDownload },
        { typeof(ISupportsFileList), BackendCapabilities.FileList },
        { typeof(ISupportsFileUpload), BackendCapabilities.FileUpload },
        { typeof(ISupportsStartPrint), BackendCapabilities.StartPrint },
        { typeof(ISupportsControlOperations), BackendCapabilities.ControlOperations },
        { typeof(ISupportsCamera), BackendCapabilities.Camera },
        { typeof(ISupportsFileMetadata), BackendCapabilities.FileMetadata },
        { typeof(ISupportsMovement), BackendCapabilities.Movement },
        { typeof(ISupportsTemperatureControl), BackendCapabilities.TemperatureControl },
        { typeof(ISupportsPrinterInformation), BackendCapabilities.PrinterInformation },
        { typeof(ISupportsHistory), BackendCapabilities.None } // History is special-cased in TryGetHistoryClient
    };

    // Cache of capabilities for each backend (computed once at initialization)
    private readonly Dictionary<PrinterBackend, BackendCapabilities> _capabilitiesCache;

    public BackendCapabilityFactory(
        IBackendClientFactory clientFactory,
        IUnifiedLoggingService logger)
    {
        ArgumentNullException.ThrowIfNull(clientFactory);
        ArgumentNullException.ThrowIfNull(logger);

        _clientFactory = clientFactory;
        _logger = logger;

        // Initialize capabilities through reflection-based detection
        _capabilitiesCache = DiscoverBackendCapabilities();

        _logger.LogInformation($"BackendCapabilityFactory initialized with capability mappings for {_capabilitiesCache.Count} backends");
    }

    /// <summary>
    /// Discovers backend capabilities by using reflection to check which capability marker interfaces
    /// each backend client implements. This is self-documenting: capabilities are declared by the client
    /// implementations themselves, not hardcoded in a central registry.
    /// </summary>
    private Dictionary<PrinterBackend, BackendCapabilities> DiscoverBackendCapabilities()
    {
        var discovered = new Dictionary<PrinterBackend, BackendCapabilities>();

        // For each backend, get its client and check which capability interfaces it implements
        foreach (PrinterBackend backend in Enum.GetValues(typeof(PrinterBackend)))
        {
            try
            {
                var client = _clientFactory.GetClient(backend);
                var clientType = client.GetType();
                var capabilities = DetectCapabilitiesFromInterfaces(clientType);

                discovered.Add(backend, capabilities);

                var implementedCapabilities = GetImplementedCapabilityNames(clientType);
                _logger.LogDebug($"Backend {backend} ({clientType.Name}) detected capabilities: {string.Join(", ", implementedCapabilities)}");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"Failed to discover capabilities for backend {backend}. Assigning no capabilities.");
                discovered.Add(backend, BackendCapabilities.None);
            }
        }

        return discovered;
    }

    /// <summary>
    /// Examines a client type and detects which capabilities it supports by checking
    /// which capability marker interfaces it implements.
    /// </summary>
    private static BackendCapabilities DetectCapabilitiesFromInterfaces(Type clientType)
    {
        var capabilities = BackendCapabilities.None;

        foreach (var (interfaceType, capabilityFlag) in CapabilityInterfaceMap)
        {
            // Check if the client type implements this capability interface
            if (clientType.GetInterfaces().Contains(interfaceType))
            {
                capabilities |= capabilityFlag;
            }
        }

        return capabilities;
    }

    /// <summary>
    /// Helper to get human-readable names of implemented capabilities for logging.
    /// </summary>
    private static List<string> GetImplementedCapabilityNames(Type clientType)
    {
        var names = new List<string>();

        foreach (var (interfaceType, capabilityFlag) in CapabilityInterfaceMap)
        {
            if (clientType.GetInterfaces().Contains(interfaceType))
            {
                names.Add(capabilityFlag.ToString());
            }
        }

        return names;
    }

    public bool TryGetFileDownloadClient(PrinterBackend backend, out IBackendClient? client)
    {
        return TryGetClientWithCapability(backend, BackendCapabilities.FileDownload, out client);
    }

    public bool TryGetFileListClient(PrinterBackend backend, out IBackendClient? client)
    {
        return TryGetClientWithCapability(backend, BackendCapabilities.FileList, out client);
    }

    public bool TryGetFileUploadClient(PrinterBackend backend, out IBackendClient? client)
    {
        return TryGetClientWithCapability(backend, BackendCapabilities.FileUpload, out client);
    }

    public bool TryGetStartPrintClient(PrinterBackend backend, out IBackendClient? client)
    {
        return TryGetClientWithCapability(backend, BackendCapabilities.StartPrint, out client);
    }

    public bool TryGetControlOperationsClient(PrinterBackend backend, out IBackendClient? client)
    {
        return TryGetClientWithCapability(backend, BackendCapabilities.ControlOperations, out client);
    }

    public bool TryGetCameraClient(PrinterBackend backend, out IBackendClient? client)
    {
        return TryGetClientWithCapability(backend, BackendCapabilities.Camera, out client);
    }

    public bool TryGetFileMetadataClient(PrinterBackend backend, out IBackendClient? client)
    {
        return TryGetClientWithCapability(backend, BackendCapabilities.FileMetadata, out client);
    }

    public bool TryGetMovementClient(PrinterBackend backend, out IBackendClient? client)
    {
        return TryGetClientWithCapability(backend, BackendCapabilities.Movement, out client);
    }

    public bool TryGetTemperatureControlClient(PrinterBackend backend, out IBackendClient? client)
    {
        return TryGetClientWithCapability(backend, BackendCapabilities.TemperatureControl, out client);
    }

    public bool TryGetPrinterInformationClient(PrinterBackend backend, out IBackendClient? client)
    {
        return TryGetClientWithCapability(backend, BackendCapabilities.PrinterInformation, out client);
    }

    public bool TryGetHistoryClient(PrinterBackend backend, out IBackendClient? client)
    {
        // History is supported by specific backends - only Moonraker and OctoPrint
        client = null;
        
        if (backend == PrinterBackend.Moonraker || backend == PrinterBackend.OctoPrint)
        {
            try
            {
                client = _clientFactory.GetClient(backend);
                return true;
            }
            catch
            {
                return false;
            }
        }
        
        return false;
    }

    public bool TryGetPrintJobControlClient(PrinterBackend backend, out IBackendClient? client)
    {
        // Print job control needs both control operations and start print capabilities
        return TryGetClientWithCapability(backend, BackendCapabilities.ControlOperations, out client);
    }

    public bool TryGetFileManagementClient(PrinterBackend backend, out IBackendClient? client)
    {
        // File management includes upload and delete operations
        return TryGetClientWithCapability(backend, BackendCapabilities.FileUpload, out client);
    }

    public bool TryGetCameraClientTyped(PrinterBackend backend, out ISupportsCamera? client)
    {
        client = null;
        if (TryGetCameraClient(backend, out var baseClient) && baseClient is ISupportsCamera cameraClient)
        {
            client = cameraClient;
            return true;
        }
        return false;
    }

    public bool TryGetHistoryClientTyped(PrinterBackend backend, out ISupportsHistory? client)
    {
        client = null;
        if (TryGetHistoryClient(backend, out var baseClient) && baseClient is ISupportsHistory historyClient)
        {
            client = historyClient;
            return true;
        }
        return false;
    }

    public bool TryGetMovementClientTyped(PrinterBackend backend, out ISupportsMovement? client)
    {
        client = null;
        if (TryGetMovementClient(backend, out var baseClient) && baseClient is ISupportsMovement movementClient)
        {
            client = movementClient;
            return true;
        }
        return false;
    }

    public bool TryGetTemperatureControlClientTyped(PrinterBackend backend, out ISupportsTemperatureControl? client)
    {
        client = null;
        if (TryGetTemperatureControlClient(backend, out var baseClient) && baseClient is ISupportsTemperatureControl tempClient)
        {
            client = tempClient;
            return true;
        }
        return false;
    }

    public bool TryGetControlOperationsClientTyped(PrinterBackend backend, out ISupportsControlOperations? client)
    {
        client = null;
        if (TryGetControlOperationsClient(backend, out var baseClient) && baseClient is ISupportsControlOperations controlClient)
        {
            client = controlClient;
            return true;
        }
        return false;
    }

    public bool TryGetFileUploadClientTyped(PrinterBackend backend, out ISupportsFileUpload? client)
    {
        client = null;
        if (TryGetFileUploadClient(backend, out var baseClient) && baseClient is ISupportsFileUpload uploadClient)
        {
            client = uploadClient;
            return true;
        }
        return false;
    }

    public bool TryGetStartPrintClientTyped(PrinterBackend backend, out ISupportsStartPrint? client)
    {
        client = null;
        if (TryGetStartPrintClient(backend, out var baseClient) && baseClient is ISupportsStartPrint startPrintClient)
        {
            client = startPrintClient;
            return true;
        }
        return false;
    }

    public BackendCapabilities GetSupportedCapabilities(PrinterBackend backend)
    {
        if (_capabilitiesCache.TryGetValue(backend, out var capabilities))
        {
            return capabilities;
        }

        _logger.LogWarning($"Unknown backend type: {backend}");
        return BackendCapabilities.None;
    }

    /// <summary>
    /// Internal helper to check if a backend supports a capability and return the client if it does.
    /// </summary>
    private bool TryGetClientWithCapability(
        PrinterBackend backend,
        BackendCapabilities requiredCapability,
        out IBackendClient? client)
    {
        client = null;

        // Check if this backend supports the requested capability
        if (!_capabilitiesCache.TryGetValue(backend, out var capabilities))
        {
            _logger.LogWarning($"Unknown backend type: {backend}");
            return false;
        }

        if (!capabilities.HasFlag(requiredCapability))
        {
            _logger.LogDebug(
                $"Backend {backend} does not support capability {requiredCapability}");
            return false;
        }

        // Get and return the client for this backend
        try
        {
            client = _clientFactory.GetClient(backend);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to get client for backend {backend}");
            return false;
        }
    }
}
