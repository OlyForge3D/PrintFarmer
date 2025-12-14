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
    /// Manages backend-specific printer status clients using plugin discovery with BackendId.
    /// This maintains extensibility by discovering status clients from the plugin registry
    /// rather than hardcoding individual client injections.
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

            DiscoverStatusClientsFromPlugins(serviceProvider, pluginRegistry);
        }

        /// <summary>
        /// Discovers and registers status clients from the plugin registry.
        /// Reads BackendId from the BackendPluginAttribute for unique identification.
        /// Detects and logs duplicate BackendIds as errors.
        /// </summary>
        private void DiscoverStatusClientsFromPlugins(IServiceProvider serviceProvider, IBackendPluginRegistry pluginRegistry)
        {
            try
            {
                var plugins = pluginRegistry.GetAllExtendedPlugins();
                var discoveredCount = 0;
                var duplicateIds = new HashSet<int>();

                foreach (var plugin in plugins)
                {
                    if (plugin.StatusClientType == null)
                    {
                        _logger.LogDebug($"Plugin {plugin.DisplayName} has no status client type, skipping.");
                        continue;
                    }

                    try
                    {
                        // Read BackendId from the BackendPluginAttribute on the plugin's assembly
                        var pluginAssembly = plugin.GetType().Assembly;
                        var backendAttr = pluginAssembly.GetCustomAttributes(typeof(Farm.Backend.Plugin.Core.BackendPluginAttribute), false)
                            .FirstOrDefault() as Farm.Backend.Plugin.Core.BackendPluginAttribute;

                        if (backendAttr == null)
                        {
                            _logger.LogWarning($"Plugin {plugin.DisplayName} assembly is missing BackendPluginAttribute, skipping.");
                            continue;
                        }

                        var backendId = (PrinterBackend)backendAttr.BackendId;

                        // Check for duplicate BackendId
                        if (_clients.ContainsKey(backendId))
                        {
                            _logger.LogError($"DUPLICATE BackendId detected! Plugin {plugin.DisplayName} has BackendId={backendAttr.BackendId} but it's already registered. This will cause incorrect backend routing.");
                            duplicateIds.Add(backendAttr.BackendId);
                            continue;
                        }

                        var statusClient = serviceProvider.GetService(plugin.StatusClientType);
                        if (statusClient is IPrinterStatusClient printerStatusClient)
                        {
                            // Register with BackendId as the unique key
                            _clients[backendId] = printerStatusClient;
                            discoveredCount++;
                            _logger.LogInformation($"✓ Registered status client: {plugin.DisplayName} (BackendId={backendAttr.BackendId}, Type={plugin.StatusClientType.Name})");
                        }
                        else
                        {
                            _logger.LogWarning($"Plugin {plugin.DisplayName} status client type {plugin.StatusClientType.Name} does not implement IPrinterStatusClient.");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning($"Failed to instantiate status client for plugin {plugin.DisplayName}: {ex.Message}");
                    }
                }

                if (discoveredCount == 0)
                {
                    _logger.LogWarning("No status clients were discovered from plugins. Factory will not have any backends registered.");
                }
                else
                {
                    _logger.LogInformation($"PrinterStatusClientFactory initialized with {discoveredCount} status client(s): {string.Join(", ", _clients.Keys)}");
                }

                if (duplicateIds.Count > 0)
                {
                    throw new InvalidOperationException($"Duplicate BackendIds detected: {string.Join(", ", duplicateIds)}. Each plugin must have a unique BackendId.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error discovering status clients from plugins: {ex.Message}");
                throw;
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
