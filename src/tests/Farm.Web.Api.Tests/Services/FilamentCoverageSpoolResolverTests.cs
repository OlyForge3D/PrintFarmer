using System.Text.Json;
using Farm.Infrastructure;
using Farm.Infrastructure.Contracts.Printers;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Interfaces;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.Spoolman;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services;

public class FilamentCoverageSpoolResolverTests
{
    [Fact]
    public async Task ResolveSpoolAsync_CanonicalCentralIdentity_RejectsChangedSource()
    {
        Mock<ISpoolmanService> central = new();
        central.Setup(service => service.GetConfig())
            .Returns(new SpoolmanConfigDto("http://new-central.local"));
        FilamentCoverageSpoolResolver resolver = new(
            central.Object,
            new Mock<IBackendClientFactory>(MockBehavior.Strict).Object,
            NullLogger<FilamentCoverageSpoolResolver>.Instance);
        CanonicalSpoolIdentity identity = new(
            SpoolSourceKind.Central,
            "http://old-central.local",
            42);

        FilamentCoverageSpoolSnapshot result =
            await resolver.ResolveSpoolAsync(identity, CancellationToken.None);

        result.Spool.Should().BeNull();
        result.ErrorReason.Should().Be(FilamentCoverageSpoolResolver.ReasonSourceUnavailable);
        central.Verify(
            service => service.ListSpoolsAsync(
                It.IsAny<SpoolmanSpoolQueryParams>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ResolveSpoolAsync_CanonicalNativeIdentity_UsesHistoricalSource()
    {
        Mock<IBackendClient> native = NativeClient(
            JsonSerializer.Serialize(new[]
            {
                new { id = 42, remaining_weight = 321, material = "PLA" },
            }));
        Mock<IBackendClientFactory> factory = new();
        factory.Setup(service => service.GetClient((int)PrinterBackend.Moonraker))
            .Returns(native.Object);
        FilamentCoverageSpoolResolver resolver = new(
            new Mock<ISpoolmanService>(MockBehavior.Strict).Object,
            factory.Object,
            NullLogger<FilamentCoverageSpoolResolver>.Instance);
        CanonicalSpoolIdentity identity = new(
            SpoolSourceKind.MoonrakerNative,
            "HTTP://MOON.LOCAL:80/",
            42);

        FilamentCoverageSpoolSnapshot result =
            await resolver.ResolveSpoolAsync(identity, CancellationToken.None);

        result.Spool!.RemainingWeightG.Should().Be(321);
        native.As<ISupportsSpoolman>().Verify(
            service => service.GetSpoolmanSpoolsAsync(
                "http://moon.local",
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ResolveSpoolAsync_DuplicateNativeAndCentralId_UsesPrinterOwningSource()
    {
        Mock<IBackendClient> native = NativeClient(
            JsonSerializer.Serialize(new[] { new { id = 7, remaining_weight = 222, material = "PETG" } }));
        Mock<IBackendClientFactory> factory = new();
        factory.Setup(f => f.GetClient((int)PrinterBackend.Moonraker)).Returns(native.Object);
        Mock<ISpoolmanService> central = new();
        central.Setup(s => s.GetConfig()).Returns(new SpoolmanConfigDto("http://central.local"));
        central.Setup(s => s.ListSpoolsAsync(
                It.IsAny<SpoolmanSpoolQueryParams>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SpoolmanPagedResult<SpoolmanSpoolDto>(
                [new SpoolmanSpoolDto(7, "central", "PLA", 111, "#FFFFFF", true)],
                1));
        FilamentCoverageSpoolResolver resolver = new(
            central.Object,
            factory.Object,
            NullLogger<FilamentCoverageSpoolResolver>.Instance);
        Printer nativePrinter = PrinterWithSpool("http://moon.local", PrinterBackend.Moonraker, 7);
        Printer managedPrinter = PrinterWithSpool("http://octo.local", PrinterBackend.OctoPrint, 7);

        FilamentCoverageSpoolSnapshot nativeResult = await resolver.ResolveSpoolAsync(
            nativePrinter,
            7,
            CancellationToken.None);
        FilamentCoverageSpoolSnapshot managedResult = await resolver.ResolveSpoolAsync(
            managedPrinter,
            7,
            CancellationToken.None);

        nativeResult.Spool!.Material.Should().Be("PETG");
        nativeResult.TracksLiveConsumption.Should().BeTrue();
        managedResult.Spool!.Material.Should().Be("PLA");
        managedResult.TracksLiveConsumption.Should().BeFalse();
    }

    [Fact]
    public async Task ResolveSpoolAsync_MissingNativeSpool_DoesNotUseCollidingCentralId()
    {
        Mock<IBackendClient> native = NativeClient("[]");
        Mock<IBackendClientFactory> factory = new();
        factory.Setup(f => f.GetClient((int)PrinterBackend.Moonraker)).Returns(native.Object);
        Mock<ISpoolmanService> central = new(MockBehavior.Strict);
        FilamentCoverageSpoolResolver resolver = new(
            central.Object,
            factory.Object,
            NullLogger<FilamentCoverageSpoolResolver>.Instance);
        Printer printer = PrinterWithSpool("http://moon.local", PrinterBackend.Moonraker, 7);

        FilamentCoverageSpoolSnapshot result = await resolver.ResolveSpoolAsync(
            printer,
            7,
            CancellationToken.None);

        result.Spool.Should().BeNull();
        result.ErrorReason.Should().Be(FilamentCoverageSpoolResolver.ReasonSpoolNotFound);
        central.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ResolveAsync_DuplicateIdsAcrossNativeSources_RemainIsolatedAndBatched()
    {
        Mock<IBackendClient> nativeA = NativeClient(
            JsonSerializer.Serialize(new[] { new { id = 7, remaining_weight = 111, material = "PLA" } }));
        Mock<IBackendClient> nativeB = NativeClient(
            JsonSerializer.Serialize(new[] { new { id = 7, remaining_weight = 222, material = "PETG" } }));
        Mock<IBackendClientFactory> factory = new();
        factory.SetupSequence(f => f.GetClient((int)PrinterBackend.Moonraker))
            .Returns(nativeA.Object)
            .Returns(nativeB.Object);

        Mock<ISpoolmanService> central = new(MockBehavior.Strict);
        FilamentCoverageSpoolResolver resolver = new(
            central.Object,
            factory.Object,
            NullLogger<FilamentCoverageSpoolResolver>.Instance);
        Printer printerA = PrinterWithSpool("http://moon-a.local", PrinterBackend.Moonraker, 7);
        Printer printerB = PrinterWithSpool("http://moon-b.local", PrinterBackend.Moonraker, 7);

        IReadOnlyDictionary<Guid, IReadOnlyDictionary<int, FilamentCoverageSpoolSnapshot>> result =
            await resolver.ResolveAsync([printerA, printerB], CancellationToken.None);

        result[printerA.Id][7].Spool!.RemainingWeightG.Should().Be(111);
        result[printerB.Id][7].Spool!.RemainingWeightG.Should().Be(222);
        result[printerA.Id][7].TracksLiveConsumption.Should().BeTrue();
        result[printerB.Id][7].TracksLiveConsumption.Should().BeTrue();
        nativeA.As<ISupportsSpoolman>().Verify(
            n => n.GetSpoolmanSpoolsAsync(printerA.ServerUrl, It.IsAny<CancellationToken>()),
            Times.Once);
        nativeB.As<ISupportsSpoolman>().Verify(
            n => n.GetSpoolmanSpoolsAsync(printerB.ServerUrl, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ResolveAsync_ManagedPrinters_UseOneCentralBatch()
    {
        Mock<IBackendClientFactory> factory = new(MockBehavior.Strict);
        Mock<ISpoolmanService> central = new();
        central.Setup(s => s.GetConfig()).Returns(new SpoolmanConfigDto("http://central.local"));
        central
            .Setup(s => s.ListSpoolsAsync(It.IsAny<SpoolmanSpoolQueryParams>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SpoolmanPagedResult<SpoolmanSpoolDto>(
                [Spool(1, 100), Spool(2, 200)],
                2));

        FilamentCoverageSpoolResolver resolver = new(
            central.Object,
            factory.Object,
            NullLogger<FilamentCoverageSpoolResolver>.Instance);
        Printer printerA = PrinterWithSpool("http://octo-a.local", PrinterBackend.OctoPrint, 1);
        Printer printerB = PrinterWithSpool("http://prusa-b.local", PrinterBackend.PrusaLink, 2);

        IReadOnlyDictionary<Guid, IReadOnlyDictionary<int, FilamentCoverageSpoolSnapshot>> result =
            await resolver.ResolveAsync([printerA, printerB], CancellationToken.None);

        result[printerA.Id][1].Spool!.RemainingWeightG.Should().Be(100);
        result[printerB.Id][2].Spool!.RemainingWeightG.Should().Be(200);
        result[printerA.Id][1].TracksLiveConsumption.Should().BeFalse();
        central.Verify(
            s => s.ListSpoolsAsync(It.IsAny<SpoolmanSpoolQueryParams>(), It.IsAny<CancellationToken>()),
            Times.Once);
        central.Verify(
            s => s.GetSpoolByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
        factory.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ResolveAsync_MoonrakerPrintersSharingSource_UseOneNativeBatch()
    {
        Mock<IBackendClient> native = NativeClient(
            JsonSerializer.Serialize(new[]
            {
                new { id = 1, remaining_weight = 100, material = "PLA" },
                new { id = 2, remaining_weight = 200, material = "PETG" },
            }));
        Mock<IBackendClientFactory> factory = new();
        factory.Setup(f => f.GetClient((int)PrinterBackend.Moonraker)).Returns(native.Object);
        FilamentCoverageSpoolResolver resolver = new(
            new Mock<ISpoolmanService>(MockBehavior.Strict).Object,
            factory.Object,
            NullLogger<FilamentCoverageSpoolResolver>.Instance);
        Printer printerA = PrinterWithSpool("http://moon.local", PrinterBackend.Moonraker, 1);
        Printer printerB = PrinterWithSpool("http://moon.local/", PrinterBackend.Moonraker, 2);

        IReadOnlyDictionary<Guid, IReadOnlyDictionary<int, FilamentCoverageSpoolSnapshot>> result =
            await resolver.ResolveAsync([printerA, printerB], CancellationToken.None);

        result[printerA.Id][1].Spool!.RemainingWeightG.Should().Be(100);
        result[printerB.Id][2].Spool!.RemainingWeightG.Should().Be(200);
        native.As<ISupportsSpoolman>().Verify(
            n => n.GetSpoolmanSpoolsAsync(printerA.ServerUrl, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ResolveAsync_MoonrakerSourcesWithDifferentlyCasedPaths_RemainIsolated()
    {
        Mock<IBackendClient> nativeA = NativeClient(
            JsonSerializer.Serialize(new[] { new { id = 7, remaining_weight = 111 } }));
        Mock<IBackendClient> nativeB = NativeClient(
            JsonSerializer.Serialize(new[] { new { id = 7, remaining_weight = 222 } }));
        Mock<IBackendClientFactory> factory = new();
        factory.SetupSequence(f => f.GetClient((int)PrinterBackend.Moonraker))
            .Returns(nativeA.Object)
            .Returns(nativeB.Object);
        FilamentCoverageSpoolResolver resolver = new(
            new Mock<ISpoolmanService>(MockBehavior.Strict).Object,
            factory.Object,
            NullLogger<FilamentCoverageSpoolResolver>.Instance);
        Printer printerA = PrinterWithSpool("http://moon.local/SpoolSource", PrinterBackend.Moonraker, 7);
        Printer printerB = PrinterWithSpool("http://moon.local/spoolsource", PrinterBackend.Moonraker, 7);

        IReadOnlyDictionary<Guid, IReadOnlyDictionary<int, FilamentCoverageSpoolSnapshot>> result =
            await resolver.ResolveAsync([printerA, printerB], CancellationToken.None);

        result[printerA.Id][7].Spool!.RemainingWeightG.Should().Be(111);
        result[printerB.Id][7].Spool!.RemainingWeightG.Should().Be(222);
        nativeA.As<ISupportsSpoolman>().Verify(
            n => n.GetSpoolmanSpoolsAsync(printerA.ServerUrl, It.IsAny<CancellationToken>()),
            Times.Once);
        nativeB.As<ISupportsSpoolman>().Verify(
            n => n.GetSpoolmanSpoolsAsync(printerB.ServerUrl, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ResolveAsync_UnavailableNativeSource_ReturnsExplicitUnknownReason()
    {
        Mock<IBackendClient> native = NativeClient(null);
        Mock<IBackendClientFactory> factory = new();
        factory.Setup(f => f.GetClient((int)PrinterBackend.Moonraker)).Returns(native.Object);
        FilamentCoverageSpoolResolver resolver = new(
            new Mock<ISpoolmanService>(MockBehavior.Strict).Object,
            factory.Object,
            NullLogger<FilamentCoverageSpoolResolver>.Instance);
        Printer printer = PrinterWithSpool("http://moon.local", PrinterBackend.Moonraker, 9);

        IReadOnlyDictionary<Guid, IReadOnlyDictionary<int, FilamentCoverageSpoolSnapshot>> result =
            await resolver.ResolveAsync([printer], CancellationToken.None);

        result[printer.Id][9].Spool.Should().BeNull();
        result[printer.Id][9].ErrorReason.Should().Be("spool-source-unavailable");
    }

    [Fact]
    public async Task ResolveAsync_UnconfiguredCentralSource_ReturnsExplicitUnknownReason()
    {
        Mock<IBackendClientFactory> factory = new(MockBehavior.Strict);
        Mock<ISpoolmanService> central = new();
        central.Setup(s => s.GetConfig()).Returns((SpoolmanConfigDto?)null);
        FilamentCoverageSpoolResolver resolver = new(
            central.Object,
            factory.Object,
            NullLogger<FilamentCoverageSpoolResolver>.Instance);
        Printer printer = PrinterWithSpool("http://octo.local", PrinterBackend.OctoPrint, 10);

        IReadOnlyDictionary<Guid, IReadOnlyDictionary<int, FilamentCoverageSpoolSnapshot>> result =
            await resolver.ResolveAsync([printer], CancellationToken.None);

        result[printer.Id][10].Spool.Should().BeNull();
        result[printer.Id][10].ErrorReason.Should().Be("spoolman-unconfigured");
        central.Verify(
            s => s.ListSpoolsAsync(It.IsAny<SpoolmanSpoolQueryParams>(), It.IsAny<CancellationToken>()),
            Times.Never);
        factory.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ResolveAsync_CentralSourceFailure_ReturnsExplicitUnknownReason()
    {
        Mock<ISpoolmanService> central = new();
        central.Setup(s => s.GetConfig()).Returns(new SpoolmanConfigDto("http://central.local"));
        central
            .Setup(s => s.ListSpoolsAsync(It.IsAny<SpoolmanSpoolQueryParams>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("offline"));
        FilamentCoverageSpoolResolver resolver = new(
            central.Object,
            new Mock<IBackendClientFactory>(MockBehavior.Strict).Object,
            NullLogger<FilamentCoverageSpoolResolver>.Instance);
        Printer printer = PrinterWithSpool("http://octo.local", PrinterBackend.OctoPrint, 10);

        IReadOnlyDictionary<Guid, IReadOnlyDictionary<int, FilamentCoverageSpoolSnapshot>> result =
            await resolver.ResolveAsync([printer], CancellationToken.None);

        result[printer.Id][10].Spool.Should().BeNull();
        result[printer.Id][10].ErrorReason.Should().Be("spool-source-unavailable");
    }

    [Fact]
    public async Task ResolveAsync_MissingSpoolInOwningSource_DoesNotFallBack()
    {
        Mock<IBackendClient> native = NativeClient("[]");
        Mock<IBackendClientFactory> factory = new();
        factory.Setup(f => f.GetClient((int)PrinterBackend.Moonraker)).Returns(native.Object);
        FilamentCoverageSpoolResolver resolver = new(
            new Mock<ISpoolmanService>(MockBehavior.Strict).Object,
            factory.Object,
            NullLogger<FilamentCoverageSpoolResolver>.Instance);
        Printer printer = PrinterWithSpool("http://moon.local", PrinterBackend.Moonraker, 99);

        IReadOnlyDictionary<Guid, IReadOnlyDictionary<int, FilamentCoverageSpoolSnapshot>> result =
            await resolver.ResolveAsync([printer], CancellationToken.None);

        result[printer.Id][99].Spool.Should().BeNull();
        result[printer.Id][99].ErrorReason.Should().Be("spool-not-found");
    }

    [Fact]
    public async Task ResolveAsync_PrinterLevelSpoolBinding_IsIncludedForManagedPrimaryFallback()
    {
        Mock<ISpoolmanService> central = new();
        central.Setup(s => s.GetConfig()).Returns(new SpoolmanConfigDto("http://central.local"));
        central
            .Setup(s => s.ListSpoolsAsync(It.IsAny<SpoolmanSpoolQueryParams>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SpoolmanPagedResult<SpoolmanSpoolDto>([Spool(44, 300)], 1));
        FilamentCoverageSpoolResolver resolver = new(
            central.Object,
            new Mock<IBackendClientFactory>(MockBehavior.Strict).Object,
            NullLogger<FilamentCoverageSpoolResolver>.Instance);
        Printer printer = PrinterWithSpool("http://octo.local", PrinterBackend.OctoPrint, 1);
        printer.Toolheads.Single().CurrentSpoolId = null;
        printer.CurrentSpoolId = 44;

        IReadOnlyDictionary<Guid, IReadOnlyDictionary<int, FilamentCoverageSpoolSnapshot>> result =
            await resolver.ResolveAsync([printer], CancellationToken.None);

        result[printer.Id][44].Spool!.RemainingWeightG.Should().Be(300);
    }

    private static Mock<IBackendClient> NativeClient(string? json)
    {
        Mock<IBackendClient> client = new();
        client.As<ISupportsSpoolman>()
            .Setup(n => n.GetSpoolmanSpoolsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(json);
        return client;
    }

    private static Printer PrinterWithSpool(string url, PrinterBackend backend, int spoolId)
    {
        Printer printer = new()
        {
            Id = Guid.NewGuid(),
            Name = url,
            ServerUrl = url,
            Backend = (int)backend,
        };
        printer.Toolheads.Add(new Toolhead
        {
            Id = Guid.NewGuid(),
            PrinterId = printer.Id,
            Index = 0,
            IsPrimary = true,
            CurrentSpoolId = spoolId,
        });
        return printer;
    }

    private static SpoolmanSpoolDto Spool(int id, double remaining)
        => new(id, $"Spool {id}", "PLA", remaining, "#FFFFFF", true);
}
