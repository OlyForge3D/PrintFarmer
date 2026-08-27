using System;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos.DataManagement;
using Farm.Infrastructure.Repositories.Printers;
using Farm.Infrastructure.Services.DataManagement;
using Farm.Infrastructure.Services.Security;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Farm.Infrastructure.Tests.DataManagement;

/// <summary>
/// Relational regressions for full-backup Replace orchestration and printer cleanup.
/// SQLite foreign keys are enabled so dependency ordering and rollback behavior match
/// production relational providers.
/// </summary>
public sealed class DataImportServiceReplaceModeCascadeTests
{
    [Fact]
    public async Task ImportFullBackupAsync_ReplaceMode_ReplacesDependentAndCatalogData()
    {
        await using SqliteConnection connection =
            new("Data Source=file:dataimport-replace-success?mode=memory&cache=shared");
        await connection.OpenAsync();
        await EnableSqliteForeignKeysAsync(connection);
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await SeedExistingPrinterAsync(options);

        await using AppDbContext act = new(options);
        DataImportService service = CreateService(act);
        FullBackupExportDto backup = CreateReplacementBackup();

        ImportResponseDto result = await service.ImportFullBackupAsync(backup, ImportMode.Replace);

        result.Success.Should().BeTrue();
        result.Errors.Should().BeEmpty();
        (await act.Printers.Select(printer => printer.Name).ToListAsync()).Should().Equal("Replacement Printer");
        (await act.Locations.Select(location => location.Name).ToListAsync()).Should().Equal("Replacement Location");
        (await act.Manufacturers.Select(manufacturer => manufacturer.Name).ToListAsync()).Should().Equal("Replacement Manufacturer");
        (await act.PrinterModels.Select(model => model.Name).ToListAsync()).Should().Equal("Replacement Model");
        (await act.GcodeFiles.SingleAsync()).PrinterModelId.Should().BeNull();
        (await act.PrintJobStatistics.SingleAsync()).PrinterModelId.Should().BeNull();
        (await act.CalibrationProjects.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task ImportFullBackupAsync_ReplaceMode_WhenCatalogImportFails_RollsBackAndClearsTrackedChanges()
    {
        await using SqliteConnection connection =
            new("Data Source=file:dataimport-replace-rollback?mode=memory&cache=shared");
        await connection.OpenAsync();
        await EnableSqliteForeignKeysAsync(connection);
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await SeedExistingPrinterAsync(options);
        FullBackupExportDto backup = CreateReplacementBackup();
        backup.Catalog.PrinterModels[0].ManufacturerName = "Missing Manufacturer";

        await using (AppDbContext act = new(options))
        {
            DataImportService service = CreateService(act);

            ImportResponseDto result = await service.ImportFullBackupAsync(backup, ImportMode.Replace);

            result.Success.Should().BeFalse();
            result.Errors.Should().NotBeEmpty();
            act.ChangeTracker.Entries().Should().OnlyContain(entry => entry.State == EntityState.Unchanged);
        }

        await using AppDbContext assert = new(options);
        (await assert.Printers.Select(printer => printer.Name).ToListAsync()).Should().Equal("Existing Printer");
        (await assert.Locations.Select(location => location.Name).ToListAsync()).Should().Equal("Existing Location");
        (await assert.Manufacturers.Select(manufacturer => manufacturer.Name).ToListAsync()).Should().Equal("Existing Manufacturer");
        (await assert.PrinterModels.Select(model => model.Name).ToListAsync()).Should().Equal("Existing Model");
        (await assert.CalibrationProjects.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task ImportCatalogAsync_ReplaceMode_WhenDeleteFails_DoesNotLeaveDeletedEntitiesTracked()
    {
        await using SqliteConnection connection =
            new("Data Source=file:dataimport-catalog-tracker?mode=memory&cache=shared");
        await connection.OpenAsync();
        await EnableSqliteForeignKeysAsync(connection);
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        await SeedExistingPrinterAsync(options);

        await using AppDbContext act = new(options);
        DataImportService service = CreateService(act);
        CatalogExportDto replacementCatalog = CreateReplacementBackup().Catalog;

        ImportResponseDto result = await service.ImportCatalogAsync(replacementCatalog, ImportMode.Replace);

        result.Success.Should().BeFalse();
        result.Errors.Should().NotBeEmpty();
        act.ChangeTracker.Entries().Should().BeEmpty();

        _ = act.Manufacturers.Add(new Manufacturer { Id = Guid.NewGuid(), Name = "Post-failure Manufacturer" });
        Func<Task> saveAfterFailure = async () => await act.SaveChangesAsync();
        await saveAfterFailure.Should().NotThrowAsync();
        (await act.Manufacturers.CountAsync()).Should().Be(2);
        (await act.Printers.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task DeleteAllPrintersAsync_WithSchedulesAlertsLogsAndDirectMappings_RunsAllCompensatingCleanups()
    {
        await using SqliteConnection connection =
            new("Data Source=file:dataimport-replace-cascade?mode=memory&cache=shared");
        await connection.OpenAsync();
        await EnableSqliteForeignKeysAsync(connection);
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

        Guid manufacturerId = Guid.NewGuid();
        Guid modelId = Guid.NewGuid();
        Guid printerId = Guid.NewGuid();
        Guid folderId = Guid.NewGuid();
        Guid sourceGcodeFileId = Guid.NewGuid();
        Guid planId = Guid.NewGuid();
        Guid scheduleId = Guid.NewGuid();
        Guid alertId = Guid.NewGuid();
        Guid logId = Guid.NewGuid();
        Guid partInventoryId = Guid.NewGuid();
        Guid directMappingId = Guid.NewGuid();

        await using (AppDbContext seed = new(options))
        {
            _ = await seed.Database.EnsureCreatedAsync();
            await EnableSqliteForeignKeysAsync(seed.Database.GetDbConnection());

            _ = seed.Manufacturers.Add(new Manufacturer { Id = manufacturerId, Name = "M" });
            _ = seed.PrinterModels.Add(new PrinterModel { Id = modelId, Name = "PM", ManufacturerId = manufacturerId });
            _ = seed.Printers.Add(new Printer
            {
                Id = printerId,
                Name = "P",
                ServerUrl = "http://p",
                ManufacturerId = manufacturerId,
                ModelId = modelId,
            });
            _ = seed.Set<FolderNode>().Add(new FolderNode { Id = folderId, Path = "/", FolderType = "gcode" });
            _ = seed.GcodeFiles.Add(new GcodeFile
            {
                Id = sourceGcodeFileId,
                Name = "src.gcode",
                FileName = "src.gcode",
                FilePath = "/tmp",
                FileHash = new string('d', 64),
                FileSizeBytes = 1,
                FolderId = folderId,
                SourcePrinterId = printerId,
                UploadedAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            _ = seed.MaintenancePlans.Add(new MaintenancePlan { Id = planId, Name = "Plan" });
            _ = seed.PrinterMaintenanceSchedules.Add(new PrinterMaintenanceSchedule
            {
                Id = scheduleId,
                MaintenancePlanId = planId,
                PrinterId = printerId,
            });
            _ = seed.MaintenanceAlerts.Add(new MaintenanceAlert
            {
                Id = alertId,
                PrinterId = printerId,
                PrinterMaintenanceScheduleId = scheduleId,
                Title = "Filter clog",
                Message = "Clean",
            });
            _ = seed.MaintenanceLogs.Add(new MaintenanceLog
            {
                Id = logId,
                PrinterId = printerId,
                PrinterMaintenanceScheduleId = scheduleId,
                TaskName = "Cleaned",
                PerformedAt = DateTime.UtcNow,
            });
            _ = seed.PartInventories.Add(new PartInventory { Id = partInventoryId, Sku = "SKU", Name = "P" });
            _ = seed.PartOutputMappings.Add(new PartOutputMapping
            {
                Id = directMappingId,
                GcodeFileId = sourceGcodeFileId,
                PrintProjectFileId = null,
                PartInventoryId = partInventoryId,
                Quantity = 1,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            _ = await seed.SaveChangesAsync();
        }

        await using (AppDbContext act = new(options))
        {
            await EnableSqliteForeignKeysAsync(act.Database.GetDbConnection());
            var sensitiveData = new NullSensitiveDataProtector();
            IPrintersRepository printersRepository = new EfPrintersRepository(act, sensitiveData);
            var service = new DataImportService(act, NullLogger<DataImportService>.Instance, sensitiveData, printersRepository);

            // Invoke DeleteAllPrintersAsync directly — this is the exact code path patched
            // by F1 (route through IPrintersRepository.RemoveAsync + F4 outer transaction).
            // The old raw <c>_context.Printers.RemoveRange(...)</c> path would FK-fail here.
            System.Reflection.MethodInfo? deleteAllPrinters = typeof(DataImportService).GetMethod(
                "DeleteAllPrintersAsync",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            deleteAllPrinters.Should().NotBeNull("DeleteAllPrintersAsync should exist as a private method");

            Func<Task> invoke = async () =>
            {
                Task task = (Task)deleteAllPrinters!.Invoke(service, new object[] { CancellationToken.None })!;
                await task;
            };

            await invoke.Should().NotThrowAsync(
                "the F1 fix routes deletion through the repository (which cleans schedules under the Restrict FK and direct PartOutputMappings under the Restrict FK); the pre-F1 raw RemoveRange would FK-fail here");
        }

        await using (AppDbContext assert = new(options))
        {
            await EnableSqliteForeignKeysAsync(assert.Database.GetDbConnection());
            (await assert.Printers.CountAsync(p => p.Id == printerId)).Should().Be(0, "printer removed");
            (await assert.PrinterMaintenanceSchedules.CountAsync(s => s.Id == scheduleId)).Should().Be(0, "schedule removed via repo");
            (await assert.MaintenanceAlerts.CountAsync(a => a.Id == alertId)).Should().Be(0, "alert cascade");
            (await assert.MaintenanceLogs.CountAsync(l => l.Id == logId)).Should().Be(0, "log cascade");
            (await assert.PartOutputMappings.CountAsync(m => m.Id == directMappingId)).Should().Be(0, "direct mapping removed by F2 cleanup");
            (await assert.GcodeFiles.CountAsync(g => g.Id == sourceGcodeFileId)).Should().Be(0, "source gcode bulk-deleted");
        }
    }

    private static async Task EnableSqliteForeignKeysAsync(System.Data.Common.DbConnection connection)
    {
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        using System.Data.Common.DbCommand cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_keys = ON;";
        _ = await cmd.ExecuteNonQueryAsync();
    }

    private static DataImportService CreateService(AppDbContext context)
    {
        var sensitiveData = new NullSensitiveDataProtector();
        IPrintersRepository printersRepository = new EfPrintersRepository(context, sensitiveData);
        return new DataImportService(context, NullLogger<DataImportService>.Instance, sensitiveData, printersRepository);
    }

    private static async Task SeedExistingPrinterAsync(DbContextOptions<AppDbContext> options)
    {
        await using AppDbContext seed = new(options);
        _ = await seed.Database.EnsureCreatedAsync();
        await EnableSqliteForeignKeysAsync(seed.Database.GetDbConnection());

        Guid manufacturerId = Guid.NewGuid();
        Guid modelId = Guid.NewGuid();
        Guid locationId = Guid.NewGuid();
        Guid folderId = Guid.NewGuid();
        Guid printJobId = Guid.NewGuid();
        Guid printerId = Guid.NewGuid();
        _ = seed.Manufacturers.Add(new Manufacturer { Id = manufacturerId, Name = "Existing Manufacturer" });
        _ = seed.PrinterModels.Add(new PrinterModel
        {
            Id = modelId,
            Name = "Existing Model",
            ManufacturerId = manufacturerId,
        });
        _ = seed.Locations.Add(new Location
        {
            Id = locationId,
            Name = "Existing Location",
            Path = "/existing-location",
            CreatedAt = DateTime.UtcNow,
            ModifiedAt = DateTime.UtcNow,
        });
        _ = seed.Printers.Add(new Printer
        {
            Id = printerId,
            Name = "Existing Printer",
            ServerUrl = "http://existing-printer",
            ManufacturerId = manufacturerId,
            ModelId = modelId,
            LocationId = locationId,
        });
        _ = seed.CalibrationProjects.Add(new CalibrationProject
        {
            Id = Guid.NewGuid(),
            OwnerUserId = Guid.NewGuid(),
            PrinterId = printerId,
            Name = "Existing calibration",
            FilamentProvider = "local",
            FilamentProductId = "PLA",
            FilamentProductName = "PLA",
            FilamentMaterial = "PLA",
            CreateRequestId = Guid.NewGuid().ToString(),
            CreatedBySubject = "test",
            UpdatedBySubject = "test",
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
        });
        _ = seed.Set<FolderNode>().Add(new FolderNode
        {
            Id = folderId,
            Path = "/",
            FolderType = "gcode",
        });
        _ = seed.GcodeFiles.Add(new GcodeFile
        {
            Id = Guid.NewGuid(),
            Name = "retained.gcode",
            FileName = "retained.gcode",
            FilePath = "/retained.gcode",
            FileHash = new string('e', 64),
            FileSizeBytes = 1,
            FolderId = folderId,
            PrinterModelId = modelId,
            UploadedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        _ = seed.PrintJobs.Add(new PrintJob
        {
            Id = printJobId,
            Name = "Retained job",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            QueuedAt = DateTime.UtcNow,
        });
        _ = seed.PrintJobStatistics.Add(new PrintJobStatistics
        {
            Id = Guid.NewGuid(),
            PrintJobId = printJobId,
            PrinterModelId = modelId,
        });
        _ = await seed.SaveChangesAsync();
    }

    private static FullBackupExportDto CreateReplacementBackup()
    {
        return new FullBackupExportDto
        {
            Catalog = new CatalogExportDto
            {
                Manufacturers =
                [
                    new ManufacturerExportDto { Name = "Replacement Manufacturer" },
                ],
                PrinterModels =
                [
                    new PrinterModelExportDto
                    {
                        Name = "Replacement Model",
                        ManufacturerName = "Replacement Manufacturer",
                    },
                ],
            },
            Locations =
            [
                new LocationExportDto { Name = "Replacement Location" },
            ],
            Printers =
            [
                new PrinterExportDto
                {
                    Name = "Replacement Printer",
                    ServerUrl = "http://replacement-printer",
                    ModelName = "Replacement Model",
                    LocationName = "Replacement Location",
                },
            ],
        };
    }

    private sealed class NullSensitiveDataProtector : ISensitiveDataProtector
    {
        public string? Protect(string? plainText) => plainText;
        public string? Unprotect(string? protectedText) => protectedText;
    }
}
