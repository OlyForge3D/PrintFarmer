using System;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.PartsInventory;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Farm.Infrastructure.Tests.Repositories.PartsInventory;

/// <summary>
/// Behaviour tests for <see cref="EfPartOutputMappingRepository.DeleteDirectMappingsForGcodeFileAsync"/>
/// under the Dallas cascade adjudication for #953. The direct
/// <c>FK_PartOutputMappings_GcodeFiles_GcodeFileId</c> is now <c>Restrict</c> (not Cascade)
/// to break the SQL Server 1785 multi-cascading-path graph GcodeFiles ⇒ PartOutputMappings
/// via {direct, via PrintProjectFiles}. Callers must explicitly delete direct mappings
/// before removing the parent GcodeFile.
///
/// The method must:
///  1) Delete every mapping whose direct source is the given GcodeFileId (i.e.,
///     <c>GcodeFileId</c> matches, not <c>PrintProjectFileId</c>).
///  2) Leave mappings whose reference is via <c>PrintProjectFileId</c> untouched — those
///     cascade normally when the PrintProjectFile itself is deleted.
///  3) Leave mappings for other GcodeFiles untouched.
/// </summary>
public sealed class EfPartOutputMappingRepositoryDirectDeletionTests
{
    [Fact]
    public async Task DeleteDirectMappingsForGcodeFileAsync_RemovesGcodeFileIdMappings_PreservesProjectFileMappings()
    {
        await using SqliteConnection connection =
            new("Data Source=file:partoutput-direct-delete?mode=memory&cache=shared");
        await connection.OpenAsync();
        await EnableSqliteForeignKeysAsync(connection);
        DbContextOptions<AppDbContext> options = OptionsFor(connection);

        Guid manufacturerId = Guid.NewGuid();
        Guid modelId = Guid.NewGuid();
        Guid printerId = Guid.NewGuid();
        Guid folderId = Guid.NewGuid();
        Guid targetGcodeFileId = Guid.NewGuid();
        Guid unrelatedGcodeFileId = Guid.NewGuid();
        Guid printProjectId = Guid.NewGuid();
        Guid printProjectFileId = Guid.NewGuid();
        Guid partInventoryIdA = Guid.NewGuid();
        Guid partInventoryIdB = Guid.NewGuid();
        Guid partInventoryIdC = Guid.NewGuid();
        Guid directMappingForTargetId = Guid.NewGuid();
        Guid directMappingForUnrelatedId = Guid.NewGuid();
        Guid projectFileMappingId = Guid.NewGuid();

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
                Id = targetGcodeFileId,
                Name = "target.gcode",
                FileName = "target.gcode",
                FilePath = "/tmp",
                FileHash = new string('a', 64),
                FileSizeBytes = 1,
                FolderId = folderId,
                UploadedAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            _ = seed.GcodeFiles.Add(new GcodeFile
            {
                Id = unrelatedGcodeFileId,
                Name = "unrelated.gcode",
                FileName = "unrelated.gcode",
                FilePath = "/tmp",
                FileHash = new string('b', 64),
                FileSizeBytes = 1,
                FolderId = folderId,
                UploadedAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            _ = seed.PrintProjects.Add(new PrintProject { Id = printProjectId, Name = "Project" });
            _ = seed.PrintProjectFiles.Add(new PrintProjectFile
            {
                Id = printProjectFileId,
                PrintProjectId = printProjectId,
                GcodeFileId = targetGcodeFileId,
            });
            _ = seed.PartInventories.Add(new PartInventory { Id = partInventoryIdA, Sku = "SKU-A", Name = "A" });
            _ = seed.PartInventories.Add(new PartInventory { Id = partInventoryIdB, Sku = "SKU-B", Name = "B" });
            _ = seed.PartInventories.Add(new PartInventory { Id = partInventoryIdC, Sku = "SKU-C", Name = "C" });

            // Direct mapping FOR the target GcodeFile — MUST be deleted.
            _ = seed.PartOutputMappings.Add(new PartOutputMapping
            {
                Id = directMappingForTargetId,
                GcodeFileId = targetGcodeFileId,
                PrintProjectFileId = null,
                PartInventoryId = partInventoryIdA,
                Quantity = 1,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });

            // Direct mapping for an UNRELATED GcodeFile — MUST be preserved.
            _ = seed.PartOutputMappings.Add(new PartOutputMapping
            {
                Id = directMappingForUnrelatedId,
                GcodeFileId = unrelatedGcodeFileId,
                PrintProjectFileId = null,
                PartInventoryId = partInventoryIdB,
                Quantity = 1,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });

            // Project-file-referenced mapping (whose GcodeFileId is NULL) — MUST be preserved.
            // Its indirect linkage to the target GcodeFile via PrintProjectFile is handled
            // separately when PrintProjectFile is deleted.
            _ = seed.PartOutputMappings.Add(new PartOutputMapping
            {
                Id = projectFileMappingId,
                GcodeFileId = null,
                PrintProjectFileId = printProjectFileId,
                PartInventoryId = partInventoryIdC,
                Quantity = 1,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            _ = await seed.SaveChangesAsync();
        }

        await using (AppDbContext act = new(options))
        {
            await EnableSqliteForeignKeysAsync(act.Database.GetDbConnection());
            var repo = new EfPartOutputMappingRepository(act);
            await repo.DeleteDirectMappingsForGcodeFileAsync(targetGcodeFileId, CancellationToken.None);
        }

        await using (AppDbContext assert = new(options))
        {
            await EnableSqliteForeignKeysAsync(assert.Database.GetDbConnection());

            (await assert.PartOutputMappings.CountAsync(m => m.Id == directMappingForTargetId)).Should().Be(0,
                "the direct mapping for the target GcodeFile must be deleted so the parent GcodeFile can be removed under the Restrict FK");
            (await assert.PartOutputMappings.CountAsync(m => m.Id == directMappingForUnrelatedId)).Should().Be(1,
                "the direct mapping for the unrelated GcodeFile must be preserved");
            (await assert.PartOutputMappings.CountAsync(m => m.Id == projectFileMappingId)).Should().Be(1,
                "the PrintProjectFile-referenced mapping must be preserved — it cascades separately when its PrintProjectFile is deleted");
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
}
