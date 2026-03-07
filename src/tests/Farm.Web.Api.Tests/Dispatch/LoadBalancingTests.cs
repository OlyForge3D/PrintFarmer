using System;
using System.Collections.Generic;
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
using Farm.Infrastructure.Services.Queue.Dispatch;
using Farm.Web.Api.Tests.Builders;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Farm.Web.Api.Tests.Dispatch;

/// <summary>
/// Integration tests for load balancing strategies in the dispatch system.
/// Pre-implementation tests — will fail until the load balancing strategies
/// are added to DispatchSettings and the dispatch engine.
///
/// Tests verify:
/// - BestFit (default) uses scoring algorithm
/// - RoundRobin distributes jobs evenly
/// - LeastBusy prefers printers with shortest queue
/// - Strategy change via DispatchSettings is respected
/// - Invalid strategy value returns appropriate error
/// </summary>
public class LoadBalancingTests : IAsyncLifetime
{
    private readonly CustomWebApplicationFactory _factory;
    private HttpClient _client = null!;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public LoadBalancingTests()
    {
        _factory = new CustomWebApplicationFactory();
    }

    public async Task InitializeAsync()
    {
        _client = await _factory.CreateAuthenticatedClientAsync();
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        await _factory.DisposeAsync();
    }

    // =========================================================================
    // LOCAL DTOs — will be replaced once load balancing is added to settings
    // =========================================================================

    /// <summary>
    /// Extended dispatch settings DTO that includes load balancing strategy.
    /// Matches the expected API contract once Lambert adds strategy support.
    /// </summary>
    private sealed class UpdateSettingsWithStrategyDto
    {
        public bool AutoDispatchEnabled { get; set; }
        public AutoDispatchMode AutoDispatchMode { get; set; }
        public int IdleThresholdSeconds { get; set; }
        public double MinimumScoreThreshold { get; set; }
        public int MaxConcurrentDispatches { get; set; }
        public string LoadBalancingStrategy { get; set; } = "BestFit";
    }

    private sealed class SettingsWithStrategyDto
    {
        public bool AutoDispatchEnabled { get; set; }
        public AutoDispatchMode AutoDispatchMode { get; set; }
        public int IdleThresholdSeconds { get; set; }
        public double MinimumScoreThreshold { get; set; }
        public int MaxConcurrentDispatches { get; set; }
        public string? LoadBalancingStrategy { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    private sealed class BatchDispatchRequest
    {
        public List<Guid> JobIds { get; set; } = [];
    }

    private sealed class BatchDispatchResponse
    {
        public int TotalRequested { get; set; }
        public int Dispatched { get; set; }
        public int Failed { get; set; }
        public int Skipped { get; set; }
        public List<BatchDispatchItemResult> Results { get; set; } = [];
    }

    private sealed class BatchDispatchItemResult
    {
        public Guid JobId { get; set; }
        public bool Success { get; set; }
        public Guid? AssignedPrinterId { get; set; }
        public string? Error { get; set; }
    }

    // =========================================================================
    // DATA SEEDING HELPERS
    // =========================================================================

    private static async Task<Guid> EnsureRootFolderAsync(AppDbContext db)
    {
        FolderNode? existing = await db.Set<FolderNode>().FirstOrDefaultAsync();
        if (existing is not null)
        {
            return existing.Id;
        }

        var folder = new FolderNode
        {
            Id = Guid.NewGuid(),
            Path = "/test-lb",
            FolderType = "gcode",
            CreatedAt = DateTime.UtcNow,
        };
        db.Set<FolderNode>().Add(folder);
        await db.SaveChangesAsync();
        return folder.Id;
    }

    private static async Task<Guid> SeedPrinterAsync(
        AppDbContext db, int index, bool isEnabled = true)
    {
        Guid manufacturerId = Guid.NewGuid();
        db.Manufacturers.Add(new Manufacturer
        {
            Id = manufacturerId,
            Name = $"LBMfg-{Guid.NewGuid():N}",
        });
        await db.SaveChangesAsync();

        Guid modelId = Guid.NewGuid();
        db.PrinterModels.Add(new PrinterModel
        {
            Id = modelId,
            Name = $"LBModel-{index}",
            ManufacturerId = manufacturerId,
        });
        await db.SaveChangesAsync();

        Guid printerId = Guid.NewGuid();
        Printer printer = new PrinterBuilder()
            .WithId(printerId)
            .WithName($"LB Printer {index}")
            .WithServerUrl($"http://192.168.210.{index}")
            .Build();
        printer.ManufacturerId = manufacturerId;
        printer.ModelId = modelId;
        printer.IsEnabled = isEnabled;
        printer.IsAvailable = true;
        db.Printers.Add(printer);
        await db.SaveChangesAsync();

        return printerId;
    }

    private static async Task<Guid> SeedQueuedJobAsync(
        AppDbContext db, Guid folderId, int index, Guid? assignedPrinterId = null)
    {
        Guid gcodeFileId = Guid.NewGuid();
        db.GcodeFiles.Add(new GcodeFile
        {
            Id = gcodeFileId,
            Name = $"lb-job-{index}.gcode",
            FileName = $"{Guid.NewGuid()}.gcode",
            FilePath = "/gcode/",
            FolderId = folderId,
            FileHash = Guid.NewGuid().ToString()[..8],
            UploadedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        Guid jobId = Guid.NewGuid();
        db.PrintJobs.Add(new PrintJob
        {
            Id = jobId,
            Name = $"LB Job {index}",
            GcodeFileId = gcodeFileId,
            Status = assignedPrinterId.HasValue ? PrintJobStatus.Assigned : PrintJobStatus.Queued,
            AssignedPrinterId = assignedPrinterId,
            Priority = 0,
            QueuePosition = index,
            QueuedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        return jobId;
    }

    private async Task SetLoadBalancingStrategyAsync(string strategy)
    {
        var settings = new UpdateSettingsWithStrategyDto
        {
            AutoDispatchEnabled = true,
            AutoDispatchMode = AutoDispatchMode.Auto,
            IdleThresholdSeconds = 0,
            MinimumScoreThreshold = 0.0,
            MaxConcurrentDispatches = 10,
            LoadBalancingStrategy = strategy,
        };
        await _client.PutAsJsonAsync("/api/dispatch-settings", settings);
    }

    // =========================================================================
    // LOAD BALANCING STRATEGY TESTS
    // =========================================================================

    [Fact]
    [Trait("Category", "Dispatch")]
    [Trait("Phase", "3")]
    public async Task Dispatch_BestFitStrategy_UsesScoringAlgorithm()
    {
        await SetLoadBalancingStrategyAsync("BestFit");

        // Verify settings persisted
        HttpResponseMessage response = await _client.GetAsync("/api/dispatch-settings");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        SettingsWithStrategyDto? settings = await response.Content
            .ReadFromJsonAsync<SettingsWithStrategyDto>(JsonOptions);
        settings.Should().NotBeNull();
        settings!.LoadBalancingStrategy.Should().Be("BestFit",
            "BestFit is the default scoring-based strategy");
    }

    [Fact]
    [Trait("Category", "Dispatch")]
    [Trait("Phase", "3")]
    public async Task Dispatch_RoundRobin_DistributesJobsEvenlyAcrossPrinters()
    {
        await SetLoadBalancingStrategyAsync("RoundRobin");

        using IServiceScope scope = _factory.Services.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        Guid folderId = await EnsureRootFolderAsync(db);
        Guid printer1 = await SeedPrinterAsync(db, 1);
        Guid printer2 = await SeedPrinterAsync(db, 2);

        Guid jobId1 = await SeedQueuedJobAsync(db, folderId, 1);
        Guid jobId2 = await SeedQueuedJobAsync(db, folderId, 2);

        var request = new BatchDispatchRequest { JobIds = [jobId1, jobId2] };
        HttpResponseMessage response = await _client.PostAsJsonAsync(
            "/api/job-queue/batch-dispatch", request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        BatchDispatchResponse? result = await response.Content
            .ReadFromJsonAsync<BatchDispatchResponse>(JsonOptions);
        result.Should().NotBeNull();
        result!.Dispatched.Should().Be(2, "both jobs should dispatch with 2 available printers");

        // RoundRobin should assign each job to a different printer
        List<Guid?> assignedPrinters = result.Results
            .Where(r => r.Success)
            .Select(r => r.AssignedPrinterId)
            .ToList();
        assignedPrinters.Distinct().Should().HaveCount(2,
            "RoundRobin should distribute jobs evenly across printers");
    }

    [Fact]
    [Trait("Category", "Dispatch")]
    [Trait("Phase", "3")]
    public async Task Dispatch_LeastBusy_PrefersShortestQueue()
    {
        await SetLoadBalancingStrategyAsync("LeastBusy");

        using IServiceScope scope = _factory.Services.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        Guid folderId = await EnsureRootFolderAsync(db);
        Guid busyPrinter = await SeedPrinterAsync(db, 3);
        Guid idlePrinter = await SeedPrinterAsync(db, 4);

        // Assign 2 existing jobs to the "busy" printer
        await SeedQueuedJobAsync(db, folderId, 50, assignedPrinterId: busyPrinter);
        await SeedQueuedJobAsync(db, folderId, 51, assignedPrinterId: busyPrinter);

        // Now dispatch a new unassigned job
        Guid newJobId = await SeedQueuedJobAsync(db, folderId, 52);
        var request = new BatchDispatchRequest { JobIds = [newJobId] };

        HttpResponseMessage response = await _client.PostAsJsonAsync(
            "/api/job-queue/batch-dispatch", request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        BatchDispatchResponse? result = await response.Content
            .ReadFromJsonAsync<BatchDispatchResponse>(JsonOptions);
        result.Should().NotBeNull();

        BatchDispatchItemResult? dispatched = result!.Results
            .FirstOrDefault(r => r.Success);
        dispatched.Should().NotBeNull("the new job should be dispatched");
        dispatched!.AssignedPrinterId.Should().Be(idlePrinter,
            "LeastBusy should prefer the printer with 0 queued jobs over the one with 2");
    }

    [Fact]
    [Trait("Category", "Dispatch")]
    [Trait("Phase", "3")]
    public async Task Dispatch_StrategyChangeViaSettings_IsRespected()
    {
        // Set to RoundRobin first
        await SetLoadBalancingStrategyAsync("RoundRobin");

        HttpResponseMessage get1 = await _client.GetAsync("/api/dispatch-settings");
        SettingsWithStrategyDto? settings1 = await get1.Content
            .ReadFromJsonAsync<SettingsWithStrategyDto>(JsonOptions);
        settings1!.LoadBalancingStrategy.Should().Be("RoundRobin");

        // Change to LeastBusy
        await SetLoadBalancingStrategyAsync("LeastBusy");

        HttpResponseMessage get2 = await _client.GetAsync("/api/dispatch-settings");
        SettingsWithStrategyDto? settings2 = await get2.Content
            .ReadFromJsonAsync<SettingsWithStrategyDto>(JsonOptions);
        settings2!.LoadBalancingStrategy.Should().Be("LeastBusy",
            "strategy change should persist and be returned on GET");
    }

    [Fact]
    [Trait("Category", "Dispatch")]
    [Trait("Phase", "3")]
    public async Task Dispatch_InvalidStrategyValue_ReturnsAppropriateError()
    {
        var settings = new UpdateSettingsWithStrategyDto
        {
            AutoDispatchEnabled = true,
            AutoDispatchMode = AutoDispatchMode.Auto,
            IdleThresholdSeconds = 30,
            MinimumScoreThreshold = 50.0,
            MaxConcurrentDispatches = 3,
            LoadBalancingStrategy = "NonExistentStrategy",
        };

        HttpResponseMessage response = await _client.PutAsJsonAsync(
            "/api/dispatch-settings", settings);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "an invalid load balancing strategy should be rejected");
    }
}
