using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Farm.Web.Api.Tests.Infrastructure;

public sealed class TrackedMotionRetirementMigrationTests
{
    private const string PreviousMigration = "20260914014156_AddUserPrinterControlMode";
    private const string TrackingEventType = "PrintFarmer.Printer.ControlOperationUpdated.v1";
    private static readonly string[] RetiredOperations = ["async_motion", "home", "home_xy", "home_z", "move", "move_to"];

    [Fact]
    public async Task Migrate_RetiredMotionBarriers_ReleasesOnlyUnownedMotion()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using AppDbContext context = CreateContext(connection);
        IMigrator migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(PreviousMigration);

        var released = new List<PrinterDispatchState>();
        var retained = new List<PrinterDispatchState>();
        foreach (string operation in RetiredOperations)
        {
            released.Add(AddBarrier(context, operation));

            PrinterDispatchState stateJob = AddBarrier(context, operation);
            stateJob.ActiveJobId = Guid.NewGuid();
            retained.Add(stateJob);

            PrinterDispatchState stateAttempt = AddBarrier(context, operation);
            stateAttempt.ActiveDispatchAttemptId = Guid.NewGuid();
            retained.Add(stateAttempt);

            PrinterDispatchState physicalAttempt = AddBarrier(context, operation);
            physicalAttempt.PhysicalControlAttemptId = Guid.NewGuid();
            retained.Add(physicalAttempt);

            foreach (PrintJobStatus status in new[] { PrintJobStatus.Starting, PrintJobStatus.Printing, PrintJobStatus.Paused })
            {
                PrinterDispatchState occupied = AddBarrier(context, operation);
                context.PrintJobs.Add(new PrintJob
                {
                    Id = Guid.NewGuid(),
                    AssignedPrinterId = occupied.PrinterId,
                    Status = status,
                    Name = "existing job",
                });
                retained.Add(occupied);
            }

            foreach (DispatchAttemptOutcome outcome in new[] { DispatchAttemptOutcome.InProgress, DispatchAttemptOutcome.Unknown, DispatchAttemptOutcome.Accepted })
            {
                PrinterDispatchState dispatch = AddBarrier(context, operation);
                context.QueueDispatchAttempts.Add(new QueueDispatchAttempt
                {
                    Id = Guid.NewGuid(),
                    PrinterId = dispatch.PrinterId,
                    Outcome = outcome,
                    ClaimedAtUtc = DateTime.UtcNow,
                });
                retained.Add(dispatch);
            }

            PrinterDispatchState reconciliation = AddBarrier(context, operation);
            context.QueueDispatchAttempts.Add(new QueueDispatchAttempt
            {
                Id = Guid.NewGuid(),
                PrinterId = reconciliation.PrinterId,
                Outcome = DispatchAttemptOutcome.Rejected,
                RequiresReconciliation = true,
                TerminalAtUtc = DateTime.UtcNow,
                ClaimedAtUtc = DateTime.UtcNow,
            });
            retained.Add(reconciliation);
        }

        foreach (string operation in new[] { "pause", "resume", "cancel", "emergency_stop", "upload", "set_temperature" })
        {
            retained.Add(AddBarrier(context, operation));
        }

        PrinterDispatchState noCommand = AddBarrier(context, "async_motion");
        noCommand.PhysicalControlCommandId = null;
        retained.Add(noCommand);

        PrinterDispatchState historyOnly = AddBarrier(context, "async_motion");
        context.PrintJobs.AddRange(
            new PrintJob { Id = Guid.NewGuid(), AssignedPrinterId = historyOnly.PrinterId, Status = PrintJobStatus.Completed },
            new PrintJob { Id = Guid.NewGuid(), AssignedPrinterId = historyOnly.PrinterId, Status = PrintJobStatus.Queued });
        context.QueueDispatchAttempts.Add(new QueueDispatchAttempt
        {
            Id = Guid.NewGuid(),
            PrinterId = historyOnly.PrinterId,
            Outcome = DispatchAttemptOutcome.Accepted,
            TerminalAtUtc = DateTime.UtcNow,
            ClaimedAtUtc = DateTime.UtcNow,
        });
        released.Add(historyOnly);

        // The fixture is seeded through the current model, which maps columns added by later
        // migrations. Bridge them for the insert, then drop them so MigrateAsync still applies
        // every later migration exactly as a real upgrade would.
        _ = await context.Database.ExecuteSqlRawAsync(
            """ALTER TABLE "QueueDispatchAttempts" ADD COLUMN "BackendSenderSettledAtUtc" TEXT NULL;""");
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        _ = await context.Database.ExecuteSqlRawAsync(
            """ALTER TABLE "QueueDispatchAttempts" DROP COLUMN "BackendSenderSettledAtUtc";""");

        await migrator.MigrateAsync();

        Dictionary<Guid, PrinterDispatchState> actual = await context.PrinterDispatchStates.ToDictionaryAsync(state => state.PrinterId);
        foreach (PrinterDispatchState before in released)
        {
            PrinterDispatchState after = actual[before.PrinterId];
            after.PhysicalControlCommandId.Should().BeNull();
            after.PhysicalControlAttemptId.Should().BeNull();
            after.PhysicalControlOperation.Should().BeNull();
            after.PhysicalControlActorSubject.Should().BeNull();
            after.PhysicalControlStartedAtUtc.Should().BeNull();
            after.PhysicalControlRequiresReconciliation.Should().BeFalse();
            after.Revision.Should().Be(before.Revision + 1);
            after.QueueRevision.Should().Be(before.QueueRevision);
            after.AcknowledgedJobId.Should().Be(before.AcknowledgedJobId);
            after.BedPreConfirmed.Should().Be(before.BedPreConfirmed);
            after.AutoDispatchState.Should().Be(before.AutoDispatchState);
        }

        foreach (PrinterDispatchState before in retained)
        {
            actual[before.PrinterId].Should().BeEquivalentTo(before, options => options.Excluding(state => state.Printer));
        }

        context.ChangeTracker.Clear();
        await migrator.MigrateAsync();
        (await context.PrinterDispatchStates.ToListAsync()).Should().BeEquivalentTo(actual.Values, options => options.Excluding(state => state.Printer));
    }

    [Fact]
    public async Task Migrate_TrackingHistory_ArchivesEvidenceAndDeadLettersOnlyUnpublishedTrackingEvents()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using AppDbContext context = CreateContext(connection);
        IMigrator migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(PreviousMigration);

        string[] states = ["Queued", "Sending", "Running", "Unknown", "Recovering", "Recovered", "Succeeded", "Failed"];
        foreach (string state in states)
        {
            Guid operationId = Guid.NewGuid();
            Guid printerId = Guid.NewGuid();
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "PrinterControlOperations"
                    ("Id", "PrinterId", "Kind", "State", "Revision", "ActorSubject", "NormalizedIntent",
                     "PrinterConfigurationIdentity", "CreatedAtUtc", "UpdatedAtUtc", "CompletionEvidence",
                     "SenderIsolation", "EmergencyStopInFlight", "FailureCode", "SendCommittedAtUtc")
                VALUES ({operationId}, {printerId}, 'Home', {state}, 9, 'existing-actor', 'home:all',
                        'configuration', {DateTime.UtcNow}, {DateTime.UtcNow}, 'None', 'Unknown', 1,
                        'existing_failure', {DateTime.UtcNow});
                """);
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "PrinterEmergencyStopAttempts"
                    ("Id", "OperationId", "PrinterId", "ActorSubject", "ConfigurationIdentity", "Delivery", "CreatedAtUtc")
                VALUES ({Guid.NewGuid()}, {operationId}, {printerId}, 'existing-actor', 'configuration', 'Unknown', {DateTime.UtcNow});
                """);
        }

        var events = new List<QueueDispatchOutbox>();
        foreach (QueueOutboxEventStatus status in Enum.GetValues<QueueOutboxEventStatus>())
        {
            foreach (string eventType in new[] { TrackingEventType, "PrintFarmer.Queue.PhysicalControlUnknown.v1" })
            {
                var item = new QueueDispatchOutbox
                {
                    Id = Guid.NewGuid(),
                    Sequence = events.Count + 1,
                    AggregateId = Guid.NewGuid(),
                    AggregateType = "Printer",
                    EventType = eventType,
                    Status = status,
                    PayloadJson = """{"operationId":"historical-evidence"}""",
                    LastError = "existing delivery error",
                    AttemptCount = 4,
                    RetryAfterUtc = DateTime.UtcNow.AddHours(1),
                    CreatedAtUtc = DateTime.UtcNow.AddHours(-1),
                    CompletedAtUtc = status is QueueOutboxEventStatus.Published or QueueOutboxEventStatus.DeadLettered ? DateTime.UtcNow : null,
                };
                events.Add(item);
                context.QueueDispatchOutbox.Add(item);
            }
        }

        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        await migrator.MigrateAsync();

        Dictionary<Guid, QueueDispatchOutbox> actual = await context.QueueDispatchOutbox.ToDictionaryAsync(item => item.Id);
        foreach (QueueDispatchOutbox before in events)
        {
            QueueDispatchOutbox after = actual[before.Id];
            if (before.EventType == TrackingEventType && before.Status is QueueOutboxEventStatus.Pending or QueueOutboxEventStatus.Processing)
            {
                after.Status.Should().Be(QueueOutboxEventStatus.DeadLettered);
                after.CompletedAtUtc.Should().NotBeNull();
                after.RetryAfterUtc.Should().BeNull();
                after.Revision.Should().Be(before.Revision + 1);
                after.PayloadJson.Should().Be(before.PayloadJson);
                after.LastError.Should().Be(before.LastError);
                after.AttemptCount.Should().Be(before.AttemptCount);
            }
            else
            {
                after.Should().BeEquivalentTo(before);
            }
        }

        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT "State", "Revision", "FailureCode", "EmergencyStopInFlight", "SendCommittedAtUtc"
            FROM "RetiredPrinterControlOperations" ORDER BY "State";
            """;
        var archivedStates = new List<string>();
        await using (SqliteDataReader reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                archivedStates.Add(reader.GetString(0));
                reader.GetInt64(1).Should().Be(9);
                reader.GetString(2).Should().Be("existing_failure");
                reader.GetBoolean(3).Should().BeTrue();
                reader.IsDBNull(4).Should().BeFalse();
            }
        }

        archivedStates.Should().BeEquivalentTo(states);
        command.CommandText = """SELECT COUNT(*) FROM "RetiredPrinterEmergencyStopAttempts" WHERE "Delivery" = 'Unknown';""";
        Convert.ToInt64(await command.ExecuteScalarAsync()).Should().Be(states.Length);
        command.CommandText = """
            SELECT COUNT(*) FROM sqlite_master
            WHERE type = 'table' AND name IN ('PrinterControlOperations', 'PrinterEmergencyStopAttempts');
            """;
        Convert.ToInt64(await command.ExecuteScalarAsync()).Should().Be(0);
        command.CommandText = "PRAGMA foreign_key_check;";
        (await command.ExecuteScalarAsync()).Should().BeNull();
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("postgres")]
    [InlineData("sqlserver")]
    public void Model_RetiredTracking_AllProviderSnapshotsMatchAndRollbackCannotReplay(string provider)
    {
        using AppDbContext context = provider switch
        {
            "postgres" => new Farm.Migrations.PostgreSQL.DesignTimeDbContextFactory().CreateDbContext([]),
            "sqlserver" => new Farm.Migrations.SqlServer.DesignTimeDbContextFactory().CreateDbContext([]),
            _ => new Farm.Migrations.Sqlite.DesignTimeDbContextFactory().CreateDbContext([]),
        };
        context.Database.HasPendingModelChanges().Should().BeFalse();
        context.Model.GetEntityTypes().Should().NotContain(entity => entity.Name.Contains("PrinterControlOperation") || entity.Name.Contains("PrinterEmergencyStopAttempt"));

        IMigrationsAssembly assembly = context.GetService<IMigrationsAssembly>();
        var retirement = assembly.Migrations.Single(entry => entry.Key.EndsWith("_RetireTrackedPrinterMotion", StringComparison.Ordinal));
        Migration migration = assembly.CreateMigration(retirement.Value, context.Database.ProviderName!);
        Action rollback = () => _ = migration.DownOperations;
        rollback.Should().Throw<NotSupportedException>();
        string previous = assembly.Migrations.Keys.Where(id => string.CompareOrdinal(id, retirement.Key) < 0).Order(StringComparer.Ordinal).Last();
        string script = context.GetService<IMigrator>().GenerateScript(previous, retirement.Key);
        script.Should().Contain("RetiredPrinterControlOperations").And.Contain("RetiredPrinterEmergencyStopAttempts");
    }

    private static AppDbContext CreateContext(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection, options => options.MigrationsAssembly("Farm.Migrations.Sqlite"))
            .Options);

    private static PrinterDispatchState AddBarrier(AppDbContext context, string operation)
    {
        Manufacturer manufacturer = context.Manufacturers.Local.FirstOrDefault()
            ?? new Manufacturer { Id = Guid.NewGuid(), Name = "Retirement fixture" };
        PrinterModel model = context.PrinterModels.Local.FirstOrDefault()
            ?? new PrinterModel { Id = Guid.NewGuid(), Name = "Retirement model", ManufacturerId = manufacturer.Id, Manufacturer = manufacturer };
        var printer = new Printer
        {
            Id = Guid.NewGuid(),
            Name = $"retirement-{operation}",
            ServerUrl = $"http://{Guid.NewGuid():N}.example.test",
            ManufacturerId = manufacturer.Id,
            Manufacturer = manufacturer,
            ModelId = model.Id,
            Model = model,
        };
        var state = new PrinterDispatchState
        {
            PrinterId = printer.Id,
            Printer = printer,
            PhysicalControlCommandId = Guid.NewGuid(),
            PhysicalControlOperation = operation,
            PhysicalControlActorSubject = "existing-actor",
            PhysicalControlStartedAtUtc = DateTime.UtcNow.AddDays(-1),
            PhysicalControlRequiresReconciliation = true,
            Revision = 10,
            QueueRevision = 27,
            AcknowledgedJobId = Guid.NewGuid(),
            BedPreConfirmed = true,
        };
        context.Printers.Add(printer);
        context.PrinterDispatchStates.Add(state);
        return state;
    }
}
