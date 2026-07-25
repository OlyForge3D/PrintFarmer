using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos;
using Farm.Web.Api.Tests.TestInfrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Farm.Web.Api.Tests.Controllers;

/// <summary>
/// Tests for custom date range support on statistics endpoints.
/// Validates startDate/endDate query parameters, precedence over days,
/// and 400 responses for invalid ranges.
/// </summary>
[Trait("Category", "Integration")]
[Collection(IntegrationTestCollection.Name)]
public class StatisticsDateRangeTests : IAsyncLifetime
{
    private readonly CustomWebApplicationFactory _factory;
    private HttpClient? _client;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public StatisticsDateRangeTests()
    {
        _factory = new CustomWebApplicationFactory();
    }

    public async Task InitializeAsync()
    {
        await _factory.ResetDatabaseAsync();
        _client = _factory.CreateClient();
    }

    public Task DisposeAsync()
    {
        _client?.Dispose();
        _factory?.Dispose();
        return Task.CompletedTask;
    }

    #region Test Data Helpers

    private async Task<Printer> CreateTestPrinterAsync(string? name = null)
    {
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        Manufacturer? manufacturer = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
            .FirstOrDefaultAsync(context.Manufacturers);
        if (manufacturer is null)
        {
            manufacturer = new Manufacturer { Id = Guid.NewGuid(), Name = "Test Manufacturer" };
            context.Manufacturers.Add(manufacturer);
            await context.SaveChangesAsync();
        }

        PrinterModel? model = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
            .FirstOrDefaultAsync(context.PrinterModels);
        if (model is null)
        {
            model = new PrinterModel { Id = Guid.NewGuid(), Name = "Test Model", ManufacturerId = manufacturer.Id };
            context.PrinterModels.Add(model);
            await context.SaveChangesAsync();
        }

        var printer = new Printer
        {
            Id = Guid.NewGuid(),
            Name = name ?? $"test-printer-{Guid.NewGuid().ToString()[..8]}",
            ServerUrl = $"http://test-{Guid.NewGuid()}.local",
            BackendPort = 7125,
            Backend = (int)PrinterBackend.Moonraker,
            ManufacturerId = manufacturer.Id,
            ModelId = model.Id,
        };

        context.Printers.Add(printer);
        await context.SaveChangesAsync();
        return printer;
    }

    private async Task CreateCompletedJobAsync(
        Printer printer,
        DateTime queuedAt,
        DateTime? actualEndTime = null,
        decimal? totalCostUsd = null,
        string? filamentName = null)
    {
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var job = new PrintJob
        {
            Id = Guid.NewGuid(),
            Name = "date-range-test.gcode",
            AssignedPrinterId = printer.Id,
            Status = PrintJobStatus.Completed,
            QueuedAt = queuedAt,
            CreatedAt = queuedAt,
            UpdatedAt = queuedAt,
            ActualStartTime = queuedAt.AddMinutes(5),
            ActualEndTime = actualEndTime ?? queuedAt.AddHours(1),
            ActualPrintTime = TimeSpan.FromHours(1),
            ActualFilamentUsage = 25.0,
            RequiredMaterialType = "PLA",
            TotalCostUsd = totalCostUsd,
            MaterialCostUsd = totalCostUsd.HasValue ? totalCostUsd.Value * 0.5m : null,
            EnergyCostUsd = totalCostUsd.HasValue ? totalCostUsd.Value * 0.2m : null,
            MachineTimeCostUsd = totalCostUsd.HasValue ? totalCostUsd.Value * 0.2m : null,
            LaborCostUsd = totalCostUsd.HasValue ? totalCostUsd.Value * 0.1m : null,
            FilamentName = filamentName,
        };

        context.PrintJobs.Add(job);
        await context.SaveChangesAsync();
    }

    #endregion

    #region Validation: startDate > endDate returns 400

    [Theory]
    [InlineData("/api/statistics/summary")]
    [InlineData("/api/statistics/jobs-over-time")]
    [InlineData("/api/statistics/cost-over-time")]
    [InlineData("/api/statistics/filament-by-material")]
    [InlineData("/api/statistics/printer-utilization")]
    [InlineData("/api/statistics/costs/summary")]
    [InlineData("/api/statistics/costs")]
    [InlineData("/api/statistics/costs/by-printer")]
    [InlineData("/api/statistics/costs/by-material")]
    public async Task Endpoint_StartDateAfterEndDate_Returns400(string endpoint)
    {
        var startDate = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var endDate = DateTime.UtcNow.AddDays(-30).ToString("yyyy-MM-dd");

        HttpResponseMessage response = await _client!.GetAsync($"{endpoint}?startDate={startDate}&endDate={endDate}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        string body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("startDate must be before endDate");
    }

    [Fact]
    public async Task CostsSummary_RangeExceeds730Days_Returns400()
    {
        var startDate = DateTime.UtcNow.AddDays(-800).ToString("yyyy-MM-dd");
        var endDate = DateTime.UtcNow.ToString("yyyy-MM-dd");

        HttpResponseMessage response = await _client!.GetAsync(
            $"/api/statistics/costs/summary?startDate={startDate}&endDate={endDate}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        string body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("730 days");
    }

    #endregion

    #region Custom date range filtering

    [Fact]
    public async Task Summary_CustomDateRange_ReturnsOnlyJobsInRange()
    {
        var printer = await CreateTestPrinterAsync();
        var now = DateTime.UtcNow;

        // Job 60 days ago (outside range)
        await CreateCompletedJobAsync(printer, now.AddDays(-60));
        // Job 10 days ago (inside range)
        await CreateCompletedJobAsync(printer, now.AddDays(-10));
        // Job 5 days ago (inside range)
        await CreateCompletedJobAsync(printer, now.AddDays(-5));

        var startDate = now.AddDays(-15).ToString("yyyy-MM-dd");
        var endDate = now.ToString("yyyy-MM-dd");

        HttpResponseMessage response = await _client!.GetAsync(
            $"/api/statistics/summary?startDate={startDate}&endDate={endDate}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var summary = await response.Content.ReadFromJsonAsync<StatisticsSummaryDto>(JsonOptions);
        summary.Should().NotBeNull();
        summary!.TotalJobs.Should().Be(2, "only 2 jobs fall within the 15-day range");
    }

    [Fact]
    public async Task CostsSummary_CustomDateRange_FiltersByActualEndTime()
    {
        var printer = await CreateTestPrinterAsync();
        var now = DateTime.UtcNow;

        // Job ended 60 days ago (outside range)
        await CreateCompletedJobAsync(printer, now.AddDays(-61), actualEndTime: now.AddDays(-60), totalCostUsd: 5.00m, filamentName: "PLA Red");
        // Job ended 10 days ago (inside range)
        await CreateCompletedJobAsync(printer, now.AddDays(-11), actualEndTime: now.AddDays(-10), totalCostUsd: 3.00m, filamentName: "PLA Blue");
        // Job ended 5 days ago (inside range)
        await CreateCompletedJobAsync(printer, now.AddDays(-6), actualEndTime: now.AddDays(-5), totalCostUsd: 7.00m, filamentName: "PLA Red");

        var startDate = now.AddDays(-15).ToString("yyyy-MM-dd");
        var endDate = now.ToString("yyyy-MM-dd");

        HttpResponseMessage response = await _client!.GetAsync(
            $"/api/statistics/costs/summary?startDate={startDate}&endDate={endDate}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var summary = await response.Content.ReadFromJsonAsync<CostStatisticsSummaryDto>(JsonOptions);
        summary.Should().NotBeNull();
        summary!.JobsWithCostData.Should().Be(2, "only 2 jobs ended within the date range");
        summary.TotalCostUsd.Should().Be(10.00m, "3.00 + 7.00 = 10.00");
    }

    #endregion

    #region Precedence: startDate/endDate over days

    [Fact]
    public async Task Summary_StartDateEndDateTakesPrecedenceOverDays()
    {
        var printer = await CreateTestPrinterAsync();
        var now = DateTime.UtcNow;

        // Job 20 days ago (within days=30 range, but outside custom range)
        await CreateCompletedJobAsync(printer, now.AddDays(-20));
        // Job 5 days ago (within both ranges)
        await CreateCompletedJobAsync(printer, now.AddDays(-5));

        var startDate = now.AddDays(-10).ToString("yyyy-MM-dd");
        var endDate = now.ToString("yyyy-MM-dd");

        // Pass days=30 AND custom range (10 days) — custom range should win
        HttpResponseMessage response = await _client!.GetAsync(
            $"/api/statistics/summary?days=30&startDate={startDate}&endDate={endDate}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var summary = await response.Content.ReadFromJsonAsync<StatisticsSummaryDto>(JsonOptions);
        summary.Should().NotBeNull();
        summary!.TotalJobs.Should().Be(1, "custom 10-day range should take precedence over days=30");
    }

    #endregion

    #region All-time still works

    [Fact]
    public async Task Summary_NoParams_ReturnsAllTimeData()
    {
        var printer = await CreateTestPrinterAsync();
        var now = DateTime.UtcNow;

        await CreateCompletedJobAsync(printer, now.AddDays(-200));
        await CreateCompletedJobAsync(printer, now.AddDays(-5));

        HttpResponseMessage response = await _client!.GetAsync("/api/statistics/summary");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var summary = await response.Content.ReadFromJsonAsync<StatisticsSummaryDto>(JsonOptions);
        summary.Should().NotBeNull();
        summary!.TotalJobs.Should().BeGreaterThanOrEqualTo(2, "all-time should include all jobs");
    }

    [Fact]
    public async Task CostsByPrinter_NoParams_ReturnsAllTimeCosts()
    {
        var printer = await CreateTestPrinterAsync("all-time-printer");
        var now = DateTime.UtcNow;

        await CreateCompletedJobAsync(printer, now.AddDays(-300), actualEndTime: now.AddDays(-299), totalCostUsd: 10.00m, filamentName: "PLA");
        await CreateCompletedJobAsync(printer, now.AddDays(-5), actualEndTime: now.AddDays(-4), totalCostUsd: 5.00m, filamentName: "PETG");

        HttpResponseMessage response = await _client!.GetAsync("/api/statistics/costs/by-printer");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var costs = await response.Content.ReadFromJsonAsync<List<CostByPrinterDto>>(JsonOptions);
        costs.Should().NotBeNull();
        costs!.Should().Contain(c => c.PrinterName == "all-time-printer");
    }

    #endregion

    #region Default behavior preserved

    [Fact]
    public async Task JobsOverTime_NoParams_DefaultsTo30Days()
    {
        HttpResponseMessage response = await _client!.GetAsync("/api/statistics/jobs-over-time");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var data = await response.Content.ReadFromJsonAsync<List<DailyJobCountDto>>(JsonOptions);
        data.Should().NotBeNull();
        data!.Count.Should().BeInRange(30, 32, "defaults to ~30 days of daily data");
    }

    [Fact]
    public async Task CostOverTime_WithDays_StillWorks()
    {
        HttpResponseMessage response = await _client!.GetAsync("/api/statistics/cost-over-time?days=7");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var data = await response.Content.ReadFromJsonAsync<List<DailyCostDto>>(JsonOptions);
        data.Should().NotBeNull();
        data!.Count.Should().BeInRange(7, 9, "should return ~7 days of data");
    }

    #endregion
}
