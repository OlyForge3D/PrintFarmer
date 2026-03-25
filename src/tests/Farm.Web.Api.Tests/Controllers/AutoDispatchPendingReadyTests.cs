using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Farm.Infrastructure;
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
/// Integration tests for PendingReady auto-dispatch state visibility.
/// These tests cover the exact state the printers page depends on:
/// queued jobs exist, the bed has not been confirmed clear, and the status endpoints
/// must surface PendingReady so the UI can render the confirmation prompt.
/// </summary>
[Trait("Category", "Integration")]
[Collection(IntegrationTestCollection.Name)]
public class AutoDispatchPendingReadyTests : IAsyncLifetime
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

    public AutoDispatchPendingReadyTests()
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
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task GetStatus_WhenQueuedJobsNeedBedClear_ReturnsPendingReadyStateWithWaitingGate()
    {
        Printer printer = await CreateTestPrinterAsync(name: "pending-ready-printer");
        await CreateQueuedJobAsync(printer.Id, "queued-job-1", queuePosition: 1);
        await CreateQueuedJobAsync(printer.Id, "queued-job-2", queuePosition: 2);
        await TransitionPrinterToPendingReadyAsync(printer.Id);

        HttpResponseMessage response = await _client!.GetAsync($"{CurrentRouteBase}/{printer.Id}/status");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        AutoDispatchStatusDto? status = await response.Content.ReadFromJsonAsync<AutoDispatchStatusDto>(JsonOptions);
        status.Should().NotBeNull();
        status!.PrinterId.Should().Be(printer.Id);
        status.State.Should().Be("PendingReady");
        status.QueueDepth.Should().Be(2);
        status.BedPreConfirmed.Should().BeFalse();
        status.AttentionMessage.Should().Be("Print completed. 2 queued jobs are blocked until you clear the bed and confirm ready. Once confirmed, the next queued job will start automatically.");
        status.ReadyGateChecks.Should().Contain(check =>
            check.Name == "Auto-Dispatch Enabled"
            && check.Passed
            && check.Message == "Auto-dispatch is enabled");
        status.ReadyGateChecks.Should().Contain(check =>
            check.Name == "Jobs in Queue"
            && check.Passed
            && check.Message.Contains("2 jobs queued"));
        status.ReadyGateChecks.Should().Contain(check =>
            check.Name == "Bed Clear Confirmed"
            && !check.Passed
            && check.Message.Contains("Waiting for operator"));
    }

    [Fact]
    public async Task GetAllStatus_WhenPrinterIsPendingReady_IncludesPrinterInBulkStatusPayload()
    {
        Printer pendingReadyPrinter = await CreateTestPrinterAsync(name: "pending-ready-printer");
        await CreateQueuedJobAsync(pendingReadyPrinter.Id, "queued-job-1", queuePosition: 1);
        await TransitionPrinterToPendingReadyAsync(pendingReadyPrinter.Id);

        Printer idlePrinter = await CreateTestPrinterAsync(name: "idle-printer");

        HttpResponseMessage response = await _client!.GetAsync($"{CurrentRouteBase}/status");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        AutoDispatchGlobalStatusDto? payload = await response.Content.ReadFromJsonAsync<AutoDispatchGlobalStatusDto>(JsonOptions);
        payload.Should().NotBeNull();
        payload!.GlobalEnabled.Should().BeTrue();
        payload.Printers.Should().Contain(p => p.PrinterId == idlePrinter.Id);
        AutoDispatchStatusDto pendingStatus = payload.Printers.Single(p => p.PrinterId == pendingReadyPrinter.Id);
        pendingStatus.State.Should().Be("PendingReady");
        pendingStatus.QueueDepth.Should().Be(1);
        pendingStatus.AttentionMessage.Should().Be("Print completed. 1 queued job is blocked until you clear the bed and confirm ready. Once confirmed, the next queued job will start automatically.");
        pendingStatus.ReadyGateChecks.Should().Contain(check =>
            check.Name == "Bed Clear Confirmed"
            && !check.Passed
            && check.Message.Contains("Waiting for operator"));
    }

    [Fact]
    public async Task GetStatus_WhenQueuedJobsExistButStateIsNone_ReadyEndpointRejectsCurrentNone()
    {
        Printer printer = await CreateTestPrinterAsync(name: "none-state-printer");
        await CreateQueuedJobAsync(printer.Id, "queued-job-1", queuePosition: 1);

        HttpResponseMessage statusResponse = await _client!.GetAsync($"{CurrentRouteBase}/{printer.Id}/status");

        statusResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        AutoDispatchStatusDto? status = await statusResponse.Content.ReadFromJsonAsync<AutoDispatchStatusDto>(JsonOptions);
        status.Should().NotBeNull();
        status!.State.Should().Be("None");
        status.QueueDepth.Should().Be(1);
        status.ReadyGateChecks.Should().Contain(check =>
            check.Name == "Bed Clear Confirmed"
            && !check.Passed
            && check.Message.Contains("No confirmation needed yet"));

        HttpResponseMessage readyResponse = await _client.PostAsync($"{CurrentRouteBase}/{printer.Id}/ready", null);

        readyResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        string body = await readyResponse.Content.ReadAsStringAsync();
        body.Should().Contain("current: None");
    }

    private async Task<Printer> CreateTestPrinterAsync(string name)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        Manufacturer manufacturer = await GetOrCreateManufacturerAsync(context);
        PrinterModel model = await GetOrCreateModelAsync(context, manufacturer.Id);

        Printer printer = new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            ServerUrl = $"http://{name}-{Guid.NewGuid():N}.local",
            BackendPort = 7125,
            Backend = (int)PrinterBackend.Moonraker,
            ManufacturerId = manufacturer.Id,
            ModelId = model.Id,
            AutoDispatchEnabled = true,
            IsEnabled = true,
            IsAvailable = true,
        };

        context.Printers.Add(printer);
        await context.SaveChangesAsync();
        return printer;
    }

    private async Task CreateQueuedJobAsync(Guid printerId, string name, int queuePosition)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

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

    private async Task TransitionPrinterToPendingReadyAsync(Guid printerId)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        IAutoDispatchService autoDispatchService = scope.ServiceProvider.GetRequiredService<IAutoDispatchService>();
        await autoDispatchService.TransitionToPendingReadyAsync(printerId);
    }

    private static async Task<Manufacturer> GetOrCreateManufacturerAsync(AppDbContext context)
    {
        Manufacturer? manufacturer = await context.Manufacturers.FirstOrDefaultAsync();
        if (manufacturer is not null)
        {
            return manufacturer;
        }

        manufacturer = new Manufacturer
        {
            Id = Guid.NewGuid(),
            Name = "PendingReady Test Manufacturer",
        };
        context.Manufacturers.Add(manufacturer);
        await context.SaveChangesAsync();
        return manufacturer;
    }

    private static async Task<PrinterModel> GetOrCreateModelAsync(AppDbContext context, Guid manufacturerId)
    {
        PrinterModel? model = await context.PrinterModels.FirstOrDefaultAsync();
        if (model is not null)
        {
            return model;
        }

        model = new PrinterModel
        {
            Id = Guid.NewGuid(),
            Name = "PendingReady Test Model",
            ManufacturerId = manufacturerId,
        };
        context.PrinterModels.Add(model);
        await context.SaveChangesAsync();
        return model;
    }
}
