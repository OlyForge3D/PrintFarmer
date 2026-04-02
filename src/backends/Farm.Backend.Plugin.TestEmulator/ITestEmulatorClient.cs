using System.Diagnostics.CodeAnalysis;
using Farm.Infrastructure.Contracts.Printers;

namespace Farm.Backend.Plugin.TestEmulator;

/// <summary>
/// Marker interface for the TestEmulator backend client.
/// Used for DI resolution to distinguish the emulated client from real backends.
/// </summary>
[SuppressMessage("Design", "CA1040:Avoid empty interfaces", Justification = "DI marker interface for backend client resolution")]
public interface ITestEmulatorClient : IBackendClient
{
}
