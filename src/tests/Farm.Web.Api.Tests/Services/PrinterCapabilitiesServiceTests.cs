using System;
using System.Linq;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.PrinterCapabilities;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Services.Interfaces;
using Farm.Web.Api.Services.PrinterCapabilities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services
{
    public class PrinterCapabilitiesServiceTests
    {
        private static AppDbContext CreateSqliteInMemoryDb()
        {
            // Use SQLite in-memory to provide relational Include/ThenInclude semantics similar to production
            SqliteConnection connection = new SqliteConnection("DataSource=:memory:");
            connection.Open();
            DbContextOptions<AppDbContext> opts = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(connection)
                .Options;

            AppDbContext ctx = new AppDbContext(opts);
            _ = ctx.Database.EnsureCreated();
            return ctx;
        }

        [Fact]
        public async Task GetAllAsync_ReturnsExistingCapabilities()
        {
            using AppDbContext db = CreateSqliteInMemoryDb();
            // Seed a printer and its capabilities
            Manufacturer manufacturer = new Manufacturer { Id = Guid.NewGuid(), Name = "M1" };
            PrinterModel model = new PrinterModel { Id = Guid.NewGuid(), Name = "Model1", ManufacturerId = manufacturer.Id, Manufacturer = manufacturer };
            Printer printer = new Printer { Id = Guid.NewGuid(), Name = "P1", ManufacturerId = manufacturer.Id, Manufacturer = manufacturer, ModelId = model.Id, Model = model };
            _ = db.Manufacturers.Add(manufacturer);
            _ = db.Models.Add(model);
            _ = db.Printers.Add(printer);
            PrinterCapabilities cap = new PrinterCapabilities { Id = Guid.NewGuid(), PrinterId = printer.Id, IsAvailable = true, LastUpdated = DateTime.UtcNow };
            cap.Printer = printer;
            _ = db.PrinterCapabilities.Add(cap);
            _ = await db.SaveChangesAsync();
            // Ensure the in-memory DB contains the capability we just added.
            _ = await db.PrinterCapabilities.Include(c => c.Printer).ToListAsync();

            Mock<IPrinterCapabilityDiscoveryService> discoveryMock = new Mock<IPrinterCapabilityDiscoveryService>();
            Mock<IUnifiedLoggingService> loggerMock = new Mock<IUnifiedLoggingService>();
            EfPrinterCapabilitiesRepository repo = new EfPrinterCapabilitiesRepository(db);

            PrinterCapabilitiesService svc = new PrinterCapabilitiesService(repo, loggerMock.Object, discoveryMock.Object);
            PrinterCapabilitiesDto? dto = await svc.GetByPrinterIdAsync(printer.Id);
            Assert.NotNull(dto);
            Assert.Equal(printer.Id, dto!.PrinterId);
        }

        [Fact]
        public async Task CreateAsync_CreatesCapabilities_WhenPrinterExists()
        {
            using AppDbContext db = CreateSqliteInMemoryDb();
            Manufacturer manufacturer = new Manufacturer { Id = Guid.NewGuid(), Name = "M2" };
            PrinterModel model = new PrinterModel { Id = Guid.NewGuid(), Name = "Model2", ManufacturerId = manufacturer.Id, Manufacturer = manufacturer };
            Printer printer = new Printer { Id = Guid.NewGuid(), Name = "P2", ManufacturerId = manufacturer.Id, Manufacturer = manufacturer, ModelId = model.Id, Model = model };
            _ = db.Manufacturers.Add(manufacturer);
            _ = db.Models.Add(model);
            _ = db.Printers.Add(printer);
            _ = await db.SaveChangesAsync();

            Mock<IPrinterCapabilityDiscoveryService> discoveryMock = new Mock<IPrinterCapabilityDiscoveryService>();
            Mock<IUnifiedLoggingService> loggerMock = new Mock<IUnifiedLoggingService>();
            EfPrinterCapabilitiesRepository repo = new EfPrinterCapabilitiesRepository(db);

            PrinterCapabilitiesService svc = new PrinterCapabilitiesService(repo, loggerMock.Object, discoveryMock.Object);

            CreatePrinterCapabilitiesDto req = new CreatePrinterCapabilitiesDto(printer.Id);

            PrinterCapabilitiesDto? created = await svc.CreateAsync(req);
            Assert.NotNull(created);
            Assert.Equal(printer.Id, created!.PrinterId);
        }

        [Fact]
        public async Task GetAllAsync_ReturnsAllCapabilities()
        {
            using AppDbContext db = CreateSqliteInMemoryDb();
            Manufacturer manufacturer = new Manufacturer { Id = Guid.NewGuid(), Name = "M3" };
            PrinterModel model = new PrinterModel { Id = Guid.NewGuid(), Name = "Model3", ManufacturerId = manufacturer.Id, Manufacturer = manufacturer };
            Printer printer1 = new Printer { Id = Guid.NewGuid(), Name = "P3", ManufacturerId = manufacturer.Id, Manufacturer = manufacturer, ModelId = model.Id, Model = model };
            Printer printer2 = new Printer { Id = Guid.NewGuid(), Name = "P4", ManufacturerId = manufacturer.Id, Manufacturer = manufacturer, ModelId = model.Id, Model = model };
            _ = db.Manufacturers.Add(manufacturer);
            _ = db.Models.Add(model);
            _ = db.Printers.Add(printer1);
            _ = db.Printers.Add(printer2);
            PrinterCapabilities cap1 = new PrinterCapabilities { Id = Guid.NewGuid(), PrinterId = printer1.Id, IsAvailable = true, LastUpdated = DateTime.UtcNow, HasHeatedBed = true };
            cap1.Printer = printer1;
            PrinterCapabilities cap2 = new PrinterCapabilities { Id = Guid.NewGuid(), PrinterId = printer2.Id, IsAvailable = false, LastUpdated = DateTime.UtcNow, HasHeatedBed = false };
            cap2.Printer = printer2;
            _ = db.PrinterCapabilities.Add(cap1);
            _ = db.PrinterCapabilities.Add(cap2);
            _ = await db.SaveChangesAsync();

            Mock<IPrinterCapabilityDiscoveryService> discoveryMock = new Mock<IPrinterCapabilityDiscoveryService>();
            Mock<IUnifiedLoggingService> loggerMock = new Mock<IUnifiedLoggingService>();
            EfPrinterCapabilitiesRepository repo = new EfPrinterCapabilitiesRepository(db);

            PrinterCapabilitiesService svc = new PrinterCapabilitiesService(repo, loggerMock.Object, discoveryMock.Object);
            IReadOnlyList<PrinterCapabilitiesDto> all = await svc.GetAllAsync();
            Assert.Equal(2, all.Count);
            Assert.Contains(all, c => c.PrinterId == printer1.Id && c.HasHeatedBed);
            Assert.Contains(all, c => c.PrinterId == printer2.Id && !c.HasHeatedBed);
        }

        [Fact]
        public async Task CreateOrUpdateAsync_CreatesNewCapabilities_WhenNoneExist()
        {
            using AppDbContext db = CreateSqliteInMemoryDb();
            Manufacturer manufacturer = new Manufacturer { Id = Guid.NewGuid(), Name = "M4" };
            PrinterModel model = new PrinterModel { Id = Guid.NewGuid(), Name = "Model4", ManufacturerId = manufacturer.Id, Manufacturer = manufacturer };
            Printer printer = new Printer { Id = Guid.NewGuid(), Name = "P5", ManufacturerId = manufacturer.Id, Manufacturer = manufacturer, ModelId = model.Id, Model = model };
            _ = db.Manufacturers.Add(manufacturer);
            _ = db.Models.Add(model);
            _ = db.Printers.Add(printer);
            _ = await db.SaveChangesAsync();

            Mock<IPrinterCapabilityDiscoveryService> discoveryMock = new Mock<IPrinterCapabilityDiscoveryService>();
            Mock<IUnifiedLoggingService> loggerMock = new Mock<IUnifiedLoggingService>();
            EfPrinterCapabilitiesRepository repo = new EfPrinterCapabilitiesRepository(db);

            PrinterCapabilitiesService svc = new PrinterCapabilitiesService(repo, loggerMock.Object, discoveryMock.Object);

            UpdatePrinterCapabilitiesDto updateReq = new UpdatePrinterCapabilitiesDto(
                NozzleDiameter: 0.4,
                HasHeatedBed: true,
                MaxBuildVolumeX: 200,
                MaxBuildVolumeY: 200,
                MaxBuildVolumeZ: 200,
                SupportedMaterials: null,
                HasEnclosure: false,
                MultiMaterial: false,
                NumberOfExtruders: 1,
                MinHotendTemp: 200,
                MaxHotendTemp: 300,
                MinBedTemp: 0,
                MaxBedTemp: 110,
                SupportsAutoLeveling: false,
                MaxPrintSpeed: null
            );

            PrinterCapabilitiesDto? result = await svc.CreateOrUpdateAsync(printer.Id, updateReq);
            Assert.NotNull(result);
            Assert.Equal(printer.Id, result!.PrinterId);
            Assert.Equal(0.4, result.NozzleDiameter);
            Assert.True(result.HasHeatedBed);
        }

        [Fact]
        public async Task CreateOrUpdateAsync_UpdatesExistingCapabilities_WhenAlreadyExist()
        {
            using AppDbContext db = CreateSqliteInMemoryDb();
            Manufacturer manufacturer = new Manufacturer { Id = Guid.NewGuid(), Name = "M5" };
            PrinterModel model = new PrinterModel { Id = Guid.NewGuid(), Name = "Model5", ManufacturerId = manufacturer.Id, Manufacturer = manufacturer };
            Printer printer = new Printer { Id = Guid.NewGuid(), Name = "P6", ManufacturerId = manufacturer.Id, Manufacturer = manufacturer, ModelId = model.Id, Model = model };
            _ = db.Manufacturers.Add(manufacturer);
            _ = db.Models.Add(model);
            _ = db.Printers.Add(printer);
            PrinterCapabilities existing = new PrinterCapabilities 
            { 
                Id = Guid.NewGuid(), 
                PrinterId = printer.Id, 
                NozzleDiameter = 0.8,
                HasHeatedBed = false,
                LastUpdated = DateTime.UtcNow 
            };
            existing.Printer = printer;
            _ = db.PrinterCapabilities.Add(existing);
            _ = await db.SaveChangesAsync();

            Mock<IPrinterCapabilityDiscoveryService> discoveryMock = new Mock<IPrinterCapabilityDiscoveryService>();
            Mock<IUnifiedLoggingService> loggerMock = new Mock<IUnifiedLoggingService>();
            EfPrinterCapabilitiesRepository repo = new EfPrinterCapabilitiesRepository(db);

            PrinterCapabilitiesService svc = new PrinterCapabilitiesService(repo, loggerMock.Object, discoveryMock.Object);

            UpdatePrinterCapabilitiesDto updateReq = new UpdatePrinterCapabilitiesDto(
                NozzleDiameter: 0.4,
                HasHeatedBed: true,
                MaxBuildVolumeX: 200,
                MaxBuildVolumeY: 200,
                MaxBuildVolumeZ: 200,
                SupportedMaterials: null,
                HasEnclosure: false,
                MultiMaterial: false,
                NumberOfExtruders: 1,
                MinHotendTemp: 200,
                MaxHotendTemp: 300,
                MinBedTemp: 0,
                MaxBedTemp: 110,
                SupportsAutoLeveling: false,
                MaxPrintSpeed: null
            );

            PrinterCapabilitiesDto? result = await svc.CreateOrUpdateAsync(printer.Id, updateReq);
            Assert.NotNull(result);
            Assert.Equal(0.4, result!.NozzleDiameter); // Updated value
            Assert.True(result.HasHeatedBed); // Updated value
        }

        [Fact]
        public async Task DeleteAsync_RemovesCapabilities_WhenExist()
        {
            using AppDbContext db = CreateSqliteInMemoryDb();
            Manufacturer manufacturer = new Manufacturer { Id = Guid.NewGuid(), Name = "M6" };
            PrinterModel model = new PrinterModel { Id = Guid.NewGuid(), Name = "Model6", ManufacturerId = manufacturer.Id, Manufacturer = manufacturer };
            Printer printer = new Printer { Id = Guid.NewGuid(), Name = "P7", ManufacturerId = manufacturer.Id, Manufacturer = manufacturer, ModelId = model.Id, Model = model };
            _ = db.Manufacturers.Add(manufacturer);
            _ = db.Models.Add(model);
            _ = db.Printers.Add(printer);
            PrinterCapabilities cap = new PrinterCapabilities { Id = Guid.NewGuid(), PrinterId = printer.Id, IsAvailable = true, LastUpdated = DateTime.UtcNow };
            cap.Printer = printer;
            _ = db.PrinterCapabilities.Add(cap);
            _ = await db.SaveChangesAsync();

            Mock<IPrinterCapabilityDiscoveryService> discoveryMock = new Mock<IPrinterCapabilityDiscoveryService>();
            Mock<IUnifiedLoggingService> loggerMock = new Mock<IUnifiedLoggingService>();
            EfPrinterCapabilitiesRepository repo = new EfPrinterCapabilitiesRepository(db);

            PrinterCapabilitiesService svc = new PrinterCapabilitiesService(repo, loggerMock.Object, discoveryMock.Object);
            bool deleted = await svc.DeleteAsync(printer.Id);
            Assert.True(deleted);
            
            PrinterCapabilitiesDto? afterDelete = await svc.GetByPrinterIdAsync(printer.Id);
            Assert.Null(afterDelete);
        }

        [Fact]
        public async Task DeleteAsync_ReturnsFalse_WhenCapabilitiesNotFound()
        {
            using AppDbContext db = CreateSqliteInMemoryDb();
            Manufacturer manufacturer = new Manufacturer { Id = Guid.NewGuid(), Name = "M7" };
            PrinterModel model = new PrinterModel { Id = Guid.NewGuid(), Name = "Model7", ManufacturerId = manufacturer.Id, Manufacturer = manufacturer };
            Printer printer = new Printer { Id = Guid.NewGuid(), Name = "P8", ManufacturerId = manufacturer.Id, Manufacturer = manufacturer, ModelId = model.Id, Model = model };
            _ = db.Manufacturers.Add(manufacturer);
            _ = db.Models.Add(model);
            _ = db.Printers.Add(printer);
            _ = await db.SaveChangesAsync();

            Mock<IPrinterCapabilityDiscoveryService> discoveryMock = new Mock<IPrinterCapabilityDiscoveryService>();
            Mock<IUnifiedLoggingService> loggerMock = new Mock<IUnifiedLoggingService>();
            EfPrinterCapabilitiesRepository repo = new EfPrinterCapabilitiesRepository(db);

            PrinterCapabilitiesService svc = new PrinterCapabilitiesService(repo, loggerMock.Object, discoveryMock.Object);
            bool deleted = await svc.DeleteAsync(printer.Id); // No capabilities exist
            Assert.False(deleted);
        }

        [Fact]
        public async Task DiscoverAsync_CreatesNewCapabilities_WhenNoneExist()
        {
            using AppDbContext db = CreateSqliteInMemoryDb();
            Manufacturer manufacturer = new Manufacturer { Id = Guid.NewGuid(), Name = "Prusa" };
            PrinterModel model = new PrinterModel { Id = Guid.NewGuid(), Name = "MINI+", ManufacturerId = manufacturer.Id, Manufacturer = manufacturer };
            Printer printer = new Printer { Id = Guid.NewGuid(), Name = "P9", ManufacturerId = manufacturer.Id, Manufacturer = manufacturer, ModelId = model.Id, Model = model };
            _ = db.Manufacturers.Add(manufacturer);
            _ = db.Models.Add(model);
            _ = db.Printers.Add(printer);
            _ = await db.SaveChangesAsync();

            Mock<IPrinterCapabilityDiscoveryService> discoveryMock = new Mock<IPrinterCapabilityDiscoveryService>();
            Mock<IUnifiedLoggingService> loggerMock = new Mock<IUnifiedLoggingService>();
            
            PrinterCapabilities discoveredCap = new PrinterCapabilities 
            { 
                Id = Guid.NewGuid(), 
                PrinterId = printer.Id, 
                NozzleDiameter = 0.4,
                HasHeatedBed = true,
                LastUpdated = DateTime.UtcNow
            };
            discoveredCap.Printer = printer;
            
            discoveryMock
                .Setup(d => d.DiscoverCapabilitiesAsync(printer, It.IsAny<CancellationToken>()))
                .ReturnsAsync(discoveredCap);

            EfPrinterCapabilitiesRepository repo = new EfPrinterCapabilitiesRepository(db);

            PrinterCapabilitiesService svc = new PrinterCapabilitiesService(repo, loggerMock.Object, discoveryMock.Object);
            (PrinterCapabilitiesDto? result, bool isNew) = await svc.DiscoverAsync(printer.Id);
            
            Assert.NotNull(result);
            Assert.True(isNew);
            Assert.Equal(0.4, result!.NozzleDiameter);
        }

        [Fact]
        public async Task DiscoverAsync_RefreshesExistingCapabilities_WhenAlreadyExist()
        {
            using AppDbContext db = CreateSqliteInMemoryDb();
            Manufacturer manufacturer = new Manufacturer { Id = Guid.NewGuid(), Name = "Prusa" };
            PrinterModel model = new PrinterModel { Id = Guid.NewGuid(), Name = "MINI+", ManufacturerId = manufacturer.Id, Manufacturer = manufacturer };
            Printer printer = new Printer { Id = Guid.NewGuid(), Name = "P10", ManufacturerId = manufacturer.Id, Manufacturer = manufacturer, ModelId = model.Id, Model = model };
            _ = db.Manufacturers.Add(manufacturer);
            _ = db.Models.Add(model);
            _ = db.Printers.Add(printer);
            PrinterCapabilities existing = new PrinterCapabilities 
            { 
                Id = Guid.NewGuid(), 
                PrinterId = printer.Id, 
                NozzleDiameter = 0.8,
                LastUpdated = DateTime.UtcNow 
            };
            existing.Printer = printer;
            _ = db.PrinterCapabilities.Add(existing);
            _ = await db.SaveChangesAsync();

            Mock<IPrinterCapabilityDiscoveryService> discoveryMock = new Mock<IPrinterCapabilityDiscoveryService>();
            Mock<IUnifiedLoggingService> loggerMock = new Mock<IUnifiedLoggingService>();

            PrinterCapabilities refreshedCap = new PrinterCapabilities 
            { 
                Id = existing.Id,
                PrinterId = printer.Id, 
                NozzleDiameter = 0.4,
                HasHeatedBed = true,
                LastUpdated = DateTime.UtcNow
            };
            refreshedCap.Printer = printer;

            discoveryMock
                .Setup(d => d.RefreshCapabilitiesAsync(existing, printer, It.IsAny<CancellationToken>()))
                .ReturnsAsync(refreshedCap);

            EfPrinterCapabilitiesRepository repo = new EfPrinterCapabilitiesRepository(db);

            PrinterCapabilitiesService svc = new PrinterCapabilitiesService(repo, loggerMock.Object, discoveryMock.Object);
            (PrinterCapabilitiesDto? result, bool isNew) = await svc.DiscoverAsync(printer.Id);
            
            Assert.NotNull(result);
            Assert.False(isNew); // Already existed
            Assert.Equal(0.4, result!.NozzleDiameter); // Refreshed value
        }
    }
}
