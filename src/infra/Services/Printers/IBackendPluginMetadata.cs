using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Services.Printers;

/// <summary>
/// Lightweight abstraction for backend plugin metadata.
/// Used by factories to discover and map backend client types without depending on the full plugin system.
/// This allows factories to be shared across different UIs (API, WPF, CLI, etc.)
/// </summary>
public interface IBackendPluginMetadata
{
    /// <summary>
    /// The PrinterBackend enum value for this backend (e.g., Moonraker, PrusaLink, SDCP, OctoPrint).
    /// </summary>
    PrinterBackend BackendType { get; }

    /// <summary>
    /// The backend client interface type for DI resolution (e.g., IMoonrakerClient).
    /// This is what the factory uses to resolve clients from the service provider.
    /// </summary>
    Type? ClientInterfaceType { get; }

    /// <summary>
    /// The backend client concrete type (fallback if ClientInterfaceType is not set).
    /// </summary>
    Type? ClientType { get; }

    /// <summary>
    /// The printer status client interface type for real-time status monitoring.
    /// </summary>
    Type? StatusClientInterfaceType { get; }

    /// <summary>
    /// The printer status client concrete type (fallback).
    /// </summary>
    Type? StatusClientType { get; }
}
