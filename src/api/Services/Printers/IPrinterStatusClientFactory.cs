using System;
using System.Collections.Generic;
using Farm.Backend.Plugin.Core;
using Farm.Infrastructure;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Services.Interfaces;

namespace Farm.Web.Api.Services.Printers
{
    /// <summary>
    /// Factory for creating printer status clients based on backend type.
    /// This service manages the creation and registry of all backend-specific status clients.
    /// Uses the plugin registry to discover status clients dynamically.
    /// </summary>
    public interface IPrinterStatusClientFactory
    {
        /// <summary>
        /// Gets the status client for a specific printer backend type.
        /// </summary>
        /// <param name="backend">The printer backend type</param>
        /// <returns>The corresponding status client implementation</returns>
        /// <exception cref="ArgumentException">Thrown if backend type is not supported</exception>
        IPrinterStatusClient GetStatusClient(PrinterBackend backend);

        /// <summary>
        /// Gets the status client for a specific backend integer value.
        /// </summary>
        /// <param name="backendValue">The integer value of the printer backend</param>
        /// <returns>The corresponding status client implementation</returns>
        IPrinterStatusClient GetStatusClient(int backendValue);

        /// <summary>
        /// Checks if a backend is supported by a registered status client.
        /// </summary>
        /// <param name="backend">The printer backend type</param>
        /// <returns>True if supported, false otherwise</returns>
        bool IsBackendSupported(PrinterBackend backend);
    }

    /// <summary>
    /// Default implementation of IPrinterStatusClientFactory.
    /// Manages backend-specific printer status clients using plugin registry.
    /// Dynamically discovers status clients from registered plugins.
    /// </summary>
    public class PrinterStatusClientFactory : IPrinterStatusClientFactory
    {
        private readonly Dictionary<PrinterBackend, IPrinterStatusClient> _clients;
        private readonly IUnifiedLoggingService _logger;

        public PrinterStatusClientFactory(
            IServiceProvider serviceProvider,
            IBackendPluginRegistry pluginRegistry,
            IUnifiedLoggingService logger)
        {
            ArgumentNullException.ThrowIfNull(serviceProvider);
            ArgumentNullException.ThrowIfNull(pluginRegistry);
            ArgumentNullException.ThrowIfNull(logger);

            _logger = logger;
            _clients = new Dictionary<PrinterBackend, IPrinterStatusClient>();

            // Discover status clients from plugins
            DiscoverStatusClientsFromPlugins(serviceProvider, pluginRegistry);
        }

        /// <summary>
        /// Discovers status clients from all registered plugins and caches them.
        /// </summary>
        private void DiscoverStatusClientsFromPlugins(IServiceProvider serviceProvider, IBackendPluginRegistry pluginRegistry)
        {
            try
            {
                foreach (var plugin in pluginRegistry.GetAllExtendedPlugins())
                {
                    try
                    {
                        // Skip plugins without status clients
                        if (plugin.StatusClientType == null || plugin.StatusClientInterfaceType == null)
                        {
                            continue;
                        }

                        // Try to get the status client from the service provider
                        var statusClient = serviceProvider.GetService(plugin.StatusClientInterfaceType);
                        if (statusClient is IPrinterStatusClient printerStatusClient)
                        {
                            // Map the backend type string to PrinterBackend enum
                            if (Enum.TryParse<PrinterBackend>(plugin.BackendType, ignoreCase: true, out var backend))
                            {
                                _clients[backend] = printerStatusClient;
                                _logger.LogInformation($"Registered status client for backend: {plugin.BackendType}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning($"Failed to register status client for plugin {plugin.BackendType}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error discovering status clients from plugins");
            }
        }

        public IPrinterStatusClient GetStatusClient(PrinterBackend backend)
        {
            if (_clients.TryGetValue(backend, out var client))
            {
                return client;
            }

            throw new ArgumentException($"Unsupported printer backend: {backend}", nameof(backend));
        }

        public IPrinterStatusClient GetStatusClient(int backendValue)
        {
            PrinterBackend backend = (PrinterBackend)backendValue;
            return GetStatusClient(backend);
        }

        public bool IsBackendSupported(PrinterBackend backend)
        {
            return _clients.ContainsKey(backend);
        }
    }
}
