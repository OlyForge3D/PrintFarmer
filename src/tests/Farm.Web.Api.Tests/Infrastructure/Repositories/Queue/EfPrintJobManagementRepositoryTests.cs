using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Queue;
using Microsoft.EntityFrameworkCore;

namespace Farm.Web.Api.Tests.Infrastructure.Repositories.Queue;

public class EfPrintJobManagementRepositoryTests
{
    [Fact]
    public async Task GetEnabledPrintersAsync_WhenServiceStateExists_LoadsServiceStateForWatermarkReads()
    {
        string dbName = $"GetEnabledPrintersAsync_{Guid.NewGuid():N}";
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;

        Guid printerId = Guid.NewGuid();
        DateTime watermarkUtc = DateTime.UtcNow.AddMinutes(-42);

        await using (AppDbContext seedContext = new(options))
        {
            Printer printer = new()
            {
                Id = printerId,
                Name = "Enabled Printer",
                ServerUrl = "http://printer.local",
                BackendPort = 80,
                Backend = (int)PrinterBackend.PrusaLink,
                IsEnabled = true,
                ManufacturerId = Guid.NewGuid(),
                ModelId = Guid.NewGuid(),
            };

            PrinterServiceState serviceState = new()
            {
                PrinterId = printerId,
                LastHistorySeedUtc = watermarkUtc,
            };

            seedContext.Printers.Add(printer);
            seedContext.PrinterServiceStates.Add(serviceState);
            await seedContext.SaveChangesAsync();
        }

        await using AppDbContext queryContext = new(options);
        EfPrintJobManagementRepository repository = new(queryContext);

        List<Printer> printers = await repository.GetEnabledPrintersAsync();

        Printer loaded = Assert.Single(printers);
        Assert.NotNull(loaded.ServiceState);
        Assert.Equal(watermarkUtc, loaded.ServiceState!.LastHistorySeedUtc);
    }
}
