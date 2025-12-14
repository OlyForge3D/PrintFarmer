using System;
using System.Collections.Generic;
using Farm.Backend.Plugin.Core;
using Farm.Infrastructure;
using Farm.Infrastructure.Telemetry;

namespace Farm.Web.Api.Services.Printers
{
    /// <summary>
    /// Default implementation of IBackendClientFactory.
    /// Dynamically manages all backend-specific client implementations provided by plugins
    /// and provides unified access to them based on printer backend type.
    /// This factory has zero hardcoded dependencies on specific backend implementations,
    /// allowing new backends to be added via plugins without modifying the API.
    /// </summary>
    public class BackendClientFactory : IBackendClientFactory
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly IBackendPluginRegistry _pluginRegistry;
        private readonly IUnifiedLoggingService _logger;
        private readonly Dictionary<PrinterBackend, IBackendClient> _clientCache;

        /// <summary>
        /// Creates a new BackendClientFactory that dynamically discovers backends from plugins.
        /// </summary>
        /// <param name="serviceProvider">Service provider for resolving plugin-registered client implementations</param>
        /// <param name="pluginRegistry">Plugin registry containing all loaded backend plugins</param>
        /// <param name="logger">Logging service</param>
        public BackendClientFactory(
            IServiceProvider serviceProvider,
            IBackendPluginRegistry pluginRegistry,
            IUnifiedLoggingService logger)
        {
            ArgumentNullException.ThrowIfNull(serviceProvider);
            ArgumentNullException.ThrowIfNull(pluginRegistry);
            ArgumentNullException.ThrowIfNull(logger);

            _serviceProvider = serviceProvider;
            _pluginRegistry = pluginRegistry;
            _logger = logger;
            _clientCache = new Dictionary<PrinterBackend, IBackendClient>();

            InitializeClients();
        }

        /// <summary>
        /// Initializes the client cache by discovering all registered backend plugins
        /// and resolving their client implementations from the service provider.
        /// </summary>
        private void InitializeClients()
        {
            var registeredBackends = _pluginRegistry.GetAllPlugins();
            var loadedCount = 0;

            foreach (var plugin in registeredBackends)
            {
                try
                {
                    // Get the client interface type from the plugin
                    var clientInterfaceType = plugin.ClientInterfaceType;
                    
                    if (clientInterfaceType == null)
                    {
                        _logger.LogWarning($"Backend plugin '{plugin.BackendType}' does not specify a ClientInterfaceType");
                        continue;
                    }

                    // Attempt to resolve the client from the service provider
                    // The plugin's RegisterAdditionalServices should have registered this type
                    var client = _serviceProvider.GetService(clientInterfaceType) as IBackendClient;
                    
                    if (client != null)
                    {
                        // Map the backend type to the client
                        if (Enum.TryParse<PrinterBackend>(plugin.BackendType, ignoreCase: true, out var backendEnum))
                        {
                            _clientCache[backendEnum] = client;
                            loadedCount++;
                            _logger.LogInformation($"Backend client loaded: {plugin.BackendType} ({plugin.DisplayName})");
                        }
                        else
                        {
                            _logger.LogWarning($"Could not parse backend type '{plugin.BackendType}' as PrinterBackend enum");
                        }
                    }
                    else
                    {
                        _logger.LogWarning($"Could not resolve client interface '{clientInterfaceType.Name}' for backend '{plugin.BackendType}'. Ensure the plugin's RegisterAdditionalServices method registered it.");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Error loading client for backend '{plugin.BackendType}': {ex.Message}");
                }
            }

            _logger.LogInformation($"BackendClientFactory initialized with {loadedCount} backend clients from plugins");
        }

        public IBackendClient GetClient(PrinterBackend backend)
        {
            if (!_clientCache.TryGetValue(backend, out var client))
            {
                throw new ArgumentException($"Unsupported printer backend: {backend}. Available backends: {string.Join(", ", _clientCache.Keys)}", nameof(backend));
            }

            return client ?? throw new InvalidOperationException($"Client for {backend} is null");
        }

        public IBackendClient GetClient(int backendValue)
        {
            PrinterBackend backend = (PrinterBackend)backendValue;
            return GetClient(backend);
        }

        public bool IsBackendSupported(PrinterBackend backend)
        {
            return _clientCache.ContainsKey(backend);
        }
    }
}
