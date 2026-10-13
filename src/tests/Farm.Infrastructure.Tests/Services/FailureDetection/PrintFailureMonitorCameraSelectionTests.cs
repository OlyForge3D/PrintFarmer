using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.FailureDetection;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Farm.Infrastructure.Tests.Services.FailureDetection;

public sealed class PrintFailureMonitorCameraSelectionTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _dbContextOptions;

    public PrintFailureMonitorCameraSelectionTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _dbContextOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        using AppDbContext dbContext = CreateDbContext();
        _ = dbContext.Database.EnsureCreated();
    }

    [Fact]
    public async Task LoadMonitoredPrinters_SelectsPrimaryLinkedCamera()
    {
        Guid printerId;
        Guid primaryId;
        using (AppDbContext seed = CreateDbContext())
        {
            Printer printer = await SeedPrinterAsync(seed, "Primary Printer");
            printerId = printer.Id;
            Camera secondary = AddCamera(seed, "A Secondary", "http://cam-secondary/snapshot.jpg");
            Camera primary = AddCamera(seed, "Z Primary", "http://cam-primary/snapshot.jpg");
            primaryId = primary.Id;
            Link(seed, secondary, printer, isPrimary: false, sortOrder: 0);
            Link(seed, primary, printer, isPrimary: true, sortOrder: 5);
            await seed.SaveChangesAsync();
        }

        using AppDbContext dbContext = CreateDbContext();
        List<Printer> printers = await PrintFailureMonitorService.LoadMonitoredPrintersAsync(dbContext, CancellationToken.None);

        Printer loaded = printers.Should().ContainSingle(p => p.Id == printerId).Subject;
        PrinterCameraSelection selection = PrintFailureMonitorService.SelectDetectionCamera(loaded);
        selection.Availability.Should().Be(PrinterCameraAvailability.Available);
        selection.Camera!.Id.Should().Be(primaryId);
        selection.Camera.SnapshotUrl.Should().Be("http://cam-primary/snapshot.jpg");
    }

    [Fact]
    public async Task LoadMonitoredPrinters_FallsBackToSecondaryWithSnapshot_WhenPrimaryHasNoSnapshotUrl()
    {
        Guid secondaryId;
        using (AppDbContext seed = CreateDbContext())
        {
            Printer printer = await SeedPrinterAsync(seed, "Fallback Printer");
            Camera primary = AddCamera(seed, "Primary RTSP", snapshotUrl: null);
            Camera secondary = AddCamera(seed, "Secondary", "http://cam-secondary/snapshot.jpg");
            secondaryId = secondary.Id;
            Link(seed, primary, printer, isPrimary: true, sortOrder: 0);
            Link(seed, secondary, printer, isPrimary: false, sortOrder: 1);
            await seed.SaveChangesAsync();
        }

        using AppDbContext dbContext = CreateDbContext();
        Printer loaded = (await PrintFailureMonitorService.LoadMonitoredPrintersAsync(dbContext, CancellationToken.None)).Single();

        PrinterCameraSelection selection = PrintFailureMonitorService.SelectDetectionCamera(loaded);
        selection.Availability.Should().Be(PrinterCameraAvailability.Available);
        selection.Camera!.Id.Should().Be(secondaryId);
    }

    [Fact]
    public async Task LoadMonitoredPrinters_FallsBackPastOfflinePrimary()
    {
        Guid secondaryId;
        using (AppDbContext seed = CreateDbContext())
        {
            Printer printer = await SeedPrinterAsync(seed, "Offline Primary Printer");
            Camera primary = AddCamera(seed, "Primary", "http://cam-primary/snapshot.jpg", CameraHealthStatus.Unhealthy);
            Camera secondary = AddCamera(seed, "Secondary", "http://cam-secondary/snapshot.jpg", CameraHealthStatus.Healthy);
            secondaryId = secondary.Id;
            Link(seed, primary, printer, isPrimary: true, sortOrder: 0);
            Link(seed, secondary, printer, isPrimary: false, sortOrder: 1);
            await seed.SaveChangesAsync();
        }

        using AppDbContext dbContext = CreateDbContext();
        Printer loaded = (await PrintFailureMonitorService.LoadMonitoredPrintersAsync(dbContext, CancellationToken.None)).Single();

        PrinterCameraSelection selection = PrintFailureMonitorService.SelectDetectionCamera(loaded);
        selection.Availability.Should().Be(PrinterCameraAvailability.Available);
        selection.Camera!.Id.Should().Be(secondaryId);
    }

    [Fact]
    public async Task LoadMonitoredPrinters_SharedCameraIsSelectedForEachLinkedPrinter()
    {
        Guid sharedId;
        Guid firstPrinterId;
        Guid secondPrinterId;
        using (AppDbContext seed = CreateDbContext())
        {
            Printer first = await SeedPrinterAsync(seed, "Shared A");
            Printer second = await SeedPrinterAsync(seed, "Shared B");
            firstPrinterId = first.Id;
            secondPrinterId = second.Id;
            Camera shared = AddCamera(seed, "Enclosure Cam", "http://cam-shared/snapshot.jpg");
            sharedId = shared.Id;
            Link(seed, shared, first, isPrimary: true, sortOrder: 0);
            Link(seed, shared, second, isPrimary: true, sortOrder: 0);
            await seed.SaveChangesAsync();
        }

        using AppDbContext dbContext = CreateDbContext();
        List<Printer> printers = await PrintFailureMonitorService.LoadMonitoredPrintersAsync(dbContext, CancellationToken.None);

        printers.Select(p => p.Id).Should().BeEquivalentTo(new[] { firstPrinterId, secondPrinterId });
        foreach (Printer printer in printers)
        {
            PrinterCameraSelection selection = PrintFailureMonitorService.SelectDetectionCamera(printer);
            selection.Availability.Should().Be(PrinterCameraAvailability.Available);
            selection.Camera!.Id.Should().Be(sharedId);
        }
    }

    [Fact]
    public async Task LoadMonitoredPrinters_NoLinkedCamera_ReportsNoCameraLinked()
    {
        using (AppDbContext seed = CreateDbContext())
        {
            _ = await SeedPrinterAsync(seed, "No Camera Printer");
        }

        using AppDbContext dbContext = CreateDbContext();
        Printer loaded = (await PrintFailureMonitorService.LoadMonitoredPrintersAsync(dbContext, CancellationToken.None)).Single();

        PrinterCameraSelection selection = PrintFailureMonitorService.SelectDetectionCamera(loaded);
        selection.Availability.Should().Be(PrinterCameraAvailability.NoCameraLinked);
        selection.Camera.Should().BeNull();
        PrintFailureMonitorService.DescribeCameraUnavailable(selection)
            .Should().Be("No camera linked to this printer. Link a camera in printer settings.");
    }

    [Fact]
    public async Task LoadMonitoredPrinters_OfflineCamera_ReportsOfflineDistinctFromNoCamera()
    {
        using (AppDbContext seed = CreateDbContext())
        {
            Printer printer = await SeedPrinterAsync(seed, "Offline Printer");
            Camera camera = AddCamera(seed, "Bed Cam", "http://cam-offline/snapshot.jpg", CameraHealthStatus.Unhealthy);
            camera.HealthMessage = "HTTP error: connection refused";
            Link(seed, camera, printer, isPrimary: true, sortOrder: 0);
            await seed.SaveChangesAsync();
        }

        using AppDbContext dbContext = CreateDbContext();
        Printer loaded = (await PrintFailureMonitorService.LoadMonitoredPrintersAsync(dbContext, CancellationToken.None)).Single();

        PrinterCameraSelection selection = PrintFailureMonitorService.SelectDetectionCamera(loaded);
        selection.Availability.Should().Be(PrinterCameraAvailability.CamerasOffline);
        PrintFailureMonitorService.DescribeCameraUnavailable(selection)
            .Should().Be("Camera 'Bed Cam' is offline: HTTP error: connection refused");
    }

    [Fact]
    public async Task LoadMonitoredPrinters_DisabledCamera_ReportsDisabled()
    {
        using (AppDbContext seed = CreateDbContext())
        {
            Printer printer = await SeedPrinterAsync(seed, "Disabled Camera Printer");
            Camera camera = AddCamera(seed, "Disabled Cam", "http://cam-disabled/snapshot.jpg");
            camera.IsEnabled = false;
            Link(seed, camera, printer, isPrimary: true, sortOrder: 0);
            await seed.SaveChangesAsync();
        }

        using AppDbContext dbContext = CreateDbContext();
        Printer loaded = (await PrintFailureMonitorService.LoadMonitoredPrintersAsync(dbContext, CancellationToken.None)).Single();

        PrinterCameraSelection selection = PrintFailureMonitorService.SelectDetectionCamera(loaded);
        selection.Availability.Should().Be(PrinterCameraAvailability.AllCamerasDisabled);
        PrintFailureMonitorService.DescribeCameraUnavailable(selection)
            .Should().Contain("disabled");
    }

    [Fact]
    public void DescribeCameraUnavailable_NoSnapshotUrl_ExplainsSnapshotRequirement()
    {
        var selection = new PrinterCameraSelection(null, PrinterCameraAvailability.NoSnapshotUrl);

        PrintFailureMonitorService.DescribeCameraUnavailable(selection)
            .Should().StartWith("Camera snapshot URL required.");
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    private AppDbContext CreateDbContext() => new(_dbContextOptions);

    private static Camera AddCamera(
        AppDbContext dbContext,
        string name,
        string? snapshotUrl,
        CameraHealthStatus health = CameraHealthStatus.Unknown)
    {
        var camera = new Camera
        {
            Id = Guid.NewGuid(),
            Name = name,
            SnapshotUrl = snapshotUrl,
            StreamUrl = "http://cam/stream",
            IsEnabled = true,
            HealthStatus = health,
        };
        _ = dbContext.Cameras.Add(camera);
        return camera;
    }

    private static void Link(AppDbContext dbContext, Camera camera, Printer printer, bool isPrimary, int sortOrder)
    {
        _ = dbContext.Set<CameraPrinter>().Add(new CameraPrinter
        {
            CameraId = camera.Id,
            PrinterId = printer.Id,
            IsPrimary = isPrimary,
            SortOrder = sortOrder,
        });
    }

    private static async Task<Printer> SeedPrinterAsync(AppDbContext dbContext, string printerName)
    {
        Manufacturer manufacturer = new()
        {
            Id = Guid.NewGuid(),
            Name = $"{printerName} Manufacturer",
        };
        PrinterModel model = new()
        {
            Id = Guid.NewGuid(),
            ManufacturerId = manufacturer.Id,
            Name = $"{printerName} Model",
        };
        Printer printer = new()
        {
            Id = Guid.NewGuid(),
            Name = printerName,
            ServerUrl = $"http://{Guid.NewGuid():N}.local",
            BackendPort = 7125,
            ManufacturerId = manufacturer.Id,
            ModelId = model.Id,
            ObicoEnabled = true,
        };

        _ = dbContext.Manufacturers.Add(manufacturer);
        _ = dbContext.PrinterModels.Add(model);
        _ = dbContext.Printers.Add(printer);
        await dbContext.SaveChangesAsync();

        return printer;
    }
}
