#pragma warning disable CA5394 // Random is adequate for test data generation
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Queue.Dispatch;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Farm.Web.Api.Tests.Services;

/// <summary>
/// Integration tests for DispatchScorer PrinterGroup filtering (Factor 10).
/// Verifies that when a GcodeFile has a PrinterGroupId, the scorer:
/// - Hard-eliminates printers NOT in that group (score = 0)
/// - Allows printers IN the group to proceed (score > 0)
/// - Maintains backward compatibility (no group = all printers pass)
/// </summary>
public class DispatchScorerPrinterGroupTests : IAsyncLifetime
{
    private readonly CustomWebApplicationFactory _factory;

    public DispatchScorerPrinterGroupTests()
    {
        _factory = new CustomWebApplicationFactory();
    }

    public async Task InitializeAsync()
    {
        await Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
    }

    // =========================================================================
    // Factor 10: PrinterGroup Hard Elimination
    // =========================================================================

    [Fact]
    [Trait("Category", "DispatchScorer")]
    public async Task ScorePrintersForJob_WithPrinterGroupId_EliminatesPrintersNotInGroup()
    {
        // Arrange: Create two printer groups and printers in each group
        Guid jobId, group1Id, group2Id, printer1Id, printer2Id;
        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // Seed manufacturer and model
            Manufacturer manufacturer = new()
            {
                Id = Guid.NewGuid(),
                Name = $"TestManufacturer_{Guid.NewGuid():N}",
                Url = "https://test.com"
            };
            db.Manufacturers.Add(manufacturer);

            PrinterModel model = new()
            {
                Id = Guid.NewGuid(),
                ManufacturerId = manufacturer.Id,
                Name = "Test Model",
                MaxBedTemp = 100
            };
            db.PrinterModels.Add(model);

            // Lookup or create filament type (shared DB may already have "PLA")
            FilamentType? filament = await db.FilamentTypes.FirstOrDefaultAsync(f => f.Name == "PLA");
            if (filament is null)
            {
                filament = new FilamentType
                {
                    Id = Guid.NewGuid(),
                    Name = "PLA",
                    IsActive = true,
                    NeedsEnclosure = false,
                    IsAbrasive = false
                };
                db.FilamentTypes.Add(filament);
            }

            // Link filament to model
            model.SupportedFilamentTypes.Add(filament);

            // Create printer groups
            PrinterGroup group1 = new()
            {
                Id = Guid.NewGuid(),
                Name = "Group 1",
                CreatedDate = DateTimeOffset.UtcNow,
                UpdatedDate = DateTimeOffset.UtcNow
            };
            PrinterGroup group2 = new()
            {
                Id = Guid.NewGuid(),
                Name = "Group 2",
                CreatedDate = DateTimeOffset.UtcNow,
                UpdatedDate = DateTimeOffset.UtcNow
            };
            db.PrinterGroups.AddRange(group1, group2);
            group1Id = group1.Id;
            group2Id = group2.Id;

            // Create printers in each group
            Printer printer1 = new()
            {
                Id = Guid.NewGuid(),
                Name = "Printer-Group1",
                ServerUrl = $"http://printer-{Guid.NewGuid():N}.local",
                Backend = (int)PrinterBackend.Moonraker,
                ModelId = model.Id,
                ManufacturerId = manufacturer.Id,
                PrinterGroupId = group1Id,
                IsEnabled = true,
                IsAvailable = true,
                InMaintenance = false
            };
            printer1Id = printer1.Id;

            Printer printer2 = new()
            {
                Id = Guid.NewGuid(),
                Name = "Printer-Group2",
                ServerUrl = $"http://printer-{Guid.NewGuid():N}.local",
                Backend = (int)PrinterBackend.PrusaLink,
                ModelId = model.Id,
                ManufacturerId = manufacturer.Id,
                PrinterGroupId = group2Id,
                IsEnabled = true,
                IsAvailable = true,
                InMaintenance = false
            };
            printer2Id = printer2.Id;

            db.Printers.AddRange(printer1, printer2);

            // Create GCode file with group1 constraint
            FolderNode gcodeRootFolder = await db.Set<FolderNode>().FirstAsync(f => f.Path == "/" && f.FolderType == "gcode");
            GcodeFile gcodeFile = new()
            {
                Id = Guid.NewGuid(),
                Name = "test.gcode",
                FileHash = "abc123",
                UploadedAt = DateTime.UtcNow,
                FileName = "test.gcode",
                FilePath = "/test/test.gcode",
                FolderId = gcodeRootFolder.Id,
                PrinterModelId = model.Id,
                PrinterGroupId = group1Id,  // Requires Group 1
                RequiredMaterial = "PLA",
                RequiredNozzleDiameter = 0.4,
                EstimatedPrintTimeMinutes = 3600,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            db.GcodeFiles.Add(gcodeFile);

            // Create print job referencing the gcode file
            PrintJob job = new()
            {
                Id = Guid.NewGuid(),
                GcodeFileId = gcodeFile.Id,
                Status = PrintJobStatus.Queued,
                RequiredMaterialType = "PLA",
                RequiredNozzleDiameter = 0.4m,
                Copies = 1,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                QueuedAt = DateTime.UtcNow
            };
            db.PrintJobs.Add(job);
            jobId = job.Id;

            await db.SaveChangesAsync();
        }

        // Act: Score all printers for the job
        List<DispatchScore> scores;
        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            DispatchScorer scorer = new(db, NullLogger<DispatchScorer>.Instance);
            scores = await scorer.ScorePrintersForJobAsync(jobId);
        }

        // Assert: Printer1 (in Group1) should pass, Printer2 (in Group2) should be eliminated
        scores.Should().HaveCount(2);

        DispatchScore? score1 = scores.FirstOrDefault(s => s.PrinterId == printer1Id);
        score1.Should().NotBeNull("Printer1 should be scored");
        score1!.Eliminated.Should().BeFalse("Printer1 is in the required group");
        score1.TotalScore.Should().BeGreaterThan(0, "Printer1 should have a positive score");

        DispatchScore? score2 = scores.FirstOrDefault(s => s.PrinterId == printer2Id);
        score2.Should().NotBeNull("Printer2 should be scored");
        score2!.Eliminated.Should().BeTrue("Printer2 is NOT in the required group");
        score2.TotalScore.Should().Be(0, "eliminated printers have zero total score");
        score2.EliminationReasons.Should().Contain(r => r.Contains("printer group"), "elimination reason should mention group mismatch");
    }

    [Fact]
    [Trait("Category", "DispatchScorer")]
    public async Task ScorePrintersForJob_WithoutPrinterGroupId_AllPrintersPass()
    {
        // Arrange: Create job WITHOUT printer group constraint (backward compatibility)
        Guid jobId, printer1Id, printer2Id;
        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // Seed manufacturer and model
            Manufacturer manufacturer = new()
            {
                Id = Guid.NewGuid(),
                Name = $"TestManufacturer_{Guid.NewGuid():N}",
                Url = "https://test.com"
            };
            db.Manufacturers.Add(manufacturer);

            PrinterModel model = new()
            {
                Id = Guid.NewGuid(),
                ManufacturerId = manufacturer.Id,
                Name = "Test Model",
                MaxBedTemp = 100
            };
            db.PrinterModels.Add(model);

            // Lookup or create filament type (shared DB may already have "PLA")
            FilamentType? filament = await db.FilamentTypes.FirstOrDefaultAsync(f => f.Name == "PLA");
            if (filament is null)
            {
                filament = new FilamentType
                {
                    Id = Guid.NewGuid(),
                    Name = "PLA",
                    IsActive = true,
                    NeedsEnclosure = false,
                    IsAbrasive = false
                };
                db.FilamentTypes.Add(filament);
            }

            // Link filament to model
            model.SupportedFilamentTypes.Add(filament);

            // Create printer group
            PrinterGroup group = new()
            {
                Id = Guid.NewGuid(),
                Name = "Test Group",
                CreatedDate = DateTimeOffset.UtcNow,
                UpdatedDate = DateTimeOffset.UtcNow
            };
            db.PrinterGroups.Add(group);

            // Create printers (one in group, one not)
            Printer printer1 = new()
            {
                Id = Guid.NewGuid(),
                Name = "Printer-InGroup",
                ServerUrl = $"http://printer-{Guid.NewGuid():N}.local",
                Backend = (int)PrinterBackend.Moonraker,
                ModelId = model.Id,
                ManufacturerId = manufacturer.Id,
                PrinterGroupId = group.Id,
                IsEnabled = true,
                IsAvailable = true,
                InMaintenance = false
            };
            printer1Id = printer1.Id;

            Printer printer2 = new()
            {
                Id = Guid.NewGuid(),
                Name = "Printer-NoGroup",
                ServerUrl = $"http://printer-{Guid.NewGuid():N}.local",
                Backend = (int)PrinterBackend.PrusaLink,
                ModelId = model.Id,
                ManufacturerId = manufacturer.Id,
                PrinterGroupId = null,  // Not in any group
                IsEnabled = true,
                IsAvailable = true,
                InMaintenance = false
            };
            printer2Id = printer2.Id;

            db.Printers.AddRange(printer1, printer2);

            // Create GCode file WITHOUT group constraint
            FolderNode gcodeRootFolder = await db.Set<FolderNode>().FirstAsync(f => f.Path == "/" && f.FolderType == "gcode");
            GcodeFile gcodeFile = new()
            {
                Id = Guid.NewGuid(),
                Name = "test.gcode",
                FileHash = "abc123",
                UploadedAt = DateTime.UtcNow,
                FileName = "test.gcode",
                FilePath = "/test/test.gcode",
                FolderId = gcodeRootFolder.Id,
                PrinterModelId = model.Id,
                PrinterGroupId = null,  // No group constraint
                RequiredMaterial = "PLA",
                RequiredNozzleDiameter = 0.4,
                EstimatedPrintTimeMinutes = 3600,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            db.GcodeFiles.Add(gcodeFile);

            // Create print job
            PrintJob job = new()
            {
                Id = Guid.NewGuid(),
                GcodeFileId = gcodeFile.Id,
                Status = PrintJobStatus.Queued,
                RequiredMaterialType = "PLA",
                RequiredNozzleDiameter = 0.4m,
                Copies = 1,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                QueuedAt = DateTime.UtcNow
            };
            db.PrintJobs.Add(job);
            jobId = job.Id;

            await db.SaveChangesAsync();
        }

        // Act: Score all printers for the job
        List<DispatchScore> scores;
        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            DispatchScorer scorer = new(db, NullLogger<DispatchScorer>.Instance);
            scores = await scorer.ScorePrintersForJobAsync(jobId);
        }

        // Assert: BOTH printers should pass (no group filtering)
        scores.Should().HaveCount(2);

        DispatchScore? score1 = scores.FirstOrDefault(s => s.PrinterId == printer1Id);
        score1.Should().NotBeNull("Printer1 should be scored");
        score1!.Eliminated.Should().BeFalse("no group constraint, so printer passes");
        score1.TotalScore.Should().BeGreaterThan(0, "printer should score above zero");

        DispatchScore? score2 = scores.FirstOrDefault(s => s.PrinterId == printer2Id);
        score2.Should().NotBeNull("Printer2 should be scored");
        score2!.Eliminated.Should().BeFalse("no group constraint, so printer passes");
        score2.TotalScore.Should().BeGreaterThan(0, "printer should score above zero");
    }

    [Fact]
    [Trait("Category", "DispatchScorer")]
    public async Task ScorePrintersForJob_WithPrinterGroupId_PrinterInCorrectGroup_PassesGate()
    {
        // Arrange: Create job with group constraint and printer IN that group
        Guid jobId, groupId, printerId;
        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // Seed manufacturer and model
            Manufacturer manufacturer = new()
            {
                Id = Guid.NewGuid(),
                Name = $"TestManufacturer_{Guid.NewGuid():N}",
                Url = "https://test.com"
            };
            db.Manufacturers.Add(manufacturer);

            PrinterModel model = new()
            {
                Id = Guid.NewGuid(),
                ManufacturerId = manufacturer.Id,
                Name = "Test Model",
                MaxBedTemp = 100
            };
            db.PrinterModels.Add(model);

            // Lookup or create filament type (shared DB may already have "PLA")
            FilamentType? filament = await db.FilamentTypes.FirstOrDefaultAsync(f => f.Name == "PLA");
            if (filament is null)
            {
                filament = new FilamentType
                {
                    Id = Guid.NewGuid(),
                    Name = "PLA",
                    IsActive = true,
                    NeedsEnclosure = false,
                    IsAbrasive = false
                };
                db.FilamentTypes.Add(filament);
            }

            // Link filament to model
            model.SupportedFilamentTypes.Add(filament);

            // Create printer group
            PrinterGroup group = new()
            {
                Id = Guid.NewGuid(),
                Name = "Prusa MK4 Fleet",
                CreatedDate = DateTimeOffset.UtcNow,
                UpdatedDate = DateTimeOffset.UtcNow
            };
            db.PrinterGroups.Add(group);
            groupId = group.Id;

            // Create printer IN the group
            Printer printer = new()
            {
                Id = Guid.NewGuid(),
                Name = "Printer-Correct-Group",
                ServerUrl = $"http://printer-{Guid.NewGuid():N}.local",
                Backend = (int)PrinterBackend.Moonraker,
                ModelId = model.Id,
                ManufacturerId = manufacturer.Id,
                PrinterGroupId = groupId,
                IsEnabled = true,
                IsAvailable = true,
                InMaintenance = false
            };
            db.Printers.Add(printer);
            printerId = printer.Id;

            // Create GCode file with group constraint
            FolderNode gcodeRootFolder = await db.Set<FolderNode>().FirstAsync(f => f.Path == "/" && f.FolderType == "gcode");
            GcodeFile gcodeFile = new()
            {
                Id = Guid.NewGuid(),
                Name = "test.gcode",
                FileHash = "abc123",
                UploadedAt = DateTime.UtcNow,
                FileName = "test.gcode",
                FilePath = "/test/test.gcode",
                FolderId = gcodeRootFolder.Id,
                PrinterModelId = model.Id,
                PrinterGroupId = groupId,  // Requires this group
                RequiredMaterial = "PLA",
                RequiredNozzleDiameter = 0.4,
                EstimatedPrintTimeMinutes = 3600,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            db.GcodeFiles.Add(gcodeFile);

            // Create print job
            PrintJob job = new()
            {
                Id = Guid.NewGuid(),
                GcodeFileId = gcodeFile.Id,
                Status = PrintJobStatus.Queued,
                RequiredMaterialType = "PLA",
                RequiredNozzleDiameter = 0.4m,
                Copies = 1,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                QueuedAt = DateTime.UtcNow
            };
            db.PrintJobs.Add(job);
            jobId = job.Id;

            await db.SaveChangesAsync();
        }

        // Act: Score the printer for the job
        List<DispatchScore> scores;
        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            DispatchScorer scorer = new(db, NullLogger<DispatchScorer>.Instance);
            scores = await scorer.ScorePrintersForJobAsync(jobId);
        }

        // Assert: Printer should pass the group gate
        scores.Should().HaveCount(1);
        DispatchScore score = scores[0];
        score.PrinterId.Should().Be(printerId);
        score.Eliminated.Should().BeFalse("printer is in the correct group");
        score.TotalScore.Should().BeGreaterThan(0, "printer should have a positive score");
        score.ScoreBreakdown.Should().ContainKey("PrinterGroup");
        score.ScoreBreakdown["PrinterGroup"].Score.Should().Be(100, "PrinterGroup factor passes with score 100");
    }

    [Fact]
    [Trait("Category", "DispatchScorer")]
    public async Task ScorePrintersForJob_WithPrinterGroupId_PrinterNotInGroup_IsEliminated()
    {
        // Arrange: Create job with group constraint and printer NOT in that group
        Guid jobId, requiredGroupId, wrongGroupId, printerId;
        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // Seed manufacturer and model
            Manufacturer manufacturer = new()
            {
                Id = Guid.NewGuid(),
                Name = $"TestManufacturer_{Guid.NewGuid():N}",
                Url = "https://test.com"
            };
            db.Manufacturers.Add(manufacturer);

            PrinterModel model = new()
            {
                Id = Guid.NewGuid(),
                ManufacturerId = manufacturer.Id,
                Name = "Test Model",
                MaxBedTemp = 100
            };
            db.PrinterModels.Add(model);

            // Lookup or create filament type (shared DB may already have "PLA")
            FilamentType? filament = await db.FilamentTypes.FirstOrDefaultAsync(f => f.Name == "PLA");
            if (filament is null)
            {
                filament = new FilamentType
                {
                    Id = Guid.NewGuid(),
                    Name = "PLA",
                    IsActive = true,
                    NeedsEnclosure = false,
                    IsAbrasive = false
                };
                db.FilamentTypes.Add(filament);
            }

            // Link filament to model
            model.SupportedFilamentTypes.Add(filament);

            // Create two printer groups
            PrinterGroup requiredGroup = new()
            {
                Id = Guid.NewGuid(),
                Name = "Required Group",
                CreatedDate = DateTimeOffset.UtcNow,
                UpdatedDate = DateTimeOffset.UtcNow
            };
            PrinterGroup wrongGroup = new()
            {
                Id = Guid.NewGuid(),
                Name = "Wrong Group",
                CreatedDate = DateTimeOffset.UtcNow,
                UpdatedDate = DateTimeOffset.UtcNow
            };
            db.PrinterGroups.AddRange(requiredGroup, wrongGroup);
            requiredGroupId = requiredGroup.Id;
            wrongGroupId = wrongGroup.Id;

            // Create printer in the WRONG group
            Printer printer = new()
            {
                Id = Guid.NewGuid(),
                Name = "Printer-Wrong-Group",
                ServerUrl = $"http://printer-{Guid.NewGuid():N}.local",
                Backend = (int)PrinterBackend.Moonraker,
                ModelId = model.Id,
                ManufacturerId = manufacturer.Id,
                PrinterGroupId = wrongGroupId,  // In wrong group
                IsEnabled = true,
                IsAvailable = true,
                InMaintenance = false
            };
            db.Printers.Add(printer);
            printerId = printer.Id;

            // Create GCode file requiring the other group
            FolderNode gcodeRootFolder = await db.Set<FolderNode>().FirstAsync(f => f.Path == "/" && f.FolderType == "gcode");
            GcodeFile gcodeFile = new()
            {
                Id = Guid.NewGuid(),
                Name = "test.gcode",
                FileHash = "abc123",
                UploadedAt = DateTime.UtcNow,
                FileName = "test.gcode",
                FilePath = "/test/test.gcode",
                FolderId = gcodeRootFolder.Id,
                PrinterModelId = model.Id,
                PrinterGroupId = requiredGroupId,  // Requires different group
                RequiredMaterial = "PLA",
                RequiredNozzleDiameter = 0.4,
                EstimatedPrintTimeMinutes = 3600,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            db.GcodeFiles.Add(gcodeFile);

            // Create print job
            PrintJob job = new()
            {
                Id = Guid.NewGuid(),
                GcodeFileId = gcodeFile.Id,
                Status = PrintJobStatus.Queued,
                RequiredMaterialType = "PLA",
                RequiredNozzleDiameter = 0.4m,
                Copies = 1,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                QueuedAt = DateTime.UtcNow
            };
            db.PrintJobs.Add(job);
            jobId = job.Id;

            await db.SaveChangesAsync();
        }

        // Act: Score the printer for the job
        List<DispatchScore> scores;
        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            DispatchScorer scorer = new(db, NullLogger<DispatchScorer>.Instance);
            scores = await scorer.ScorePrintersForJobAsync(jobId);
        }

        // Assert: Printer should be hard-eliminated
        scores.Should().HaveCount(1);
        DispatchScore score = scores[0];
        score.PrinterId.Should().Be(printerId);
        score.Eliminated.Should().BeTrue("printer is not in the required group");
        score.TotalScore.Should().Be(0, "eliminated printers have zero total score");
        score.EliminationReasons.Should().NotBeEmpty();
        score.EliminationReasons.Should().Contain(r => r.Contains("requires printer group"), "reason should explain group mismatch");
    }

    [Fact]
    [Trait("Category", "DispatchScorer")]
    public async Task ScorePrintersForJob_PrinterPendingBedClear_IsEliminated()
    {
        Guid jobId, printerId;
        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            Manufacturer manufacturer = new()
            {
                Id = Guid.NewGuid(),
                Name = $"TestMfr_{Guid.NewGuid():N}",
                Url = "https://test.com"
            };
            db.Manufacturers.Add(manufacturer);

            PrinterModel model = new()
            {
                Id = Guid.NewGuid(),
                ManufacturerId = manufacturer.Id,
                Name = "Test Model",
                MaxBedTemp = 100
            };
            db.PrinterModels.Add(model);

            FilamentType? filament = await db.FilamentTypes.FirstOrDefaultAsync(f => f.Name == "PLA");
            if (filament is null)
            {
                filament = new FilamentType
                {
                    Id = Guid.NewGuid(),
                    Name = "PLA",
                    IsActive = true
                };
                db.FilamentTypes.Add(filament);
            }

            model.SupportedFilamentTypes.Add(filament);

            FolderNode rootFolder = await db.Set<FolderNode>().FirstAsync(f => f.Path == "/" && f.FolderType == "gcode");

            printerId = Guid.NewGuid();
            Printer printer = new()
            {
                Id = printerId,
                Name = "BedFullPrinter",
                ServerUrl = "http://192.168.1.99",
                Backend = (int)PrinterBackend.Moonraker,
                IsEnabled = true,
                IsAvailable = true,
                InMaintenance = false,
                ModelId = model.Id,
                ManufacturerId = manufacturer.Id,
                AutoDispatchEnabled = true,
                DispatchState = new PrinterDispatchState { AutoDispatchState = AutoDispatchState.PendingReady }
            };
            db.Printers.Add(printer);

            GcodeFile gcodeFile = new()
            {
                Id = Guid.NewGuid(),
                Name = "test.gcode",
                FileName = $"{Guid.NewGuid()}.gcode",
                FilePath = "/gcode/",
                FolderId = rootFolder.Id,
                FileHash = Guid.NewGuid().ToString("N"),
                RequiredMaterial = "PLA",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                UploadedAt = DateTime.UtcNow
            };
            db.GcodeFiles.Add(gcodeFile);

            PrintJob job = new()
            {
                Id = Guid.NewGuid(),
                Name = "Test Job",
                GcodeFileId = gcodeFile.Id,
                Status = PrintJobStatus.Queued,
                Priority = 1,
                RequiredMaterialType = "PLA",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                QueuedAt = DateTime.UtcNow
            };
            db.PrintJobs.Add(job);
            jobId = job.Id;

            await db.SaveChangesAsync();
        }

        List<DispatchScore> scores;
        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            DispatchScorer scorer = new(db, NullLogger<DispatchScorer>.Instance);
            scores = await scorer.ScorePrintersForJobAsync(jobId);
        }

        DispatchScore? score = scores.FirstOrDefault(s => s.PrinterId == printerId);
        score.Should().NotBeNull();
        score!.Eliminated.Should().BeTrue("printer with PendingReady bed state should be eliminated");
        score.EliminationReasons.Should().Contain(r => r.Contains("bed clear"));
    }
}
