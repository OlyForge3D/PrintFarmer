using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.AutoDispatch;
using Farm.Web.Api.Tests.TestInfrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Farm.Web.Api.Tests.Controllers;

/// <summary>
/// Integration tests for the bed pre-clear feature.
/// POST /api/auto-dispatch/{printerId}/pre-clear marks the bed as pre-confirmed
/// so the next job dispatches immediately without the PendingReady gate.
/// </summary>
[Trait("Category", "Integration")]
[Collection(IntegrationTestCollection.Name)]
public class AutoDispatchPreClearTests : IAsyncLifetime
{
    private const string CurrentRouteBase = "/api/auto-dispatch";
    private readonly CustomWebApplicationFactory _factory;
    private HttpClient? _client;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public AutoDispatchPreClearTests()
    {
        _factory = new CustomWebApplicationFactory();
    }

    public async Task InitializeAsync()
    {
        await _factory.ResetDatabaseAsync();
        _client = await _factory.CreateAuthenticatedClientAsync();
    }

    public Task DisposeAsync()
    {
        _client?.Dispose();
        _factory?.Dispose();
        return Task.CompletedTask;
    }

    private async Task<Printer> CreateTestPrinterAsync(
        string? name = null,
        bool autoDispatchEnabled = true)
    {
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        Manufacturer? manufacturer = await context.Manufacturers.FirstOrDefaultAsync();
        if (manufacturer is null)
        {
            manufacturer = new Manufacturer { Id = Guid.NewGuid(), Name = "Test Manufacturer" };
            context.Manufacturers.Add(manufacturer);
            await context.SaveChangesAsync();
        }

        PrinterModel? model = await context.PrinterModels.FirstOrDefaultAsync();
        if (model is null)
        {
            model = new PrinterModel { Id = Guid.NewGuid(), Name = "Test Model", ManufacturerId = manufacturer.Id };
            context.PrinterModels.Add(model);
            await context.SaveChangesAsync();
        }

        var printer = new Printer
        {
            Id = Guid.NewGuid(),
            Name = name ?? $"preclear-test-{Guid.NewGuid():N}".Substring(0, 20),
            ServerUrl = $"http://preclear-{Guid.NewGuid()}.local",
            BackendPort = 7125,
            Backend = (int)PrinterBackend.Moonraker,
            ManufacturerId = manufacturer.Id,
            ModelId = model.Id,
            AutoDispatchEnabled = autoDispatchEnabled
        };

        context.Printers.Add(printer);
        await context.SaveChangesAsync();
        return printer;
    }

    // ── Test 1: Happy path — pre-clear returns 200 ──

    [Fact]
    public async Task PreClear_ValidPrinterWithAutoDispatchEnabled_Returns200()
    {
        Printer printer = await CreateTestPrinterAsync(autoDispatchEnabled: true);

        HttpResponseMessage response = await _client!.PostAsync(
            $"{CurrentRouteBase}/{printer.Id}/pre-clear", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var status = await response.Content.ReadFromJsonAsync<AutoDispatchStatusDto>(JsonOptions);
        status.Should().NotBeNull();
        status!.PrinterId.Should().Be(printer.Id);
        status.BedPreConfirmed.Should().BeTrue();
    }

    // ── Test 2: Non-existent printer returns 400 (error message contains "not found") ──

    [Fact]
    public async Task PreClear_NonExistentPrinter_Returns400WithNotFoundMessage()
    {
        var bogusId = Guid.NewGuid();

        HttpResponseMessage response = await _client!.PostAsync(
            $"{CurrentRouteBase}/{bogusId}/pre-clear", null);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        string body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("not found");
    }

    // ── Test 3: Auto-dispatch disabled returns 400 ──

    [Fact]
    public async Task PreClear_AutoDispatchDisabled_Returns400()
    {
        Printer printer = await CreateTestPrinterAsync(autoDispatchEnabled: false);

        HttpResponseMessage response = await _client!.PostAsync(
            $"{CurrentRouteBase}/{printer.Id}/pre-clear", null);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        string body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("not enabled");
    }

    // ── Test 4: After pre-clear, status shows bedPreConfirmed: true ──

    [Fact]
    public async Task GetStatus_AfterPreClear_ShowsBedPreConfirmedTrue()
    {
        Printer printer = await CreateTestPrinterAsync(autoDispatchEnabled: true);

        // Pre-clear
        HttpResponseMessage preClearResponse = await _client!.PostAsync(
            $"{CurrentRouteBase}/{printer.Id}/pre-clear", null);
        preClearResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // Check status
        HttpResponseMessage statusResponse = await _client!.GetAsync(
            $"{CurrentRouteBase}/{printer.Id}/status");
        statusResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var status = await statusResponse.Content.ReadFromJsonAsync<AutoDispatchStatusDto>(JsonOptions);
        status.Should().NotBeNull();
        status!.BedPreConfirmed.Should().BeTrue();
    }

    // ── Test 5: BedPreConfirmed is included in status DTO ──

    [Fact]
    public async Task GetStatus_DefaultPrinter_BedPreConfirmedIsFalse()
    {
        Printer printer = await CreateTestPrinterAsync(autoDispatchEnabled: true);

        HttpResponseMessage statusResponse = await _client!.GetAsync(
            $"{CurrentRouteBase}/{printer.Id}/status");
        statusResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var status = await statusResponse.Content.ReadFromJsonAsync<AutoDispatchStatusDto>(JsonOptions);
        status.Should().NotBeNull();
        status!.BedPreConfirmed.Should().BeFalse();
    }

    // ── Test 6: Pre-clearing an already pre-cleared printer is idempotent ──

    [Fact]
    public async Task PreClear_AlreadyPreCleared_SucceedsIdempotently()
    {
        Printer printer = await CreateTestPrinterAsync(autoDispatchEnabled: true);

        HttpResponseMessage first = await _client!.PostAsync(
            $"{CurrentRouteBase}/{printer.Id}/pre-clear", null);
        first.StatusCode.Should().Be(HttpStatusCode.OK);

        HttpResponseMessage second = await _client!.PostAsync(
            $"{CurrentRouteBase}/{printer.Id}/pre-clear", null);
        second.StatusCode.Should().Be(HttpStatusCode.OK);

        var status = await second.Content.ReadFromJsonAsync<AutoDispatchStatusDto>(JsonOptions);
        status!.BedPreConfirmed.Should().BeTrue();
    }

    [Fact]
    public async Task GetStatus_AfterPreClear_MarksBedClearConfirmedGateAsPassed()
    {
        Printer printer = await CreateTestPrinterAsync(autoDispatchEnabled: true);

        HttpResponseMessage preClearResponse = await _client!.PostAsync(
            $"{CurrentRouteBase}/{printer.Id}/pre-clear", null);
        preClearResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        HttpResponseMessage statusResponse = await _client.GetAsync(
            $"{CurrentRouteBase}/{printer.Id}/status");
        statusResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        AutoDispatchStatusDto? status = await statusResponse.Content.ReadFromJsonAsync<AutoDispatchStatusDto>(JsonOptions);
        status.Should().NotBeNull();

        ReadyGateCheckDto? bedClearGate = status!.ReadyGateChecks
            .FirstOrDefault(check => check.Name == "Bed Clear Confirmed");

        bedClearGate.Should().NotBeNull();
        bedClearGate!.Passed.Should().BeTrue();
        bedClearGate.Message.Should().Contain("pre-cleared");
    }

    [Fact]
    public async Task Ready_AfterPreClear_CurrentRouteSucceedsAndClearsPreConfirmedFlag()
    {
        Printer printer = await CreateTestPrinterAsync(autoDispatchEnabled: true);
        await CreateQueuedJobAsync(printer.Id, "queued-job-1", queuePosition: 1);

        HttpResponseMessage preClearResponse = await _client!.PostAsync(
            $"{CurrentRouteBase}/{printer.Id}/pre-clear", null);
        preClearResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        HttpResponseMessage statusResponse = await _client.GetAsync(
            $"{CurrentRouteBase}/{printer.Id}/status");
        statusResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        AutoDispatchStatusDto? statusBeforeReady = await statusResponse.Content.ReadFromJsonAsync<AutoDispatchStatusDto>(JsonOptions);
        statusBeforeReady.Should().NotBeNull();
        statusBeforeReady!.State.Should().Be("None");
        statusBeforeReady.BedPreConfirmed.Should().BeTrue();

        HttpResponseMessage readyResponse = await _client.PostAsync(
            $"{CurrentRouteBase}/{printer.Id}/ready", null);
        readyResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        AutoDispatchReadyResult? readyResult = await readyResponse.Content.ReadFromJsonAsync<AutoDispatchReadyResult>(JsonOptions);
        readyResult.Should().NotBeNull();
        readyResult!.Status.State.Should().Be("Ready");
        readyResult.Status.BedPreConfirmed.Should().BeFalse();
        readyResult.NextJob.Should().NotBeNull();
    }

    private async Task CreateQueuedJobAsync(Guid printerId, string name, int queuePosition)
    {
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        context.PrintJobs.Add(new PrintJob
        {
            Id = Guid.NewGuid(),
            Name = name,
            AssignedPrinterId = printerId,
            Status = Farm.Infrastructure.PrintJobStatus.Queued,
            Priority = 0,
            QueuePosition = queuePosition,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            QueuedAt = DateTime.UtcNow,
        });

        await context.SaveChangesAsync();
    }
}
