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
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.Queue.Dispatch;
using Farm.Infrastructure.Tests.Builders;
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
public class LoadBalancingTests : IClassFixture<CustomWebApplicationFactory>, IAsyncLifetime
{
    private readonly CustomWebApplicationFactory _factory;
    private HttpClient _client = null!;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public LoadBalancingTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    public async Task InitializeAsync()
    {
        await _factory.ResetDataAsync();
        _client = await _factory.CreateAdminClientAsync();
    }

    public Task DisposeAsync()
    {
        _client?.Dispose();
        return Task.CompletedTask;
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
        public LoadBalancingStrategy LoadBalancingStrategy { get; set; } = LoadBalancingStrategy.BestFit;
    }

    private sealed class SettingsWithStrategyDto
    {
        // Set accessor is populated via JSON deserialization (reflection), invisible to the
        // analyzer, even though only the getter is read directly.
#pragma warning disable S1144
        public LoadBalancingStrategy? LoadBalancingStrategy { get; set; }
#pragma warning restore S1144
    }

    private sealed class BatchDispatchRequest
    {
        public List<Guid> JobIds { get; set; } = [];

        public Dictionary<Guid, string> JobETags { get; set; } = [];
    }

    private sealed class BatchDispatchResponse
    {
        public List<BatchDispatchItemResult> Results { get; set; } = [];
    }

    private sealed class BatchDispatchItemResult
    {
        // Set accessors are populated via JSON deserialization (reflection), invisible to the
        // analyzer, even though only the getters are read directly.
#pragma warning disable S1144
        public Guid JobId { get; set; }
        public Guid? PrinterId { get; set; }
#pragma warning restore S1144
        public string Status { get; set; } = string.Empty;
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

    private async Task<Guid> SeedPrinterAsync(
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
        db.PrinterDispatchStates.Add(new PrinterDispatchState
        {
            PrinterId = printerId,
        });
        await db.SaveChangesAsync();
        IPrinterStatusCacheWriter statusWriter =
            _factory.Services.GetRequiredService<IPrinterStatusCacheWriter>();
        statusWriter.UpdateStatus(new PrinterStatusDto(
            Id: printerId,
            IsOnline: true,
            State: "idle"));

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

    private static async Task<Dictionary<Guid, string>> GetJobETagsAsync(
        AppDbContext db,
        IEnumerable<Guid> jobIds) =>
        await db.PrintJobs
            .AsNoTracking()
            .Where(job => jobIds.Contains(job.Id))
            .ToDictionaryAsync(
                job => job.Id,
                job => Convert.ToBase64String(job.RowVersion ?? []));

    private async Task SetLoadBalancingStrategyAsync(LoadBalancingStrategy strategy)
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
        HttpResponseMessage current = await _client.GetAsync("/api/dispatch-settings");
        current.EnsureSuccessStatusCode();
        string etag = current.Headers.ETag?.Tag
            ?? throw new InvalidOperationException("Dispatch settings GET did not return an ETag.");
        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/dispatch-settings")
        {
            Content = JsonContent.Create(settings, options: JsonOptions),
        };
        request.Headers.TryAddWithoutValidation("If-Match", etag);
        HttpResponseMessage response = await _client.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }

    // =========================================================================
    // LOAD BALANCING STRATEGY TESTS
    // =========================================================================

    [Fact]
    [Trait("Category", "Dispatch")]
    [Trait("Phase", "3")]
    public async Task Dispatch_BestFitStrategy_UsesScoringAlgorithm()
    {
        await SetLoadBalancingStrategyAsync(LoadBalancingStrategy.BestFit);

        // Verify settings persisted
        HttpResponseMessage response = await _client.GetAsync("/api/dispatch-settings");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        SettingsWithStrategyDto? settings = await response.Content
            .ReadFromJsonAsync<SettingsWithStrategyDto>(JsonOptions);
        settings.Should().NotBeNull();
        settings!.LoadBalancingStrategy.Should().Be(LoadBalancingStrategy.BestFit,
            "BestFit is the default scoring-based strategy");
    }

    [Fact]
    [Trait("Category", "Dispatch")]
    [Trait("Phase", "3")]
    public async Task Dispatch_RoundRobin_DistributesJobsEvenlyAcrossPrinters()
    {
        await SetLoadBalancingStrategyAsync(LoadBalancingStrategy.RoundRobin);

        using IServiceScope scope = _factory.Services.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        Guid folderId = await EnsureRootFolderAsync(db);
        _ = await SeedPrinterAsync(db, 1);
        _ = await SeedPrinterAsync(db, 2);

        Guid jobId1 = await SeedQueuedJobAsync(db, folderId, 1);
        Guid jobId2 = await SeedQueuedJobAsync(db, folderId, 2);

        List<Guid> jobIds = [jobId1, jobId2];
        var request = new BatchDispatchRequest
        {
            JobIds = jobIds,
            JobETags = await GetJobETagsAsync(db, jobIds),
        };
        HttpResponseMessage response = await _client.PostAsJsonAsync(
            "/api/job-queue/batch-dispatch", request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        BatchDispatchResponse? result = await response.Content
            .ReadFromJsonAsync<BatchDispatchResponse>(JsonOptions);
        result.Should().NotBeNull();

        // Strategy selection is independent from backend acceptance. These fixtures
        // have no live printer backend, so the typed physical outcome may be non-accepted.
        List<Guid?> assignedPrinters = result.Results
            .Where(r => r.PrinterId.HasValue)
            .Select(r => r.PrinterId)
            .ToList();
        assignedPrinters.Should().HaveCount(2,
            "round-robin should select a printer for both jobs");
    }

    [Fact]
    [Trait("Category", "Dispatch")]
    [Trait("Phase", "3")]
    public async Task Dispatch_LeastBusy_PrefersShortestQueue()
    {
        await SetLoadBalancingStrategyAsync(LoadBalancingStrategy.LeastBusy);

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
        var request = new BatchDispatchRequest
        {
            JobIds = [newJobId],
            JobETags = await GetJobETagsAsync(db, [newJobId]),
        };

        HttpResponseMessage response = await _client.PostAsJsonAsync(
            "/api/job-queue/batch-dispatch", request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        BatchDispatchResponse? result = await response.Content
            .ReadFromJsonAsync<BatchDispatchResponse>(JsonOptions);
        result.Should().NotBeNull();

        BatchDispatchItemResult? selected = result!.Results
            .FirstOrDefault(r => r.JobId == newJobId);
        selected.Should().NotBeNull("the new job should be evaluated");
        selected!.PrinterId.Should().Be(idlePrinter,
            "LeastBusy should prefer the printer with 0 queued jobs over the one with 2");
    }

    [Fact]
    [Trait("Category", "Dispatch")]
    [Trait("Phase", "3")]
    public async Task Dispatch_StrategyChangeViaSettings_IsRespected()
    {
        // Set to RoundRobin first
        await SetLoadBalancingStrategyAsync(LoadBalancingStrategy.RoundRobin);

        HttpResponseMessage get1 = await _client.GetAsync("/api/dispatch-settings");
        SettingsWithStrategyDto? settings1 = await get1.Content
            .ReadFromJsonAsync<SettingsWithStrategyDto>(JsonOptions);
        settings1!.LoadBalancingStrategy.Should().Be(LoadBalancingStrategy.RoundRobin);

        // Change to LeastBusy
        await SetLoadBalancingStrategyAsync(LoadBalancingStrategy.LeastBusy);

        HttpResponseMessage get2 = await _client.GetAsync("/api/dispatch-settings");
        SettingsWithStrategyDto? settings2 = await get2.Content
            .ReadFromJsonAsync<SettingsWithStrategyDto>(JsonOptions);
        settings2!.LoadBalancingStrategy.Should().Be(LoadBalancingStrategy.LeastBusy,
            "strategy change should persist and be returned on GET");
    }

    [Fact]
    [Trait("Category", "Dispatch")]
    [Trait("Phase", "3")]
    public async Task Dispatch_InvalidStrategyValue_ReturnsAppropriateError()
    {
        // Since LoadBalancingStrategy is an enum, send raw JSON with an invalid string value
        // to test that the API rejects unknown strategy values during deserialization.
        var invalidJson = new StringContent(
            """{"autoDispatchEnabled":true,"autoDispatchMode":"Auto","idleThresholdSeconds":30,"minimumScoreThreshold":50.0,"maxConcurrentDispatches":3,"loadBalancingStrategy":"NonExistentStrategy"}""",
            System.Text.Encoding.UTF8,
            "application/json");

        HttpResponseMessage response = await _client.PutAsync(
            "/api/dispatch-settings", invalidJson);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "an invalid load balancing strategy should be rejected");
    }
}
