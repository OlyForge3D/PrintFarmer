using System;
using Farm.Infrastructure;

namespace Farm.Web.Api.Services.Printers
{
    #pragma warning disable CA1040 // Marker interface intentionally empty
    /// <summary>
    /// Marker interface for all backend-specific client implementations.
    /// Implemented by IMoonrakerClient, IPrusaLinkClient, ISdcpClient, and IOctoPrintClient
    /// to enable them to be accessed through the factory interface.
    /// </summary>
    public interface IBackendClient
    {
        // Marker interface - no methods defined
    }
    #pragma warning restore CA1040

    /// <summary>
    /// Factory interface for accessing backend-specific client implementations.
    /// Provides a single abstraction for all printer backend operations,
    /// eliminating the need to pass individual backend clients into PrintersService.
    /// This makes it easy to add new backends without modifying PrintersService constructor.
    /// </summary>
    public interface IBackendClientFactory
    {
        /// <summary>
        /// Gets the backend-specific client for a given printer backend type.
        /// Returns the client as an IBackendClient which should be cast to the appropriate interface
        /// (IMoonrakerClient, IPrusaLinkClient, ISdcpClient, or IOctoPrintClient) based on the backend type.
        /// </summary>
        /// <param name="backend">The printer backend type (Moonraker, PrusaLink, SDCP, OctoPrint)</param>
        /// <returns>The backend client implementation</returns>
        /// <exception cref="ArgumentException">Thrown if backend type is not supported</exception>
        IBackendClient GetClient(PrinterBackend backend);

        /// <summary>
        /// Gets the backend-specific client for a given backend integer value.
        /// </summary>
        /// <param name="backendValue">The integer value of the printer backend</param>
        /// <returns>The backend client implementation</returns>
        IBackendClient GetClient(int backendValue);

        /// <summary>
        /// Checks if a backend is supported by a registered client.
        /// </summary>
        /// <param name="backend">The printer backend type</param>
        /// <returns>True if supported, false otherwise</returns>
        bool IsBackendSupported(PrinterBackend backend);
    }
}


