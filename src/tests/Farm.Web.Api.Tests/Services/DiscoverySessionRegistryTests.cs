using Farm.Infrastructure;
using Farm.Infrastructure.Discovery;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Discovery;

namespace Farm.Web.Api.Tests.Services;

public sealed class DiscoverySessionRegistryTests
{
    [Fact]
    public void TryGetPrinter_RequiresOwnerUnlessAdministratorBypassIsExplicit()
    {
        DiscoverySessionRegistry registry = new();
        Guid ownerUserId = Guid.NewGuid();
        Guid otherUserId = Guid.NewGuid();
        registry.RegisterSession("session-1", ownerUserId);
        DiscoveryPrinterFoundDto found = registry.StorePrinter(
            CreateInternalDiscoveryPrinter())!;

        bool ownerFound = registry.TryGetPrinter(
            "session-1",
            found.Printer.DiscoveryId,
            ownerUserId,
            allowAdministratorBypass: false,
            out DiscoveredPrinterDto? ownerPrinter);
        bool otherFound = registry.TryGetPrinter(
            "session-1",
            found.Printer.DiscoveryId,
            otherUserId,
            allowAdministratorBypass: false,
            out _);
        bool administratorFound = registry.TryGetPrinter(
            "session-1",
            found.Printer.DiscoveryId,
            otherUserId,
            allowAdministratorBypass: true,
            out DiscoveredPrinterDto? administratorPrinter);

        Assert.True(ownerFound);
        Assert.False(otherFound);
        Assert.True(administratorFound);
        Assert.Equal("http://printer.internal:7125", ownerPrinter!.ServerUrl);
        Assert.Same(ownerPrinter, administratorPrinter);
    }

    [Fact]
    public void RemovePrinter_MakesOpaqueIdentifierSingleUse()
    {
        DiscoverySessionRegistry registry = new();
        Guid ownerUserId = Guid.NewGuid();
        registry.RegisterSession("session-1", ownerUserId);
        DiscoveryPrinterFoundDto found = registry.StorePrinter(
            CreateInternalDiscoveryPrinter())!;

        registry.RemovePrinter("session-1", found.Printer.DiscoveryId);
        Assert.False(registry.TryGetPrinter(
            "session-1",
            found.Printer.DiscoveryId,
            ownerUserId,
            allowAdministratorBypass: false,
            out _));
    }

    private static InternalDiscoveryPrinterFoundDto CreateInternalDiscoveryPrinter() =>
        new(
            SessionId: "session-1",
            Name: "Test Printer",
            ServerUrl: "http://printer.internal:7125",
            OriginalServerUrl: null,
            IpAddress: "192.168.1.10",
            Backend: PrinterBackend.Moonraker,
            BackendPort: 7125,
            FrontendPort: null,
            CameraStreamUrl: null,
            CameraSnapshotUrl: null,
            Manufacturer: "Test Manufacturer",
            Model: "Test Model",
            Notes: null,
            DiscoveredAt: DateTime.UtcNow,
            IsReachable: true);
}
