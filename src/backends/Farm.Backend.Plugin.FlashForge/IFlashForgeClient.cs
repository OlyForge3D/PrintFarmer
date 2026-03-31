using Farm.Infrastructure.Contracts.Printers;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;

namespace Farm.Backend.Plugin.FlashForge;

/// <summary>
/// Interface for FlashForge TCP client providing communication with FlashForge printers.
/// FlashForge printers use a proprietary TCP serial protocol with G-code-like commands
/// on a configurable port (default 8899, some models use 8080).
/// </summary>
public interface IFlashForgeClient : IBackendClient,
    ISupportsFileUpload,
    ISupportsStartPrint,
    ISupportsControlOperations,
    ISupportsStatus,
    ISupportsCompositeStatus,
    ISupportsPrinterInformation,
    ISupportsTemperatureControl,
    ISupportsMultiExtruderTemperatureControl,
    ISupportsConnectionTest,
    IDisposable
{
    /// <summary>
    /// The default TCP port for FlashForge printer communication.
    /// Most models use 8899, but some (e.g., Adventurer 5X) use 8080.
    /// </summary>
    public const int DefaultPort = 8899;

    /// <summary>
    /// Sends a raw FlashForge command over TCP and returns the response.
    /// </summary>
    /// <param name="host">The printer hostname or IP address</param>
    /// <param name="port">The TCP port (typically 8899 or 8080)</param>
    /// <param name="command">The FlashForge command to send (e.g., "~M601 S1")</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>The raw response string from the printer</returns>
    Task<string> SendCommandAsync(string host, int port, string command, CancellationToken ct = default);
}
