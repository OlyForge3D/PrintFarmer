using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Cost;
using Farm.Web.Api.Services.PowerMonitor;
using Farm.Web.Api.Services.SmartPlug;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services.PowerMonitor;

/// <summary>
/// Verifies that kWh backfill for completed jobs works even when the
/// PowerMonitor is disabled — the Enabled flag gates future polling only.
/// </summary>
public class PowerMonitorBackfillDisabledTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public PowerMonitorBackfillDisabledTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var ctx = new AppDbContext(_options);
        ctx.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private AppDbContext CreateContext() => new(_options);

    [Fact]
    public async Task AggregateCompletedJobs_MonitorDisabled_StillPopulatesKwhUsed()
    {
        // Arrange — seed data
        Guid printerId = Guid.NewGuid();
        Guid manufacturerId = Guid.NewGuid();
        Guid modelId = Guid.NewGuid();
        Guid jobId = Guid.NewGuid();
        DateTime jobStart = DateTime.UtcNow.AddHours(-2);
        DateTime jobEnd = DateTime.UtcNow.AddHours(-1);

        using (AppDbContext db = CreateContext())
        {
            db.Manufacturers.Add(new Manufacturer { Id = manufacturerId, Name = "TestMfg" });
            db.PrinterModels.Add(new PrinterModel { Id = modelId, Name = "TestModel", ManufacturerId = manufacturerId });
            await db.SaveChangesAsync();

            db.Printers.Add(new Printer
            {
                Id = printerId,
                Name = "Test Printer",
                ServerUrl = "http://192.168.1.100:7125",
                BackendPort = 7125,
                Backend = 1,
                ManufacturerId = manufacturerId,
                ModelId = modelId,
            });
            await db.SaveChangesAsync();

            // Monitor is DISABLED — simulates user disabling after job completed
            var monitor = new Farm.Infrastructure.Domain.PowerMonitor
            {
                PrinterId = printerId,
                ProviderType = "Kasa",
                DeviceAddress = "192.168.1.200",
                IsEnabled = false,
            };
            db.PowerMonitors.Add(monitor);
            await db.SaveChangesAsync();

            db.PrintJobs.Add(new PrintJob
            {
                Id = jobId,
                Name = "test.gcode",
                Status = PrintJobStatus.Completed,
                AssignedPrinterId = printerId,
                ActualStartTime = jobStart,
                ActualEndTime = jobEnd,
                KwhUsed = null,
            });

            db.PowerReadings.Add(new Farm.Infrastructure.Domain.PowerReading
            {
                PowerMonitorId = monitor.Id,
                WattsNow = 100m,
                RecordedAt = jobStart.AddMinutes(10),
            });
            db.PowerReadings.Add(new Farm.Infrastructure.Domain.PowerReading
            {
                PowerMonitorId = monitor.Id,
                WattsNow = 120m,
                RecordedAt = jobStart.AddMinutes(40),
            });
            await db.SaveChangesAsync();
        }

        // Build service
        Mock<IJobCostCalculationService> costServiceMock = new();
        costServiceMock
            .Setup(c => c.CalculateAndStoreCostsAsync(jobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new[]
            {
                new KeyValuePair<string, string?>("PowerMonitor:PollIntervalSeconds", "30")
            })
            .Build());
        services.AddScoped(_ => CreateContext());
        services.AddScoped<IJobCostCalculationService>(_ => costServiceMock.Object);
        services.AddScoped<ISmartPlugProvider, FakeSmartPlugProvider>();

        using ServiceProvider provider = services.BuildServiceProvider();
        IServiceScopeFactory scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        PowerMonitorPollingService svc = new(
            scopeFactory,
            provider.GetRequiredService<IConfiguration>(),
            NullLogger<PowerMonitorPollingService>.Instance);

        // Act — run one poll cycle
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        await svc.StartAsync(cts.Token);
        await Task.Delay(TimeSpan.FromSeconds(2));
        await svc.StopAsync(CancellationToken.None);

        // Assert
        using (AppDbContext db = CreateContext())
        {
            PrintJob? updatedJob = db.PrintJobs.FirstOrDefault(j => j.Id == jobId);
            Assert.NotNull(updatedJob);
            Assert.NotNull(updatedJob.KwhUsed);
            Assert.True(updatedJob.KwhUsed > 0,
                $"Expected KwhUsed > 0 but got {updatedJob.KwhUsed}");
        }

        costServiceMock.Verify(
            c => c.CalculateAndStoreCostsAsync(jobId, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    private sealed class FakeSmartPlugProvider : ISmartPlugProvider
    {
        public string ProviderType => "Kasa";

        public Task<Farm.Web.Api.Services.SmartPlug.PowerReading?> GetCurrentReadingAsync(string deviceAddress, CancellationToken ct)
            => Task.FromResult<Farm.Web.Api.Services.SmartPlug.PowerReading?>(null);

        public Task<bool> TestConnectionAsync(string deviceAddress, CancellationToken ct)
            => Task.FromResult(true);
    }
}
