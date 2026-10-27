using Farm.Backend.Plugin.FlashForge;
using Farm.Backend.Plugin.PrusaLink;
using Farm.Backend.Plugin.Sdcp;
using Farm.Infrastructure.Contracts.Printers;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Farm.Backend.Plugins.Tests.Backends;

public sealed class PrinterFileEnrichmentCapabilityTests
{
    [Fact]
    public void TryGetFileEnrichmentClientTyped_ActualCapabilityOnly_OtherFileCapabilitiesAndUnknownFailClosed()
    {
        Mock<IBackendClientFactory> clients = new();
        Mock<IBackendClient> enrichment = new();
        enrichment.As<ISupportsPrinterFileEnrichment>();
        Mock<IBackendClient> inventoryOnly = new();
        inventoryOnly.As<ISupportsFileInventory>();
        Mock<IBackendClient> legacy = new();
        legacy.As<ISupportsFileList>();
        clients.Setup(factory => factory.GetClient(PrinterBackend.PrusaLink)).Returns(enrichment.Object);
        clients.Setup(factory => factory.GetClient(PrinterBackend.Moonraker)).Returns(inventoryOnly.Object);
        clients.Setup(factory => factory.GetClient(PrinterBackend.SDCP)).Returns(legacy.Object);
        BackendCapabilityFactory factory = new(clients.Object, NullLogger<BackendCapabilityFactory>.Instance);

        Assert.True(factory.TryGetFileEnrichmentClientTyped(PrinterBackend.PrusaLink, out ISupportsPrinterFileEnrichment? native));
        Assert.Same(enrichment.Object, native);
        Assert.False(factory.TryGetFileEnrichmentClientTyped(PrinterBackend.Moonraker, out ISupportsPrinterFileEnrichment? inventoryClient));
        Assert.Null(inventoryClient);
        Assert.False(factory.TryGetFileEnrichmentClientTyped(PrinterBackend.SDCP, out ISupportsPrinterFileEnrichment? legacyClient));
        Assert.Null(legacyClient);
        Assert.False(factory.TryGetFileEnrichmentClientTyped(PrinterBackend.Unknown, out ISupportsPrinterFileEnrichment? unknownClient));
        Assert.Null(unknownClient);
    }

    [Fact]
    public void TryGetFileEnrichmentClientTyped_FileInventoryDoesNotDependOnEnrichmentAndViceVersa()
    {
        Mock<IBackendClientFactory> clients = new();
        Mock<IBackendClient> both = new();
        both.As<ISupportsFileInventory>();
        both.As<ISupportsPrinterFileEnrichment>();
        clients.Setup(factory => factory.GetClient(PrinterBackend.PrusaLink)).Returns(both.Object);
        BackendCapabilityFactory factory = new(clients.Object, NullLogger<BackendCapabilityFactory>.Instance);

        Assert.True(factory.TryGetFileInventoryClientTyped(PrinterBackend.PrusaLink, out ISupportsFileInventory? inventory));
        Assert.True(factory.TryGetFileEnrichmentClientTyped(PrinterBackend.PrusaLink, out ISupportsPrinterFileEnrichment? enrichment));
        Assert.Same(both.Object, inventory);
        Assert.Same(both.Object, enrichment);
    }

    [Fact]
    public void TryGetFileEnrichmentClientTyped_ClientLookupThrows_FailsClosed()
    {
        Mock<IBackendClientFactory> clients = new();
        BackendCapabilityFactory factory = new(clients.Object, NullLogger<BackendCapabilityFactory>.Instance);
        clients.Setup(client => client.GetClient(PrinterBackend.PrusaLink)).Throws(new InvalidOperationException("boom"));

        Assert.False(factory.TryGetFileEnrichmentClientTyped(PrinterBackend.PrusaLink, out ISupportsPrinterFileEnrichment? client));
        Assert.Null(client);
    }

    [Fact]
    public void TryGetFileEnrichmentClientTyped_FactoryWithoutAnOverride_DefaultsToUnsupported()
    {
        Mock<IBackendCapabilityFactory> factory = new() { CallBase = true };

        Assert.False(factory.Object.TryGetFileEnrichmentClientTyped(PrinterBackend.PrusaLink, out ISupportsPrinterFileEnrichment? client));
        Assert.Null(client);
    }

    [Fact]
    public void EnrichmentCapability_ImplementedByPrusaLinkAndNotByBackendsWithoutIt()
    {
        Assert.True(typeof(ISupportsPrinterFileEnrichment).IsAssignableFrom(typeof(PrusaLinkClient)));
        Assert.False(typeof(ISupportsPrinterFileEnrichment).IsAssignableFrom(typeof(SdcpClient)));
        Assert.False(typeof(ISupportsPrinterFileEnrichment).IsAssignableFrom(typeof(FlashForgeClient)));
    }
}
