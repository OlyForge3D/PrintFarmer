#pragma warning disable S2219 // Type pattern matching style is intentional

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Farm.Backend.Plugin.Core;
using Farm.Infrastructure.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.Printers;

/// <summary>
/// Default implementation of IPrinterStatusClientFactory.
/// Maps PrinterBackend enum values to status client types discovered from plugins.
/// This factory discovers available status clients at initialization and resolves them on-demand
/// using service scopes to properly handle scoped dependencies.
///
/// NOTE: Status clients are NOT registered in DI. They are instantiated on-demand
/// by this factory using dependency injection to resolve their dependencies.
/// Status clients may depend on scoped services (like backend clients), so the factory
/// creates a temporary scope for each instantiation to ensure proper dependency resolution.
/// </summary>
public class PrinterStatusClientFactory : IPrinterStatusClientFactory
{
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly Dictionary<PrinterBackend, Type> _statusClientTypeMap;
    private readonly ILogger<PrinterStatusClientFactory> _logger;

    public PrinterStatusClientFactory(
        IServiceProvider serviceProvider,
        IBackendPluginRegistry pluginRegistry,
        ILogger<PrinterStatusClientFactory> logger)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(pluginRegistry);
        ArgumentNullException.ThrowIfNull(logger);

        _serviceScopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();
        _logger = logger;
        _statusClientTypeMap = new Dictionary<PrinterBackend, Type>();

        DiscoverStatusClientTypes(pluginRegistry);
    }

    /// <summary>
    /// Discovers status client TYPES from the plugin registry.
    /// Maps BackendId to the status client type, but does NOT instantiate clients yet.
    /// Instances are resolved on-demand when GetStatusClient() is called.
    /// </summary>
    private void DiscoverStatusClientTypes(IBackendPluginRegistry pluginRegistry)
    {
        try
        {
            IEnumerable<IExtendedBackendPlugin> plugins = pluginRegistry.GetAllExtendedPlugins();
            if (plugins == null)
            {
                _logger.LogWarning("Plugin registry returned null for GetAllExtendedPlugins()");
                return;
            }

            var pluginsList = plugins.ToList();
            _logger.LogDebug($"Got {pluginsList.Count} plugins from registry");

            int discoveredCount = 0;
            var duplicateIds = new HashSet<int>();

            foreach (IExtendedBackendPlugin? plugin in pluginsList)
            {
                if (plugin == null)
                {
                    _logger.LogWarning("Plugin registry returned a null plugin object");
                    continue;
                }

                if (plugin.StatusClientType == null)
                {
                    _logger.LogDebug($"Plugin {plugin.DisplayName} has no status client type, skipping.");
                    continue;
                }

                try
                {
                    // Try to get BackendId from assembly attribute first
                    Assembly pluginAssembly = plugin.GetType().Assembly;
                    var backendAttr = pluginAssembly.GetCustomAttributes(typeof(BackendPluginAttribute), false)
                        .FirstOrDefault() as BackendPluginAttribute;

                    PrinterBackend backendId;

                    if (backendAttr != null)
                    {
                        // Use BackendId from assembly attribute if available
                        backendId = (PrinterBackend)backendAttr.BackendId;
                    }
                    else
                    {
                        // Fallback: Try to parse BackendType string to PrinterBackend enum
                        if (Enum.TryParse<PrinterBackend>(plugin.BackendType, out PrinterBackend parsedBackend))
                        {
                            backendId = parsedBackend;
                        }
                        else
                        {
                            _logger.LogWarning($"Plugin {plugin.DisplayName} assembly is missing BackendPluginAttribute and BackendType '{plugin.BackendType}' cannot be parsed to PrinterBackend enum, skipping.");
                            continue;
                        }
                    }

                    // Check for duplicate BackendId
                    if (_statusClientTypeMap.ContainsKey(backendId))
                    {
                        _logger.LogError($"DUPLICATE BackendId detected! Plugin {plugin.DisplayName} has BackendId={backendId} but it's already registered. This will cause incorrect backend routing.");
                        duplicateIds.Add((int)backendId);
                        continue;
                    }

                    // Map the BackendId to the status client type (don't instantiate yet)
                    // TRUST the plugin's StatusClientType - it's already defined in the plugin descriptor
                    // Don't do IsAssignableFrom check due to potential assembly loading issues
                    _statusClientTypeMap[backendId] = plugin.StatusClientType;
                    discoveredCount++;
                    _logger.LogInformation($"✓ Registered status client type: {plugin.DisplayName} (BackendId={backendId}, Type={plugin.StatusClientType.Name})");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"Failed to discover status client type for plugin {plugin.DisplayName}: {ex.Message}");
                }
            }

            if (discoveredCount == 0)
            {
                _logger.LogWarning("No status client types were discovered from plugins. Factory will not have any backends registered.");
            }
            else
            {
                _logger.LogInformation($"PrinterStatusClientFactory discovered {discoveredCount} status client type(s): {string.Join(", ", _statusClientTypeMap.Keys)}");
            }

            if (duplicateIds.Count > 0)
            {
                throw new InvalidOperationException($"Duplicate BackendIds detected: {string.Join(", ", duplicateIds)}. Each plugin must have a unique BackendId.");
            }
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            _logger.LogError($"Error discovering status client types from plugins: {ex.Message}");
            throw;
        }
    }

    public IPrinterStatusClient GetStatusClient(PrinterBackend backend)
    {
        if (!_statusClientTypeMap.TryGetValue(backend, out Type? statusClientType))
        {
            _logger.LogError($"✗ Unsupported printer backend requested: {backend}. Available status client backends: {string.Join(", ", _statusClientTypeMap.Keys)}");
            throw new ArgumentException($"Unsupported printer backend: {backend}", nameof(backend));
        }

        try
        {
            // Create a temporary scope to resolve the status client and its dependencies
            // Status clients depend on scoped services (like their backend client),
            // so we create them in a proper scope rather than registering them in DI.
            using IServiceScope scope = _serviceScopeFactory.CreateScope();
            IServiceProvider scopedProvider = scope.ServiceProvider;

            // Activate the status client with its dependencies resolved from the scope
            // Use ActivatorUtilities which automatically resolves constructor dependencies
            object statusClient = ActivatorUtilities.CreateInstance(scopedProvider, statusClientType) ?? throw new InvalidOperationException($"Failed to instantiate status client type: {statusClientType.Name}");

            // Verify it's assignable to IPrinterStatusClient
            // (don't use explicit cast due to potential assembly loading issues)
            if (!typeof(IPrinterStatusClient).IsAssignableFrom(statusClient.GetType()))
            {
                throw new InvalidOperationException(
                    $"Status client {statusClientType.Name} does not implement IPrinterStatusClient interface. " +
                    $"Expected interface assembly: {typeof(IPrinterStatusClient).Assembly.FullName}, " +
                    $"Status client type: {statusClient.GetType().FullName}");
            }

            // Cast is safe now - we verified it
            var typedClient = (IPrinterStatusClient)statusClient;
            _logger.LogDebug($"✓ Instantiated status client for {backend}: {statusClientType.Name}");
            return typedClient;
        }
        catch (Exception ex)
        {
            _logger.LogError($"✗ Failed to instantiate status client for {backend} (type: {statusClientType?.Name}): {ex.Message}");
            throw new InvalidOperationException($"Could not instantiate status client for backend {backend}: {ex.Message}", ex);
        }
    }

    public IPrinterStatusClient GetStatusClient(int backendValue)
    {
        PrinterBackend backend = (PrinterBackend)backendValue;
        return GetStatusClient(backend);
    }

    public bool IsBackendSupported(PrinterBackend backend)
    {
        return _statusClientTypeMap.ContainsKey(backend);
    }
}
