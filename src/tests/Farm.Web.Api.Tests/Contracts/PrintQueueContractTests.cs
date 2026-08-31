using System.Net;
using System.Text.Json;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Testing.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace Farm.Web.Api.Tests.Contracts;

/// <summary>
/// Wire-contract corpus for the print-queue overview and print-job families
/// (<c>GET /api/job-queue</c> and <c>GET /api/job-queue/{id}</c>). Issue #2238:
/// fixtures are produced by a real <c>WebApplicationFactory</c> HTTP round trip through the
/// actual registered MVC <c>JsonSerializerOptions</c>
/// (<c>src/api/Startup/ControllerStartup.cs</c>), never a hand-built CLR object.
/// </summary>
public sealed class PrintQueueContractTests : IAsyncLifetime
{
    private readonly CustomWebApplicationFactory _factory = CustomWebApplicationFactory.CreateWithIsolatedDatabase();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    /// <summary>
    /// Empty-collection variant: no printers seeded, so <c>JobQueueService.GetQueueOverviewAsync</c>
    /// returns a genuinely empty list — the endpoint returns an empty JSON array, not a missing
    /// key or null.
    /// </summary>
    [Fact]
    public async Task GetQueue_NoPrinters_ReturnsEmptyCollection()
    {
        using HttpClient client = await _factory.CreateAdminClientAsync(
            username: "wire-contract-queue-empty",
            email: "wire-contract-queue-empty@example.com");

        using HttpResponseMessage response = await client.GetAsync("/api/job-queue");
        _ = response.StatusCode.Should().Be(HttpStatusCode.OK);

        string json = await response.Content.ReadAsStringAsync();
        using JsonDocument document = JsonDocument.Parse(json);
        _ = document.RootElement.ValueKind.Should().Be(JsonValueKind.Array);
        _ = document.RootElement.GetArrayLength().Should().Be(0);

        await WireContractFixtureWriter.CaptureOrVerifyAsync(
            WireContractCorpusPaths.ApiRoot,
            "print-queue/queue.empty-collection.json",
            endpoint: "GET /api/job-queue",
            producingTest: $"{nameof(PrintQueueContractTests)}.{nameof(GetQueue_NoPrinters_ReturnsEmptyCollection)}",
            schemaVersion: "1.0",
            actualJson: json);
    }

    /// <summary>
    /// Populated + missing-key variant: seeds a single available printer (via
    /// <see cref="AppDbContext"/>, the same real EF Core store the production
    /// <c>JobQueueService</c> reads from) with a populated <c>supportedMaterials</c> collection
    /// but no <c>NozzleModel</c> — so <c>nozzleDiameter</c> resolves to <see langword="null"/>
    /// and, per <c>ControllerStartup</c>'s <c>WhenWritingNull</c> policy, is omitted from the
    /// wire payload entirely (missing key, not explicit null). <c>modelAliases</c>, by contrast,
    /// sources from an EF Core collection navigation property (<c>printer.Model.Aliases</c>)
    /// that materializes as an empty (never-null) list when no <c>ModelAlias</c> rows are
    /// seeded — real production evidence that "no aliases" and "no nozzle model" take two
    /// different wire shapes (empty array vs. missing key) for what looks like the same
    /// "nothing was seeded" scenario, depending on whether the source is a scalar navigation
    /// (<see langword="null"/>-able) or a collection navigation (empty-but-present).
    /// </summary>
    [Fact]
    public async Task GetQueue_PopulatedPrinter_MatchesCorpus()
    {
        Guid printerId = await SeedAvailablePrinterAsync();

        using HttpClient client = await _factory.CreateAdminClientAsync(
            username: "wire-contract-queue-populated",
            email: "wire-contract-queue-populated@example.com");

        using HttpResponseMessage response = await client.GetAsync("/api/job-queue");
        _ = response.StatusCode.Should().Be(HttpStatusCode.OK);
        string json = await response.Content.ReadAsStringAsync();

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        _ = root.ValueKind.Should().Be(JsonValueKind.Array);
        _ = root.GetArrayLength().Should().Be(1);
        JsonElement entry = root[0];

        JsonElement printerIdElement = JsonContractAssertions.AssertProperty(entry, "printerId", JsonValueKind.String);
        Assert.Equal(printerId.ToString(), printerIdElement.GetString(), ignoreCase: true);
        JsonContractAssertions.AssertMissingKey(entry, "nozzleDiameter");
        JsonContractAssertions.AssertEmptyCollection(entry, "modelAliases");
        JsonContractAssertions.AssertMissingKey(entry, "currentJobId");
        JsonContractAssertions.AssertMissingKey(entry, "currentJobName");
        JsonElement supportedMaterials = JsonContractAssertions.AssertNonEmptyCollection(entry, "supportedMaterials");
        _ = supportedMaterials.GetArrayLength().Should().Be(2);
        _ = JsonContractAssertions.AssertProperty(entry, "isAvailable", JsonValueKind.True);
        _ = JsonContractAssertions.AssertProperty(entry, "queuedJobsCount", JsonValueKind.Number);

        var volatilePaths = new HashSet<string> { "$[0].printerId" };
        await WireContractFixtureWriter.CaptureOrVerifyAsync(
            WireContractCorpusPaths.ApiRoot,
            "print-queue/queue.populated.json",
            endpoint: "GET /api/job-queue",
            producingTest: $"{nameof(PrintQueueContractTests)}.{nameof(GetQueue_PopulatedPrinter_MatchesCorpus)}",
            schemaVersion: "1.0",
            actualJson: json,
            volatilePaths: volatilePaths);
    }

    /// <summary>
    /// Minimal print-job variant consumed by iOS <c>PrintJob</c>: required scalar and collection
    /// members remain present, while every nullable job, printer, timing, material, cost,
    /// dispatch, calibration, project, and harvest member is omitted by the production
    /// <c>WhenWritingNull</c> policy.
    /// </summary>
    [Fact]
    public async Task GetJob_MinimalPrintJob_OmitsOptionalKeysAndMatchesCorpusAsync()
    {
        Guid jobId = await SeedMinimalPrintJobAsync();

        using HttpClient client = await _factory.CreateAdminClientAsync(
            username: "wire-contract-print-job-minimal",
            email: "wire-contract-print-job-minimal@example.com");

        using HttpResponseMessage response = await client.GetAsync($"/api/job-queue/{jobId}");
        _ = response.StatusCode.Should().Be(HttpStatusCode.OK);
        string json = await response.Content.ReadAsStringAsync();

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        JsonContractAssertions.AssertEnumToken(root, "status", "Queued");
        JsonContractAssertions.AssertEnumToken(root, "priority", "Normal");
        _ = JsonContractAssertions.AssertProperty(root, "rowVersion", JsonValueKind.String);
        _ = JsonContractAssertions.AssertProperty(root, "queuePosition", JsonValueKind.Number);
        _ = JsonContractAssertions.AssertProperty(root, "gcodeFileName", JsonValueKind.String);
        _ = JsonContractAssertions.AssertProperty(root, "assignedPrinterName", JsonValueKind.String);
        _ = JsonContractAssertions.AssertProperty(root, "copies", JsonValueKind.Number);
        _ = JsonContractAssertions.AssertProperty(root, "completedCopies", JsonValueKind.Number);
        _ = JsonContractAssertions.AssertProperty(root, "remainingCopies", JsonValueKind.Number);
        JsonContractAssertions.AssertEmptyCollection(root, "toolRequirements");
        JsonContractAssertions.AssertEmptyCollection(root, "toolheadUsages");

        foreach (string propertyName in new[]
        {
            "dispatchStateRowVersion",
            "dispatchResult",
            "jobKind",
            "calibrationProjectId",
            "pinnedPrinterConfigRevision",
            "gcodeFileId",
            "assignedPrinterId",
            "requiredNozzleDiameter",
            "requiredMaterialType",
            "estimatedPrintTime",
            "estimatedFilamentUsage",
            "actualStartTime",
            "actualEndTime",
            "actualPrintTime",
            "actualFilamentUsage",
            "failureReason",
            "spoolmanFilamentId",
            "filamentName",
            "filamentVendor",
            "filamentColor",
            "estimatedCost",
            "actualCost",
            "projectFileId",
            "plateIndex",
            "plateName",
            "deadlineAtUtc",
            "harvestedAt",
            "slicerEngine",
            "progressPercent",
        })
        {
            JsonContractAssertions.AssertMissingKey(root, propertyName);
        }

        await WireContractFixtureWriter.CaptureOrVerifyAsync(
            WireContractCorpusPaths.ApiRoot,
            "print-jobs/job.minimal-missing-optional.json",
            endpoint: "GET /api/job-queue/{id}",
            producingTest: $"{nameof(PrintQueueContractTests)}.{nameof(GetJob_MinimalPrintJob_OmitsOptionalKeysAndMatchesCorpusAsync)}",
            schemaVersion: "1.0",
            actualJson: json);
    }

    /// <summary>
    /// Populated print-job variant consumed by iOS <c>PrintJob</c>. The real queue service
    /// projection supplies exact status/priority/dispatch enum tokens, revision ETags, related
    /// G-code and printer names, timing/material/cost values, multi-copy state, and harvest data.
    /// It also proves that this contract does not collapse into the separate slicer-host
    /// <c>GET /api/slice/{id}</c> payload family.
    /// </summary>
    [Fact]
    public async Task GetJob_PopulatedPrintJob_UsesExactEnumTokensAndMatchesCorpusAsync()
    {
        Guid jobId = await SeedPopulatedPrintJobAsync();

        using HttpClient client = await _factory.CreateAdminClientAsync(
            username: "wire-contract-print-job-populated",
            email: "wire-contract-print-job-populated@example.com");

        using HttpResponseMessage response = await client.GetAsync($"/api/job-queue/{jobId}");
        _ = response.StatusCode.Should().Be(HttpStatusCode.OK);
        string json = await response.Content.ReadAsStringAsync();

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        JsonContractAssertions.AssertEnumToken(root, "status", "Completed");
        JsonContractAssertions.AssertEnumToken(root, "priority", "Urgent");
        JsonContractAssertions.AssertEnumToken(root, "jobKind", "Standard");
        JsonElement dispatchResult = JsonContractAssertions.AssertProperty(
            root,
            "dispatchResult",
            JsonValueKind.Object);
        JsonContractAssertions.AssertEnumToken(dispatchResult, "outcome", "Accepted");

        foreach (string propertyName in new[]
        {
            "rowVersion",
            "dispatchStateRowVersion",
            "gcodeFileId",
            "gcodeFileName",
            "assignedPrinterId",
            "assignedPrinterName",
            "requiredMaterialType",
            "estimatedPrintTime",
            "actualStartTime",
            "actualEndTime",
            "actualPrintTime",
            "filamentName",
            "filamentVendor",
            "filamentColor",
            "projectFileId",
            "plateName",
            "deadlineAtUtc",
            "harvestedAt",
        })
        {
            _ = JsonContractAssertions.AssertProperty(root, propertyName, JsonValueKind.String);
        }

        foreach (string propertyName in new[]
        {
            "requiredNozzleDiameter",
            "estimatedFilamentUsage",
            "actualFilamentUsage",
            "spoolmanFilamentId",
            "estimatedCost",
            "actualCost",
            "plateIndex",
        })
        {
            _ = JsonContractAssertions.AssertProperty(root, propertyName, JsonValueKind.Number);
        }

        JsonElement toolRequirements = JsonContractAssertions.AssertNonEmptyCollection(root, "toolRequirements");
        _ = toolRequirements.GetArrayLength().Should().Be(1);
        _ = JsonContractAssertions.AssertProperty(toolRequirements[0], "toolIndex", JsonValueKind.Number);
        _ = JsonContractAssertions.AssertProperty(toolRequirements[0], "materialType", JsonValueKind.String);

        JsonContractAssertions.AssertMissingKey(root, "slicerEngine");
        JsonContractAssertions.AssertMissingKey(root, "progressPercent");
        JsonContractAssertions.AssertMissingKey(root, "modelFileName");

        string sliceFixturePath = Path.Join(
            WireContractCorpusPaths.ApiRoot,
            "slice-jobs",
            "job.completed-populated.json");
        using JsonDocument sliceDocument = JsonDocument.Parse(await File.ReadAllTextAsync(sliceFixturePath));
        JsonElement sliceRoot = sliceDocument.RootElement;
        _ = JsonContractAssertions.AssertProperty(sliceRoot, "slicerEngine", JsonValueKind.String);
        _ = JsonContractAssertions.AssertProperty(sliceRoot, "progressPercent", JsonValueKind.Number);
        JsonContractAssertions.AssertMissingKey(sliceRoot, "gcodeFileName");
        JsonContractAssertions.AssertMissingKey(sliceRoot, "priority");
        JsonContractAssertions.AssertMissingKey(sliceRoot, "copies");

        await WireContractFixtureWriter.CaptureOrVerifyAsync(
            WireContractCorpusPaths.ApiRoot,
            "print-jobs/job.populated.json",
            endpoint: "GET /api/job-queue/{id}",
            producingTest: $"{nameof(PrintQueueContractTests)}.{nameof(GetJob_PopulatedPrintJob_UsesExactEnumTokensAndMatchesCorpusAsync)}",
            schemaVersion: "1.0",
            actualJson: json);
    }

    private async Task<Guid> SeedAvailablePrinterAsync(Guid? fixedPrinterId = null)
    {
        Guid printerId = fixedPrinterId ?? Guid.NewGuid();
        Guid manufacturerId = Guid.NewGuid();
        Guid modelId = Guid.NewGuid();

        using IServiceScope scope = _factory.Services.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        _ = db.Manufacturers.Add(new Manufacturer
        {
            Id = manufacturerId,
            Name = "Wire Contract Manufacturer",
        });
        _ = db.PrinterModels.Add(new PrinterModel
        {
            Id = modelId,
            ManufacturerId = manufacturerId,
            Name = "Wire Contract Model",
        });
        _ = db.Printers.Add(new Printer
        {
            Id = printerId,
            Name = "Wire Contract Queue Printer",
            ServerUrl = "http://10.0.0.51",
            BackendPort = 7125,
            Backend = (int)PrinterBackend.Moonraker,
            ManufacturerId = manufacturerId,
            ModelId = modelId,
            IsEnabled = true,
            IsAvailable = true,
            Toolheads =
            [
                new Toolhead
                {
                    Id = Guid.NewGuid(),
                    PrinterId = printerId,
                    Name = "T0",
                    Index = 0,
                    IsPrimary = true,
                    ToolheadType = ToolheadType.Physical,
                    SupportedMaterials = ["PLA", "PETG"],
                },
            ],
        });

        _ = await db.SaveChangesAsync();
        return printerId;
    }

    private async Task<Guid> SeedMinimalPrintJobAsync()
    {
        Guid jobId = Guid.Parse("8fef6e78-93d6-41ff-904a-c06a00a80b5f");
        DateTime createdAt = new(2026, 8, 30, 18, 0, 0, DateTimeKind.Utc);

        using IServiceScope scope = _factory.Services.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        _ = db.PrintJobs.Add(new PrintJob
        {
            Id = jobId,
            Revision = 3,
            Name = "Wire Contract Minimal Job",
            Status = PrintJobStatus.Queued,
            Priority = (int)PrintJobPriority.Normal,
            QueuePosition = 1,
            Copies = 1,
            CompletedCopies = 0,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
            QueuedAt = createdAt,
        });

        _ = await db.SaveChangesAsync();
        return jobId;
    }

    private async Task<Guid> SeedPopulatedPrintJobAsync()
    {
        Guid printerId = Guid.Parse("65e82da4-f836-48d5-9c77-826a1dc17df1");
        _ = await SeedAvailablePrinterAsync(printerId);

        Guid folderId = Guid.Parse("1d2ff667-0343-4f7b-959f-432c8aaa7a44");
        Guid gcodeFileId = Guid.Parse("16bbd4aa-6b8a-4efd-b2ef-ae3aa9d05aa6");
        Guid jobId = Guid.Parse("0d8a40a5-a20c-4bdf-b747-efc46f211746");
        Guid dispatchAttemptId = Guid.Parse("e366a787-e8c0-4648-9459-e880a01388dc");
        DateTime createdAt = new(2026, 8, 30, 15, 0, 0, DateTimeKind.Utc);
        DateTime startedAt = createdAt.AddMinutes(10);
        DateTime completedAt = startedAt.AddMinutes(95);

        using IServiceScope scope = _factory.Services.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        _ = db.Set<FolderNode>().Add(new FolderNode
        {
            Id = folderId,
            Path = "/wire-contract",
            FolderType = "gcode",
            CreatedAt = createdAt,
        });
        _ = db.GcodeFiles.Add(new GcodeFile
        {
            Id = gcodeFileId,
            Revision = 4,
            Name = "wire-contract-populated.gcode",
            FileName = "wire-contract-populated.gcode",
            FolderId = folderId,
            FilePath = "/wire-contract/wire-contract-populated.gcode",
            FileHash = new string('a', 64),
            FileSizeBytes = 4096,
            UploadedAt = createdAt.AddMinutes(-5),
            CreatedAt = createdAt.AddMinutes(-5),
            UpdatedAt = createdAt.AddMinutes(-5),
            Source = GcodeSource.Upload,
        });

        var job = new PrintJob
        {
            Id = jobId,
            Revision = 9,
            Name = "Wire Contract Populated Job",
            GcodeFileId = gcodeFileId,
            AssignedPrinterId = printerId,
            Status = PrintJobStatus.Completed,
            Priority = (int)PrintJobPriority.Urgent,
            QueuePosition = 3,
            RequiredNozzleDiameter = 0.6m,
            RequiredMaterialType = "PETG",
            RequiredMaterialsPerTool =
            [
                new PrintJobToolMaterialRequirement(0, "PETG", "#336699", 24.5),
            ],
            EstimatedPrintTime = TimeSpan.FromMinutes(90),
            EstimatedFilamentUsage = 24.5,
            ActualStartTime = startedAt,
            ActualEndTime = completedAt,
            ActualPrintTime = completedAt - startedAt,
            ActualFilamentUsage = 25.25,
            SpoolmanFilamentId = 314,
            FilamentName = "Wire Contract PETG",
            FilamentVendor = "OlyForge",
            FilamentColor = "#336699",
            EstimatedCost = 1.23m,
            ActualCost = 1.34m,
            Copies = 4,
            CompletedCopies = 4,
            ProjectFileId = Guid.Parse("b0208544-9461-4c0c-b6c9-a2cf00f18c98"),
            PlateIndex = 2,
            PlateName = "Production Plate",
            DeadlineAtUtc = completedAt.AddHours(2),
            CreatedAt = createdAt,
            UpdatedAt = completedAt,
            QueuedAt = createdAt,
            HarvestedAt = completedAt.AddMinutes(15),
            JobKind = JobKind.Standard,
            PinnedPrinterConfigRevision = 23,
        };
        _ = db.PrintJobs.Add(job);
        _ = db.PrinterDispatchStates.Add(new PrinterDispatchState
        {
            PrinterId = printerId,
            Revision = 11,
            QueueRevision = 7,
        });
        _ = db.QueueDispatchAttempts.Add(new QueueDispatchAttempt
        {
            Id = dispatchAttemptId,
            Revision = 5,
            PrintJobId = jobId,
            PrinterId = printerId,
            PrinterConfigRevision = 23,
            AttemptNumber = 2,
            ActorSubject = "wire-contract",
            StartPathKind = "Manual",
            ClaimedAtUtc = startedAt.AddMinutes(-1),
            BackendAcceptedAtUtc = startedAt,
            Outcome = DispatchAttemptOutcome.Accepted,
            IsRetryable = false,
            RequiresReconciliation = false,
            UpdatedAtUtc = completedAt,
        });

        _ = await db.SaveChangesAsync();
        return jobId;
    }
}
