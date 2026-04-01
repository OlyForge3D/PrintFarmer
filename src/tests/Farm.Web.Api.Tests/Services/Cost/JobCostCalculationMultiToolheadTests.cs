using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Cost;
using Farm.Infrastructure.Services.Interfaces;
using Farm.Infrastructure.Settings;
using Farm.Web.Api.Tests.TestInfrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Farm.Web.Api.Tests.Services.Cost;

/// <summary>
/// Tests for multi-toolhead cost calculation in JobCostCalculationService.
/// Covers per-extruder cost calculation, multi-spool consumption totals, cost aggregation
/// across toolheads, and edge cases (missing spool data, partial consumption).
/// </summary>
[Trait("Category", "Unit")]
[Collection(IntegrationTestCollection.Name)]
public class JobCostCalculationMultiToolheadTests : IAsyncLifetime
{
    private readonly CustomWebApplicationFactory _factory;
    private Printer? _testPrinter;

    public JobCostCalculationMultiToolheadTests()
    {
        _factory = new CustomWebApplicationFactory();
    }

    public async Task InitializeAsync()
    {
        await _factory.ResetDatabaseAsync();
        _testPrinter = await CreateTestPrinterAsync();
    }

    public async Task DisposeAsync()
    {
        _factory?.Dispose();
    }

    private async Task<Printer> CreateTestPrinterAsync()
    {
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var manufacturer = new Manufacturer { Id = Guid.NewGuid(), Name = "Test Mfg" };
        context.Manufacturers.Add(manufacturer);
        await context.SaveChangesAsync();

        var model = new PrinterModel
        {
            Id = Guid.NewGuid(),
            Name = "Test Multi-Extruder Model",
            ManufacturerId = manufacturer.Id,
            DefaultWattage = 200m,
            DefaultHourlyRate = 5m
        };
        context.PrinterModels.Add(model);
        await context.SaveChangesAsync();

        var printer = new Printer
        {
            Id = Guid.NewGuid(),
            Name = "test-multi-extruder-printer",
            ServerUrl = "http://test.local",
            BackendPort = 7125,
            Backend = (int)PrinterBackend.Moonraker,
            ManufacturerId = manufacturer.Id,
            ModelId = model.Id,
            MachineHourlyRate = 10m,
            Wattage = 250m
        };

        context.Printers.Add(printer);
        await context.SaveChangesAsync();
        return printer;
    }

    private async Task<PrintJob> CreateTestJobWithToolheadUsagesAsync(
        double? printTimeHours = null,
        params (int toolheadIndex, double usageGrams, int? spoolId)[] toolheadUsages)
    {
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var job = new PrintJob
        {
            Id = Guid.NewGuid(),
            Name = "multi-toolhead-job.gcode",
            AssignedPrinterId = _testPrinter!.Id,
            Status = PrintJobStatus.Completed,
            ActualPrintTime = printTimeHours.HasValue ? TimeSpan.FromHours(printTimeHours.Value) : TimeSpan.FromHours(2),
            ActualStartTime = DateTime.UtcNow.AddHours(-2),
            ActualEndTime = DateTime.UtcNow
        };

        context.PrintJobs.Add(job);
        await context.SaveChangesAsync();

        // Add toolhead usage records
        foreach (var (toolheadIndex, usageGrams, spoolId) in toolheadUsages)
        {
            var usage = new PrintJobToolheadUsage
            {
                Id = Guid.NewGuid(),
                PrintJobId = job.Id,
                ToolheadIndex = toolheadIndex,
                FilamentUsageGrams = usageGrams,
                SpoolmanSpoolId = spoolId,
                FilamentName = spoolId.HasValue ? $"Filament {spoolId}" : null,
                FilamentColor = spoolId.HasValue ? "#FF0000" : null
            };
            context.PrintJobToolheadUsages.Add(usage);
        }
        await context.SaveChangesAsync();

        return job;
    }

    [Fact]
    public async Task CalculateAndStoreCostsAsync_WithSingleToolhead_CalculatesPerExtruderCost()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IJobCostCalculationService>();
        var settingsService = scope.ServiceProvider.GetRequiredService<ISettingsService>();

        settingsService.Save(new CostTrackingSettings
        {
            EnableAutomaticCostCalculation = true,
            DefaultFilamentPricePerKg = 25m,
            ElectricityRatePerKwh = 0.12m,
            DefaultMachineHourlyRate = 5m,
            LaborMarkupPercent = 25m
        });

        // Create job with single toolhead usage: 100g on T0
        var job = await CreateTestJobWithToolheadUsagesAsync(
            printTimeHours: 2,
            (toolheadIndex: 0, usageGrams: 100.0, spoolId: null));

        // Act
        bool result = await service.CalculateAndStoreCostsAsync(job.Id, CancellationToken.None);

        // Assert
        result.Should().BeTrue();

        var updatedJob = await context.PrintJobs
            .Include(j => j.ToolheadUsages)
            .FirstOrDefaultAsync(j => j.Id == job.Id);
        updatedJob.Should().NotBeNull();

        // Material cost: 100g / 1000g × $25/kg = $2.50
        updatedJob!.MaterialCostUsd.Should().Be(2.50m);

        // Per-toolhead cost should be stored
        var t0Usage = updatedJob.ToolheadUsages.First(u => u.ToolheadIndex == 0);
        t0Usage.MaterialCostUsd.Should().Be(2.50m);
    }

    [Fact]
    public async Task CalculateAndStoreCostsAsync_WithMultipleToolheads_AggregatesCostAcrossToolheads()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IJobCostCalculationService>();
        var settingsService = scope.ServiceProvider.GetRequiredService<ISettingsService>();

        settingsService.Save(new CostTrackingSettings
        {
            EnableAutomaticCostCalculation = true,
            DefaultFilamentPricePerKg = 25m,
            ElectricityRatePerKwh = 0.12m,
            DefaultMachineHourlyRate = 5m,
            LaborMarkupPercent = 25m
        });

        // Create job with 3 toolheads: T0=50g, T1=75g, T2=100g
        var job = await CreateTestJobWithToolheadUsagesAsync(
            printTimeHours: 3,
            (toolheadIndex: 0, usageGrams: 50.0, spoolId: null),
            (toolheadIndex: 1, usageGrams: 75.0, spoolId: null),
            (toolheadIndex: 2, usageGrams: 100.0, spoolId: null));

        // Act
        bool result = await service.CalculateAndStoreCostsAsync(job.Id, CancellationToken.None);

        // Assert
        result.Should().BeTrue();

        var updatedJob = await context.PrintJobs
            .Include(j => j.ToolheadUsages)
            .FirstOrDefaultAsync(j => j.Id == job.Id);
        updatedJob.Should().NotBeNull();

        // Total material cost: Sum of per-toolhead rounded costs = $1.25 + $1.88 + $2.50 = $5.63
        updatedJob!.MaterialCostUsd.Should().Be(5.63m);

        // Verify per-toolhead costs
        var t0Usage = updatedJob.ToolheadUsages.First(u => u.ToolheadIndex == 0);
        var t1Usage = updatedJob.ToolheadUsages.First(u => u.ToolheadIndex == 1);
        var t2Usage = updatedJob.ToolheadUsages.First(u => u.ToolheadIndex == 2);

        // T0: 50g / 1000 × $25 = $1.25
        t0Usage.MaterialCostUsd.Should().Be(1.25m);
        // T1: 75g / 1000 × $25 = $1.875 → $1.88
        t1Usage.MaterialCostUsd.Should().Be(1.88m);
        // T2: 100g / 1000 × $25 = $2.50
        t2Usage.MaterialCostUsd.Should().Be(2.50m);

        // Sum of per-toolhead costs should equal total material cost
        var sumOfToolheadCosts = (t0Usage.MaterialCostUsd ?? 0) +
                                  (t1Usage.MaterialCostUsd ?? 0) +
                                  (t2Usage.MaterialCostUsd ?? 0);
        sumOfToolheadCosts.Should().Be(updatedJob.MaterialCostUsd);
    }

    [Fact]
    public async Task CalculateAndStoreCostsAsync_WithMissingSpoolData_UsesGlobalDefault()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IJobCostCalculationService>();
        var settingsService = scope.ServiceProvider.GetRequiredService<ISettingsService>();

        settingsService.Save(new CostTrackingSettings
        {
            EnableAutomaticCostCalculation = true,
            DefaultFilamentPricePerKg = 30m, // Custom default
            ElectricityRatePerKwh = 0.12m,
            DefaultMachineHourlyRate = 5m,
            LaborMarkupPercent = 0m
        });

        // Create job with toolhead usage but no spool ID
        var job = await CreateTestJobWithToolheadUsagesAsync(
            printTimeHours: 1,
            (toolheadIndex: 0, usageGrams: 100.0, spoolId: null));

        // Act
        bool result = await service.CalculateAndStoreCostsAsync(job.Id, CancellationToken.None);

        // Assert
        result.Should().BeTrue();

        var updatedJob = await context.PrintJobs
            .Include(j => j.ToolheadUsages)
            .FirstOrDefaultAsync(j => j.Id == job.Id);
        updatedJob.Should().NotBeNull();

        // Material cost: 100g / 1000g × $30/kg = $3.00
        updatedJob!.MaterialCostUsd.Should().Be(3.00m);

        var t0Usage = updatedJob.ToolheadUsages.First(u => u.ToolheadIndex == 0);
        t0Usage.MaterialCostUsd.Should().Be(3.00m);
    }

    [Fact]
    public async Task CalculateAndStoreCostsAsync_WithPartialConsumption_OnlySumsToolheadsWithUsage()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IJobCostCalculationService>();
        var settingsService = scope.ServiceProvider.GetRequiredService<ISettingsService>();

        settingsService.Save(new CostTrackingSettings
        {
            EnableAutomaticCostCalculation = true,
            DefaultFilamentPricePerKg = 25m,
            ElectricityRatePerKwh = 0.12m,
            DefaultMachineHourlyRate = 5m,
            LaborMarkupPercent = 0m
        });

        // Create job with 4 toolheads, but only 2 have non-zero usage
        var job = await CreateTestJobWithToolheadUsagesAsync(
            printTimeHours: 2,
            (toolheadIndex: 0, usageGrams: 50.0, spoolId: null),
            (toolheadIndex: 1, usageGrams: 0.0, spoolId: null),   // Zero usage
            (toolheadIndex: 2, usageGrams: 100.0, spoolId: null),
            (toolheadIndex: 3, usageGrams: 0.0, spoolId: null));  // Zero usage

        // Act
        bool result = await service.CalculateAndStoreCostsAsync(job.Id, CancellationToken.None);

        // Assert
        result.Should().BeTrue();

        var updatedJob = await context.PrintJobs
            .Include(j => j.ToolheadUsages)
            .FirstOrDefaultAsync(j => j.Id == job.Id);
        updatedJob.Should().NotBeNull();

        // Only T0 (50g) and T2 (100g) should contribute: 150g / 1000 × $25 = $3.75
        updatedJob!.MaterialCostUsd.Should().Be(3.75m);

        // T0 and T2 should have costs, T1 and T3 should be null
        var t0Usage = updatedJob.ToolheadUsages.First(u => u.ToolheadIndex == 0);
        var t1Usage = updatedJob.ToolheadUsages.First(u => u.ToolheadIndex == 1);
        var t2Usage = updatedJob.ToolheadUsages.First(u => u.ToolheadIndex == 2);
        var t3Usage = updatedJob.ToolheadUsages.First(u => u.ToolheadIndex == 3);

        t0Usage.MaterialCostUsd.Should().Be(1.25m); // 50g / 1000 × $25
        t1Usage.MaterialCostUsd.Should().BeNull();
        t2Usage.MaterialCostUsd.Should().Be(2.50m); // 100g / 1000 × $25
        t3Usage.MaterialCostUsd.Should().BeNull();
    }

    [Fact]
    public async Task CalculateAndStoreCostsAsync_WithNullFilamentUsage_SkipsToolhead()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IJobCostCalculationService>();
        var settingsService = scope.ServiceProvider.GetRequiredService<ISettingsService>();

        settingsService.Save(new CostTrackingSettings
        {
            EnableAutomaticCostCalculation = true,
            DefaultFilamentPricePerKg = 25m,
            ElectricityRatePerKwh = 0.12m,
            DefaultMachineHourlyRate = 5m,
            LaborMarkupPercent = 0m
        });

        var job = await CreateTestJobWithToolheadUsagesAsync(
            printTimeHours: 1,
            (toolheadIndex: 0, usageGrams: 100.0, spoolId: null));

        // Manually set T0 usage to null to test null handling
        using (var nullScope = _factory.Services.CreateAsyncScope())
        {
            var nullContext = nullScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var usage = await nullContext.PrintJobToolheadUsages
                .FirstAsync(u => u.PrintJobId == job.Id && u.ToolheadIndex == 0);
            usage.FilamentUsageGrams = null;
            await nullContext.SaveChangesAsync();
        }

        // Act
        bool result = await service.CalculateAndStoreCostsAsync(job.Id, CancellationToken.None);

        // Assert
        result.Should().BeTrue();

        var updatedJob = await context.PrintJobs
            .Include(j => j.ToolheadUsages)
            .FirstOrDefaultAsync(j => j.Id == job.Id);
        updatedJob.Should().NotBeNull();

        // No material cost should be calculated since the only toolhead has null usage
        updatedJob!.MaterialCostUsd.Should().BeNull();

        var t0Usage = updatedJob.ToolheadUsages.First(u => u.ToolheadIndex == 0);
        t0Usage.MaterialCostUsd.Should().BeNull();
    }

    [Fact]
    public async Task CalculateAndStoreCostsAsync_WithAllZeroUsage_ReturnsNullMaterialCost()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IJobCostCalculationService>();
        var settingsService = scope.ServiceProvider.GetRequiredService<ISettingsService>();

        settingsService.Save(new CostTrackingSettings
        {
            EnableAutomaticCostCalculation = true,
            DefaultFilamentPricePerKg = 25m
        });

        // All toolheads have zero usage
        var job = await CreateTestJobWithToolheadUsagesAsync(
            printTimeHours: 1,
            (toolheadIndex: 0, usageGrams: 0.0, spoolId: null),
            (toolheadIndex: 1, usageGrams: 0.0, spoolId: null));

        // Act
        bool result = await service.CalculateAndStoreCostsAsync(job.Id, CancellationToken.None);

        // Assert
        result.Should().BeTrue();

        var updatedJob = await context.PrintJobs
            .Include(j => j.ToolheadUsages)
            .FirstOrDefaultAsync(j => j.Id == job.Id);
        updatedJob.Should().NotBeNull();

        // Material cost should be null when no filament was consumed
        updatedJob!.MaterialCostUsd.Should().BeNull();
    }

    [Fact]
    public async Task CalculateAndStoreCostsAsync_MultiToolhead_IncludesEnergyAndMachineCosts()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IJobCostCalculationService>();
        var settingsService = scope.ServiceProvider.GetRequiredService<ISettingsService>();

        settingsService.Save(new CostTrackingSettings
        {
            EnableAutomaticCostCalculation = true,
            DefaultFilamentPricePerKg = 25m,
            ElectricityRatePerKwh = 0.10m,
            AveragePrinterWattage = 250m,
            DefaultMachineHourlyRate = 5m,
            LaborMarkupPercent = 20m
        });

        var job = await CreateTestJobWithToolheadUsagesAsync(
            printTimeHours: 2, // 2 hours print time
            (toolheadIndex: 0, usageGrams: 100.0, spoolId: null),
            (toolheadIndex: 1, usageGrams: 50.0, spoolId: null));

        // Act
        bool result = await service.CalculateAndStoreCostsAsync(job.Id, CancellationToken.None);

        // Assert
        result.Should().BeTrue();

        var updatedJob = await context.PrintJobs
            .Include(j => j.ToolheadUsages)
            .FirstOrDefaultAsync(j => j.Id == job.Id);
        updatedJob.Should().NotBeNull();

        // Material: (100g + 50g) / 1000 × $25 = $3.75
        updatedJob!.MaterialCostUsd.Should().Be(3.75m);

        // Energy: 2 hours × 250W / 1000 × $0.10/kWh = 0.5 kWh × $0.10 = $0.05
        updatedJob.EnergyCostUsd.Should().Be(0.05m);

        // Machine: 2 hours × $10/hour (printer override) = $20.00
        updatedJob.MachineTimeCostUsd.Should().Be(20.00m);

        // Subtotal: $3.75 + $0.05 + $20.00 = $23.80
        // Labor: $23.80 × 20% = $4.76
        updatedJob.LaborCostUsd.Should().Be(4.76m);

        // Total: $23.80 + $4.76 = $28.56
        updatedJob.TotalCostUsd.Should().Be(28.56m);
    }

    [Fact]
    public async Task CalculateAndStoreCostsAsync_WithEmptyToolheadUsages_FallsBackToSingleSpoolPath()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IJobCostCalculationService>();
        var settingsService = scope.ServiceProvider.GetRequiredService<ISettingsService>();

        settingsService.Save(new CostTrackingSettings
        {
            EnableAutomaticCostCalculation = true,
            DefaultFilamentPricePerKg = 25m
        });

        // Create job without toolhead usages
        var job = new PrintJob
        {
            Id = Guid.NewGuid(),
            Name = "single-spool-job.gcode",
            AssignedPrinterId = _testPrinter!.Id,
            Status = PrintJobStatus.Completed,
            ActualFilamentUsage = 100.0, // Single-spool usage
            ActualPrintTime = TimeSpan.FromHours(1),
            ActualStartTime = DateTime.UtcNow.AddHours(-1),
            ActualEndTime = DateTime.UtcNow
        };

        using (var jobScope = _factory.Services.CreateAsyncScope())
        {
            var jobContext = jobScope.ServiceProvider.GetRequiredService<AppDbContext>();
            jobContext.PrintJobs.Add(job);
            await jobContext.SaveChangesAsync();
        }

        // Act
        bool result = await service.CalculateAndStoreCostsAsync(job.Id, CancellationToken.None);

        // Assert
        result.Should().BeTrue();

        var updatedJob = await context.PrintJobs
            .Include(j => j.ToolheadUsages)
            .FirstOrDefaultAsync(j => j.Id == job.Id);
        updatedJob.Should().NotBeNull();

        // Should use single-spool path: 100g / 1000 × $25 = $2.50
        updatedJob!.MaterialCostUsd.Should().Be(2.50m);
        updatedJob.ToolheadUsages.Should().BeEmpty();
    }

    [Fact]
    public async Task CalculateAndStoreCostsAsync_MultiToolhead_BoundaryBetweenSingleAndMulti()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IJobCostCalculationService>();
        var settingsService = scope.ServiceProvider.GetRequiredService<ISettingsService>();

        settingsService.Save(new CostTrackingSettings
        {
            EnableAutomaticCostCalculation = true,
            DefaultFilamentPricePerKg = 25m
        });

        // Test with exactly 1 toolhead usage (boundary between paths)
        var job = await CreateTestJobWithToolheadUsagesAsync(
            printTimeHours: 1,
            (toolheadIndex: 0, usageGrams: 100.0, spoolId: null));

        // Act
        bool result = await service.CalculateAndStoreCostsAsync(job.Id, CancellationToken.None);

        // Assert
        result.Should().BeTrue();

        var updatedJob = await context.PrintJobs
            .Include(j => j.ToolheadUsages)
            .FirstOrDefaultAsync(j => j.Id == job.Id);
        updatedJob.Should().NotBeNull();

        // Should use multi-toolhead path even with 1 toolhead
        updatedJob!.MaterialCostUsd.Should().Be(2.50m);
        updatedJob.ToolheadUsages.Should().HaveCount(1);
        updatedJob.ToolheadUsages.First().MaterialCostUsd.Should().Be(2.50m);
    }

    [Fact]
    public async Task CalculateAndStoreCostsAsync_WithVerySmallUsage_RoundsCorrectly()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IJobCostCalculationService>();
        var settingsService = scope.ServiceProvider.GetRequiredService<ISettingsService>();

        settingsService.Save(new CostTrackingSettings
        {
            EnableAutomaticCostCalculation = true,
            DefaultFilamentPricePerKg = 25m
        });

        // Very small usage amounts to test rounding
        var job = await CreateTestJobWithToolheadUsagesAsync(
            printTimeHours: 1,
            (toolheadIndex: 0, usageGrams: 0.5, spoolId: null),  // 0.5g = $0.0125 → rounds to $0.01
            (toolheadIndex: 1, usageGrams: 1.5, spoolId: null)); // 1.5g = $0.0375 → rounds to $0.04

        // Act
        bool result = await service.CalculateAndStoreCostsAsync(job.Id, CancellationToken.None);

        // Assert
        result.Should().BeTrue();

        var updatedJob = await context.PrintJobs
            .Include(j => j.ToolheadUsages)
            .FirstOrDefaultAsync(j => j.Id == job.Id);
        updatedJob.Should().NotBeNull();

        var t0Usage = updatedJob!.ToolheadUsages.First(u => u.ToolheadIndex == 0);
        var t1Usage = updatedJob.ToolheadUsages.First(u => u.ToolheadIndex == 1);

        // T0: 0.5g / 1000 × $25 = $0.0125 → rounds to $0.01
        t0Usage.MaterialCostUsd.Should().Be(0.01m);
        // T1: 1.5g / 1000 × $25 = $0.0375 → rounds to $0.04
        t1Usage.MaterialCostUsd.Should().Be(0.04m);

        // Total: $0.01 + $0.04 = $0.05
        updatedJob.MaterialCostUsd.Should().Be(0.05m);
    }

    [Fact]
    public async Task CalculateAndStoreCostsAsync_WithNegativeUsage_SkipsToolhead()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IJobCostCalculationService>();
        var settingsService = scope.ServiceProvider.GetRequiredService<ISettingsService>();

        settingsService.Save(new CostTrackingSettings
        {
            EnableAutomaticCostCalculation = true,
            DefaultFilamentPricePerKg = 25m
        });

        var job = await CreateTestJobWithToolheadUsagesAsync(
            printTimeHours: 1,
            (toolheadIndex: 0, usageGrams: 100.0, spoolId: null));

        // Manually set negative usage (edge case)
        using (var negScope = _factory.Services.CreateAsyncScope())
        {
            var negContext = negScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var usage = await negContext.PrintJobToolheadUsages
                .FirstAsync(u => u.PrintJobId == job.Id && u.ToolheadIndex == 0);
            usage.FilamentUsageGrams = -50.0; // Invalid negative
            await negContext.SaveChangesAsync();
        }

        // Act
        bool result = await service.CalculateAndStoreCostsAsync(job.Id, CancellationToken.None);

        // Assert
        result.Should().BeTrue();

        var updatedJob = await context.PrintJobs
            .Include(j => j.ToolheadUsages)
            .FirstOrDefaultAsync(j => j.Id == job.Id);
        updatedJob.Should().NotBeNull();

        // Negative usage should be skipped, resulting in null material cost
        updatedJob!.MaterialCostUsd.Should().BeNull();

        var t0Usage = updatedJob.ToolheadUsages.First(u => u.ToolheadIndex == 0);
        t0Usage.MaterialCostUsd.Should().BeNull();
    }
}
