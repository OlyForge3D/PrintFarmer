using System.Text.Json;
using System.Text.RegularExpressions;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Farm.Web.Api.Tests.Infrastructure;

public sealed class GcodeHarvestRetirementMigrationTests
{
    private const string PreviousMigration = "20261025192142_AddPrinterFileCatalog";
    private static readonly string[] RetiredTables =
    [
        "GcodeHarvestQueueItems",
        "HarvestFileGcodeFileMappings",
        "HarvestDiscoveredFiles",
        "GcodeHarvestOperations",
    ];

    [Fact]
    public async Task Migrate_PopulatedLegacyHarvest_DropsOnlyRetiredTablesAndGrants()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        await connection.OpenAsync();
        await using AppDbContext context = CreateContext(connection);
        IMigrator migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(PreviousMigration);

        DateTime timestamp = new(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc);
        Guid manufacturerId = Guid.NewGuid();
        Guid modelId = Guid.NewGuid();
        Guid printerId = Guid.NewGuid();
        Guid folderId = Guid.NewGuid();
        Guid gcodeId = Guid.NewGuid();
        Guid locationId = Guid.NewGuid();
        Guid roleId = Guid.NewGuid();
        Guid adminRoleId = Guid.NewGuid();
        Guid readActionId = Guid.NewGuid();
        Guid createActionId = Guid.NewGuid();
        Guid adminActionId = Guid.NewGuid();
        Guid harvestResourceId = Guid.NewGuid();
        Guid libraryResourceId = Guid.NewGuid();
        Guid printerFilesResourceId = Guid.NewGuid();
        Guid lookalikeResourceId = Guid.NewGuid();
        Guid operationId = Guid.NewGuid();
        Guid discoveredFileId = Guid.NewGuid();

        await InsertAsync(connection, Table<Manufacturer>(context),
            ("Id", manufacturerId), ("Name", "Retirement manufacturer"));
        await InsertAsync(connection, Table<PrinterModel>(context),
            ("Id", modelId), ("Name", "Retirement model"), ("ManufacturerId", manufacturerId));
        await InsertAsync(connection, Table<Printer>(context),
            ("Id", printerId), ("Name", "Retirement printer"), ("ServerUrl", "http://printer.invalid"),
            ("ManufacturerId", manufacturerId), ("ModelId", modelId));
        await InsertAsync(connection, Table<FolderNode>(context),
            ("Id", folderId), ("Path", "/"), ("FolderType", "gcode"), ("CreatedAt", timestamp));
        await InsertAsync(connection, Table<GcodeFile>(context),
            ("Id", gcodeId), ("Name", "retained.gcode"), ("FileName", "retained.gcode"),
            ("FolderId", folderId), ("FilePath", "library"), ("FileSizeBytes", 128L),
            ("FileHash", new string('a', 64)), ("UploadedAt", timestamp),
            ("CreatedAt", timestamp), ("UpdatedAt", timestamp), ("Source", (int)GcodeSource.Harvested),
            ("SourcePrinterId", printerId), ("OriginalPrinterPath", "gcodes/retained.gcode"),
            ("LastSeenOnPrinter", timestamp), ("ContentSha256", new string('a', 64)));
        await InsertAsync(connection, Table<PrinterFileLocation>(context),
            ("Id", locationId), ("SourcePrinterId", printerId), ("RegisteredPrinterId", printerId),
            ("StorageRoot", "gcodes"), ("RelativePath", "retained.gcode"),
            ("IdentityKey", new string('b', 64)), ("DisplayName", "retained.gcode"),
            ("FirstSeenAtUtc", timestamp), ("LastSeenAtUtc", timestamp), ("Availability", "Available"));
        await InsertAsync(connection, Table<PrinterFileOrigin>(context),
            ("Id", Guid.NewGuid()), ("OperationId", Guid.NewGuid()), ("SourcePrinterId", printerId),
            ("ActorId", Guid.NewGuid()), ("LocationId", locationId), ("GcodeFileId", gcodeId),
            ("StorageRoot", "gcodes"), ("RelativePath", "retained.gcode"),
            ("ContentSha256", new string('a', 64)), ("SizeBytes", 128L),
            ("Verification", "UnverifiedSnapshot"), ("CapturedAtUtc", timestamp));
        await InsertAsync(connection, Table<PrintJob>(context),
            ("Id", Guid.NewGuid()), ("Name", "Harvested printed parts"),
            ("HarvestedAt", timestamp), ("HarvestOperationKey", "retained-parts-harvest"),
            ("HarvestedByUserId", "operator"));

        await InsertAsync(connection, Table<Role>(context),
            ("Id", roleId), ("Name", "retirement_operator"), ("DisplayName", "Operator"),
            ("IsActive", 1), ("CreatedAt", timestamp), ("UpdatedAt", timestamp));
        await InsertAsync(connection, Table<Role>(context),
            ("Id", adminRoleId), ("Name", "retirement_admin"), ("DisplayName", "Admin"),
            ("IsSystemRole", 1), ("IsActive", 1), ("CreatedAt", timestamp), ("UpdatedAt", timestamp));
        foreach ((Guid id, string name) in new[] { (readActionId, "read"), (createActionId, "create"), (adminActionId, "admin") })
        {
            await InsertAsync(connection, Table<UserAction>(context),
                ("Id", id), ("Name", name), ("DisplayName", name),
                ("CreatedAt", timestamp), ("UpdatedAt", timestamp));
        }

        foreach ((Guid id, string name) in new[]
        {
            (harvestResourceId, "gcode_harvest"),
            (libraryResourceId, "gcode_library"),
            (printerFilesResourceId, "printer_files"),
            (lookalikeResourceId, "gcode_harvest_archive"),
        })
        {
            await InsertAsync(connection, Table<Resource>(context),
                ("Id", id), ("Name", name), ("DisplayName", name), ("ResourceType", "test"),
                ("IsActive", 1), ("CreatedAt", timestamp), ("UpdatedAt", timestamp));
        }

        foreach ((Guid role, Guid resource, Guid action) in new[]
        {
            (roleId, harvestResourceId, createActionId),
            (roleId, harvestResourceId, adminActionId),
            (adminRoleId, harvestResourceId, adminActionId),
            (roleId, libraryResourceId, readActionId),
            (roleId, printerFilesResourceId, readActionId),
            (roleId, lookalikeResourceId, createActionId),
        })
        {
            await InsertAsync(connection, Table<RolePermission>(context),
                ("Id", Guid.NewGuid()), ("RoleId", role), ("ResourceId", resource),
                ("ActionId", action), ("Granted", 1), ("CreatedAt", timestamp));
        }

        await InsertAsync(connection, "GcodeHarvestOperations",
            ("Id", operationId), ("PrinterId", printerId), ("StartedAt", timestamp), ("Status", 0));
        await InsertAsync(connection, "GcodeHarvestQueueItems",
            ("Id", Guid.NewGuid()), ("PrinterId", printerId), ("Parameters", "{}"),
            ("QueuedAt", timestamp), ("Status", 0));
        await InsertAsync(connection, "HarvestDiscoveredFiles",
            ("Id", discoveredFileId), ("HarvestOperationId", operationId),
            ("FileName", "retained.gcode"), ("FilePath", "gcodes/retained.gcode"),
            ("DiscoveredAt", timestamp), ("Size", 128L), ("Status", 0));
        await InsertAsync(connection, "HarvestFileGcodeFileMappings",
            ("Id", Guid.NewGuid()), ("HarvestDiscoveredFileId", discoveredFileId),
            ("GcodeFileId", gcodeId), ("CreatedAt", timestamp));

        foreach (string table in RetiredTables)
        {
            (await ReadRowsAsync(connection, table)).Should().ContainSingle();
        }

        const string retainedResources = "\"Name\" <> 'gcode_harvest'";
        const string retiredGrants = "\"ResourceId\" IN (SELECT \"Id\" FROM \"Resources\" WHERE \"Name\" = 'gcode_harvest')";
        (await ReadRowsAsync(connection, "Resources", "\"Name\" = 'gcode_harvest'")).Should().ContainSingle();
        (await ReadRowsAsync(connection, "RolePermissions", retiredGrants)).Should().HaveCount(3);
        List<string> expectedResources = await ReadRowsAsync(connection, "Resources", retainedResources);
        List<string> expectedGrants = await ReadRowsAsync(connection, "RolePermissions", $"NOT ({retiredGrants})");
        string[] retainedTables =
        [
            Table<Manufacturer>(context), Table<PrinterModel>(context), Table<Printer>(context),
            Table<FolderNode>(context), Table<GcodeFile>(context), Table<PrinterFileLocation>(context),
            Table<PrinterFileOrigin>(context), Table<PrintJob>(context),
            Table<Role>(context), Table<UserAction>(context), Table<PartHarvestOutputSnapshot>(context),
        ];
        var expectedRows = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (string table in retainedTables)
        {
            expectedRows.Add(table, await ReadRowsAsync(connection, table));
        }

        await AssertForeignKeysAsync(connection);
        string retirementId = RetirementId(context);
        await migrator.MigrateAsync(retirementId);
        await AssertRetiredTablesAbsentAsync(connection);
        foreach (string table in retainedTables)
        {
            (await ReadRowsAsync(connection, table)).Should().Equal(expectedRows[table], $"{table} must be preserved");
        }

        (await ReadRowsAsync(connection, "Resources")).Should().Equal(expectedResources);
        (await ReadRowsAsync(connection, "RolePermissions")).Should().Equal(expectedGrants);
        await AssertForeignKeysAsync(connection);
        await migrator.MigrateAsync(retirementId);
        (await ReadRowsAsync(connection, "Resources")).Should().Equal(expectedResources);
        (await ReadRowsAsync(connection, "RolePermissions")).Should().Equal(expectedGrants);
    }

    [Fact]
    public async Task Migrate_NeverHarvestedDatabase_RetiresLegacySchema()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        await connection.OpenAsync();
        await using AppDbContext context = CreateContext(connection);
        IMigrator migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(PreviousMigration);
        await migrator.MigrateAsync();
        await AssertRetiredTablesAbsentAsync(connection);
        await AssertForeignKeysAsync(connection);
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("postgres")]
    [InlineData("sqlserver")]
    public void Model_RetiredHarvest_ProviderSnapshotsMatchAndRollbackCannotReplay(string provider)
    {
        using AppDbContext context = provider switch
        {
            "sqlite" => new Farm.Migrations.Sqlite.DesignTimeDbContextFactory().CreateDbContext([]),
            "postgres" => new Farm.Migrations.PostgreSQL.DesignTimeDbContextFactory().CreateDbContext([]),
            "sqlserver" => new Farm.Migrations.SqlServer.DesignTimeDbContextFactory().CreateDbContext([]),
            _ => throw new ArgumentOutOfRangeException(nameof(provider)),
        };
        context.Database.HasPendingModelChanges().Should().BeFalse();
        context.Model.GetEntityTypes().Select(entity => entity.GetTableName()).Should().NotIntersectWith(RetiredTables);
        context.Model.FindEntityType(typeof(GcodeFile)).Should().NotBeNull();
        context.Model.FindEntityType(typeof(PartHarvestOutputSnapshot)).Should().NotBeNull();
        context.Model.FindEntityType(typeof(PrinterFileOrigin)).Should().NotBeNull();
        IMigrationsAssembly assembly = context.GetService<IMigrationsAssembly>();
        string retirementId = RetirementId(context);
        Migration migration = assembly.CreateMigration(assembly.Migrations[retirementId], context.Database.ProviderName!);
        Action rollback = () => _ = migration.DownOperations;
        rollback.Should().Throw<NotSupportedException>().WithMessage("*pre-upgrade backup*");
        string previousId = assembly.Migrations.Keys
            .Where(id => string.CompareOrdinal(id, retirementId) < 0)
            .Order(StringComparer.Ordinal)
            .Last();
        string script = context.GetService<IMigrator>().GenerateScript(previousId, retirementId);
        Regex.Matches(script, "DELETE FROM", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)).Should().HaveCount(2);
        script.Should().Contain("gcode_harvest").And.NotContain("DROP COLUMN");
        script.IndexOf("RolePermissions", StringComparison.Ordinal).Should()
            .BeLessThan(script.LastIndexOf("Resources", StringComparison.Ordinal));
        Regex.Matches(script, """DROP TABLE [\["]([^"\]]+)[\]"];""", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))
            .Select(match => match.Groups[1].Value).Should().Equal(RetiredTables);
    }

    private static AppDbContext CreateContext(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection, options => options.MigrationsAssembly("Farm.Migrations.Sqlite")).Options);

    private static string Table<TEntity>(AppDbContext context) =>
        context.Model.FindEntityType(typeof(TEntity))!.GetTableName()!;

    private static string RetirementId(AppDbContext context) =>
        context.GetService<IMigrationsAssembly>().Migrations.Keys.Single(id => id.EndsWith("_RetireGcodeHarvest", StringComparison.Ordinal));

    // Seed the historical schema, not today's entity shape, so later model additions cannot change this upgrade fixture.
    private static async Task InsertAsync(SqliteConnection connection, string table, params (string Column, object? Value)[] values)
    {
        var supplied = values.ToDictionary(value => value.Column, value => value.Value, StringComparer.Ordinal);
        var columns = new List<string>();
        await using (SqliteCommand schema = connection.CreateCommand())
        {
            schema.CommandText = "SELECT name, type, \"notnull\", dflt_value FROM pragma_table_info($table);";
            schema.Parameters.AddWithValue("$table", table);
            await using SqliteDataReader reader = await schema.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                string column = reader.GetString(0);
                columns.Add(column);
                if (!supplied.ContainsKey(column) && reader.GetInt64(2) != 0 && reader.IsDBNull(3))
                {
                    supplied.Add(column, reader.GetString(1) switch
                    {
                        "INTEGER" => 0L,
                        "REAL" => 0.0,
                        "BLOB" => Array.Empty<byte>(),
                        _ => string.Empty,
                    });
                }
            }
        }

        columns.Should().NotBeEmpty($"{table} must exist before retirement");
        supplied.Keys.Should().BeSubsetOf(columns);
        await using SqliteCommand command = connection.CreateCommand();
        string[] names = supplied.Keys.ToArray();
        string[] parameters = names.Select((_, index) => $"$p{index}").ToArray();
        command.CommandText = $"INSERT INTO {Quote(table)} ({string.Join(", ", names.Select(Quote))}) VALUES ({string.Join(", ", parameters)});";
        for (int index = 0; index < names.Length; index++)
        {
            command.Parameters.AddWithValue(parameters[index], supplied[names[index]] ?? DBNull.Value);
        }

        (await command.ExecuteNonQueryAsync()).Should().Be(1);
    }

    private static async Task<List<string>> ReadRowsAsync(SqliteConnection connection, string table, string predicate = "1 = 1")
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT * FROM {Quote(table)} WHERE {predicate} ORDER BY \"Id\";";
        await using SqliteDataReader reader = await command.ExecuteReaderAsync();
        var rows = new List<string>();
        while (await reader.ReadAsync())
        {
            var values = new object?[reader.FieldCount];
            for (int index = 0; index < values.Length; index++)
            {
                values[index] = reader.IsDBNull(index) ? null : reader.GetValue(index);
            }

            rows.Add(JsonSerializer.Serialize(values));
        }

        return rows;
    }

    private static async Task AssertRetiredTablesAbsentAsync(SqliteConnection connection)
    {
        foreach (string table in RetiredTables)
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name = $name;";
            command.Parameters.AddWithValue("$name", table);
            (await command.ExecuteScalarAsync()).Should().BeNull($"{table} must be dropped");
        }
    }

    private static async Task AssertForeignKeysAsync(SqliteConnection connection)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_key_check;";
        (await command.ExecuteScalarAsync()).Should().BeNull();
    }

    private static string Quote(string identifier) => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
}
