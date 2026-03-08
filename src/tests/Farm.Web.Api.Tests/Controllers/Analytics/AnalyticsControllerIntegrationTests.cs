using System.Net;
using System.Net.Http.Json;
using Farm.Infrastructure.Dtos;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Farm.Web.Api.Tests.Controllers.Analytics;

public class AnalyticsControllerIntegrationTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _client;
    private readonly CustomWebApplicationFactory _factory;

    public AnalyticsControllerIntegrationTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
    }

    [Fact]
    public async Task ExportPdfReport_Returns200WithPdfContentType()
    {
        // Act
        var response = await _client.GetAsync("/api/statistics/export/pdf?days=30");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/pdf");
    }

    [Fact]
    public async Task ExportJobHistoryCsv_Returns200WithCsvContentType()
    {
        // Act
        var response = await _client.GetAsync("/api/statistics/export/jobs-csv?days=30");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/csv");
    }

    [Fact]
    public async Task ExportCostCsv_Returns200WithCsvContentType()
    {
        // Act
        var response = await _client.GetAsync("/api/statistics/export/cost-csv?days=30");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/csv");
    }

    [Fact]
    public async Task ExportUtilizationCsv_Returns200WithCsvContentType()
    {
        // Act
        var response = await _client.GetAsync("/api/statistics/export/utilization-csv?days=30");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/csv");
    }

    [Fact]
    public async Task GetMaterialSuccessRates_Returns200WithValidJson()
    {
        // Act
        var response = await _client.GetAsync("/api/correlation-analytics/material-success-rates?days=30");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<List<MaterialSuccessRateDto>>();
        result.Should().NotBeNull();
    }

    [Fact]
    public async Task GetPrinterMaterialPerformance_Returns200WithValidJson()
    {
        // Act
        var response = await _client.GetAsync("/api/correlation-analytics/printer-material-performance?days=30");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<List<PrinterMaterialPerformanceDto>>();
        result.Should().NotBeNull();
    }

    [Fact]
    public async Task GetTemperatureQualityData_Returns200WithValidJson()
    {
        // Act
        var response = await _client.GetAsync("/api/correlation-analytics/temperature-quality?days=30");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<List<TemperatureQualityCorrelationDto>>();
        result.Should().NotBeNull();
    }

    [Fact]
    public async Task GetDurationTrends_Returns200WithValidJson()
    {
        // Act
        var response = await _client.GetAsync("/api/correlation-analytics/duration-trends?days=30");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<List<DurationTrendDto>>();
        result.Should().NotBeNull();
    }

    [Fact]
    public async Task GetFailureReasons_Returns200WithValidJson()
    {
        // Act
        var response = await _client.GetAsync("/api/correlation-analytics/failure-reasons?days=30");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<List<FailureReasonDto>>();
        result.Should().NotBeNull();
    }

    [Fact]
    public async Task PredictJobFailure_Returns200WithValidPrediction()
    {
        // Arrange
        var authClient = await _factory.CreateAuthenticatedClientAsync();
        var request = new PredictionRequest
        {
            PrinterId = Guid.NewGuid(),
            Material = "PLA",
            EstimatedDurationMinutes = 120
        };

        // Act
        var response = await authClient.PostAsJsonAsync("/api/predictive-analytics/predict-job-failure", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<JobFailurePredictionDto>();
        result.Should().NotBeNull();
        result!.RiskLevel.Should().NotBeNullOrEmpty();
        result.Factors.Should().NotBeEmpty();
    }

    [Fact]
    public async Task GetMaintenanceForecast_Returns200WithValidJson()
    {
        // Act
        var response = await _client.GetAsync("/api/predictive-analytics/maintenance-forecast");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<List<MaintenanceForecastDto>>();
        result.Should().NotBeNull();
    }

    [Fact]
    public async Task GetActiveAlerts_Returns200WithValidJson()
    {
        // Act
        var response = await _client.GetAsync("/api/predictive-analytics/active-alerts");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<List<PredictiveAlertDto>>();
        result.Should().NotBeNull();
    }
}
