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
/// Integration tests for the dispatch queue status and history APIs.
/// Pre-implementation tests — will return 404 until the endpoints are implemented.
///
/// Tests verify:
/// - GET /api/dispatch/queue-status returns queue depth per printer
/// - GET /api/dispatch/queue-status includes pending unassigned count
/// - GET /api/dispatch/history returns paginated dispatch logs
/// - GET /api/dispatch/history supports date range filtering
/// - Unauthorized access returns 401
/// </summary>
public class DispatchQueueStatusTests : IAsyncLifetime
{
    private readonly CustomWebApplicationFactory _factory;
    private HttpClient _client = null!;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public DispatchQueueStatusTests()
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
    // LOCAL DTOs — will be replaced once the endpoints are implemented
    // =========================================================================

    private sealed class QueueStatusResponse
    {
        public int PendingUnassignedCount { get; set; }
        public List<PrinterQueueDepth> PrinterQueues { get; set; } = [];
    }

    private sealed class PrinterQueueDepth
    {
        public Guid PrinterId { get; set; }
        public string PrinterName { get; set; } = string.Empty;
        public int QueuedCount { get; set; }
        public int ActiveCount { get; set; }
    }

    private sealed class DispatchHistoryResponse
    {
        public int TotalCount { get; set; }
        public int Page { get; set; }
        public int PageSize { get; set; }
        public List<DispatchHistoryEntry> Items { get; set; } = [];
    }

    private sealed class DispatchHistoryEntry
    {
        public Guid Id { get; set; }
        public Guid PrintJobId { get; set; }
        public Guid PrinterId { get; set; }
        public string Action { get; set; } = string.Empty;
        public double? Score { get; set; }
        public string? Reason { get; set; }
        public DateTime CreatedAtUtc { get; set; }
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
            Path = "/test-qstatus",
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
            Name = $"QSMfg-{Guid.NewGuid():N}",
        });
        await db.SaveChangesAsync();

        Guid modelId = Guid.NewGuid();
        db.PrinterModels.Add(new PrinterModel
        {
            Id = modelId,
            Name = $"QSModel-{index}",
            ManufacturerId = manufacturerId,
        });
        await db.SaveChangesAsync();

        Guid printerId = Guid.NewGuid();
        Printer printer = new PrinterBuilder()
            .WithId(printerId)
            .WithName($"QS Printer {index}")
            .WithServerUrl($"http://192.168.220.{index}")
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
            Name = $"qs-job-{index}.gcode",
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
            Name = $"QS Job {index}",
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

    private static async Task SeedDispatchLogAsync(
        AppDbContext db, Guid printJobId, Guid printerId, DateTime? createdAt = null)
    {
        db.Set<DispatchLog>().Add(new DispatchLog
        {
            Id = Guid.NewGuid(),
            PrintJobId = printJobId,
            PrinterId = printerId,
            Action = DispatchAction.Dispatched,
            Score = 85.0,
            Reason = "Auto-dispatched via test",
            CreatedAtUtc = createdAt ?? DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    // =========================================================================
    // QUEUE STATUS TESTS
    // =========================================================================

    [Fact]
    [Trait("Category", "Dispatch")]
    [Trait("Phase", "3")]
    public async Task GetQueueStatus_ReturnsQueueDepthPerPrinter()
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        Guid folderId = await EnsureRootFolderAsync(db);
        Guid printer1 = await SeedPrinterAsync(db, 1);
        Guid printer2 = await SeedPrinterAsync(db, 2);

        // Assign 2 jobs to printer1 and 1 job to printer2
        await SeedQueuedJobAsync(db, folderId, 1, assignedPrinterId: printer1);
        await SeedQueuedJobAsync(db, folderId, 2, assignedPrinterId: printer1);
        await SeedQueuedJobAsync(db, folderId, 3, assignedPrinterId: printer2);

        HttpResponseMessage response = await _client.GetAsync("/api/dispatch/queue-status");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        QueueStatusResponse? result = await response.Content
            .ReadFromJsonAsync<QueueStatusResponse>(JsonOptions);
        result.Should().NotBeNull();
        result!.PrinterQueues.Should().NotBeEmpty("printers with queued jobs should appear");

        PrinterQueueDepth? p1Queue = result.PrinterQueues
            .Find(q => q.PrinterId == printer1);
        p1Queue.Should().NotBeNull();
        p1Queue!.QueuedCount.Should().Be(2, "printer1 has 2 assigned jobs");

        PrinterQueueDepth? p2Queue = result.PrinterQueues
            .Find(q => q.PrinterId == printer2);
        p2Queue.Should().NotBeNull();
        p2Queue!.QueuedCount.Should().Be(1, "printer2 has 1 assigned job");
    }

    [Fact]
    [Trait("Category", "Dispatch")]
    [Trait("Phase", "3")]
    public async Task GetQueueStatus_IncludesPendingUnassignedCount()
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        Guid folderId = await EnsureRootFolderAsync(db);
        Guid printer1 = await SeedPrinterAsync(db, 10);

        // 1 assigned + 2 unassigned jobs
        await SeedQueuedJobAsync(db, folderId, 10, assignedPrinterId: printer1);
        await SeedQueuedJobAsync(db, folderId, 11);
        await SeedQueuedJobAsync(db, folderId, 12);

        HttpResponseMessage response = await _client.GetAsync("/api/dispatch/queue-status");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        QueueStatusResponse? result = await response.Content
            .ReadFromJsonAsync<QueueStatusResponse>(JsonOptions);
        result.Should().NotBeNull();
        result!.PendingUnassignedCount.Should().Be(2,
            "2 jobs are queued without a printer assignment");
    }

    // =========================================================================
    // DISPATCH HISTORY TESTS
    // =========================================================================

    [Fact]
    [Trait("Category", "Dispatch")]
    [Trait("Phase", "3")]
    public async Task GetDispatchHistory_ReturnsPaginatedLogs()
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        Guid folderId = await EnsureRootFolderAsync(db);
        Guid printerId = await SeedPrinterAsync(db, 20);

        // Seed 5 dispatch logs
        for (int i = 1; i <= 5; i++)
        {
            Guid jobId = await SeedQueuedJobAsync(db, folderId, 20 + i, assignedPrinterId: printerId);
            await SeedDispatchLogAsync(db, jobId, printerId);
        }

        HttpResponseMessage response = await _client.GetAsync(
            "/api/dispatch/history?page=1&pageSize=3");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        DispatchHistoryResponse? result = await response.Content
            .ReadFromJsonAsync<DispatchHistoryResponse>(JsonOptions);
        result.Should().NotBeNull();
        result!.TotalCount.Should().Be(5, "5 dispatch log entries were seeded");
        result.Items.Should().HaveCount(3, "pageSize=3 should return only 3 items");
        result.Page.Should().Be(1);
        result.PageSize.Should().Be(3);
    }

    [Fact]
    [Trait("Category", "Dispatch")]
    [Trait("Phase", "3")]
    public async Task GetDispatchHistory_SupportsDateRangeFiltering()
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        Guid folderId = await EnsureRootFolderAsync(db);
        Guid printerId = await SeedPrinterAsync(db, 30);

        // Seed logs at specific dates
        DateTime oldDate = new(2025, 1, 15, 12, 0, 0, DateTimeKind.Utc);
        DateTime recentDate = new(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc);

        Guid oldJobId = await SeedQueuedJobAsync(db, folderId, 30, assignedPrinterId: printerId);
        await SeedDispatchLogAsync(db, oldJobId, printerId, createdAt: oldDate);

        Guid recentJobId = await SeedQueuedJobAsync(db, folderId, 31, assignedPrinterId: printerId);
        await SeedDispatchLogAsync(db, recentJobId, printerId, createdAt: recentDate);

        // Filter to only recent entries
        HttpResponseMessage response = await _client.GetAsync(
            "/api/dispatch/history?from=2025-06-01&to=2025-12-31");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        DispatchHistoryResponse? result = await response.Content
            .ReadFromJsonAsync<DispatchHistoryResponse>(JsonOptions);
        result.Should().NotBeNull();
        result!.Items.Should().HaveCount(1,
            "only the June log entry should match the date range filter");
        result.Items[0].PrintJobId.Should().Be(recentJobId);
    }

    [Fact]
    [Trait("Category", "Dispatch")]
    [Trait("Phase", "3")]
    public async Task GetDispatchHistory_UnauthorizedAccess_Returns401()
    {
        HttpClient unauthClient = _factory.CreateClient();

        HttpResponseMessage response = await unauthClient.GetAsync("/api/dispatch/history");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
