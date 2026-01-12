namespace Farm.Infrastructure.Services.Printers;

/// <summary>
/// Factory interface for creating printer status clients.
/// Status clients are used for real-time status monitoring of printers.
/// </summary>
public interface IPrinterStatusClientFactory
{
    /// <summary>
    /// Gets a status client for a given printer backend type.
    /// </summary>
    /// <param name="backend">The printer backend type</param>
    /// <returns>The status client implementation</returns>
    /// <exception cref="ArgumentException">Thrown if backend type is not supported</exception>
    IPrinterStatusClient GetStatusClient(PrinterBackend backend);

    /// <summary>
    /// Gets a status client for a given backend integer value.
    /// </summary>
    /// <param name="backendValue">The integer value of the printer backend</param>
    /// <returns>The status client implementation</returns>
    IPrinterStatusClient GetStatusClient(int backendValue);

    /// <summary>
    /// Checks if a backend is supported by a registered status client.
    /// </summary>
    /// <param name="backend">The printer backend type</param>
    /// <returns>True if supported, false otherwise</returns>
    bool IsBackendSupported(PrinterBackend backend);
}
