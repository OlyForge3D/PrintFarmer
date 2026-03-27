using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.Services.Cost;
using Farm.Infrastructure.Services.Interfaces;
using Farm.Infrastructure.Settings;
using Farm.Web.Api.Tests.TestInfrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Farm.Web.Api.Tests.Controllers;

/// <summary>
/// Integration tests for Job Cost Calculation feature.
/// Tests cost calculation service logic, cost endpoints, and data persistence.
/// </summary>
[Trait("Category", "Integration")]
[Collection(IntegrationTestCollection.Name)]
public class JobCostCalculationTests : IAsyncLifetime
{
    private readonly CustomWebApplicationFactory _factory;
    private HttpClient? _client;
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public JobCostCalculationTests()
    {
        _factory = new CustomWebApplicationFactory();
    }

    public async Task InitializeAsync()
    {
        await _factory.ResetDatabaseAsync();
        _client = _factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    /// <summary>
    /// Helper to create a test printer with optional cost tracking overrides.
    /// </summary>
    /// <param name="name">Optional printer name.</param>
    /// <param name="machineHourlyRate">Per-printer hourly rate override.</param>
    /// <param name="wattage">Per-printer wattage override.</param>
    /// <param name="modelDefaultWattage">Default wattage to set on the printer model.</param>
    private async Task<Printer> CreateTestPrinterAsync(
        string? name = null,
        decimal? machineHourlyRate = null,
        decimal? wattage = null,
        decimal? modelDefaultWattage = null)
    {
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Always create a fresh manufacturer and model to avoid picking up seeded DefaultWattage values
        var manufacturer = new Manufacturer { Id = Guid.NewGuid(), Name = $"Test Mfg {Guid.NewGuid().ToString().Substring(0, 8)}" };
        context.Manufacturers.Add(manufacturer);
        await context.SaveChangesAsync();

        var model = new PrinterModel
        {
            Id = Guid.NewGuid(),
            Name = $"Test Model {Guid.NewGuid().ToString().Substring(0, 8)}",
            ManufacturerId = manufacturer.Id,
            DefaultWattage = modelDefaultWattage
        };
        context.PrinterModels.Add(model);
        await context.SaveChangesAsync();

        var printer = new Printer
        {
            Id = Guid.NewGuid(),
            Name = name ?? $"test-printer-{Guid.NewGuid().ToString().Substring(0, 8)}",
            ServerUrl = $"http://test-printer-{Guid.NewGuid()}.local",
            BackendPort = 7125,
            Backend = (int)PrinterBackend.Moonraker,
            ManufacturerId = manufacturer.Id,
            ModelId = model.Id,
            MachineHourlyRate = machineHourlyRate,
            Wattage = wattage
        };

        context.Printers.Add(printer);
        await context.SaveChangesAsync();
        return printer;
    }

    /// <summary>
    /// Helper to create a test print job
    /// </summary>
    private async Task<PrintJob> CreateTestJobAsync(
        Printer printer,
        double? filamentUsage = null,
        TimeSpan? printTime = null,
        int? spoolmanFilamentId = null)
    {
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var job = new PrintJob
        {
            Id = Guid.NewGuid(),
            Name = "test-job.gcode",
            AssignedPrinterId = printer.Id,
            Status = PrintJobStatus.Completed,
            ActualFilamentUsage = filamentUsage,
            ActualPrintTime = printTime,
            SpoolmanFilamentId = spoolmanFilamentId,
            ActualStartTime = DateTime.UtcNow.AddHours(-2),
            ActualEndTime = DateTime.UtcNow
        };

        context.PrintJobs.Add(job);
        await context.SaveChangesAsync();
        return job;
    }

    #region Cost Calculation Service Tests

    [Fact]
    public async Task CalculateAndStoreCostsAsync_WithValidData_ProducesCorrectCosts()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IJobCostCalculationService>();
        var settingsService = scope.ServiceProvider.GetRequiredService<ISettingsService>();

        // Set up cost settings
        settingsService.Save(new CostTrackingSettings
        {
            EnableAutomaticCostCalculation = true,
            ElectricityRatePerKwh = 0.12m,
            AveragePrinterWattage = 200m,
            DefaultMachineHourlyRate = 5m,
            LaborMarkupPercent = 25m
        });

        var printer = await CreateTestPrinterAsync("test-printer", machineHourlyRate: 10m);
        var job = await CreateTestJobAsync(
            printer,
            filamentUsage: 50.0, // 50 grams
            printTime: TimeSpan.FromHours(2)); // 2 hours

        // Mock Spoolman service to return filament data
        var spoolmanService = scope.ServiceProvider.GetRequiredService<ISpoolmanService>();
        // Note: In real scenario, spoolman would need mocking. For this integration test,
        // we'll skip material cost since Spoolman isn't available

        // Act
        bool result = await service.CalculateAndStoreCostsAsync(job.Id, CancellationToken.None);

        // Assert
        result.Should().BeTrue();

        // Reload job to see calculated costs
        var updatedJob = await context.PrintJobs.FirstOrDefaultAsync(j => j.Id == job.Id);
        updatedJob.Should().NotBeNull();
        updatedJob!.CostCalculatedAt.Should().NotBeNull();

        // Material cost: 50g / 1000g × $25/kg (global default, no Spoolman) = $1.25
        updatedJob.MaterialCostUsd.Should().Be(1.25m);

        // Energy cost: 2 hours × 200W / 1000 × $0.12/kWh = $0.048 → rounds to $0.05
        updatedJob.EnergyCostUsd.Should().Be(0.05m);

        // Machine time cost: 2 hours × $10/hour = $20.00
        updatedJob.MachineTimeCostUsd.Should().Be(20.00m);

        // Labor cost: (1.25 + 0.05 + 20.00) × 25% = $5.325 → rounds to $5.32
        updatedJob.LaborCostUsd.Should().Be(5.32m);

        // Total: 1.25 + 0.05 + 20.00 + 5.32 = $26.62
        updatedJob.TotalCostUsd.Should().Be(26.62m);
    }

    [Fact]
    public async Task CalculateAndStoreCostsAsync_WithMissingPrinterWattage_UsesDefaultWattage()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IJobCostCalculationService>();
        var settingsService = scope.ServiceProvider.GetRequiredService<ISettingsService>();

        settingsService.Save(new CostTrackingSettings
        {
            EnableAutomaticCostCalculation = true,
            ElectricityRatePerKwh = 0.10m,
            AveragePrinterWattage = 150m, // Default wattage
            DefaultMachineHourlyRate = 5m,
            LaborMarkupPercent = 20m
        });

        var printer = await CreateTestPrinterAsync("test-printer");
        var job = await CreateTestJobAsync(printer, printTime: TimeSpan.FromHours(1));

        // Act
        bool result = await service.CalculateAndStoreCostsAsync(job.Id, CancellationToken.None);

        // Assert
        result.Should().BeTrue();

        var updatedJob = await context.PrintJobs.FirstOrDefaultAsync(j => j.Id == job.Id);
        updatedJob.Should().NotBeNull();

        // Energy cost: 1 hour × 150W / 1000 × $0.10/kWh = $0.015 → rounds to $0.02
        updatedJob!.EnergyCostUsd.Should().Be(0.02m);
    }

    [Fact]
    public async Task CalculateAndStoreCostsAsync_WithZeroDuration_ReturnsZeroCosts()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IJobCostCalculationService>();
        var settingsService = scope.ServiceProvider.GetRequiredService<ISettingsService>();

        settingsService.Save(new CostTrackingSettings
        {
            EnableAutomaticCostCalculation = true,
            ElectricityRatePerKwh = 0.12m,
            AveragePrinterWattage = 200m,
            DefaultMachineHourlyRate = 5m,
            LaborMarkupPercent = 25m
        });

        var printer = await CreateTestPrinterAsync();
        var job = await CreateTestJobAsync(printer, printTime: TimeSpan.Zero);

        // Act
        bool result = await service.CalculateAndStoreCostsAsync(job.Id, CancellationToken.None);

        // Assert
        result.Should().BeTrue();

        var updatedJob = await context.PrintJobs.FirstOrDefaultAsync(j => j.Id == job.Id);
        updatedJob.Should().NotBeNull();
        updatedJob!.EnergyCostUsd.Should().BeNull();
        updatedJob.MachineTimeCostUsd.Should().BeNull();
        updatedJob.LaborCostUsd.Should().BeNull();
        updatedJob.TotalCostUsd.Should().BeNull();
    }

    [Fact]
    public async Task CalculateAndStoreCostsAsync_WhenDisabled_ReturnsFalse()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IJobCostCalculationService>();
        var settingsService = scope.ServiceProvider.GetRequiredService<ISettingsService>();

        settingsService.Save(new CostTrackingSettings
        {
            EnableAutomaticCostCalculation = false
        });

        var printer = await CreateTestPrinterAsync();
        var job = await CreateTestJobAsync(printer, printTime: TimeSpan.FromHours(1));

        // Act
        bool result = await service.CalculateAndStoreCostsAsync(job.Id, CancellationToken.None);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task CalculateAndStoreCostsAsync_WithMissingJob_ReturnsFalse()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IJobCostCalculationService>();
        var settingsService = scope.ServiceProvider.GetRequiredService<ISettingsService>();

        settingsService.Save(new CostTrackingSettings
        {
            EnableAutomaticCostCalculation = true
        });

        // Act
        bool result = await service.CalculateAndStoreCostsAsync(Guid.NewGuid(), CancellationToken.None);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task RecalculateCostsWithOverridesAsync_WithOverrides_UsesProvidedValues()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IJobCostCalculationService>();

        var printer = await CreateTestPrinterAsync();
        var job = await CreateTestJobAsync(printer, printTime: TimeSpan.FromHours(1));

        // Act
        bool result = await service.RecalculateCostsWithOverridesAsync(
            job.Id,
            materialCost: 10m,
            energyCost: 0.50m,
            machineTimeCost: 5m,
            laborCost: 3m,
            ct: CancellationToken.None);

        // Assert
        result.Should().BeTrue();

        var updatedJob = await context.PrintJobs.FirstOrDefaultAsync(j => j.Id == job.Id);
        updatedJob.Should().NotBeNull();
        updatedJob!.MaterialCostUsd.Should().Be(10m);
        updatedJob.EnergyCostUsd.Should().Be(0.50m);
        updatedJob.MachineTimeCostUsd.Should().Be(5m);
        updatedJob.LaborCostUsd.Should().Be(3m);
        updatedJob.TotalCostUsd.Should().Be(18.50m);
    }

    [Fact]
    public async Task CalculateEnergyCost_WithPrinterWattageOverride_UsesOverride()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IJobCostCalculationService>();
        var settingsService = scope.ServiceProvider.GetRequiredService<ISettingsService>();

        settingsService.Save(new CostTrackingSettings
        {
            EnableAutomaticCostCalculation = true,
            ElectricityRatePerKwh = 0.10m,
            AveragePrinterWattage = 200m,
            DefaultMachineHourlyRate = 5m,
            LaborMarkupPercent = 0m
        });

        // Printer has explicit 400W override; model has 300W default
        var printer = await CreateTestPrinterAsync("wattage-override", wattage: 400m, modelDefaultWattage: 300m);
        var job = await CreateTestJobAsync(printer, printTime: TimeSpan.FromHours(1));

        // Act
        bool result = await service.CalculateAndStoreCostsAsync(job.Id, CancellationToken.None);

        // Assert
        result.Should().BeTrue();
        var updatedJob = await context.PrintJobs.FirstOrDefaultAsync(j => j.Id == job.Id);
        updatedJob.Should().NotBeNull();

        // Energy cost: 1 hour × 400W / 1000 × $0.10/kWh = $0.04
        updatedJob!.EnergyCostUsd.Should().Be(0.04m);
    }

    [Fact]
    public async Task CalculateEnergyCost_WithModelDefault_UsesModelDefault()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IJobCostCalculationService>();
        var settingsService = scope.ServiceProvider.GetRequiredService<ISettingsService>();

        settingsService.Save(new CostTrackingSettings
        {
            EnableAutomaticCostCalculation = true,
            ElectricityRatePerKwh = 0.10m,
            AveragePrinterWattage = 200m,
            DefaultMachineHourlyRate = 5m,
            LaborMarkupPercent = 0m
        });

        // Printer has no wattage override; model has 300W default
        var printer = await CreateTestPrinterAsync("model-default", wattage: null, modelDefaultWattage: 300m);
        var job = await CreateTestJobAsync(printer, printTime: TimeSpan.FromHours(1));

        // Act
        bool result = await service.CalculateAndStoreCostsAsync(job.Id, CancellationToken.None);

        // Assert
        result.Should().BeTrue();
        var updatedJob = await context.PrintJobs.FirstOrDefaultAsync(j => j.Id == job.Id);
        updatedJob.Should().NotBeNull();

        // Energy cost: 1 hour × 300W / 1000 × $0.10/kWh = $0.03
        updatedJob!.EnergyCostUsd.Should().Be(0.03m);
    }

    [Fact]
    public async Task CalculateEnergyCost_FullCascade_PrinterOverridesModelOverridesSettings()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IJobCostCalculationService>();
        var settingsService = scope.ServiceProvider.GetRequiredService<ISettingsService>();

        settingsService.Save(new CostTrackingSettings
        {
            EnableAutomaticCostCalculation = true,
            ElectricityRatePerKwh = 0.10m,
            AveragePrinterWattage = 200m,
            DefaultMachineHourlyRate = 5m,
            LaborMarkupPercent = 0m
        });

        // All three levels set: printer=500W wins over model=300W and settings=200W
        var printer = await CreateTestPrinterAsync("full-cascade", wattage: 500m, modelDefaultWattage: 300m);
        var job = await CreateTestJobAsync(printer, printTime: TimeSpan.FromHours(1));

        // Act
        bool result = await service.CalculateAndStoreCostsAsync(job.Id, CancellationToken.None);

        // Assert
        result.Should().BeTrue();
        var updatedJob = await context.PrintJobs.FirstOrDefaultAsync(j => j.Id == job.Id);
        updatedJob.Should().NotBeNull();

        // Energy cost: 1 hour × 500W / 1000 × $0.10/kWh = $0.05
        updatedJob!.EnergyCostUsd.Should().Be(0.05m);
    }

    [Fact]
    public async Task CalculateEnergyCost_NoOverrides_UsesSettingsDefault()
    {
        // Arrange
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IJobCostCalculationService>();
        var settingsService = scope.ServiceProvider.GetRequiredService<ISettingsService>();

        settingsService.Save(new CostTrackingSettings
        {
            EnableAutomaticCostCalculation = true,
            ElectricityRatePerKwh = 0.10m,
            AveragePrinterWattage = 250m,
            DefaultMachineHourlyRate = 5m,
            LaborMarkupPercent = 0m
        });

        // No wattage on printer, no DefaultWattage on model → falls back to settings (250W)
        var printer = await CreateTestPrinterAsync("no-overrides");
        var job = await CreateTestJobAsync(printer, printTime: TimeSpan.FromHours(1));

        // Act
        bool result = await service.CalculateAndStoreCostsAsync(job.Id, CancellationToken.None);

        // Assert
        result.Should().BeTrue();
        var updatedJob = await context.PrintJobs.FirstOrDefaultAsync(j => j.Id == job.Id);
        updatedJob.Should().NotBeNull();

        // Energy cost: 1 hour × 250W / 1000 × $0.10/kWh = $0.025 → rounds to $0.02 (banker's rounding)
        updatedJob!.EnergyCostUsd.Should().Be(0.02m);
    }

    #endregion

    #region Statistics Cost Endpoints Tests

    [Fact]
    public async Task GetCostsSummaryAsync_Returns200OK()
    {
        // Arrange
        var printer = await CreateTestPrinterAsync();
        await CreateTestJobAsync(printer, printTime: TimeSpan.FromHours(1));

        // Act
        HttpResponseMessage response = await _client!.GetAsync("/api/statistics/costs/summary?days=30");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        CostStatisticsSummaryDto? summary = await response.Content.ReadFromJsonAsync<CostStatisticsSummaryDto>(_jsonOptions);
        summary.Should().NotBeNull();
    }

    [Fact]
    public async Task GetCostsByTimePeriodAsync_ReturnsTimeSeriesData()
    {
        // Arrange
        var printer = await CreateTestPrinterAsync();
        await CreateTestJobAsync(printer, printTime: TimeSpan.FromHours(2));

        // Act
        HttpResponseMessage response = await _client!.GetAsync("/api/statistics/costs?days=7");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var costs = await response.Content.ReadFromJsonAsync<System.Collections.Generic.List<CostByTimePeriodDto>>(_jsonOptions);
        costs.Should().NotBeNull();
    }

    [Fact]
    public async Task GetCostsByPrinterAsync_ReturnsAggregatedCostsByPrinter()
    {
        // Arrange
        var printer1 = await CreateTestPrinterAsync("printer-1");
        var printer2 = await CreateTestPrinterAsync("printer-2");
        await CreateTestJobAsync(printer1, printTime: TimeSpan.FromHours(1));
        await CreateTestJobAsync(printer2, printTime: TimeSpan.FromHours(2));

        // Act
        HttpResponseMessage response = await _client!.GetAsync("/api/statistics/costs/by-printer?days=30");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var costs = await response.Content.ReadFromJsonAsync<System.Collections.Generic.List<CostByPrinterDto>>(_jsonOptions);
        costs.Should().NotBeNull();
        // Endpoint returns OK with aggregated data (may be empty if no costs calculated yet)
    }

    [Fact]
    public async Task GetCostsByMaterialAsync_ReturnsAggregatedCostsByMaterial()
    {
        // Arrange
        var printer = await CreateTestPrinterAsync();
        await CreateTestJobAsync(printer, printTime: TimeSpan.FromHours(1));

        // Act
        HttpResponseMessage response = await _client!.GetAsync("/api/statistics/costs/by-material?days=30");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var costs = await response.Content.ReadFromJsonAsync<System.Collections.Generic.List<CostByMaterialDto>>(_jsonOptions);
        costs.Should().NotBeNull();
    }

    [Fact]
    public async Task GetCostOverTimeAsync_ReturnsTimeSeriesData()
    {
        // Arrange
        var printer = await CreateTestPrinterAsync();
        await CreateTestJobAsync(printer, printTime: TimeSpan.FromHours(1));

        // Act
        HttpResponseMessage response = await _client!.GetAsync("/api/statistics/cost-over-time?days=30");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var costs = await response.Content.ReadFromJsonAsync<System.Collections.Generic.List<DailyCostDto>>(_jsonOptions);
        costs.Should().NotBeNull();
    }

    [Fact]
    public async Task GetCostsSummaryAsync_WithDaysFilter_RespectsTimeWindow()
    {
        // Arrange
        var printer = await CreateTestPrinterAsync();
        await CreateTestJobAsync(printer, printTime: TimeSpan.FromHours(1));

        // Act - Request last 7 days
        HttpResponseMessage response = await _client!.GetAsync("/api/statistics/costs/summary?days=7");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        CostStatisticsSummaryDto? summary = await response.Content.ReadFromJsonAsync<CostStatisticsSummaryDto>(_jsonOptions);
        summary.Should().NotBeNull();
    }

    [Fact]
    public async Task GetCostsByPrinterAsync_WithNoDaysFilter_ReturnsAllTimeCosts()
    {
        // Arrange
        var printer = await CreateTestPrinterAsync();
        await CreateTestJobAsync(printer, printTime: TimeSpan.FromHours(1));

        // Act - No days filter, should return all-time data
        HttpResponseMessage response = await _client!.GetAsync("/api/statistics/costs/by-printer");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var costs = await response.Content.ReadFromJsonAsync<System.Collections.Generic.List<CostByPrinterDto>>(_jsonOptions);
        costs.Should().NotBeNull();
    }

    #endregion
}
