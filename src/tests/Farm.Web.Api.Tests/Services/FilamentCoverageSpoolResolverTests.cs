using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Farm.Infrastructure;
using Farm.Infrastructure.Contracts.Printers;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Interfaces;
using Farm.Infrastructure.Services.Mutations;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.Spoolman;
using Farm.Infrastructure.Settings;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
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
    public async Task ResolveSpoolAsync_CanonicalCentralIdentity_CapturesOriginBeforeObservation()
    {
        var sequence = new MockSequence();
        Mock<IMutationWatermarkReader> watermarkReader = new(MockBehavior.Strict);
        watermarkReader.InSequence(sequence)
            .Setup(reader => reader.GetCurrentAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(37);
        Mock<ISpoolmanService> central = new(MockBehavior.Strict);
        central.InSequence(sequence)
            .Setup(service => service.GetConfig())
            .Returns(new SpoolmanConfigDto("http://central.local"));
        central.InSequence(sequence)
            .Setup(service => service.GetConfig())
            .Returns(new SpoolmanConfigDto("http://central.local"));
        central.InSequence(sequence)
            .Setup(service => service.ListSpoolsAsync(
                It.IsAny<SpoolmanSpoolQueryParams>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SpoolmanPagedResult<SpoolmanSpoolDto>(
                [new SpoolmanSpoolDto(42, "spool", "PLA", 321, "#FFFFFF", true)],
                1));
        FilamentCoverageSpoolResolver resolver = new(
            central.Object,
            new Mock<IBackendClientFactory>(MockBehavior.Strict).Object,
            NullLogger<FilamentCoverageSpoolResolver>.Instance,
            watermarkReader: watermarkReader.Object);
        CanonicalSpoolIdentity identity = new(
            SpoolSourceKind.Central,
            "http://central.local",
            42);

        FilamentCoverageSpoolSnapshot result =
            await resolver.ResolveSpoolAsync(identity, CancellationToken.None);

        result.Spool!.RemainingWeightG.Should().Be(321);
        result.OriginWatermark.Should().Be(37);
        watermarkReader.VerifyAll();
        central.VerifyAll();
    }

    [Fact]
    public async Task ResolveSpoolAsync_CanonicalNativeIdentity_PreservesConfiguredSubPath()
    {
        await using AppDbContext db = CreateContext();
        db.Printers.Add(PrinterWithSpool(
            "HTTP://MOON.LOCAL:80/proxy/",
            PrinterBackend.Moonraker,
            42));
        await db.SaveChangesAsync();
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
            NullLogger<FilamentCoverageSpoolResolver>.Instance,
            db);
        CanonicalSpoolIdentity identity = new(
            SpoolSourceKind.MoonrakerNative,
            "http://moon.local/proxy",
            42);

        FilamentCoverageSpoolSnapshot result =
            await resolver.ResolveSpoolAsync(identity, CancellationToken.None);

        result.Spool!.RemainingWeightG.Should().Be(321);
        native.As<ISupportsSpoolman>().Verify(
            service => service.GetSpoolmanSpoolsAsync(
                "http://moon.local/proxy/",
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ResolveSpoolAsync_UnconfiguredNativeIdentity_RejectsBeforeOutboundCall()
    {
        await using AppDbContext db = CreateContext();
        db.Printers.Add(PrinterWithSpool(
            "http://known-moon.local",
            PrinterBackend.Moonraker,
            42));
        await db.SaveChangesAsync();
        Mock<IBackendClientFactory> factory = new(MockBehavior.Strict);
        FilamentCoverageSpoolResolver resolver = new(
            new Mock<ISpoolmanService>(MockBehavior.Strict).Object,
            factory.Object,
            NullLogger<FilamentCoverageSpoolResolver>.Instance,
            db);
        CanonicalSpoolIdentity identity = new(
            SpoolSourceKind.MoonrakerNative,
            "http://169.254.169.254/latest/meta-data",
            42);

        FilamentCoverageSpoolSnapshot result =
            await resolver.ResolveSpoolAsync(identity, CancellationToken.None);

        result.Spool.Should().BeNull();
        result.ErrorReason.Should().Be(
            FilamentCoverageSpoolResolver.ReasonSourceUnavailable);
        factory.VerifyNoOtherCalls();
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
    public async Task ResolveAsync_DistinctNativeSources_ResolveConcurrently()
    {
        TaskCompletionSource<bool> sourceAStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> sourceBStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> releaseSources = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Mock<IBackendClient> native = new();
        native.As<ISupportsSpoolman>()
            .Setup(service => service.GetSpoolmanSpoolsAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Returns(async (string baseUrl, CancellationToken ct) =>
            {
                TaskCompletionSource<bool> started = baseUrl.Contains("moon-a", StringComparison.Ordinal)
                    ? sourceAStarted
                    : sourceBStarted;
                _ = started.TrySetResult(true);
                await releaseSources.Task.WaitAsync(ct);
                int spoolId = ReferenceEquals(started, sourceAStarted) ? 1 : 2;
                return JsonSerializer.Serialize(new[]
                {
                    new { id = spoolId, remaining_weight = 100, material = "PLA" },
                });
            });
        Mock<IBackendClientFactory> factory = new();
        factory.Setup(service => service.GetClient((int)PrinterBackend.Moonraker))
            .Returns(native.Object);
        FilamentCoverageSpoolResolver resolver = new(
            new Mock<ISpoolmanService>(MockBehavior.Strict).Object,
            factory.Object,
            NullLogger<FilamentCoverageSpoolResolver>.Instance);
        Printer printerA = PrinterWithSpool("http://moon-a.local", PrinterBackend.Moonraker, 1);
        Printer printerB = PrinterWithSpool("http://moon-b.local", PrinterBackend.Moonraker, 2);

        Task<IReadOnlyDictionary<Guid, IReadOnlyDictionary<int, FilamentCoverageSpoolSnapshot>>> resolving =
            resolver.ResolveAsync([printerA, printerB], CancellationToken.None);
        try
        {
            await Task.WhenAll(sourceAStarted.Task, sourceBStarted.Task)
                .WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            _ = releaseSources.TrySetResult(true);
        }

        IReadOnlyDictionary<Guid, IReadOnlyDictionary<int, FilamentCoverageSpoolSnapshot>> result =
            await resolving;
        result[printerA.Id][1].Spool!.RemainingWeightG.Should().Be(100);
        result[printerB.Id][2].Spool!.RemainingWeightG.Should().Be(100);
    }

    [Fact]
    public async Task ResolveAsync_ManyDistinctNativeSources_CompletesWellUnderReadinessTimeout()
    {
        // Regression evidence for issue #2118: the mobile client's own readiness/health
        // probe (ServerManagementViewModel.check) uses a 5s per-request timeout. Before
        // the bounded-concurrency fix, ResolveAsync awaited each distinct spool source
        // sequentially, so a fleet with enough independent sources could blow past that
        // budget even though every individual source was healthy. This test proves the
        // fixed, bounded-concurrency (max 4) path stays well under that timeout for a
        // source count whose *sequential* total would have exceeded it.
        const int sourceCount = 20;
        const int perSourceDelayMs = 300;
        TimeSpan readinessTimeout = TimeSpan.FromSeconds(5);
        TimeSpan hypotheticalSequentialDuration = TimeSpan.FromMilliseconds(sourceCount * perSourceDelayMs);
        hypotheticalSequentialDuration.Should().BeGreaterThan(readinessTimeout,
            "the scenario is only meaningful evidence if the old sequential path would have missed the readiness budget");

        Mock<IBackendClient> native = new();
        native.As<ISupportsSpoolman>()
            .Setup(service => service.GetSpoolmanSpoolsAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Returns(async (string baseUrl, CancellationToken ct) =>
            {
                await Task.Delay(perSourceDelayMs, ct);
                int spoolId = int.Parse(baseUrl.Split('-')[^1].Split('.')[0], CultureInfo.InvariantCulture);
                return JsonSerializer.Serialize(new[]
                {
                    new { id = spoolId, remaining_weight = 100, material = "PLA" },
                });
            });
        Mock<IBackendClientFactory> factory = new();
        factory.Setup(service => service.GetClient((int)PrinterBackend.Moonraker))
            .Returns(native.Object);
        FilamentCoverageSpoolResolver resolver = new(
            new Mock<ISpoolmanService>(MockBehavior.Strict).Object,
            factory.Object,
            NullLogger<FilamentCoverageSpoolResolver>.Instance);
        List<Printer> printers = Enumerable.Range(1, sourceCount)
            .Select(i => PrinterWithSpool($"http://moon-{i}.local", PrinterBackend.Moonraker, i))
            .ToList();

        Stopwatch stopwatch = Stopwatch.StartNew();
        IReadOnlyDictionary<Guid, IReadOnlyDictionary<int, FilamentCoverageSpoolSnapshot>> result =
            await resolver.ResolveAsync(printers, CancellationToken.None);
        stopwatch.Stop();

        stopwatch.Elapsed.Should().BeLessThan(readinessTimeout);
        result.Should().HaveCount(sourceCount);
        for (int i = 1; i <= sourceCount; i++)
        {
            result[printers[i - 1].Id][i].Spool!.RemainingWeightG.Should().Be(100);
        }
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
    public async Task ResolveAsync_DarkNativeSource_DegradesWithinConfiguredTimeout()
    {
        // Regression evidence for issue #2118 (re-opened): the previous fix bounded how
        // many spool sources are read at once but never bounded how long a single source
        // may take. A powered-down Moonraker printer that still holds its address
        // black-holes packets, so the read inherited the backend's print-control timeout
        // (BackendTimeoutSettings.PrintControlTimeoutSeconds = 60) and stalled the whole
        // fleet projection. The mobile readiness gate allows 10s per probe, so both
        // /api/attention and /api/printers/filament-coverage were reported unavailable.
        const int sourceTimeoutMs = 300;
        TimeSpan inheritedPrintControlTimeout = TimeSpan.FromSeconds(60);
        Mock<IBackendClientFactory> factory = new();
        factory.Setup(f => f.GetClient((int)PrinterBackend.Moonraker)).Returns(DarkNativeClient().Object);
        FilamentCoverageSpoolResolver resolver = new(
            new Mock<ISpoolmanService>(MockBehavior.Strict).Object,
            factory.Object,
            NullLogger<FilamentCoverageSpoolResolver>.Instance,
            settingsService: SettingsWithSourceTimeout(sourceTimeoutMs));
        Printer printer = PrinterWithSpool("http://dark-moon.local", PrinterBackend.Moonraker, 7);

        Stopwatch stopwatch = Stopwatch.StartNew();
        IReadOnlyDictionary<Guid, IReadOnlyDictionary<int, FilamentCoverageSpoolSnapshot>> result =
            await resolver.ResolveAsync([printer], CancellationToken.None);
        stopwatch.Stop();

        stopwatch.Elapsed.Should().BeLessThan(inheritedPrintControlTimeout);
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5),
            "the bounded read must resolve well inside the mobile client's 10s readiness budget");
        result[printer.Id][7].Spool.Should().BeNull();
        result[printer.Id][7].ErrorReason.Should().Be("spool-source-unavailable");
    }

    [Fact]
    public async Task ResolveAsync_DarkNativeSource_DoesNotStallHealthySources()
    {
        // The reported symptom was fleet-wide: one unreachable printer degraded coverage
        // for every other printer because ResolveAsync awaits all sources together.
        const string darkUrl = "http://dark-moon.local";
        Mock<IBackendClient> native = new();
        native.As<ISupportsSpoolman>()
            .Setup(n => n.GetSpoolmanSpoolsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (string baseUrl, CancellationToken ct) =>
            {
                if (string.Equals(baseUrl, darkUrl, StringComparison.OrdinalIgnoreCase))
                {
                    await SwallowedHangAsync(ct);
                    return null;
                }

                return JsonSerializer.Serialize(new[]
                {
                    new { id = 1, remaining_weight = 250, material = "PLA" },
                });
            });
        Mock<IBackendClientFactory> factory = new();
        factory.Setup(f => f.GetClient((int)PrinterBackend.Moonraker)).Returns(native.Object);
        FilamentCoverageSpoolResolver resolver = new(
            new Mock<ISpoolmanService>(MockBehavior.Strict).Object,
            factory.Object,
            NullLogger<FilamentCoverageSpoolResolver>.Instance,
            settingsService: SettingsWithSourceTimeout(300));
        Printer healthyPrinter = PrinterWithSpool("http://good-moon.local", PrinterBackend.Moonraker, 1);
        Printer darkPrinter = PrinterWithSpool(darkUrl, PrinterBackend.Moonraker, 2);

        Stopwatch stopwatch = Stopwatch.StartNew();
        IReadOnlyDictionary<Guid, IReadOnlyDictionary<int, FilamentCoverageSpoolSnapshot>> result =
            await resolver.ResolveAsync([healthyPrinter, darkPrinter], CancellationToken.None);
        stopwatch.Stop();

        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
        result[healthyPrinter.Id][1].Spool!.RemainingWeightG.Should().Be(250);
        result[healthyPrinter.Id][1].ErrorReason.Should().BeNull();
        result[darkPrinter.Id][2].ErrorReason.Should().Be("spool-source-unavailable");
    }

    [Fact]
    public async Task ResolveAsync_CallerCancellation_PropagatesInsteadOfDegrading()
    {
        // The Moonraker client swallows every exception from its Spoolman proxy and
        // returns a null body, so without the resolver's explicit post-call cancellation
        // check a genuine caller cancellation would be silently recorded as an unavailable
        // source. The mock here swallows cancellation exactly as production does, so this
        // test genuinely exercises that check rather than an exception escaping the mock.
        using SemaphoreSlim sourceReadStarted = new(0);
        Mock<IBackendClient> native = new();
        native.As<ISupportsSpoolman>()
            .Setup(n => n.GetSpoolmanSpoolsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (string _, CancellationToken ct) =>
            {
                sourceReadStarted.Release();
                await SwallowedHangAsync(ct);
                return (string?)null;
            });
        Mock<IBackendClientFactory> factory = new();
        factory.Setup(f => f.GetClient((int)PrinterBackend.Moonraker)).Returns(native.Object);
        FilamentCoverageSpoolResolver resolver = new(
            new Mock<ISpoolmanService>(MockBehavior.Strict).Object,
            factory.Object,
            NullLogger<FilamentCoverageSpoolResolver>.Instance,
            // Long enough that the per-source deadline cannot fire first, so the only
            // possible cancellation is the caller's.
            settingsService: SettingsWithSourceTimeout(30_000));
        Printer printer = PrinterWithSpool("http://dark-moon.local", PrinterBackend.Moonraker, 7);
        using CancellationTokenSource cts = new();

        Task<IReadOnlyDictionary<Guid, IReadOnlyDictionary<int, FilamentCoverageSpoolSnapshot>>> resolve =
            resolver.ResolveAsync([printer], cts.Token);
        // Cancel only once the source read is genuinely in flight, so this cannot pass by
        // short-circuiting at the semaphore or before the request was ever issued.
        (await sourceReadStarted.WaitAsync(UnreachableHostWatchdog)).Should().BeTrue(
            "the source read must start before the caller cancels");
        await cts.CancelAsync();

        OperationCanceledException? canceled = null;
        try
        {
            _ = await resolve;
        }
        catch (OperationCanceledException ex)
        {
            canceled = ex;
        }

        canceled.Should().NotBeNull("caller cancellation must propagate rather than degrade to an unavailable source");
        canceled!.CancellationToken.Should().Be(cts.Token, "the caller's cancellation must surface, not a per-source timeout");
    }

    [Fact]
    public async Task ResolveAsync_UnreachableCentralSource_DegradesWithinConfiguredTimeout()
    {
        // Central Spoolman is read through the same fan-out, so an unreachable central
        // host must not stall coverage any longer than a dark printer does.
        Mock<ISpoolmanService> central = new();
        central.Setup(s => s.GetConfig()).Returns(new SpoolmanConfigDto("http://central.local"));
        central
            .Setup(s => s.ListSpoolsAsync(It.IsAny<SpoolmanSpoolQueryParams>(), It.IsAny<CancellationToken>()))
            .Returns(async (SpoolmanSpoolQueryParams _, CancellationToken ct) =>
            {
                // Mirrors the real SpoolmanService, which catches every exception -
                // cancellation included - and returns an EMPTY page rather than throwing.
                // A mock that threw would hide the fact that a timed-out central read used
                // to fall through and be reported as `spool-not-found`.
                await SwallowedHangAsync(ct);
                return new SpoolmanPagedResult<SpoolmanSpoolDto>([], 0);
            });
        FilamentCoverageSpoolResolver resolver = new(
            central.Object,
            new Mock<IBackendClientFactory>(MockBehavior.Strict).Object,
            NullLogger<FilamentCoverageSpoolResolver>.Instance,
            settingsService: SettingsWithSourceTimeout(300));
        Printer printer = PrinterWithSpool("http://octo.local", PrinterBackend.OctoPrint, 10);

        Stopwatch stopwatch = Stopwatch.StartNew();
        IReadOnlyDictionary<Guid, IReadOnlyDictionary<int, FilamentCoverageSpoolSnapshot>> result =
            await resolver.ResolveAsync([printer], CancellationToken.None);
        stopwatch.Stop();

        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
        result[printer.Id][10].Spool.Should().BeNull();
        result[printer.Id][10].ErrorReason.Should().Be("spool-source-unavailable");
    }

    [Fact]
    public async Task ResolveAsync_SourceFanOut_AllowsMoreThanFourConcurrentReads()
    {
        // Guards MaxConcurrentSourceRequests. The constant is load-bearing once each source
        // carries its own deadline: dark sources serialise into ceil(sources / limit)
        // timeout waves, so a silent regression back to 4 would double the worst-case wall
        // clock for a farm with several printers powered down and push it back past the
        // mobile client's 10s readiness budget. Nothing else in the suite fails if the
        // limit changes, so assert it directly.
        const int requiredConcurrency = 5;
        int[] active = [0];
        int[] peak = [0];
        SemaphoreSlim arrivals = new(0);
        using SemaphoreSlim release = new(0);
        Mock<IBackendClient> native = new();
        native.As<ISupportsSpoolman>()
            .Setup(n => n.GetSpoolmanSpoolsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (string baseUrl, CancellationToken ct) =>
            {
                int inFlight = Interlocked.Increment(ref active[0]);
                int observed = Volatile.Read(ref peak[0]);
                while (inFlight > observed)
                {
                    int previous = Interlocked.CompareExchange(ref peak[0], inFlight, observed);
                    if (previous == observed) { break; }
                    observed = previous;
                }

                _ = arrivals.Release();
                _ = await release.WaitAsync(UnreachableHostWatchdog, ct);
                _ = Interlocked.Decrement(ref active[0]);
                int spoolId = int.Parse(baseUrl.Split('-')[^1].Split('.')[0], CultureInfo.InvariantCulture);
                return JsonSerializer.Serialize(new[]
                {
                    new { id = spoolId, remaining_weight = 100, material = "PLA" },
                });
            });
        Mock<IBackendClientFactory> factory = new();
        factory.Setup(f => f.GetClient((int)PrinterBackend.Moonraker)).Returns(native.Object);
        FilamentCoverageSpoolResolver resolver = new(
            new Mock<ISpoolmanService>(MockBehavior.Strict).Object,
            factory.Object,
            NullLogger<FilamentCoverageSpoolResolver>.Instance,
            settingsService: SettingsWithSourceTimeout(30_000));
        List<Printer> printers = Enumerable.Range(1, 8)
            .Select(i => PrinterWithSpool($"http://moon-{i}.local", PrinterBackend.Moonraker, i))
            .ToList();

        Task<IReadOnlyDictionary<Guid, IReadOnlyDictionary<int, FilamentCoverageSpoolSnapshot>>> resolve =
            resolver.ResolveAsync(printers, CancellationToken.None);
        for (int i = 0; i < requiredConcurrency; i++)
        {
            bool arrived = await arrivals.WaitAsync(TimeSpan.FromSeconds(10));
            arrived.Should().BeTrue(
                $"at least {requiredConcurrency} sources must be readable at once, but only {i} started");
        }

        release.Release(8);
        _ = await resolve;

        Volatile.Read(ref peak[0]).Should().BeGreaterThanOrEqualTo(requiredConcurrency);
    }

    [Fact]
    public async Task ResolveAsync_FleetOfDarkSources_StaysWithinFleetDeadline()
    {
        // The per-source deadline bounds a source, NOT the endpoint. Dark sources each hold
        // a fan-out slot for their full timeout, so N of them serialise into
        // ceil(N / MaxConcurrentSourceRequests) waves and total latency still grows with
        // fleet size - which is exactly how the original bug reached the mobile client's 10s
        // readiness budget.
        //
        // The numbers here are chosen so the test FAILS if the fleet deadline is removed:
        // 24 dark sources at concurrency 8 is 3 waves, and at a 5s per-source timeout that
        // is ~15s of pure source-wait. Only the 1s fleet budget can bring it under the 5s
        // assertion below, so a per-source-only implementation cannot pass.
        Mock<IBackendClient> native = new();
        native.As<ISupportsSpoolman>()
            .Setup(n => n.GetSpoolmanSpoolsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (string _, CancellationToken ct) =>
            {
                await SwallowedHangAsync(ct);
                return (string?)null;
            });
        Mock<IBackendClientFactory> factory = new();
        factory.Setup(f => f.GetClient((int)PrinterBackend.Moonraker)).Returns(native.Object);
        FilamentCoverageSpoolResolver resolver = new(
            new Mock<ISpoolmanService>(MockBehavior.Strict).Object,
            factory.Object,
            NullLogger<FilamentCoverageSpoolResolver>.Instance,
            settingsService: SettingsWithSourceTimeout(5_000, fleetTimeoutMs: 1_000));
        List<Printer> printers = Enumerable.Range(1, 24)
            .Select(i => PrinterWithSpool($"http://dark-{i}.local", PrinterBackend.Moonraker, i))
            .ToList();

        Stopwatch stopwatch = Stopwatch.StartNew();
        IReadOnlyDictionary<Guid, IReadOnlyDictionary<int, FilamentCoverageSpoolSnapshot>> result =
            await resolver.ResolveAsync(printers, CancellationToken.None);
        stopwatch.Stop();

        // Comfortably above the 1s budget so this asserts the bound exists rather than
        // timing the CI host, but far below both the ~15s three-wave worst case and even a
        // single 5s per-source wave, so neither can sneak past.
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));

        // Sources queued behind the gate when the deadline fired were never read at all.
        // They must still be reported, and reported as unavailable - never as
        // `spool-not-found`, which would be an affirmative claim about a source nobody
        // reached, and never dropped from the projection.
        result.Should().HaveCount(printers.Count);
        foreach (Printer printer in printers)
        {
            FilamentCoverageSpoolSnapshot snapshot = result[printer.Id].Values.Single();
            snapshot.Spool.Should().BeNull();
            snapshot.ErrorReason.Should().Be(
                "spool-source-unavailable",
                "a source cut short by the fleet deadline was never reached, so it must not be reported as spool-not-found");
        }

        // Deliberately NOT asserting an upper bound on dispatch count here. A previous revision
        // used Times.AtMost(MaxConcurrentSourceRequests) on the premise that each read holds its
        // slot for the 5s per-source timeout, so only one wave could dispatch inside the 1s fleet
        // budget. That premise is false, and the assertion was flaky: the fleet deadline cuts the
        // in-flight reads FIRST, so all their slots are released at ~1s via the finally block.
        // Queued waiters are then parked on WaitAsync with an already-cancelled token, and
        // SemaphoreSlim completes a dequeued waiter concurrently with its cancellation callback -
        // so a waiter that wins that race acquires a permit and dispatches. Observed at 9 and 14
        // dispatches under a loaded suite while passing consistently when this class runs alone.
        //
        // Those late dispatches are harmless in production: the source links off an already
        // cancelled budget token, the backend client returns immediately without network I/O, and
        // the result still degrades to spool-source-unavailable. The invariant that actually
        // matters - every source reported, none claimed spool-not-found - is asserted
        // deterministically by the loop above, which is unaffected by the race.
    }

    [Fact]
    public async Task ResolveAsync_CentralSourceCallerCancellation_PropagatesInsteadOfDegrading()
    {
        // The central path needs its own cancellation test because SpoolmanService swallows
        // every exception - cancellation included - and returns an EMPTY page. Without the
        // resolver's explicit post-call check, a cancelled read would fall through to
        // BuildSnapshots and be reported as `spool-not-found`: an affirmative "that spool
        // does not exist" claim about a source that was never actually read.
        using SemaphoreSlim sourceReadStarted = new(0);
        Mock<ISpoolmanService> central = new();
        central.Setup(s => s.GetConfig()).Returns(new SpoolmanConfigDto("http://central.local"));
        central
            .Setup(s => s.ListSpoolsAsync(It.IsAny<SpoolmanSpoolQueryParams>(), It.IsAny<CancellationToken>()))
            .Returns(async (SpoolmanSpoolQueryParams _, CancellationToken ct) =>
            {
                sourceReadStarted.Release();
                await SwallowedHangAsync(ct);
                return new SpoolmanPagedResult<SpoolmanSpoolDto>([], 0);
            });
        FilamentCoverageSpoolResolver resolver = new(
            central.Object,
            new Mock<IBackendClientFactory>(MockBehavior.Strict).Object,
            NullLogger<FilamentCoverageSpoolResolver>.Instance,
            // Long enough that neither deadline can fire first, so the only possible
            // cancellation is the caller's.
            settingsService: SettingsWithSourceTimeout(30_000, fleetTimeoutMs: 60_000));
        Printer printer = PrinterWithSpool("http://octo.local", PrinterBackend.OctoPrint, 10);
        using CancellationTokenSource cts = new();

        Task<IReadOnlyDictionary<Guid, IReadOnlyDictionary<int, FilamentCoverageSpoolSnapshot>>> resolve =
            resolver.ResolveAsync([printer], cts.Token);
        (await sourceReadStarted.WaitAsync(UnreachableHostWatchdog)).Should().BeTrue(
            "the central read must start before the caller cancels");
        await cts.CancelAsync();

        OperationCanceledException? canceled = null;
        try
        {
            _ = await resolve;
        }
        catch (OperationCanceledException ex)
        {
            canceled = ex;
        }

        canceled.Should().NotBeNull("caller cancellation must propagate rather than degrade to an unavailable source");
        canceled!.CancellationToken.Should().Be(cts.Token, "the caller's cancellation must surface, not a timeout");
    }

    [Fact]
    public async Task ResolveAsync_CallerCancellationWhileSourcesQueued_ReportsCallerTokenUniformly()
    {
        // UNIFORMITY GUARD, NOT A REGRESSION TEST - it cannot fail by construction, and that
        // was verified by mutation: deleting the queued-path normalising catch arm in
        // FilamentCoverageSpoolResolver leaves this test passing.
        //
        // ResolveAsync surfaces cancellation via Task.WhenAll, which reports the LOWEST-INDEX
        // cancelled task's token. SemaphoreSlim.WaitAsync completes synchronously while permits
        // remain, so indices 0..MaxConcurrentSourceRequests-1 always run straight through the
        // gate and are always in flight; only higher indices park. A queued source therefore can
        // never be the task whose token escapes, so the queued-path defect is unobservable here.
        //
        // Kept because it documents the intended contract across the fan-out and would catch a
        // future change that consumed sources in COMPLETION order - Task.WhenEach, or a channel -
        // since a source cancelled at the gate unwinds faster than one unwinding through a backend
        // call, so a queued task could then be the first observed. Awaiting individually in index
        // order would NOT expose it: index 0 is still in flight and still wins.
        const int fleetSize = 20;
        using SemaphoreSlim readsStarted = new(0);
        Mock<IBackendClient> native = new();
        native.As<ISupportsSpoolman>()
            .Setup(n => n.GetSpoolmanSpoolsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (string _, CancellationToken ct) =>
            {
                readsStarted.Release();
                await SwallowedHangAsync(ct);
                return (string?)null;
            });
        Mock<IBackendClientFactory> factory = new();
        factory.Setup(f => f.GetClient((int)PrinterBackend.Moonraker)).Returns(native.Object);
        FilamentCoverageSpoolResolver resolver = new(
            new Mock<ISpoolmanService>(MockBehavior.Strict).Object,
            factory.Object,
            NullLogger<FilamentCoverageSpoolResolver>.Instance,
            // Both deadlines far out, so the only cancellation that can occur is the
            // caller's - the fleet deadline must not be what ends this test.
            settingsService: SettingsWithSourceTimeout(30_000, fleetTimeoutMs: 60_000));
        List<Printer> printers = Enumerable.Range(1, fleetSize)
            .Select(i => PrinterWithSpool($"http://moon-{i}.local", PrinterBackend.Moonraker, i))
            .ToList();
        using CancellationTokenSource cts = new();

        Task<IReadOnlyDictionary<Guid, IReadOnlyDictionary<int, FilamentCoverageSpoolSnapshot>>> resolve =
            resolver.ResolveAsync(printers, cts.Token);

        // Fill the gate so the remaining sources are genuinely parked on WaitAsync, then
        // cancel. fleetSize exceeds MaxConcurrentSourceRequests, so some must still be queued.
        for (int i = 0; i < 8; i++)
        {
            (await readsStarted.WaitAsync(UnreachableHostWatchdog)).Should().BeTrue(
                "the fan-out must saturate before the caller cancels");
        }

        await cts.CancelAsync();

        OperationCanceledException? canceled = null;
        try
        {
            _ = await resolve;
        }
        catch (OperationCanceledException ex)
        {
            canceled = ex;
        }

        canceled.Should().NotBeNull("caller cancellation must propagate rather than degrade to an unavailable source");
        canceled!.CancellationToken.Should().Be(
            cts.Token,
            "cancellation must surface the caller's token, never the internal budget token");
    }

    [Fact]
    public async Task ResolveAsync_ReadsSpoolBudgetOnceForTheWholeFleet()
    {
        // Every concurrent source used to read SpoolSourceTimeout independently.
        // SettingsService.Get enumerates a shared dictionary, so a concurrent settings save
        // could throw mid-enumeration and silently drop SOME sources back to the default
        // timeout while others kept the configured one. Reading the budget once per resolve
        // removes that race, so pin the call count.
        Mock<ISettingsService> settings = new();
        settings.Setup(s => s.Get<SpoolCoverageSettings>())
            .Returns(new SpoolCoverageSettings { SpoolSourceTimeoutMs = 5_000, FleetResolveTimeoutMs = 60_000 });
        Mock<IBackendClient> native = new();
        native.As<ISupportsSpoolman>()
            .Setup(n => n.GetSpoolmanSpoolsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string baseUrl, CancellationToken _) =>
            {
                int spoolId = int.Parse(baseUrl.Split('-')[^1].Split('.')[0], CultureInfo.InvariantCulture);
                return JsonSerializer.Serialize(new[]
                {
                    new { id = spoolId, remaining_weight = 100, material = "PLA" },
                });
            });
        Mock<IBackendClientFactory> factory = new();
        factory.Setup(f => f.GetClient((int)PrinterBackend.Moonraker)).Returns(native.Object);
        FilamentCoverageSpoolResolver resolver = new(
            new Mock<ISpoolmanService>(MockBehavior.Strict).Object,
            factory.Object,
            NullLogger<FilamentCoverageSpoolResolver>.Instance,
            settingsService: settings.Object);
        List<Printer> printers = Enumerable.Range(1, 6)
            .Select(i => PrinterWithSpool($"http://moon-{i}.local", PrinterBackend.Moonraker, i))
            .ToList();

        _ = await resolver.ResolveAsync(printers, CancellationToken.None);

        settings.Verify(s => s.Get<SpoolCoverageSettings>(), Times.Once);
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

    [Fact]
    public async Task ResolveAsync_PrinterKnownOfflineInStatusCache_SkipsNetworkReadAndStaysBounded()
    {
        // Regression evidence for issue #2118: FilamentCoverageSpoolResolver.ResolveAsync
        // awaited every distinct spool source with Task.WhenAll, so a single printer that
        // is powered down but still holds its network address (black-holing packets
        // instead of refusing the connection) stalled the whole fleet projection for a
        // full backend timeout. The offline printer's source must never even be attempted
        // when the status cache already knows it is unreachable, and a healthy sibling on
        // a distinct source must still resolve normally in the same call.
        TaskCompletionSource<string?> neverCompletes = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Mock<IBackendClient> offlineNative = new(MockBehavior.Strict);
        offlineNative.As<ISupportsSpoolman>()
            .Setup(n => n.GetSpoolmanSpoolsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(neverCompletes.Task);
        Mock<IBackendClient> onlineNative = NativeClient(
            JsonSerializer.Serialize(new[] { new { id = 5, remaining_weight = 400, material = "PLA" } }));
        Mock<IBackendClientFactory> factory = new();
        factory.SetupSequence(f => f.GetClient((int)PrinterBackend.Moonraker))
            .Returns(offlineNative.Object)
            .Returns(onlineNative.Object);

        Printer offlinePrinter = PrinterWithSpool("http://moon-offline.local", PrinterBackend.Moonraker, 1);
        Printer onlinePrinter = PrinterWithSpool("http://moon-online.local", PrinterBackend.Moonraker, 5);

        Mock<IPrinterStatusCacheReader> statusCache = new();
        statusCache.Setup(c => c.GetStatus(offlinePrinter.Id))
            .Returns(new PrinterStatusDto(offlinePrinter.Id, IsOnline: false, State: null));
        statusCache.Setup(c => c.GetStatus(onlinePrinter.Id))
            .Returns(new PrinterStatusDto(onlinePrinter.Id, IsOnline: true, State: "idle"));

        FilamentCoverageSpoolResolver resolver = new(
            new Mock<ISpoolmanService>(MockBehavior.Strict).Object,
            factory.Object,
            NullLogger<FilamentCoverageSpoolResolver>.Instance,
            statusCache: statusCache.Object);

        Stopwatch stopwatch = Stopwatch.StartNew();
        IReadOnlyDictionary<Guid, IReadOnlyDictionary<int, FilamentCoverageSpoolSnapshot>> result =
            await resolver.ResolveAsync([offlinePrinter, onlinePrinter], CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(5));
        stopwatch.Stop();

        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
        result[offlinePrinter.Id][1].Spool.Should().BeNull();
        result[offlinePrinter.Id][1].ErrorReason.Should().Be(FilamentCoverageSpoolResolver.ReasonSourceUnavailable);
        result[onlinePrinter.Id][5].Spool!.RemainingWeightG.Should().Be(400);
        offlineNative.As<ISupportsSpoolman>().Verify(
            n => n.GetSpoolmanSpoolsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private static Mock<IBackendClient> NativeClient(string? json)
    {
        Mock<IBackendClient> client = new();
        client.As<ISupportsSpoolman>()
            .Setup(n => n.GetSpoolmanSpoolsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(json);
        return client;
    }

    /// <summary>
    /// A spool source that never answers, modelling a printer that is powered down but
    /// still holds its address: it black-holes packets instead of refusing them, so the
    /// read hangs rather than failing fast.
    ///
    /// <para>
    /// Crucially this mirrors <c>MoonrakerClient.SpoolmanProxyRequestAsync</c>, which
    /// catches EVERY exception - cancellation included - and returns a null body. A mock
    /// that let <see cref="OperationCanceledException"/> escape would not exercise the
    /// resolver's explicit post-call cancellation check at all.
    /// </para>
    ///
    /// <para>
    /// The delay is long-but-finite rather than infinite so that a regression which
    /// removes the per-source deadline FAILS the elapsed-time assertion instead of
    /// hanging the test host until CI's external job limit.
    /// </para>
    /// </summary>
    private static Mock<IBackendClient> DarkNativeClient()
    {
        Mock<IBackendClient> client = new();
        client.As<ISupportsSpoolman>()
            .Setup(n => n.GetSpoolmanSpoolsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (string _, CancellationToken ct) =>
            {
                await SwallowedHangAsync(ct);
                return (string?)null;
            });
        return client;
    }

    /// <summary>
    /// Watchdog bound for a mock that models an unreachable host. Comfortably longer than
    /// any timeout configured in these tests, and far shorter than the 60s print-control
    /// timeout the fix exists to avoid inheriting.
    /// </summary>
    private static readonly TimeSpan UnreachableHostWatchdog = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Hangs until cancelled or the watchdog fires, then swallows the cancellation exactly
    /// as the production Spoolman clients do.
    /// </summary>
    private static async Task SwallowedHangAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(UnreachableHostWatchdog, ct);
        }
        catch (OperationCanceledException)
        {
            // Swallowed on purpose: both MoonrakerClient.SpoolmanProxyRequestAsync and
            // SpoolmanService.ListSpoolsAsync catch everything and return an empty result.
        }
    }

    private static ISettingsService SettingsWithSourceTimeout(int timeoutMs, int fleetTimeoutMs = 60_000)
    {
        Mock<ISettingsService> settings = new();
        settings.Setup(s => s.Get<SpoolCoverageSettings>())
            .Returns(new SpoolCoverageSettings
            {
                SpoolSourceTimeoutMs = timeoutMs,
                // Default the fleet budget to its maximum so a test that is specifically
                // exercising the per-source deadline cannot be satisfied by the fleet
                // deadline firing first. Tests for the fleet bound set it explicitly.
                FleetResolveTimeoutMs = fleetTimeoutMs,
            });
        return settings.Object;
    }

    private static AppDbContext CreateContext()
    {
        DbContextOptions<AppDbContext> options =
            new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
        return new AppDbContext(options);
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
