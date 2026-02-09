#pragma warning disable CA1851 // Multiple enumeration intentional for distinct operations

using System;
using System.Collections.Generic;
using System.Linq;
using Farm.Backend.Plugin.Core;
using Farm.Infrastructure.Contracts.Printers;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Telemetry;

namespace Farm.Infrastructure.Services.Printers;

/// <summary>
/// Implementation of IBackendCapabilityFactory.
/// Integrates with the plugin registry for backend client discovery while maintaining
/// backward compatibility with reflection-based capability detection.
/// Capabilities are detected via plugin metadata and/or reflection on client implementations.
/// </summary>
public class BackendCapabilityFactory : IBackendCapabilityFactory
{
    private readonly IBackendClientFactory _clientFactory;
    private readonly IBackendPluginRegistry? _pluginRegistry;
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
        { typeof(ISupportsConfiguredCameraDetection), BackendCapabilities.Camera },
        { typeof(ISupportsFileMetadata), BackendCapabilities.FileMetadata },
        { typeof(ISupportsMovement), BackendCapabilities.Movement },
        { typeof(ISupportsTemperatureControl), BackendCapabilities.TemperatureControl },
        { typeof(ISupportsPrinterInformation), BackendCapabilities.PrinterInformation },
        { typeof(ISupportsHistory), BackendCapabilities.History },
        { typeof(ISupportsFileDelete), BackendCapabilities.FileDelete }
    };

    // Cache of capabilities for each backend (computed once at initialization)
    private readonly Dictionary<PrinterBackend, BackendCapabilities> _capabilitiesCache;

    public BackendCapabilityFactory(
        IBackendClientFactory clientFactory,
        IUnifiedLoggingService logger,
        IBackendPluginRegistry? pluginRegistry = null)
    {
        ArgumentNullException.ThrowIfNull(clientFactory);
        ArgumentNullException.ThrowIfNull(logger);

        _clientFactory = clientFactory;
        _logger = logger;
        _pluginRegistry = pluginRegistry;

        // Initialize capabilities through reflection-based detection and plugin registry
        _capabilitiesCache = DiscoverBackendCapabilities();

        _logger.LogInformation($"BackendCapabilityFactory initialized with capability mappings for {_capabilitiesCache.Count} backends");

        if (_pluginRegistry != null)
        {
            int registeredPlugins = _pluginRegistry.GetAllPlugins().Count();
            _logger.LogInformation($"Plugin registry integrated: {registeredPlugins} backend client plugins registered");
        }
    }

    /// <summary>
    /// Discovers backend capabilities by checking the plugin registry and/or
    /// using reflection to detect capability marker interfaces on client implementations.
    /// </summary>
    private Dictionary<PrinterBackend, BackendCapabilities> DiscoverBackendCapabilities()
    {
        var discovered = new Dictionary<PrinterBackend, BackendCapabilities>();

        // For each backend, get its client and check which capability interfaces it implements
        foreach (PrinterBackend backend in Enum.GetValues<PrinterBackend>())
        {
            try
            {
                string backendType = GetBackendTypeName(backend);
                BackendCapabilities capabilities = BackendCapabilities.None;
                string capabilitySource = "reflection";

                // First, try to get capabilities from plugin registry
                if (_pluginRegistry?.IsRegistered(backendType) == true)
                {
                    capabilities = GetCapabilitiesFromPlugin(backendType);
                    capabilitySource = "plugin registry";
                }
                else
                {
                    // Fall back to reflection-based detection
                    IBackendClient client = _clientFactory.GetClient(backend);
                    Type clientType = client.GetType();
                    capabilities = DetectCapabilitiesFromInterfaces(clientType);
                }

                discovered.Add(backend, capabilities);

                List<string> implementedCapabilities = GetCapabilityNames(capabilities);
                _logger.LogDebug($"Backend {backend} ({backendType}) detected capabilities from {capabilitySource}: {string.Join(", ", implementedCapabilities)}");
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
    /// Converts a PrinterBackend enum value to the backend type string used by plugins.
    /// </summary>
    private static string GetBackendTypeName(PrinterBackend backend) => backend switch
    {
        PrinterBackend.Moonraker => "moonraker",
        PrinterBackend.OctoPrint => "octoprint",
        PrinterBackend.PrusaLink => "prusalink",
        PrinterBackend.SDCP => "sdcp",
        _ => backend.ToString().ToLowerInvariant()
    };

    /// <summary>
    /// Gets capabilities from the plugin registry by examining the plugin's supported capability types.
    /// </summary>
    private BackendCapabilities GetCapabilitiesFromPlugin(string backendType)
    {
        IBackendClientPlugin? plugin = _pluginRegistry?.GetPlugin(backendType);
        if (plugin == null)
        {
            return BackendCapabilities.None;
        }

        BackendCapabilities capabilities = BackendCapabilities.None;
        IEnumerable<Type> pluginCapabilities = plugin.GetCapabilities();

        foreach ((Type? interfaceType, BackendCapabilities capabilityFlag) in CapabilityInterfaceMap)
        {
            if (pluginCapabilities.Contains(interfaceType))
            {
                capabilities |= capabilityFlag;
            }
        }

        return capabilities;
    }

    /// <summary>
    /// Examines a client type and detects which capabilities it supports by checking
    /// which capability marker interfaces it implements.
    /// </summary>
    private static BackendCapabilities DetectCapabilitiesFromInterfaces(Type clientType)
    {
        BackendCapabilities capabilities = BackendCapabilities.None;

        foreach ((Type? interfaceType, BackendCapabilities capabilityFlag) in CapabilityInterfaceMap)
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
    private static List<string> GetCapabilityNames(BackendCapabilities capabilities)
    {
        var names = new List<string>();

        foreach ((Type _, BackendCapabilities capabilityFlag) in CapabilityInterfaceMap)
        {
            if ((capabilities & capabilityFlag) == capabilityFlag && capabilityFlag != BackendCapabilities.None)
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
        _logger.LogWarning($"[DIAGNOSTIC] TryGetFileListClient called for backend {backend}. Cache contains {_capabilitiesCache.Count} entries. Cache has key: {_capabilitiesCache.ContainsKey(backend)}");
        if (_capabilitiesCache.TryGetValue(backend, out BackendCapabilities caps))
        {
            _logger.LogWarning($"[DIAGNOSTIC] Backend {backend} capabilities from cache: {caps}. Has FileList: {caps.HasFlag(BackendCapabilities.FileList)}");
        }
        else
        {
            _logger.LogWarning($"[DIAGNOSTIC] Backend {backend} NOT FOUND in capabilities cache!");
        }

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
        return TryGetClientWithCapability(backend, BackendCapabilities.History, out client);
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

    public bool TryGetFileDeleteClient(PrinterBackend backend, out IBackendClient? client)
    {
        return TryGetClientWithCapability(backend, BackendCapabilities.FileDelete, out client);
    }

    public bool TryGetFileDeleteClientTyped(PrinterBackend backend, out ISupportsFileDelete? client)
    {
        client = null;
        if (TryGetFileDeleteClient(backend, out IBackendClient? baseClient) && baseClient is ISupportsFileDelete deleteClient)
        {
            client = deleteClient;
            return true;
        }

        return false;
    }

    public bool TryGetCameraClientTyped(PrinterBackend backend, out ISupportsCamera? client)
    {
        client = null;
        if (TryGetCameraClient(backend, out IBackendClient? baseClient) && baseClient is ISupportsCamera cameraClient)
        {
            client = cameraClient;
            return true;
        }

        return false;
    }

    public bool TryGetConfiguredCameraDetectionClient(PrinterBackend backend, out ISupportsConfiguredCameraDetection? client)
    {
        client = null;
        if (TryGetCameraClient(backend, out IBackendClient? baseClient) && baseClient is ISupportsConfiguredCameraDetection detectionClient)
        {
            client = detectionClient;
            return true;
        }

        return false;
    }

    public bool TryGetHistoryClientTyped(PrinterBackend backend, out ISupportsHistory? client)
    {
        client = null;
        if (TryGetHistoryClient(backend, out IBackendClient? baseClient) && baseClient is ISupportsHistory historyClient)
        {
            client = historyClient;
            return true;
        }

        return false;
    }

    public bool TryGetMovementClientTyped(PrinterBackend backend, out ISupportsMovement? client)
    {
        client = null;
        if (TryGetMovementClient(backend, out IBackendClient? baseClient) && baseClient is ISupportsMovement movementClient)
        {
            client = movementClient;
            return true;
        }

        return false;
    }

    public bool TryGetTemperatureControlClientTyped(PrinterBackend backend, out ISupportsTemperatureControl? client)
    {
        client = null;
        if (TryGetTemperatureControlClient(backend, out IBackendClient? baseClient) && baseClient is ISupportsTemperatureControl tempClient)
        {
            client = tempClient;
            return true;
        }

        return false;
    }

    public bool TryGetControlOperationsClientTyped(PrinterBackend backend, out ISupportsControlOperations? client)
    {
        client = null;
        if (TryGetControlOperationsClient(backend, out IBackendClient? baseClient) && baseClient is ISupportsControlOperations controlClient)
        {
            client = controlClient;
            return true;
        }

        return false;
    }

    public bool TryGetFileUploadClientTyped(PrinterBackend backend, out ISupportsFileUpload? client)
    {
        client = null;
        if (TryGetFileUploadClient(backend, out IBackendClient? baseClient) && baseClient is ISupportsFileUpload uploadClient)
        {
            client = uploadClient;
            return true;
        }

        return false;
    }

    public bool TryGetStartPrintClientTyped(PrinterBackend backend, out ISupportsStartPrint? client)
    {
        client = null;
        if (TryGetStartPrintClient(backend, out IBackendClient? baseClient) && baseClient is ISupportsStartPrint startPrintClient)
        {
            client = startPrintClient;
            return true;
        }

        return false;
    }

    public BackendCapabilities GetSupportedCapabilities(PrinterBackend backend)
    {
        if (_capabilitiesCache.TryGetValue(backend, out BackendCapabilities capabilities))
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
        _logger.LogWarning($"[DIAGNOSTIC] TryGetClientWithCapability ENTRY: backend={backend}, requiredCapability={requiredCapability}");

        client = null;

        // Check if this backend supports the requested capability
        if (!_capabilitiesCache.TryGetValue(backend, out BackendCapabilities capabilities))
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

            // DIAGNOSTIC: Log the actual client type returned and its interfaces
            if (client != null)
            {
                Type clientType = client.GetType();
                string? clientFullName = clientType.FullName;
                var interfaces = clientType.GetInterfaces().Select(i => i.Name).ToList();
                bool implementsRequiredInterface = false;

                // Check if client implements the specific interface for this capability
                switch (requiredCapability)
                {
                    case BackendCapabilities.FileList:
                        implementsRequiredInterface = client is ISupportsFileList;
                        break;
                    case BackendCapabilities.FileDownload:
                        implementsRequiredInterface = client is ISupportsFileDownload;
                        break;
                    case BackendCapabilities.FileUpload:
                        implementsRequiredInterface = client is ISupportsFileUpload;
                        break;
                    case BackendCapabilities.StartPrint:
                        implementsRequiredInterface = client is ISupportsStartPrint;
                        break;
                    case BackendCapabilities.ControlOperations:
                        implementsRequiredInterface = client is ISupportsControlOperations;
                        break;
                    case BackendCapabilities.Camera:
                        implementsRequiredInterface = client is ISupportsCamera;
                        break;
                    case BackendCapabilities.FileMetadata:
                        implementsRequiredInterface = client is ISupportsFileMetadata;
                        break;
                    case BackendCapabilities.Movement:
                        implementsRequiredInterface = client is ISupportsMovement;
                        break;
                    case BackendCapabilities.TemperatureControl:
                        implementsRequiredInterface = client is ISupportsTemperatureControl;
                        break;
                    case BackendCapabilities.PrinterInformation:
                        implementsRequiredInterface = client is ISupportsPrinterInformation;
                        break;
                }

                _logger.LogWarning(
                    $"[DIAGNOSTIC] TryGetClientWithCapability({backend}, {requiredCapability}) => " +
                    $"Type: {clientFullName}, " +
                    $"Implements {requiredCapability} Interface: {implementsRequiredInterface}, " +
                    $"All Interfaces: [{string.Join(", ", interfaces)}]");
            }
            else
            {
                _logger.LogWarning($"[DIAGNOSTIC] TryGetClientWithCapability({backend}, {requiredCapability}) => client is NULL!");
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to get client for backend {backend}");
            return false;
        }
    }
}
