using System;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Printers;
using Farm.Infrastructure.Services.Security;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Farm.Web.Api.Tests.Repositories.Printers;

/// <summary>
/// Cascade / audit behavior tests for <see cref="EfPrintersRepository.RemoveAsync"/>
/// under the Dallas cascade adjudication for #953.
///
/// Two invariants:
///  1) Removing a printer deletes its schedules first (Restrict on
///     <c>PrinterMaintenanceSchedule.PrinterId</c>), then cascade-deletes its alerts and
///     logs (Cascade on <c>MaintenanceAlert.PrinterId</c> and <c>MaintenanceLog.PrinterId</c>).
///     Without the explicit schedule cleanup, the Restrict FK would abort the printer
///     removal entirely.
///  2) Deleting an isolated schedule (not tied to a printer removal) nulls the schedule
///     link on any referencing alerts/logs (SetNull FK preserved) and leaves the audit
///     rows in place. This is the "alerts outlive removed schedules" property called out
///     in <c>MaintenanceAlertConfiguration</c> and preserved by the Dallas adjudication.
///
/// Uses SQLite in-memory with <c>EnsureCreatedAsync</c> so the schema comes directly from
/// the model config (no migrations), and PRAGMA <c>foreign_keys = ON</c> so the DB actually
/// enforces the FK behaviors under test.
/// </summary>
public sealed class EfPrintersRepositoryRemoveAsyncCascadeTests
{
    [Fact]
    public async Task RemoveAsync_DeletesSchedules_ThenCascadeDeletesAlertsAndLogs()
    {
        await using SqliteConnection connection =
            new("Data Source=file:printer-remove-cascade?mode=memory&cache=shared");
        await connection.OpenAsync();
        await EnableSqliteForeignKeysAsync(connection);
        DbContextOptions<AppDbContext> options = OptionsFor(connection);

        Guid manufacturerId = Guid.NewGuid();
        Guid modelId = Guid.NewGuid();
        Guid printerId = Guid.NewGuid();
        Guid planId = Guid.NewGuid();
        Guid scheduleId = Guid.NewGuid();
        Guid alertId = Guid.NewGuid();
        Guid logId = Guid.NewGuid();

        await using (AppDbContext seed = new(options))
        {
            _ = await seed.Database.EnsureCreatedAsync();
            await EnableSqliteForeignKeysAsync(seed.Database.GetDbConnection());

            _ = seed.Manufacturers.Add(new Manufacturer { Id = manufacturerId, Name = "TestCo" });
            _ = seed.PrinterModels.Add(new PrinterModel
            {
                Id = modelId,
                Name = "T-800",
                ManufacturerId = manufacturerId,
            });
            _ = seed.Printers.Add(new Printer
            {
                Id = printerId,
                Name = "T-800",
                ServerUrl = "http://p",
                ManufacturerId = manufacturerId,
                ModelId = modelId,
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
                Message = "Clean it",
            });
            _ = seed.MaintenanceLogs.Add(new MaintenanceLog
            {
                Id = logId,
                PrinterId = printerId,
                PrinterMaintenanceScheduleId = scheduleId,
                TaskName = "Cleaned filter",
                PerformedAt = DateTime.UtcNow,
            });
            _ = await seed.SaveChangesAsync();
        }

        await using (AppDbContext act = new(options))
        {
            await EnableSqliteForeignKeysAsync(act.Database.GetDbConnection());
            var repository = new EfPrintersRepository(act, NullSensitiveDataProtector.Instance);
            Printer detached = new() { Id = printerId };
            await repository.RemoveAsync(detached, CancellationToken.None);
        }

        await using (AppDbContext assert = new(options))
        {
            await EnableSqliteForeignKeysAsync(assert.Database.GetDbConnection());

            (await assert.Printers.CountAsync(p => p.Id == printerId)).Should().Be(0,
                "the printer must be removed at the end of RemoveAsync");
            (await assert.PrinterMaintenanceSchedules.CountAsync(s => s.Id == scheduleId)).Should().Be(0,
                "the schedule must be explicitly deleted BEFORE the printer to avoid the Restrict FK aborting the removal");
            (await assert.MaintenanceAlerts.CountAsync(a => a.Id == alertId)).Should().Be(0,
                "the alert must be explicitly deleted by RemoveAsync before the printer is removed");
            (await assert.MaintenanceLogs.CountAsync(l => l.Id == logId)).Should().Be(0,
                "the log must be explicitly deleted by RemoveAsync before the printer is removed");
        }
    }

    [Fact]
    public async Task IsolatedScheduleDeletion_NullsScheduleLinks_PreservesAlertAndLogAuditRows()
    {
        await using SqliteConnection connection =
            new("Data Source=file:schedule-isolated-delete?mode=memory&cache=shared");
        await connection.OpenAsync();
        await EnableSqliteForeignKeysAsync(connection);
        DbContextOptions<AppDbContext> options = OptionsFor(connection);

        Guid manufacturerId = Guid.NewGuid();
        Guid modelId = Guid.NewGuid();
        Guid printerId = Guid.NewGuid();
        Guid planId = Guid.NewGuid();
        Guid scheduleId = Guid.NewGuid();
        Guid alertId = Guid.NewGuid();
        Guid logId = Guid.NewGuid();

        await using (AppDbContext seed = new(options))
        {
            _ = await seed.Database.EnsureCreatedAsync();
            await EnableSqliteForeignKeysAsync(seed.Database.GetDbConnection());

            _ = seed.Manufacturers.Add(new Manufacturer { Id = manufacturerId, Name = "TestCo" });
            _ = seed.PrinterModels.Add(new PrinterModel
            {
                Id = modelId,
                Name = "P",
                ManufacturerId = manufacturerId,
            });
            _ = seed.Printers.Add(new Printer
            {
                Id = printerId,
                Name = "P",
                ServerUrl = "http://p",
                ManufacturerId = manufacturerId,
                ModelId = modelId,
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
                Message = "Clean it",
            });
            _ = seed.MaintenanceLogs.Add(new MaintenanceLog
            {
                Id = logId,
                PrinterId = printerId,
                PrinterMaintenanceScheduleId = scheduleId,
                TaskName = "Cleaned filter",
                PerformedAt = DateTime.UtcNow,
            });
            _ = await seed.SaveChangesAsync();
        }

        await using (AppDbContext act = new(options))
        {
            await EnableSqliteForeignKeysAsync(act.Database.GetDbConnection());
            _ = await act.PrinterMaintenanceSchedules
                .Where(s => s.Id == scheduleId)
                .ExecuteDeleteAsync();
        }

        await using (AppDbContext assert = new(options))
        {
            await EnableSqliteForeignKeysAsync(assert.Database.GetDbConnection());

            (await assert.PrinterMaintenanceSchedules.CountAsync(s => s.Id == scheduleId)).Should().Be(0,
                "the schedule was directly deleted");

            MaintenanceAlert? alert = await assert.MaintenanceAlerts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == alertId);
            alert.Should().NotBeNull("alerts must outlive removed schedules");
            alert!.PrinterMaintenanceScheduleId.Should().BeNull(
                "the schedule link on the alert must be nulled by SetNull FK");
            alert.PrinterId.Should().Be(printerId, "the printer link on the alert must not be affected");

            MaintenanceLog? log = await assert.MaintenanceLogs.AsNoTracking().SingleOrDefaultAsync(l => l.Id == logId);
            log.Should().NotBeNull("logs must outlive removed schedules");
            log!.PrinterMaintenanceScheduleId.Should().BeNull(
                "the schedule link on the log must be nulled by SetNull FK");
            log.PrinterId.Should().Be(printerId, "the printer link on the log must not be affected");

            (await assert.Printers.CountAsync(p => p.Id == printerId)).Should().Be(1,
                "the printer must not be affected by an isolated schedule deletion");
        }
    }

    [Fact]
    public async Task RemoveAsync_WithPartOutputMappingsForSourceGcodeFiles_DeletesDirectMappingsThenGcodeFilesThenPrinter()
    {
        // F2 — regression against a real relational provider that would FK-fail without the
        // compensating direct-mapping delete. Seeds a printer, a source-attributed GcodeFile,
        // and a PartOutputMapping directly referencing that GcodeFile. The Restrict FK on
        // PartOutputMappings.GcodeFileId would abort the bulk GcodeFiles delete inside
        // RemoveAsync if the mapping weren't cleared first.
        await using SqliteConnection connection =
            new("Data Source=file:printer-remove-partoutput?mode=memory&cache=shared");
        await connection.OpenAsync();
        await EnableSqliteForeignKeysAsync(connection);
        DbContextOptions<AppDbContext> options = OptionsFor(connection);

        Guid manufacturerId = Guid.NewGuid();
        Guid modelId = Guid.NewGuid();
        Guid printerId = Guid.NewGuid();
        Guid folderId = Guid.NewGuid();
        Guid sourceGcodeFileId = Guid.NewGuid();
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
                Name = "source.gcode",
                FileName = "source.gcode",
                FilePath = "/tmp",
                FileHash = new string('c', 64),
                FileSizeBytes = 1,
                FolderId = folderId,
                SourcePrinterId = printerId,
                UploadedAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
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
            var repository = new EfPrintersRepository(act, NullSensitiveDataProtector.Instance);
            await repository.RemoveAsync(new Printer { Id = printerId }, CancellationToken.None);
        }

        await using (AppDbContext assert = new(options))
        {
            await EnableSqliteForeignKeysAsync(assert.Database.GetDbConnection());

            (await assert.PartOutputMappings.CountAsync(m => m.Id == directMappingId)).Should().Be(0,
                "the direct PartOutputMapping must be deleted BEFORE the source GcodeFile bulk delete — without this, the Restrict FK on PartOutputMappings.GcodeFileId would abort the whole removal");
            (await assert.GcodeFiles.CountAsync(g => g.Id == sourceGcodeFileId)).Should().Be(0,
                "the source-attributed GcodeFile must be bulk-deleted");
            (await assert.Printers.CountAsync(p => p.Id == printerId)).Should().Be(0,
                "the printer must be removed at the end");
        }
    }

    [Fact]
    public async Task RemoveAsync_WhenLaterStepFails_RollsBackEarlierCompensatingDeletes()
    {
        // F4 — failure-injection test. Uses a DbContext.SavingChanges interceptor to throw
        // at the FINAL Printers.Remove SaveChanges. Prior ExecuteDeleteAsync writes
        // (schedules, direct mappings, gcode files, print jobs, harvest ops) must all
        // roll back because the entire sequence runs inside a single IDbContextTransaction.
        await using SqliteConnection connection =
            new("Data Source=file:printer-remove-rollback?mode=memory&cache=shared");
        await connection.OpenAsync();
        await EnableSqliteForeignKeysAsync(connection);
        DbContextOptions<AppDbContext> options = OptionsFor(connection);

        Guid manufacturerId = Guid.NewGuid();
        Guid modelId = Guid.NewGuid();
        Guid printerId = Guid.NewGuid();
        Guid planId = Guid.NewGuid();
        Guid scheduleId = Guid.NewGuid();

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
            _ = seed.MaintenancePlans.Add(new MaintenancePlan { Id = planId, Name = "Plan" });
            _ = seed.PrinterMaintenanceSchedules.Add(new PrinterMaintenanceSchedule
            {
                Id = scheduleId,
                MaintenancePlanId = planId,
                PrinterId = printerId,
            });
            _ = await seed.SaveChangesAsync();
        }

        // Act — trigger failure on the final Printers SaveChanges via a SavingChanges hook.
        await using (AppDbContext act = new(options))
        {
            await EnableSqliteForeignKeysAsync(act.Database.GetDbConnection());
            act.SavingChanges += (sender, args) =>
            {
                DbContext ctx = (DbContext)sender!;
                if (ctx.ChangeTracker.Entries<Printer>().Any(e => e.State == EntityState.Deleted))
                {
                    throw new InvalidOperationException("simulated late failure on Printers.Remove SaveChanges");
                }
            };
            var repository = new EfPrintersRepository(act, NullSensitiveDataProtector.Instance);
            Func<Task> action = async () => await repository.RemoveAsync(new Printer { Id = printerId }, CancellationToken.None);
            _ = await action.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("simulated late failure*");
        }

        // Assert — schedule + printer both still present (rollback preserved the world).
        await using (AppDbContext assert = new(options))
        {
            await EnableSqliteForeignKeysAsync(assert.Database.GetDbConnection());
            (await assert.PrinterMaintenanceSchedules.CountAsync(s => s.Id == scheduleId)).Should().Be(1,
                "the schedule ExecuteDeleteAsync must have rolled back when the final Printers.Remove SaveChanges failed");
            (await assert.Printers.CountAsync(p => p.Id == printerId)).Should().Be(1,
                "the printer must remain because its Remove SaveChanges failed and the transaction rolled back");
        }
    }

    private static DbContextOptions<AppDbContext> OptionsFor(SqliteConnection connection)
        => new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

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

    /// <summary>
    /// Null-op protector — this test only exercises delete semantics, not encryption/decryption.
    /// </summary>
    private sealed class NullSensitiveDataProtector : ISensitiveDataProtector
    {
        public static NullSensitiveDataProtector Instance { get; } = new();
        public string? Protect(string? plainText) => plainText;
        public string? Unprotect(string? protectedText) => protectedText;
    }
}
