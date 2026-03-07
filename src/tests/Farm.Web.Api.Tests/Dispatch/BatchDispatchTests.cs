using System;
using System.Collections.Generic;
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
/// Integration tests for the batch dispatch API (POST /api/job-queue/batch-dispatch).
/// Pre-implementation tests — will return 404 until the endpoint is implemented.
///
/// Tests verify:
/// - Batch dispatch with valid job IDs returns success with per-job results
/// - Empty job list returns 400
/// - MaxConcurrentDispatches limit is respected
/// - Already-assigned jobs are skipped
/// - No eligible printers scenario returns per-job failures
/// - Unauthorized access returns 401
/// - Individual job failures don't fail the entire batch
/// </summary>
public class BatchDispatchTests : IAsyncLifetime
{
    private readonly CustomWebApplicationFactory _factory;
    private HttpClient _client = null!;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public BatchDispatchTests()
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
    // LOCAL DTOs — to be replaced by shared DTOs once endpoint is implemented
    // =========================================================================

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
            Path = "/test-batch",
            FolderType = "gcode",
            CreatedAt = DateTime.UtcNow,
        };
        db.Set<FolderNode>().Add(folder);
        await db.SaveChangesAsync();
        return folder.Id;
    }

    private static async Task<Guid> SeedPrinterAsync(AppDbContext db, int index)
    {
        Guid manufacturerId = Guid.NewGuid();
        db.Manufacturers.Add(new Manufacturer
        {
            Id = manufacturerId,
            Name = $"BatchMfg-{Guid.NewGuid():N}",
        });
        await db.SaveChangesAsync();

        Guid modelId = Guid.NewGuid();
        db.PrinterModels.Add(new PrinterModel
        {
            Id = modelId,
            Name = $"BatchModel-{index}",
            ManufacturerId = manufacturerId,
        });
        await db.SaveChangesAsync();

        Guid printerId = Guid.NewGuid();
        Printer printer = new PrinterBuilder()
            .WithId(printerId)
            .WithName($"Batch Printer {index}")
            .WithServerUrl($"http://192.168.200.{index}")
            .Build();
        printer.ManufacturerId = manufacturerId;
        printer.ModelId = modelId;
        printer.IsEnabled = true;
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
            Name = $"batch-job-{index}.gcode",
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
            Name = $"Batch Job {index}",
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

    // =========================================================================
    // BATCH DISPATCH ENDPOINT TESTS
    // =========================================================================

    [Fact]
    [Trait("Category", "Dispatch")]
    [Trait("Phase", "3")]
    public async Task BatchDispatch_WithValidJobIds_ReturnsSuccessWithDispatchResults()
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        Guid folderId = await EnsureRootFolderAsync(db);
        await SeedPrinterAsync(db, 1);
        await SeedPrinterAsync(db, 2);

        Guid jobId1 = await SeedQueuedJobAsync(db, folderId, 1);
        Guid jobId2 = await SeedQueuedJobAsync(db, folderId, 2);
        Guid jobId3 = await SeedQueuedJobAsync(db, folderId, 3);

        var request = new BatchDispatchRequest { JobIds = [jobId1, jobId2, jobId3] };

        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/job-queue/batch-dispatch", request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        BatchDispatchResponse? result = await response.Content
            .ReadFromJsonAsync<BatchDispatchResponse>(JsonOptions);
        result.Should().NotBeNull();
        result!.TotalRequested.Should().Be(3);
        result.Results.Should().HaveCount(3);
    }

    [Fact]
    [Trait("Category", "Dispatch")]
    [Trait("Phase", "3")]
    public async Task BatchDispatch_WithEmptyList_Returns400BadRequest()
    {
        var request = new BatchDispatchRequest { JobIds = [] };

        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/job-queue/batch-dispatch", request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    [Trait("Category", "Dispatch")]
    [Trait("Phase", "3")]
    public async Task BatchDispatch_RespectsMaxConcurrentDispatchesLimit()
    {
        // Set MaxConcurrentDispatches=1 via settings
        var settingsUpdate = new UpdateDispatchSettingsDto
        {
            AutoDispatchEnabled = true,
            AutoDispatchMode = AutoDispatchMode.Auto,
            IdleThresholdSeconds = 30,
            MinimumScoreThreshold = 0.5,
            MaxConcurrentDispatches = 1,
        };
        await _client.PutAsJsonAsync("/api/dispatch-settings", settingsUpdate);

        using IServiceScope scope = _factory.Services.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        Guid folderId = await EnsureRootFolderAsync(db);
        await SeedPrinterAsync(db, 10);
        await SeedPrinterAsync(db, 11);
        await SeedPrinterAsync(db, 12);

        Guid jobId1 = await SeedQueuedJobAsync(db, folderId, 10);
        Guid jobId2 = await SeedQueuedJobAsync(db, folderId, 11);
        Guid jobId3 = await SeedQueuedJobAsync(db, folderId, 12);

        var request = new BatchDispatchRequest { JobIds = [jobId1, jobId2, jobId3] };

        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/job-queue/batch-dispatch", request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        BatchDispatchResponse? result = await response.Content
            .ReadFromJsonAsync<BatchDispatchResponse>(JsonOptions);
        result.Should().NotBeNull();
        result!.Dispatched.Should().BeInRange(0, 1,
            "MaxConcurrentDispatches=1 should limit concurrent dispatches");
    }

    [Fact]
    [Trait("Category", "Dispatch")]
    [Trait("Phase", "3")]
    public async Task BatchDispatch_SkipsJobsAlreadyAssignedToPrinters()
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        Guid folderId = await EnsureRootFolderAsync(db);
        Guid printerId = await SeedPrinterAsync(db, 20);

        Guid assignedJobId = await SeedQueuedJobAsync(db, folderId, 20, assignedPrinterId: printerId);
        Guid unassignedJobId = await SeedQueuedJobAsync(db, folderId, 21);

        var request = new BatchDispatchRequest { JobIds = [assignedJobId, unassignedJobId] };

        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/job-queue/batch-dispatch", request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        BatchDispatchResponse? result = await response.Content
            .ReadFromJsonAsync<BatchDispatchResponse>(JsonOptions);
        result.Should().NotBeNull();
        result!.Skipped.Should().BeGreaterThan(0, "already-assigned jobs should be skipped");
    }

    [Fact]
    [Trait("Category", "Dispatch")]
    [Trait("Phase", "3")]
    public async Task BatchDispatch_NoEligiblePrinters_ReturnsResultsWithFailures()
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        Guid folderId = await EnsureRootFolderAsync(db);
        Guid jobId1 = await SeedQueuedJobAsync(db, folderId, 30);
        Guid jobId2 = await SeedQueuedJobAsync(db, folderId, 31);

        var request = new BatchDispatchRequest { JobIds = [jobId1, jobId2] };

        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/job-queue/batch-dispatch", request);

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "batch dispatch returns 200 even when individual jobs fail");

        BatchDispatchResponse? result = await response.Content
            .ReadFromJsonAsync<BatchDispatchResponse>(JsonOptions);
        result.Should().NotBeNull();
        result!.Failed.Should().Be(2, "all jobs should fail when no eligible printers exist");
        result.Dispatched.Should().Be(0);
    }

    [Fact]
    [Trait("Category", "Dispatch")]
    [Trait("Phase", "3")]
    public async Task BatchDispatch_UnauthorizedAccess_Returns401()
    {
        HttpClient unauthClient = _factory.CreateClient();

        var request = new BatchDispatchRequest { JobIds = [Guid.NewGuid()] };

        HttpResponseMessage response = await unauthClient.PostAsJsonAsync(
            "/api/job-queue/batch-dispatch", request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    [Trait("Category", "Dispatch")]
    [Trait("Phase", "3")]
    public async Task BatchDispatch_IndividualJobFailureDoesNotFailEntireBatch()
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        Guid folderId = await EnsureRootFolderAsync(db);
        await SeedPrinterAsync(db, 40);

        Guid validJobId = await SeedQueuedJobAsync(db, folderId, 40);
        Guid nonExistentJobId = Guid.NewGuid();

        var request = new BatchDispatchRequest { JobIds = [validJobId, nonExistentJobId] };

        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/job-queue/batch-dispatch", request);

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "batch should succeed overall even if individual jobs fail");

        BatchDispatchResponse? result = await response.Content
            .ReadFromJsonAsync<BatchDispatchResponse>(JsonOptions);
        result.Should().NotBeNull();
        result!.TotalRequested.Should().Be(2);
        result.Results.Should().Contain(
            r => r.JobId == nonExistentJobId && !r.Success,
            "non-existent job should report failure in results");
    }
}
