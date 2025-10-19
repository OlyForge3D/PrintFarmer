using System;
using System.Linq;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Repositories.PrinterCapabilities;
using Farm.Web.Api.Services.PrinterCapabilities;
using Farm.Web.Shared;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Farm.Web.Api.Tests.Services
{
    public class PrinterCapabilitiesServiceTests
    {
        private static AppDbContext CreateSqliteInMemoryDb()
        {
            // Use SQLite in-memory to provide relational Include/ThenInclude semantics similar to production
            var connection = new Microsoft.Data.Sqlite.SqliteConnection("DataSource=:memory:");
            connection.Open();
            var opts = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(connection)
                .Options;

            var ctx = new AppDbContext(opts);
            ctx.Database.EnsureCreated();
            return ctx;
        }

        [Fact]
        public async Task GetAllAsync_ReturnsExistingCapabilities()
        {
            using var db = CreateSqliteInMemoryDb();
            // Seed a printer and its capabilities
            var manufacturer = new Farm.Infrastructure.Domain.Manufacturer { Id = Guid.NewGuid(), Name = "M1" };
            var model = new Farm.Infrastructure.Domain.PrinterModel { Id = Guid.NewGuid(), Name = "Model1", ManufacturerId = manufacturer.Id, Manufacturer = manufacturer };
            var printer = new Farm.Infrastructure.Domain.Printer { Id = Guid.NewGuid(), Name = "P1", ManufacturerId = manufacturer.Id, Manufacturer = manufacturer, ModelId = model.Id, Model = model };
            db.Manufacturers.Add(manufacturer);
            db.Models.Add(model);
            db.Printers.Add(printer);
            var cap = new Farm.Infrastructure.Domain.PrinterCapabilities { Id = Guid.NewGuid(), PrinterId = printer.Id, IsAvailable = true, LastUpdated = DateTime.UtcNow };
            cap.Printer = printer;
            db.PrinterCapabilities.Add(cap);
            await db.SaveChangesAsync();
            // Ensure the in-memory DB contains the capability we just added.
            await db.PrinterCapabilities.Include(c => c.Printer).ToListAsync();

            var discoveryMock = new Moq.Mock<Farm.Web.Api.Services.Interfaces.IPrinterCapabilityDiscoveryService>();
            var loggerMock = new Moq.Mock<Farm.Infrastructure.Telemetry.IUnifiedLoggingService>();
            var repo = new EfPrinterCapabilitiesRepository(db);

            var svc = new PrinterCapabilitiesService(repo, loggerMock.Object, discoveryMock.Object);
            var dto = await svc.GetByPrinterIdAsync(printer.Id);
            Assert.NotNull(dto);
            Assert.Equal(printer.Id, dto!.PrinterId);
        }

        [Fact]
        public async Task CreateAsync_CreatesCapabilities_WhenPrinterExists()
        {
            using var db = CreateSqliteInMemoryDb();
            var manufacturer = new Farm.Infrastructure.Domain.Manufacturer { Id = Guid.NewGuid(), Name = "M2" };
            var model = new Farm.Infrastructure.Domain.PrinterModel { Id = Guid.NewGuid(), Name = "Model2", ManufacturerId = manufacturer.Id, Manufacturer = manufacturer };
            var printer = new Farm.Infrastructure.Domain.Printer { Id = Guid.NewGuid(), Name = "P2", ManufacturerId = manufacturer.Id, Manufacturer = manufacturer, ModelId = model.Id, Model = model };
            db.Manufacturers.Add(manufacturer);
            db.Models.Add(model);
            db.Printers.Add(printer);
            await db.SaveChangesAsync();

            var discoveryMock = new Moq.Mock<Farm.Web.Api.Services.Interfaces.IPrinterCapabilityDiscoveryService>();
            var loggerMock = new Moq.Mock<Farm.Infrastructure.Telemetry.IUnifiedLoggingService>();
            var repo = new EfPrinterCapabilitiesRepository(db);

            var svc = new PrinterCapabilitiesService(repo, loggerMock.Object, discoveryMock.Object);

            var req = new CreatePrinterCapabilitiesDto(printer.Id);

            var created = await svc.CreateAsync(req);
            Assert.NotNull(created);
            Assert.Equal(printer.Id, created!.PrinterId);
        }
    }
}
