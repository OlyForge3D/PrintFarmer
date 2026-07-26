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

namespace Farm.Web.Api.Tests.DataManagement;

/// <summary>
/// F1 regression for the Dallas cascade adjudication of #953: DataImportService's Replace
/// mode must route printer deletion through the authoritative IPrintersRepository.RemoveAsync
/// path so every compensating cleanup (schedules under the Restrict FK, direct
/// PartOutputMappings under the Restrict FK, source-GcodeFiles, PrintJobs, etc.) actually
/// runs. Uses a real relational provider (SQLite with PRAGMA foreign_keys=ON) so the FK
/// constraints are enforced — the older <c>_context.Printers.RemoveRange(...)</c> path
/// would FK-fail on any of these compensations.
/// <para>
/// Exercises <c>DeleteAllPrintersAsync</c> in isolation via reflection because
/// <c>ImportFullBackupAsync</c> calls <c>DeleteAllCatalogDataAsync</c> first, which would
/// FK-fail before ever reaching the F1 code under test.
/// </para>
/// </summary>
public sealed class DataImportServiceReplaceModeCascadeTests
{
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

    private sealed class NullSensitiveDataProtector : ISensitiveDataProtector
    {
        public string? Protect(string? plainText) => plainText;
        public string? Unprotect(string? protectedText) => protectedText;
    }
}
