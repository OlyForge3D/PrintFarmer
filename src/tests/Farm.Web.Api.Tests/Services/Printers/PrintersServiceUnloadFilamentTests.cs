using Farm.Backend.Plugin.Core;
using Farm.Infrastructure;
using Farm.Infrastructure.Contracts.Printers;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Printers;
using Farm.Infrastructure.Repositories.UnitOfWork;
using Farm.Infrastructure.Services.Interfaces;
using Farm.Infrastructure.Services.Printers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Farm.Web.Api.Tests.Services.Printers;

/// <summary>
/// Covers the outgoing-spool source precedence added for the guided filament swap flow
/// (GitHub issue OlyForge3D/PrintFarmer#710). The unload endpoint must resolve the residual
/// weight from the correct spool source:
///   * explicit toolhead/lane index -> that lane's CurrentSpoolId (MMU/multi-slot);
///   * otherwise Printer.CurrentSpoolId (legacy single-tool source of truth) takes precedence
///     over the primary physical toolhead's CurrentSpoolId, falling back to it when unset.
/// </summary>
public class PrintersServiceUnloadFilamentTests
{
    [Fact]
    public async Task UnloadFilamentAsync_LegacyPrinterScalar_TakesPrecedenceOverPrimaryToolhead()
    {
        await using AppDbContext db = CreateDbContext();
        Printer printer = CreatePrinter();
        printer.CurrentSpoolId = 100; // legacy single-tool source of truth
        printer.Toolheads = new List<Toolhead>
        {
            new() { Id = Guid.NewGuid(), PrinterId = printer.Id, Index = 0, IsPrimary = true, CurrentSpoolId = 999, CurrentMaterial = "ABS" },
        };

        var spoolman = new Mock<ISpoolmanService>();
        spoolman.Setup(s => s.GetSpoolByIdAsync(100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Spool(100, "PLA", remainingG: 250.0));

        PrintersService service = CreateService(db, printer, spoolman, unloadResult: true);

        FilamentUnloadResult result = await service.UnloadFilamentAsync(printer.Id, toolheadIndex: null, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.SpoolId.Should().Be(100); // printer scalar, NOT the primary toolhead's 999
        result.Material.Should().Be("PLA");
        result.ResidualWeightG.Should().Be(250.0);
        spoolman.Verify(s => s.GetSpoolByIdAsync(999, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UnloadFilamentAsync_FallsBackToPrimaryToolhead_WhenPrinterScalarUnset()
    {
        await using AppDbContext db = CreateDbContext();
        Printer printer = CreatePrinter();
        printer.CurrentSpoolId = null; // no legacy scalar -> fall back to primary toolhead
        printer.Toolheads = new List<Toolhead>
        {
            new() { Id = Guid.NewGuid(), PrinterId = printer.Id, Index = 0, IsPrimary = false, CurrentSpoolId = 7, CurrentMaterial = "PETG" },
            new() { Id = Guid.NewGuid(), PrinterId = printer.Id, Index = 1, IsPrimary = true, CurrentSpoolId = 42, CurrentMaterial = "TPU" },
        };

        var spoolman = new Mock<ISpoolmanService>();
        spoolman.Setup(s => s.GetSpoolByIdAsync(42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Spool(42, "TPU", remainingG: 88.0));

        PrintersService service = CreateService(db, printer, spoolman, unloadResult: true);

        FilamentUnloadResult result = await service.UnloadFilamentAsync(printer.Id, toolheadIndex: null, CancellationToken.None);

        result.SpoolId.Should().Be(42); // primary toolhead wins over the non-primary lane
        result.ResidualWeightG.Should().Be(88.0);
    }

    [Fact]
    public async Task UnloadFilamentAsync_ExplicitToolheadIndex_UsesThatLaneSpool_NotPrinterScalar()
    {
        await using AppDbContext db = CreateDbContext();
        Printer printer = CreatePrinter();
        printer.CurrentSpoolId = 100; // must be ignored when a specific lane is targeted
        printer.Toolheads = new List<Toolhead>
        {
            new() { Id = Guid.NewGuid(), PrinterId = printer.Id, Index = 0, IsPrimary = true, CurrentSpoolId = 100, CurrentMaterial = "PLA" },
            new() { Id = Guid.NewGuid(), PrinterId = printer.Id, Index = 2, IsPrimary = false, CurrentSpoolId = 55, CurrentMaterial = "PETG" },
        };

        var spoolman = new Mock<ISpoolmanService>();
        spoolman.Setup(s => s.GetSpoolByIdAsync(55, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Spool(55, "PETG", remainingG: 500.0));

        PrintersService service = CreateService(db, printer, spoolman, unloadResult: true);

        FilamentUnloadResult result = await service.UnloadFilamentAsync(printer.Id, toolheadIndex: 2, CancellationToken.None);

        result.SpoolId.Should().Be(55); // gate lane spool, not printer scalar 100
        result.Material.Should().Be("PETG");
        result.ResidualWeightG.Should().Be(500.0);
        spoolman.Verify(s => s.GetSpoolByIdAsync(100, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UnloadFilamentAsync_ExplicitToolheadIndex_NotFound_ReturnsFailure()
    {
        await using AppDbContext db = CreateDbContext();
        Printer printer = CreatePrinter();
        printer.Toolheads = new List<Toolhead>
        {
            new() { Id = Guid.NewGuid(), PrinterId = printer.Id, Index = 0, IsPrimary = true, CurrentSpoolId = 100 },
        };

        var spoolman = new Mock<ISpoolmanService>(MockBehavior.Strict);
        PrintersService service = CreateService(db, printer, spoolman, unloadResult: true);

        FilamentUnloadResult result = await service.UnloadFilamentAsync(printer.Id, toolheadIndex: 9, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("Toolhead index 9 not found");
        result.SpoolId.Should().BeNull();
        // No spool source resolved for a missing lane -> Spoolman is never consulted.
        spoolman.Verify(s => s.GetSpoolByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static SpoolmanSpoolDto Spool(int id, string material, double? remainingG) =>
        new(id, $"spool-{id}", material, remainingG, ColorHex: null, InUse: true);

    private static AppDbContext CreateDbContext()
    {
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"PrintersServiceUnloadFilamentTests_{Guid.NewGuid():N}")
            .Options;

        return new AppDbContext(options);
    }

    private static Printer CreatePrinter() => new()
    {
        Id = Guid.NewGuid(),
        Name = "Moonraker",
        ServerUrl = "http://moonraker.local",
        FrontendPort = 7125,
        Backend = (int)PrinterBackend.Moonraker,
    };

    private static PrintersService CreateService(
        AppDbContext db,
        Printer printer,
        Mock<ISpoolmanService> spoolman,
        bool unloadResult)
    {
        var printersRepository = new Mock<IPrintersRepository>();
        printersRepository
            .Setup(r => r.FindByIdWithToolheadsAsync(printer.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(printer);

        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(u => u.Printers).Returns(printersRepository.Object);

        var backendClient = new Mock<IBackendClient>();
        Mock<ISupportsFilamentControl> filamentClient = backendClient.As<ISupportsFilamentControl>();
        filamentClient
            .Setup(c => c.UnloadFilamentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(unloadResult);

        var backendClientFactory = new Mock<IBackendClientFactory>();
        backendClientFactory
            .Setup(f => f.GetClient(PrinterBackend.Moonraker))
            .Returns((IBackendClient)filamentClient.Object);

        return new PrintersService(
            unitOfWork.Object,
            db,
            backendClientFactory.Object,
            Mock.Of<IBackendCapabilityFactory>(),
            Mock.Of<Farm.Infrastructure.Services.Catalog.ICatalogService>(),
            Mock.Of<IHttpClientFactory>(),
            NullLogger<PrintersService>.Instance,
            Mock.Of<IPrinterStatusBroadcaster>(),
            Mock.Of<IMultiPrinterStatusCoordinator>(),
            Mock.Of<IPrinterStatusClientFactory>(),
            Mock.Of<IPrinterStatusCacheReader>(),
            Mock.Of<Farm.Infrastructure.Services.Locations.ILocationService>(),
            Mock.Of<Farm.Infrastructure.Services.Security.ISensitiveDataProtector>(),
            spoolman.Object,
            Mock.Of<Farm.Infrastructure.Services.Cameras.IGo2RtcService>(),
            Mock.Of<Farm.Infrastructure.Services.StorageManagement.IStoragePathService>());
    }
}
