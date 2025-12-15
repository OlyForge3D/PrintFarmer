using System;
using System.Collections.Generic;
using System.Linq;
using Farm.Backend.Plugin.Core;
using Farm.Infrastructure;
using Farm.Infrastructure.Telemetry;
using Microsoft.Extensions.DependencyInjection;

namespace Farm.Web.Api.Services.Printers
{
    /// <summary>
    /// Default implementation of IBackendClientFactory.
    /// Maps PrinterBackend enum values to backend client interface types discovered from plugins.
    /// This factory discovers available clients at initialization and resolves them on-demand
    /// from the current DI scope (must be called from within a scoped context).
    /// 
    /// CRITICAL: Backend clients are registered as SCOPED services. This factory MUST be called
    /// from within a scoped context (e.g., from a scoped service like PrintersService).
    /// The factory does NOT create its own scope - the caller must ensure proper scoping.
    /// </summary>
    public class BackendClientFactory : IBackendClientFactory
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly Dictionary<PrinterBackend, Type> _clientTypeMap;
        private readonly IUnifiedLoggingService _logger;

        public BackendClientFactory(
            IServiceProvider serviceProvider,
            IBackendPluginRegistry pluginRegistry,
            IUnifiedLoggingService logger)
        {
            ArgumentNullException.ThrowIfNull(serviceProvider);
            ArgumentNullException.ThrowIfNull(pluginRegistry);
            ArgumentNullException.ThrowIfNull(logger);

            _serviceProvider = serviceProvider;
            _logger = logger;
            _clientTypeMap = new Dictionary<PrinterBackend, Type>();

            DiscoverAvailableClients(pluginRegistry);
        }

        /// <summary>
        /// Discovers available backend client types from the plugin registry.
        /// Maps PrinterBackend enum values to their corresponding interface types.
        /// Does NOT instantiate clients - they are resolved on-demand when requested.
        /// </summary>
        private void DiscoverAvailableClients(IBackendPluginRegistry pluginRegistry)
        {
            try
            {
                var plugins = pluginRegistry.GetAllExtendedPlugins();
                var discoveredCount = 0;
                var duplicateIds = new HashSet<int>();

                foreach (var plugin in plugins)
                {
                    if (plugin.ClientType == null)
                    {
                        _logger.LogDebug($"Plugin {plugin.DisplayName} has no client type, skipping.");
                        continue;
                    }

                    try
                    {
                        // Read BackendId from the BackendPluginAttribute on the plugin's assembly
                        var pluginAssembly = plugin.GetType().Assembly;
                        var backendAttr = pluginAssembly.GetCustomAttributes(typeof(BackendPluginAttribute), false)
                            .FirstOrDefault() as BackendPluginAttribute;

                        if (backendAttr == null)
                        {
                            _logger.LogWarning($"Plugin {plugin.DisplayName} assembly is missing BackendPluginAttribute, skipping.");
                            continue;
                        }

                        var backendId = (PrinterBackend)backendAttr.BackendId;

                        // Check for duplicate BackendId
                        if (_clientTypeMap.ContainsKey(backendId))
                        {
                            _logger.LogError($"DUPLICATE BackendId detected! Plugin {plugin.DisplayName} has BackendId={backendAttr.BackendId} but it's already registered. This will cause incorrect backend routing.");
                            duplicateIds.Add(backendAttr.BackendId);
                            continue;
                        }

                        // Map the BackendId to the client type (don't instantiate yet)
                        if (typeof(IBackendClient).IsAssignableFrom(plugin.ClientType))
                        {
                            _clientTypeMap[backendId] = plugin.ClientType;
                            discoveredCount++;
                            _logger.LogInformation($"✓ Registered backend client type: {plugin.DisplayName} (BackendId={backendAttr.BackendId}, Type={plugin.ClientType.Name})");
                        }
                        else
                        {
                            _logger.LogWarning($"Plugin {plugin.DisplayName} client type {plugin.ClientType.Name} does not implement IBackendClient.");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning($"Failed to discover client type for plugin {plugin.DisplayName}: {ex.Message}");
                    }
                }

                if (discoveredCount == 0)
                {
                    _logger.LogWarning("No backend client types were discovered from plugins. Factory will not have any backends registered.");
                }
                else
                {
                    _logger.LogInformation($"BackendClientFactory discovered {discoveredCount} backend client type(s): {string.Join(", ", _clientTypeMap.Keys)}");
                }

                if (duplicateIds.Count > 0)
                {
                    throw new InvalidOperationException($"Duplicate BackendIds detected: {string.Join(", ", duplicateIds)}. Each plugin must have a unique BackendId.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error discovering backend client types from plugins: {ex.Message}");
                throw;
            }
        }

        public IBackendClient GetClient(PrinterBackend backend)
        {
            if (!_clientTypeMap.TryGetValue(backend, out var clientType))
            {
                throw new ArgumentException($"Unsupported printer backend: {backend}", nameof(backend));
            }

            // Resolve the backend client from the current scope
            // Caller MUST be in a scoped context for this to work
            var client = _serviceProvider.GetService(clientType) as IBackendClient;
            
            if (client == null)
            {
                throw new InvalidOperationException($"Could not resolve backend client for backend {backend} (type: {clientType.Name}). Ensure you are calling this from within a scoped context (e.g., from a scoped service or HTTP request). The plugin may not have registered it correctly.");
            }

            return client;
        }

        public IBackendClient GetClient(int backendValue)
        {
            PrinterBackend backend = (PrinterBackend)backendValue;
            return GetClient(backend);
        }

        public bool IsBackendSupported(PrinterBackend backend)
        {
            return _clientTypeMap.ContainsKey(backend);
        }
    }
}
