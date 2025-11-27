using System;
using System.Linq;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.PrinterCapabilities;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Services.Interfaces;
using Farm.Web.Api.Services.PrinterCapabilities;
using Farm.Web.Shared;
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
            SqliteConnection connection = new Microsoft.Data.Sqlite.SqliteConnection("DataSource=:memory:");
            connection.Open();
            DbContextOptions<AppDbContext> opts = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(connection)
                .Options;

            AppDbContext ctx = new AppDbContext(opts);
            ctx.Database.EnsureCreated();
            return ctx;
        }

        [Fact]
        public async Task GetAllAsync_ReturnsExistingCapabilities()
        {
            using AppDbContext db = CreateSqliteInMemoryDb();
            // Seed a printer and its capabilities
            Manufacturer manufacturer = new Farm.Infrastructure.Domain.Manufacturer { Id = Guid.NewGuid(), Name = "M1" };
            PrinterModel model = new Farm.Infrastructure.Domain.PrinterModel { Id = Guid.NewGuid(), Name = "Model1", ManufacturerId = manufacturer.Id, Manufacturer = manufacturer };
            Printer printer = new Farm.Infrastructure.Domain.Printer { Id = Guid.NewGuid(), Name = "P1", ManufacturerId = manufacturer.Id, Manufacturer = manufacturer, ModelId = model.Id, Model = model };
            db.Manufacturers.Add(manufacturer);
            db.Models.Add(model);
            db.Printers.Add(printer);
            PrinterCapabilities cap = new Farm.Infrastructure.Domain.PrinterCapabilities { Id = Guid.NewGuid(), PrinterId = printer.Id, IsAvailable = true, LastUpdated = DateTime.UtcNow };
            cap.Printer = printer;
            db.PrinterCapabilities.Add(cap);
            await db.SaveChangesAsync();
            // Ensure the in-memory DB contains the capability we just added.
            await db.PrinterCapabilities.Include(c => c.Printer).ToListAsync();

            Mock<IPrinterCapabilityDiscoveryService> discoveryMock = new Moq.Mock<Farm.Web.Api.Services.Interfaces.IPrinterCapabilityDiscoveryService>();
            Mock<IUnifiedLoggingService> loggerMock = new Moq.Mock<Farm.Infrastructure.Telemetry.IUnifiedLoggingService>();
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
            Manufacturer manufacturer = new Farm.Infrastructure.Domain.Manufacturer { Id = Guid.NewGuid(), Name = "M2" };
            PrinterModel model = new Farm.Infrastructure.Domain.PrinterModel { Id = Guid.NewGuid(), Name = "Model2", ManufacturerId = manufacturer.Id, Manufacturer = manufacturer };
            Printer printer = new Farm.Infrastructure.Domain.Printer { Id = Guid.NewGuid(), Name = "P2", ManufacturerId = manufacturer.Id, Manufacturer = manufacturer, ModelId = model.Id, Model = model };
            db.Manufacturers.Add(manufacturer);
            db.Models.Add(model);
            db.Printers.Add(printer);
            await db.SaveChangesAsync();

            Mock<IPrinterCapabilityDiscoveryService> discoveryMock = new Moq.Mock<Farm.Web.Api.Services.Interfaces.IPrinterCapabilityDiscoveryService>();
            Mock<IUnifiedLoggingService> loggerMock = new Moq.Mock<Farm.Infrastructure.Telemetry.IUnifiedLoggingService>();
            EfPrinterCapabilitiesRepository repo = new EfPrinterCapabilitiesRepository(db);

            PrinterCapabilitiesService svc = new PrinterCapabilitiesService(repo, loggerMock.Object, discoveryMock.Object);

            CreatePrinterCapabilitiesDto req = new CreatePrinterCapabilitiesDto(printer.Id);

            PrinterCapabilitiesDto? created = await svc.CreateAsync(req);
            Assert.NotNull(created);
            Assert.Equal(printer.Id, created!.PrinterId);
        }
    }
}
