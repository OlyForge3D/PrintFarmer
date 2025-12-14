namespace Farm.Infrastructure.Contracts.Printers;

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
