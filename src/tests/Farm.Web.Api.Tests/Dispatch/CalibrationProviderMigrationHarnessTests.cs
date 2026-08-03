// <copyright file="CalibrationProviderMigrationHarnessTests.cs" company="OlyForge3D">
// Copyright (c) OlyForge3D. All rights reserved.
// </copyright>

using System.Text.Json;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.Dtos.PrintQueue;
using Farm.Infrastructure.Services;
using Farm.Infrastructure.Services.Interfaces;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.Queue;
using Farm.Infrastructure.Services.Queue.Dispatch;
using Farm.Infrastructure.Services.SignalR;
using Farm.Web.Api.Tests.TestInfrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Dispatch;

/// <summary>
/// Provider-correct migration and model-consistency harnesses for the calibration queue
/// dispatch schema added by issue #900.
///
/// The PostgreSQL and SQL Server tests are skipped unless the corresponding connection
/// strings are provided via environment variables, so they pass in environments without
/// local containers while remaining runnable in CI with real databases.
///
/// The SQLite test always runs (no external dependency).
///
/// The tests verify:
/// 1. All pending migrations can be applied without error using REAL migrations
///    (<c>MigrateAsync()</c>) on every provider — never <c>EnsureCreated</c>.
/// 2. After migration, the <c>OutboxSequenceState</c> seed row (Id=1, NextSequence=0) is present.
/// 3. The <c>QueueDispatchOutbox</c>, <c>QueueDispatchAttempts</c>, and
///    <c>PrinterDispatchState</c> tables exist and are queryable.
/// 4. Backfill: existing <c>PrintJob</c> rows have <c>JobKind = Standard</c> with all
///    nullable calibration fields null — no ambiguous legacy flag becomes a valid ack or lease.
/// 5. The filtered unique index on <c>(IdempotencyScope, IdempotencyKey)</c> is enforced
///    for active calibration jobs.
/// </summary>
[Collection(ProviderDatabaseTestCollection.Name)]
public class CalibrationProviderMigrationHarnessTests
{
    // Environment variable names for optional provider connection strings.
    private const string PostgresConnEnvVar = "PFARM_TEST_POSTGRES_CONN";
    private const string SqlServerConnEnvVar = "PFARM_TEST_SQLSERVER_CONN";

    // Provider labels used for assertion messages AND for the provider-specific
    // RowVersion branch below. Shared constants so a rename cannot silently
    // desynchronise the call site from the comparison and skip the assertion.
    private const string PostgresProviderLabel = "PostgreSQL";
    private const string SqlServerProviderLabel = "SQL Server";

    /// <summary>
    /// Creates an AppDbContext backed by the given SQLite connection string with
    /// FK constraints disabled. This matches the test pattern used by
    /// <see cref="CalibrationQueueConcurrencyTests"/> so tests can insert entities
    /// in arbitrary order without FK failures.
    /// </summary>
    private static AppDbContext CreateSqliteContext(string connString)
    {
        DbContextOptions<AppDbContext> opts = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connString, sqlite => sqlite.MigrationsAssembly("Farm.Migrations.Sqlite"))
            .Options;
        var ctx = new AppDbContext(opts);
        ctx.Database.ExecuteSqlRaw("PRAGMA foreign_keys = OFF;");
        return ctx;
    }

    // =========================================================================
    // SQLite — always runs (no external containers required)
    // =========================================================================

    [Fact]
    [Trait("Category", "DbHeavy")]
    public async Task SQLite_CalibrationQueueDispatchSchema_CreatesCleanly()
    {
        string dbName = $"pfarm_mig_{Guid.NewGuid():N}";
        string connString = $"Data Source=file:{dbName}?mode=memory&cache=shared;Foreign Keys=False";

        using var keepAlive = new SqliteConnection(connString);
        keepAlive.Open();

        await using var ctx = CreateSqliteContext(connString);

        // REAL migrations — never EnsureCreated (issue #900, defect 14).
        await ctx.Database.MigrateAsync();

        IEnumerable<string> applied = await ctx.Database.GetAppliedMigrationsAsync();
        applied.Should().Contain(
            m => m.EndsWith("InitialV2", StringComparison.Ordinal),
            "the squashed baseline migration must be applied");

        (await ctx.Database.GetPendingMigrationsAsync()).Should().BeEmpty(
            "no pending migrations may remain after MigrateAsync");

        // Seed row must exist after schema creation.
        OutboxSequenceState seqState = await ctx.OutboxSequenceStates.SingleAsync();
        seqState.Id.Should().Be(1, "OutboxSequenceState seed must use Id=1");
        seqState.NextSequence.Should().Be(0, "OutboxSequenceState seed must start at NextSequence=0");

        // Key calibration tables must be queryable.
        (await ctx.QueueDispatchOutbox.LongCountAsync()).Should().Be(0);
        (await ctx.QueueDispatchAttempts.LongCountAsync()).Should().Be(0);
        (await ctx.PrinterDispatchStates.LongCountAsync()).Should().Be(0);
        (await ctx.QueueOperationAudits.LongCountAsync()).Should().Be(0);
    }

    [Fact]
    [Trait("Category", "DbHeavy")]
    public async Task SQLite_PrintJobBackfill_LegacyJobsDefaultToStandard_NullCalibrationFields()
    {
        string dbName = $"pfarm_backfill_{Guid.NewGuid():N}";
        string connString = $"Data Source=file:{dbName}?mode=memory&cache=shared;Foreign Keys=False";

        using var keepAlive = new SqliteConnection(connString);
        keepAlive.Open();

        await using var seedCtx = CreateSqliteContext(connString);
        await seedCtx.Database.MigrateAsync();

        // Seed a minimal PrintJob to simulate a pre-calibration-feature row.
        var mfr = new Manufacturer { Id = Guid.NewGuid(), Name = "BackfillMfr" };
        seedCtx.Manufacturers.Add(mfr);
        var mdl = new PrinterModel { Id = Guid.NewGuid(), ManufacturerId = mfr.Id, Name = "BackfillModel" };
        seedCtx.PrinterModels.Add(mdl);

        var folder = new FolderNode { Id = Guid.NewGuid(), Path = "/", FolderType = "gcode" };

        seedCtx.Set<FolderNode>().Add(folder);


        var gcode = new GcodeFile
        {
            FolderId = folder.Id,
            Id = Guid.NewGuid(),
            Name = "legacy.gcode",
            FileName = "legacy.gcode",
            FileSizeBytes = 1024,
            FilePath = "/legacy",
        };
        seedCtx.GcodeFiles.Add(gcode);

        var legacyJob = new PrintJob
        {
            Id = Guid.NewGuid(),
            Name = "legacy-job",
            GcodeFileId = gcode.Id,
            Status = PrintJobStatus.Completed,
            Priority = (int)PrintJobPriority.Normal,
            JobKind = JobKind.Standard, // Default for legacy rows
            CreatedAt = DateTime.UtcNow.AddDays(-1),
            UpdatedAt = DateTime.UtcNow.AddDays(-1),
            QueuedAt = DateTime.UtcNow.AddDays(-1),
        };
        seedCtx.PrintJobs.Add(legacyJob);
        await seedCtx.SaveChangesAsync();

        // Re-read: legacy job must be Standard with null calibration fields.
        await using var verifyCtx = CreateSqliteContext(connString);
        PrintJob? loaded = await verifyCtx.PrintJobs.FindAsync(legacyJob.Id);

        loaded.Should().NotBeNull();
        loaded!.JobKind.Should().Be(JobKind.Standard, "backfilled rows must default to Standard");
        loaded.IdempotencyScope.Should().BeNull("legacy rows must not have an IdempotencyScope");
        loaded.IdempotencyKey.Should().BeNull("legacy rows must not have an IdempotencyKey");
        loaded.CalibrationProjectId.Should().BeNull("legacy rows must not have CalibrationProjectId");
        loaded.CalibrationAttemptId.Should().BeNull("legacy rows must not have CalibrationAttemptId");
        loaded.CalibrationConfigSnapshotId.Should().BeNull("legacy rows must not have CalibrationConfigSnapshotId");
        loaded.CalibrationOrchestrationId.Should().BeNull("legacy rows must not have CalibrationOrchestrationId");
        loaded.RequiredFirmwareFamily.Should().BeNull("legacy rows must not have RequiredFirmwareFamily");
        loaded.GcodeContentSha256.Should().BeNull("legacy rows must not have GcodeContentSha256");
    }

    [Fact]
    [Trait("Category", "DbHeavy")]
    public async Task SQLite_IdempotencyUniqueIndex_EnforcedForActiveCalibrationJobs()
    {
        // Verifies the filtered unique index on (IdempotencyScope, IdempotencyKey) prevents
        // duplicate active calibration jobs with the same scope+key.
        string dbName = $"pfarm_idx_{Guid.NewGuid():N}";
        string connString = $"Data Source=file:{dbName}?mode=memory&cache=shared;Foreign Keys=False";

        using var keepAlive = new SqliteConnection(connString);
        keepAlive.Open();

        await using var seedCtx = CreateSqliteContext(connString);
        await seedCtx.Database.MigrateAsync();

        var mfr = new Manufacturer { Id = Guid.NewGuid(), Name = "IdxMfr" };
        seedCtx.Manufacturers.Add(mfr);
        var mdl = new PrinterModel { Id = Guid.NewGuid(), ManufacturerId = mfr.Id, Name = "IdxModel" };
        seedCtx.PrinterModels.Add(mdl);

        var folder = new FolderNode { Id = Guid.NewGuid(), Path = "/", FolderType = "gcode" };

        seedCtx.Set<FolderNode>().Add(folder);


        var gcode = new GcodeFile
        {
            FolderId = folder.Id,
            Id = Guid.NewGuid(),
            Name = "idx.gcode",
            FileName = "idx.gcode",
            FileSizeBytes = 512,
            FilePath = "/idx",
        };
        seedCtx.GcodeFiles.Add(gcode);
        await seedCtx.SaveChangesAsync();

        // Insert first calibration job (Queued = active under the filtered index).
        await using var ctx1 = CreateSqliteContext(connString);
        ctx1.PrintJobs.Add(new PrintJob
        {
            Id = Guid.NewGuid(),
            Name = "idx-job-1",
            GcodeFileId = gcode.Id,
            Status = PrintJobStatus.Queued,
            JobKind = JobKind.FilamentCalibration,
            IdempotencyScope = "idx-scope",
            IdempotencyKey = "idx-key",
            Priority = (int)PrintJobPriority.Normal,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            QueuedAt = DateTime.UtcNow,
        });
        await ctx1.SaveChangesAsync();

        // Insert second calibration job with same scope+key (must violate unique index).
        await using var ctx2 = CreateSqliteContext(connString);
        ctx2.PrintJobs.Add(new PrintJob
        {
            Id = Guid.NewGuid(),
            Name = "idx-job-2-duplicate",
            GcodeFileId = gcode.Id,
            Status = PrintJobStatus.Queued,
            JobKind = JobKind.FilamentCalibration,
            IdempotencyScope = "idx-scope",
            IdempotencyKey = "idx-key",
            Priority = (int)PrintJobPriority.Normal,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            QueuedAt = DateTime.UtcNow,
        });

        Func<Task> duplicateInsert = async () => await ctx2.SaveChangesAsync();
        await duplicateInsert.Should().ThrowAsync<Exception>(
            "inserting a duplicate active calibration job must violate the unique index on (IdempotencyScope, IdempotencyKey)");
    }

    [Fact]
    [Trait("Category", "DbHeavy")]
    public async Task SQLite_TerminalCalibrationJob_UniqueIndexPreventsRawDuplicateInsert()
    {
        // The filtered unique index on (IdempotencyScope, IdempotencyKey) covers all
        // calibration jobs — including terminal ones. Application-level logic detects
        // existing terminal jobs and returns a replay; the unique index prevents raw
        // duplicate DB inserts that bypass the application layer.
        string dbName = $"pfarm_terminal_{Guid.NewGuid():N}";
        string connString = $"Data Source=file:{dbName}?mode=memory&cache=shared;Foreign Keys=False";

        using var keepAlive = new SqliteConnection(connString);
        keepAlive.Open();

        await using var seedCtx = CreateSqliteContext(connString);
        await seedCtx.Database.MigrateAsync();

        var mfr = new Manufacturer { Id = Guid.NewGuid(), Name = "TermMfr" };
        seedCtx.Manufacturers.Add(mfr);
        var mdl = new PrinterModel { Id = Guid.NewGuid(), ManufacturerId = mfr.Id, Name = "TermModel" };
        seedCtx.PrinterModels.Add(mdl);

        var folder = new FolderNode { Id = Guid.NewGuid(), Path = "/", FolderType = "gcode" };

        seedCtx.Set<FolderNode>().Add(folder);


        var gcode = new GcodeFile
        {
            FolderId = folder.Id,
            Id = Guid.NewGuid(),
            Name = "term.gcode",
            FileName = "term.gcode",
            FileSizeBytes = 256,
            FilePath = "/term",
        };
        seedCtx.GcodeFiles.Add(gcode);

        // Insert a TERMINAL calibration job (Completed) with a given key.
        var terminalJob = new PrintJob
        {
            Id = Guid.NewGuid(),
            Name = "terminal-job",
            GcodeFileId = gcode.Id,
            Status = PrintJobStatus.Completed,
            JobKind = JobKind.FilamentCalibration,
            IdempotencyScope = "term-scope",
            IdempotencyKey = "term-key",
            Priority = (int)PrintJobPriority.Normal,
            CreatedAt = DateTime.UtcNow.AddHours(-1),
            UpdatedAt = DateTime.UtcNow.AddHours(-1),
            QueuedAt = DateTime.UtcNow.AddHours(-1),
        };
        seedCtx.PrintJobs.Add(terminalJob);
        await seedCtx.SaveChangesAsync();

        // Attempting to insert another job (active) with the SAME scope+key MUST fail
        // at the DB level — the unique index covers all calibration jobs regardless of status.
        // Application-level replay logic handles this case before reaching the DB.
        await using var ctx2 = CreateSqliteContext(connString);
        ctx2.PrintJobs.Add(new PrintJob
        {
            Id = Guid.NewGuid(),
            Name = "duplicate-attempt-job",
            GcodeFileId = gcode.Id,
            Status = PrintJobStatus.Queued,
            JobKind = JobKind.FilamentCalibration,
            IdempotencyScope = "term-scope", // Same scope+key as terminal
            IdempotencyKey = "term-key",
            Priority = (int)PrintJobPriority.Normal,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            QueuedAt = DateTime.UtcNow,
        });

        Func<Task> duplicateAttempt = async () => await ctx2.SaveChangesAsync();
        await duplicateAttempt.Should().ThrowAsync<Exception>(
            "raw duplicate inserts with the same scope+key are prevented at DB level " +
            "regardless of terminal status; application-level replay logic prevents reaching this path in production");
    }

    // =========================================================================
    // PostgreSQL — skipped unless PFARM_TEST_POSTGRES_CONN env var is set
    // =========================================================================

    [Fact]
    [Trait("Category", "Docker")]
    public async Task PostgreSQL_CalibrationQueueDispatch_MigrationsApplyAndSchemaIsConsistent()
    {
        string? connString = Environment.GetEnvironmentVariable(PostgresConnEnvVar);

        // The provider job MUST report visibly rather than silently returning green:
        // a skipped provider test is reported as Skipped by the test framework, so CI can
        // assert that provisioned provider jobs actually executed (issue #900, defect 14).
        if (string.IsNullOrWhiteSpace(connString))
        {
            Assert.Fail(
                $"PostgreSQL provider verification DID NOT RUN: set {PostgresConnEnvVar} to a live " +
            "PostgreSQL connection string. CI provider jobs MUST provision this.");
        }


        DbContextOptions<AppDbContext> opts = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(
                connString,
                provider => provider.MigrationsAssembly("Farm.Migrations.PostgreSQL"))
            .Options;

        await RunProviderSchemaAssertionsAsync(opts, PostgresProviderLabel);
    }

    // =========================================================================
    // SQL Server — skipped unless PFARM_TEST_SQLSERVER_CONN env var is set
    // =========================================================================

    [Fact]
    [Trait("Category", "Docker")]
    public async Task SqlServer_CalibrationQueueDispatch_MigrationsApplyAndSchemaIsConsistent()
    {
        string? connString = Environment.GetEnvironmentVariable(SqlServerConnEnvVar);

        if (string.IsNullOrWhiteSpace(connString))
        {
            Assert.Fail(
                $"SQL Server provider verification DID NOT RUN: set {SqlServerConnEnvVar} to a live " +
            "SQL Server connection string. CI provider jobs MUST provision this.");
        }


        DbContextOptions<AppDbContext> opts = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer(
                connString,
                provider => provider.MigrationsAssembly("Farm.Migrations.SqlServer"))
            .Options;

        await RunProviderSchemaAssertionsAsync(opts, SqlServerProviderLabel);
    }

    // =========================================================================
    // Shared helpers
    // =========================================================================

    private static async Task RunProviderSchemaAssertionsAsync(
        DbContextOptions<AppDbContext> opts,
        string providerName)
    {
        await using var ctx = new AppDbContext(opts);

        await ctx.Database.EnsureDeletedAsync();
        IMigrator migrator = ctx.Database.GetService<IMigrator>();
        await migrator.MigrateAsync();
        ctx.ChangeTracker.Clear();

        IEnumerable<string> applied = await ctx.Database.GetAppliedMigrationsAsync();
        applied.Should().Contain(
            m => m.EndsWith("InitialV2", StringComparison.Ordinal),
            $"[{providerName}] the squashed baseline migration must be applied");

        // OutboxSequenceState seed row must exist.
        bool seedExists = await ctx.OutboxSequenceStates.AnyAsync(s => s.Id == 1);
        seedExists.Should().BeTrue($"[{providerName}] OutboxSequenceState seed must be present after migration");

        OutboxSequenceState seqState = await ctx.OutboxSequenceStates.SingleAsync(s => s.Id == 1);
        seqState.NextSequence.Should().BeGreaterThanOrEqualTo(
            0, $"[{providerName}] NextSequence must be non-negative");
        // RowVersion on a migration-seeded row is provider-dependent by design. SQL Server uses a
        // native ROWVERSION column that the database generates, so the seeded row is stamped on
        // insert. On PostgreSQL/SQLite the column is application-managed (see
        // AppDbContext.OnModelCreating) and is only written by StampRowVersions() during
        // SaveChanges, so a row created by migrationBuilder.InsertData stays NULL until the
        // application first writes it. The fence itself is proven behaviourally by
        // AssertProviderNativeConcurrencyAsync below.
        if (providerName == SqlServerProviderLabel)
        {
            seqState.RowVersion.Should().NotBeNullOrEmpty(
                $"[{providerName}] the database-generated ROWVERSION must stamp the seeded fence row");
        }

        // All calibration queue tables must be queryable.
        (await ctx.QueueDispatchOutbox.LongCountAsync()).Should()
            .BeGreaterThanOrEqualTo(0, $"[{providerName}] QueueDispatchOutbox must be queryable");
        (await ctx.QueueDispatchAttempts.LongCountAsync()).Should()
            .BeGreaterThanOrEqualTo(0, $"[{providerName}] QueueDispatchAttempts must be queryable");
        (await ctx.PrinterDispatchStates.LongCountAsync()).Should()
            .BeGreaterThanOrEqualTo(0, $"[{providerName}] PrinterDispatchStates must be queryable");
        (await ctx.QueueOperationAudits.LongCountAsync()).Should()
            .BeGreaterThanOrEqualTo(0, $"[{providerName}] QueueOperationAudits must be queryable");

        // Fencing: the outbox sequence must be unique at the database level.
        bool duplicateSequences = await ctx.QueueDispatchOutbox
            .GroupBy(e => e.Sequence)
            .AnyAsync(g => g.Count() > 1);
        duplicateSequences.Should().BeFalse(
            $"[{providerName}] the unique index must prevent duplicate outbox sequences");

        await AssertProviderNativeConcurrencyAsync(opts, providerName);
        await AssertProviderBusinessRacesAsync(opts, providerName);
        await AssertProviderSchedulerOccurrencesAsync(opts, providerName);
        await AssertProviderDispatchPhasesAsync(opts, providerName);
        await AssertProviderSchedulingHistoryWireAsync(opts, providerName);

        // No pending migrations must remain.
        IEnumerable<string> pending = await ctx.Database.GetPendingMigrationsAsync();
        pending.Should().BeEmpty(
            $"[{providerName}] no pending migrations must remain after MigrateAsync");
    }

    private static async Task AssertProviderNativeConcurrencyAsync(
        DbContextOptions<AppDbContext> options,
        string providerName)
    {
        await using var first = new AppDbContext(options);
        await using var second = new AppDbContext(options);
        long[] sequences = await Task.WhenAll(
            AllocateAndInsertTerminalEventAsync(first),
            AllocateAndInsertTerminalEventAsync(second));
        sequences.Should().OnlyHaveUniqueItems(
            $"[{providerName}] simultaneous producers need distinct provider-native outbox ordering");

        await using var firstPositionContext = new AppDbContext(options);
        await using var secondPositionContext = new AppDbContext(options);
        Guid printerScope = Guid.NewGuid();
        int[] positions = await Task.WhenAll(
            new QueuePositionAllocator(firstPositionContext).AllocateAsync(printerScope),
            new QueuePositionAllocator(secondPositionContext).AllocateAsync(printerScope));
        positions.Should().OnlyHaveUniqueItems(
            $"[{providerName}] simultaneous queue producers need distinct positions");
    }

    private static async Task<long> AllocateAndInsertTerminalEventAsync(AppDbContext context)
    {
        await using var transaction = await context.Database.BeginTransactionAsync();
        long sequence = await new DbOutboxSequenceAllocator().AllocateAsync(context);
        context.QueueDispatchOutbox.Add(CreateTerminalEvent(sequence));
        await context.SaveChangesAsync();
        await transaction.CommitAsync();
        return sequence;
    }

    private static async Task AssertProviderBusinessRacesAsync(
        DbContextOptions<AppDbContext> options,
        string providerName)
    {
        ProviderFixture claimFixture = await SeedProviderFixtureAsync(
            options,
            "claim",
            PrintJobStatus.Assigned,
            DispatchAttemptOutcome.InProgress,
            createAttempt: false);
        await using (var first = new AppDbContext(options))
        await using (var second = new AppDbContext(options))
        {
            DispatchClaimResult[] claims = await Task.WhenAll(
                CreateProviderClaim(first, claimFixture.PrinterId)
                    .AcquireClaimAsync(ClaimRequest(claimFixture, "provider-a")),
                CreateProviderClaim(second, claimFixture.PrinterId)
                    .AcquireClaimAsync(ClaimRequest(claimFixture, "provider-b")));
            claims.Count(result => result.Success).Should().Be(
                1,
                $"[{providerName}] one cross-process claim must win");
        }

        ProviderFixture ackFixture = await SeedProviderFixtureAsync(
            options,
            "ack",
            PrintJobStatus.Assigned,
            DispatchAttemptOutcome.InProgress,
            createAttempt: false);
        byte[] jobVersion;
        byte[] dispatchVersion;
        await using (var read = new AppDbContext(options))
        {
            jobVersion = (await read.PrintJobs.FindAsync(ackFixture.JobId))!.RowVersion!;
            dispatchVersion = (await read.PrinterDispatchStates.FindAsync(
                ackFixture.PrinterId))!.RowVersion!;
        }

        var ackRequest = new AcknowledgeBedClearRequest(
            ackFixture.JobId,
            ackFixture.PrinterId,
            "provider-operator",
            "provider-ack-key",
            dispatchVersion,
            1,
            jobVersion);
        await using (var first = new AppDbContext(options))
        await using (var second = new AppDbContext(options))
        {
            AcknowledgeBedClearResult[] acknowledgements = await Task.WhenAll(
                CreateProviderAck(first, ackFixture.PrinterId)
                    .AcknowledgeAsync(ackRequest),
                CreateProviderAck(second, ackFixture.PrinterId)
                    .AcknowledgeAsync(ackRequest));
            acknowledgements.Count(result =>
                    result.Outcome == BedClearAckOutcome.Accepted)
                .Should().Be(
                    1,
                    $"[{providerName}] one bed-clear acknowledgement must win");
        }

        var management = new Mock<IPrintJobManagementService>();
        management.Setup(service => service.DispatchJobWithAckAsync(
                ackFixture.JobId.ToString(),
                "provider-operator",
                "provider-ack-key",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(BackendStartOutcome.Accepted(Guid.NewGuid()));
        using (ServiceProvider provider = CreateProviderServices(
                   options,
                   management: management.Object))
        {
            var first = new BackendStartCommandConsumerService(
                provider.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<BackendStartCommandConsumerService>.Instance);
            var second = new BackendStartCommandConsumerService(
                provider.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<BackendStartCommandConsumerService>.Instance);
            await Task.WhenAll(
                first.ProcessPendingCommandsAsync(CancellationToken.None),
                second.ProcessPendingCommandsAsync(CancellationToken.None));
        }

        management.Verify(service => service.DispatchJobWithAckAsync(
            ackFixture.JobId.ToString(),
            "provider-operator",
            "provider-ack-key",
            It.IsAny<CancellationToken>()), Times.Once);

        ProviderFixture reconcileFixture = await SeedProviderFixtureAsync(
            options,
            "reconcile",
            PrintJobStatus.Starting,
            DispatchAttemptOutcome.Unknown,
            createAttempt: true);
        var reconcilePrinters = new Mock<IPrintersService>();
        reconcilePrinters.Setup(service => service.GetStatusDtoAsync(
                reconcileFixture.PrinterId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PrinterStatusDto(
                reconcileFixture.PrinterId,
                IsOnline: true,
                State: "printing",
                FileName: reconcileFixture.BackendFileIdentity));
        using (ServiceProvider provider = CreateProviderServices(
                   options,
                   printers: reconcilePrinters.Object))
        {
            var first = new QueueReconciliationService(
                provider.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<QueueReconciliationService>.Instance);
            var second = new QueueReconciliationService(
                provider.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<QueueReconciliationService>.Instance);
            await Task.WhenAll(
                IgnoreExpectedRaceAsync(() =>
                    first.ReconcileStaleAttemptsAsync(CancellationToken.None)),
                IgnoreExpectedRaceAsync(() =>
                    second.ReconcileStaleAttemptsAsync(CancellationToken.None)));
        }

        await using (var verify = new AppDbContext(options))
        {
            (await verify.PrintJobs.FindAsync(reconcileFixture.JobId))!.Status
                .Should().Be(PrintJobStatus.Printing);
            (await verify.QueueDispatchOutbox.CountAsync(@event =>
                @event.AggregateId == reconcileFixture.JobId &&
                @event.EventType ==
                DispatchClaimService.EventTypeReconciliationAccepted)).Should().Be(1);
        }

        ProviderFixture controlFixture = await SeedProviderFixtureAsync(
            options,
            "control",
            PrintJobStatus.Printing,
            DispatchAttemptOutcome.Accepted,
            createAttempt: true);
        Guid controlCommandId = await AddProviderControlCommandAsync(
            options,
            controlFixture);
        var controlPrinters = new Mock<IPrintersService>();
        controlPrinters.Setup(service => service.ExecuteControlAsync(
                controlFixture.PrinterId,
                BackendControlOperation.Cancel,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(BackendControlOutcome.Accepted());
        using (ServiceProvider provider = CreateProviderServices(
                   options,
                   printers: controlPrinters.Object))
        {
            var first = new BackendControlCommandConsumerService(
                provider.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<BackendControlCommandConsumerService>.Instance);
            var second = new BackendControlCommandConsumerService(
                provider.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<BackendControlCommandConsumerService>.Instance);
            await Task.WhenAll(
                first.ProcessPendingAsync(CancellationToken.None),
                second.ProcessPendingAsync(CancellationToken.None));
        }

        controlPrinters.Verify(service => service.ExecuteControlAsync(
            controlFixture.PrinterId,
            BackendControlOperation.Cancel,
            It.IsAny<CancellationToken>()), Times.Once);
        await using (var verify = new AppDbContext(options))
        {
            (await verify.QueueDispatchOutbox.FindAsync(controlCommandId))!.Status
                .Should().Be(QueueOutboxEventStatus.Published);
        }

        await using (var firstContext = new AppDbContext(options))
        await using (var secondContext = new AppDbContext(options))
        {
            IHubContext<PrinterHub> hub = CreateProviderHub();
            var first = new PrintJobCompletionService(
                firstContext,
                hub,
                NullLogger<PrintJobCompletionService>.Instance);
            var second = new PrintJobCompletionService(
                secondContext,
                hub,
                NullLogger<PrintJobCompletionService>.Instance);
            bool[] externalResults = await Task.WhenAll(
                first.EnsureExternalPrintJobExistsAsync(
                    ackFixture.PrinterId,
                    "provider-external.gcode"),
                second.EnsureExternalPrintJobExistsAsync(
                    ackFixture.PrinterId,
                    "provider-external.gcode"));
            externalResults.Count(result => result).Should().Be(
                1,
                $"[{providerName}] one external active-print observer must win");
        }

        await using (var verify = new AppDbContext(options))
        {
            (await verify.PrintJobs.CountAsync(job =>
                job.ActiveExternalPrinterId == ackFixture.PrinterId &&
                job.IsExternalPrint &&
                job.Status == PrintJobStatus.Printing)).Should().Be(1);
        }
    }

    private static async Task AssertProviderSchedulerOccurrencesAsync(
        DbContextOptions<AppDbContext> options,
        string providerName)
    {
        (bool Recurring, DispatchAttemptOutcome Outcome)[] scenarios =
        [
            (false, DispatchAttemptOutcome.Accepted),
            (false, DispatchAttemptOutcome.Rejected),
            (false, DispatchAttemptOutcome.FailedBeforeStart),
            (false, DispatchAttemptOutcome.Unknown),
            (true, DispatchAttemptOutcome.Accepted),
            (true, DispatchAttemptOutcome.Rejected),
            (true, DispatchAttemptOutcome.FailedBeforeStart),
            (true, DispatchAttemptOutcome.Unknown),
        ];
        foreach ((bool recurring, DispatchAttemptOutcome outcome) in scenarios)
        {
            ProviderFixture fixture = await SeedProviderFixtureAsync(
                options,
                $"schedule-{recurring}-{outcome}",
                PrintJobStatus.Assigned,
                DispatchAttemptOutcome.InProgress,
                createAttempt: false);
            Guid actorId = Guid.NewGuid();
            DateTime due = DateTime.UtcNow.AddMinutes(-1);
            await using var db = new AppDbContext(options);
            var schedule = new JobSchedule
            {
                Id = Guid.NewGuid(),
                PrintJobId = fixture.JobId,
                RootPrintJobId = fixture.JobId,
                ScheduledStartTime = due,
                TimeZone = "UTC",
                RecurrencePattern = recurring ? "Daily" : null,
                RecurrenceInterval = 1,
                IsActive = true,
                IsPaused = false,
                InitiatingActorSubject = actorId.ToString(),
                RequiresOperatorReauthorization = false,
                ScheduledAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            db.JobSchedules.Add(schedule);
            await db.QueuePositionStates
                .Where(state => state.ScopeId == fixture.PrinterId)
                .ExecuteDeleteAsync();
            await db.SaveChangesAsync();

            var management = new Mock<IPrintJobManagementService>();
            management.Setup(service => service.DispatchJobAsync(
                    fixture.JobId.ToString(),
                    actorId.ToString(),
                    null,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new QueuedPrintJobDto
                {
                    Id = fixture.JobId.ToString(),
                    DispatchResult = new DispatchAttemptResultDto
                    {
                        AttemptId = Guid.NewGuid(),
                        AttemptNumber = 1,
                        Outcome = outcome,
                        RequiresReconciliation =
                            outcome == DispatchAttemptOutcome.Unknown,
                    },
                });
            var authorization = new Mock<IQueueResourceAuthorizationService>();
            authorization.Setup(service => service.CanActorAccessJobAsync(
                    It.IsAny<string>(),
                    It.IsAny<Guid>(),
                    It.IsAny<PrinterGroupAccessLevel>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
            authorization.Setup(service => service.CanActorAccessPrinterAsync(
                    It.IsAny<string>(),
                    It.IsAny<Guid>(),
                    It.IsAny<PrinterGroupAccessLevel>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
            var scheduler = new JobSchedulingService(
                db,
                NullLogger<JobSchedulingService>.Instance,
                management.Object,
                authorization.Object);

            await scheduler.TriggerScheduledJobsAsync();
            if (outcome == DispatchAttemptOutcome.Unknown)
            {
                await scheduler.TriggerScheduledJobsAsync();
            }

            db.ChangeTracker.Clear();
            JobSchedule persisted = await db.JobSchedules
                .Include(candidate => candidate.PrintJob)
                .SingleAsync(candidate => candidate.Id == schedule.Id);
            int executionCount = await db.JobExecutions.CountAsync(
                execution => execution.JobScheduleId == schedule.Id);
            executionCount.Should().Be(
                1,
                $"[{providerName}] {outcome} must create one occurrence record");
            if (outcome == DispatchAttemptOutcome.Accepted && recurring)
            {
                persisted.ScheduledStartTime.Should().BeAfter(due);
                persisted.PrintJobId.Should().NotBe(fixture.JobId);
                persisted.PrintJob.Status.Should().Be(PrintJobStatus.Assigned);
            }
            else if (outcome == DispatchAttemptOutcome.Accepted)
            {
                persisted.IsActive.Should().BeFalse();
            }
            else
            {
                persisted.IsActive.Should().BeTrue();
                persisted.ScheduledStartTime.Should().BeCloseTo(
                    due,
                    TimeSpan.FromMilliseconds(1));
                persisted.PrintJobId.Should().Be(fixture.JobId);
            }

            management.Verify(service => service.DispatchJobAsync(
                    fixture.JobId.ToString(),
                    actorId.ToString(),
                    null,
                    It.IsAny<CancellationToken>()),
                Times.Once,
                $"[{providerName}] unresolved occurrences must not duplicate backend calls");
            persisted.IsActive = false;
            await db.SaveChangesAsync();
        }
    }

    private static async Task AssertProviderDispatchPhasesAsync(
        DbContextOptions<AppDbContext> options,
        string providerName)
    {
        ProviderFixture preCall = await SeedProviderFixtureAsync(
            options,
            "phase-pre",
            PrintJobStatus.Assigned,
            DispatchAttemptOutcome.InProgress,
            createAttempt: false);
        ProviderFixture backendCall = await SeedProviderFixtureAsync(
            options,
            "phase-backend",
            PrintJobStatus.Assigned,
            DispatchAttemptOutcome.InProgress,
            createAttempt: false);
        ProviderFixture accepted = await SeedProviderFixtureAsync(
            options,
            "phase-accepted",
            PrintJobStatus.Assigned,
            DispatchAttemptOutcome.InProgress,
            createAttempt: false);
        var snapshots = new Mock<IPrinterStatusSnapshotReader>();
        snapshots.Setup(reader => reader.GetStatusSnapshot(It.IsAny<Guid>()))
            .Returns((Guid printerId) => new PrinterStatusSnapshot(
                new PrinterStatusDto(
                    printerId,
                    IsOnline: true,
                    State: "idle"),
                DateTime.UtcNow.AddSeconds(-1),
                DateTime.UtcNow.AddSeconds(-1),
                "provider-test"));
        await using var db = new AppDbContext(options);
        var service = new DispatchClaimService(
            db,
            snapshots.Object,
            new DbOutboxSequenceAllocator(),
            NullLogger<DispatchClaimService>.Instance,
            DispatchTestDoubles.TelemetryFreshnessPolicy());

        DispatchClaimResult preClaim = await service.AcquireClaimAsync(
            ClaimRequest(preCall, "provider-phase"));
        DispatchExceptionDisposition preDisposition =
            await service.RecordDispatchExceptionAsync(
                preClaim.Attempt!.Id,
                "secret=/private/path?token=abc");

        DispatchClaimResult backendClaim = await service.AcquireClaimAsync(
            ClaimRequest(backendCall, "provider-phase"));
        (await service.RecordBackendCallStartedAsync(
            backendClaim.Attempt!.Id)).Should().BeTrue();
        DispatchExceptionDisposition backendDisposition =
            await service.RecordDispatchExceptionAsync(
                backendClaim.Attempt.Id,
                "secret=/private/path?token=abc");

        DispatchClaimResult acceptedClaim = await service.AcquireClaimAsync(
            ClaimRequest(accepted, "provider-phase"));
        (await service.RecordBackendCallStartedAsync(
            acceptedClaim.Attempt!.Id)).Should().BeTrue();
        (await service.RecordBackendAcceptedAsync(
            acceptedClaim.Attempt.Id,
            "provider-job")).Should().BeTrue();
        DispatchExceptionDisposition acceptedDisposition =
            await service.RecordDispatchExceptionAsync(
                acceptedClaim.Attempt.Id,
                "secret=/private/path?token=abc");
        (await service.RecordPostAcceptCompletedAsync(
            acceptedClaim.Attempt.Id)).Should().BeTrue();

        db.ChangeTracker.Clear();
        QueueDispatchAttempt[] attempts = await db.QueueDispatchAttempts
            .Where(attempt =>
                attempt.Id == preClaim.Attempt.Id ||
                attempt.Id == backendClaim.Attempt.Id ||
                attempt.Id == acceptedClaim.Attempt.Id)
            .ToArrayAsync();
        QueueDispatchAttempt persistedPre = attempts.Single(
            attempt => attempt.Id == preClaim.Attempt.Id);
        QueueDispatchAttempt persistedBackend = attempts.Single(
            attempt => attempt.Id == backendClaim.Attempt.Id);
        QueueDispatchAttempt persistedAccepted = attempts.Single(
            attempt => attempt.Id == acceptedClaim.Attempt.Id);
        preDisposition.Should().Be(
            DispatchExceptionDisposition.ReleasedBeforeStart);
        persistedPre.Outcome.Should().Be(
            DispatchAttemptOutcome.FailedBeforeStart);
        backendDisposition.Should().Be(
            DispatchExceptionDisposition.AwaitingReconciliation);
        persistedBackend.Outcome.Should().Be(DispatchAttemptOutcome.Unknown);
        acceptedDisposition.Should().Be(
            DispatchExceptionDisposition.Accepted);
        persistedAccepted.Outcome.Should().Be(DispatchAttemptOutcome.Accepted);
        persistedAccepted.BackendCallPhase.Should().Be(
            DispatchBackendCallPhase.PostAccept);
        attempts.Should().NotContain(attempt =>
            attempt.ErrorDetail != null &&
            (attempt.ErrorDetail.Contains(
                "private",
                StringComparison.OrdinalIgnoreCase) ||
             attempt.ErrorDetail.Contains(
                 "token",
                 StringComparison.OrdinalIgnoreCase)),
            $"[{providerName}] persisted exception details must be redacted");
    }

    private static async Task AssertProviderSchedulingHistoryWireAsync(
        DbContextOptions<AppDbContext> options,
        string providerName)
    {
        ProviderFixture fixture = await SeedProviderFixtureAsync(
            options,
            "schedule-history-wire",
            PrintJobStatus.Assigned,
            DispatchAttemptOutcome.InProgress,
            createAttempt: false);
        Guid actorId = Guid.NewGuid();
        DateTime scheduledUtc = new(
            2026,
            11,
            1,
            8,
            30,
            0,
            DateTimeKind.Utc);
        await using var db = new AppDbContext(options);
        var schedule = new JobSchedule
        {
            Id = Guid.NewGuid(),
            PrintJobId = fixture.JobId,
            RootPrintJobId = fixture.JobId,
            ScheduledStartTime = scheduledUtc,
            TimeZone = "America/New_York",
            IsActive = true,
            InitiatingActorSubject = actorId.ToString(),
            RequiresOperatorReauthorization = false,
            ScheduledAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.JobSchedules.Add(schedule);
        db.JobExecutions.Add(new JobExecution
        {
            Id = Guid.NewGuid(),
            JobScheduleId = schedule.Id,
            OccurrencePrintJobId = fixture.JobId,
            ScheduledExecutionTime = scheduledUtc,
            ActualStartTime = scheduledUtc.AddSeconds(5),
            Status = "Completed",
            CreatedAt = scheduledUtc,
            UpdatedAt = scheduledUtc.AddSeconds(5),
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var authorization = new Mock<IQueueResourceAuthorizationService>();
        authorization.Setup(service => service.CanActorAccessJobAsync(
                It.IsAny<string>(),
                It.IsAny<Guid>(),
                It.IsAny<PrinterGroupAccessLevel>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        authorization.Setup(service => service.CanActorAccessPrinterAsync(
                It.IsAny<string>(),
                It.IsAny<Guid>(),
                It.IsAny<PrinterGroupAccessLevel>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        authorization.Setup(service => service.CanActorAccessProjectAsync(
                It.IsAny<string>(),
                It.IsAny<Guid>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var scheduler = new JobSchedulingService(
            db,
            NullLogger<JobSchedulingService>.Instance,
            Mock.Of<IPrintJobManagementService>(),
            authorization.Object);

        IReadOnlyList<JobExecutionDto>? history =
            await scheduler.GetExecutionHistoryAsync(
                fixture.JobId,
                actorId.ToString());

        JobExecutionDto execution = history.Should().ContainSingle().Subject;
        execution.ScheduledExecutionTime.Kind.Should().Be(
            DateTimeKind.Utc,
            $"[{providerName}] provider timestamps must be normalized before serialization");
        using JsonDocument wire = JsonDocument.Parse(JsonSerializer.Serialize(
            execution,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        wire.RootElement.GetProperty("scheduledExecutionTime")
            .GetString().Should().Be("2026-11-01T08:30:00Z");
        wire.RootElement.GetProperty("actualStartTime")
            .GetString().Should().Be("2026-11-01T08:30:05Z");
    }

    private static DispatchClaimService CreateProviderClaim(
        AppDbContext db,
        Guid printerId) =>
        new(
            db,
            DispatchTestDoubles.OnlineIdleReader(printerId),
            new DbOutboxSequenceAllocator(),
            NullLogger<DispatchClaimService>.Instance,
            DispatchTestDoubles.TelemetryFreshnessPolicy());

    private static BedClearAcknowledgementService CreateProviderAck(
        AppDbContext db,
        Guid printerId) =>
        new(
            db,
            new DbOutboxSequenceAllocator(),
            DispatchTestDoubles.OnlineIdleReader(printerId),
            NullLogger<BedClearAcknowledgementService>.Instance,
            DispatchTestDoubles.TelemetryFreshnessPolicy());

    private static DispatchClaimRequest ClaimRequest(
        ProviderFixture fixture,
        string actor) =>
        new(
            fixture.JobId,
            fixture.PrinterId,
            actor,
            "ProviderRace",
            null,
            null,
            null);

    private static async Task<ProviderFixture> SeedProviderFixtureAsync(
        DbContextOptions<AppDbContext> options,
        string suffix,
        PrintJobStatus status,
        DispatchAttemptOutcome attemptOutcome,
        bool createAttempt)
    {
        await using var db = new AppDbContext(options);
        Guid token = Guid.NewGuid();
        var manufacturer = new Manufacturer
        {
            Id = Guid.NewGuid(),
            Name = $"Provider maker {suffix} {token:N}",
        };
        var model = new PrinterModel
        {
            Id = Guid.NewGuid(),
            ManufacturerId = manufacturer.Id,
            Name = $"Provider model {suffix} {token:N}",
        };
        var folder = new FolderNode
        {
            Id = Guid.NewGuid(),
            Path = $"/provider-{suffix}-{token:N}",
            FolderType = "gcode",
        };
        var gcode = new GcodeFile
        {
            Id = Guid.NewGuid(),
            FolderId = folder.Id,
            Name = $"provider-{suffix}.gcode",
            FileName = $"provider-{suffix}.gcode",
            FilePath = folder.Path,
            FileSizeBytes = 10,
            FileHash = token.ToString("N").PadRight(64, '0'),
        };
        var printer = new Printer
        {
            Id = Guid.NewGuid(),
            Name = $"Provider printer {suffix} {token:N}",
            ServerUrl = $"http://provider-{token:N}",
            ManufacturerId = manufacturer.Id,
            ModelId = model.Id,
            IsEnabled = true,
            IsAvailable = true,
            ConfigurationRevision = 1,
        };
        var job = new PrintJob
        {
            Id = Guid.NewGuid(),
            Name = $"Provider job {suffix}",
            GcodeFileId = gcode.Id,
            AssignedPrinterId = printer.Id,
            Status = status,
            JobKind = JobKind.Standard,
            Priority = (int)PrintJobPriority.Normal,
            QueuePosition = 1000 + Math.Abs(token.GetHashCode() % 100_000),
            ActualStartTime = status is PrintJobStatus.Starting or
                PrintJobStatus.Printing
                ? DateTime.UtcNow.AddMinutes(-30)
                : null,
            CreatedAt = DateTime.UtcNow.AddMinutes(-30),
            UpdatedAt = DateTime.UtcNow.AddMinutes(-30),
            QueuedAt = DateTime.UtcNow.AddMinutes(-30),
        };
        var state = new PrinterDispatchState { PrinterId = printer.Id };
        Guid? attemptId = null;
        string backendFileIdentity = $"provider-{suffix}.gcode";
        db.Manufacturers.Add(manufacturer);
        db.PrinterModels.Add(model);
        db.Set<FolderNode>().Add(folder);
        db.GcodeFiles.Add(gcode);
        db.Printers.Add(printer);
        db.PrintJobs.Add(job);
        db.PrinterDispatchStates.Add(state);
        if (createAttempt)
        {
            attemptId = Guid.NewGuid();
            var attempt = new QueueDispatchAttempt
            {
                Id = attemptId.Value,
                PrintJobId = job.Id,
                PrinterId = printer.Id,
                PrinterConfigRevision = 1,
                AttemptNumber = 1,
                ActorSubject = "provider-operator",
                StartPathKind = "ProviderRace",
                ClaimedAtUtc = DateTime.UtcNow.AddMinutes(-30),
                Outcome = attemptOutcome,
                RequiresReconciliation =
                    attemptOutcome == DispatchAttemptOutcome.Unknown,
                BackendFileName = backendFileIdentity,
                BackendFileIdentity = backendFileIdentity,
                BackendCallPhase = attemptOutcome == DispatchAttemptOutcome.Unknown
                    ? DispatchBackendCallPhase.AwaitingReconciliation
                    : DispatchBackendCallPhase.PostAccept,
                UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-30),
            };
            db.QueueDispatchAttempts.Add(attempt);
            state.ActiveJobId = job.Id;
            state.ActiveDispatchAttemptId = attempt.Id;
        }

        await db.SaveChangesAsync();
        return new ProviderFixture(
            printer.Id,
            job.Id,
            attemptId,
            backendFileIdentity);
    }

    private static async Task<Guid> AddProviderControlCommandAsync(
        DbContextOptions<AppDbContext> options,
        ProviderFixture fixture)
    {
        await using var db = new AppDbContext(options);
        await using var transaction = await db.Database.BeginTransactionAsync();
        Guid commandId = Guid.NewGuid();
        db.QueueDispatchOutbox.Add(new QueueDispatchOutbox
        {
            Id = commandId,
            Sequence = await new DbOutboxSequenceAllocator().AllocateAsync(db),
            AggregateType = nameof(PrintJob),
            AggregateId = fixture.JobId,
            PrinterId = fixture.PrinterId,
            AttemptId = fixture.AttemptId,
            EventType = BackendControlCommandConsumerService.EventType,
            SchemaVersion = "1",
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                jobId = fixture.JobId,
                printerId = fixture.PrinterId,
                attemptId = fixture.AttemptId,
                backendJobId = (string?)null,
                backendFileIdentity = fixture.BackendFileIdentity,
                operation = "cancel",
                actorSubject = "provider-operator",
            }),
            Status = QueueOutboxEventStatus.Pending,
            CreatedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return commandId;
    }

    private static ServiceProvider CreateProviderServices(
        DbContextOptions<AppDbContext> options,
        IPrintersService? printers = null,
        IPrintJobManagementService? management = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped(_ => new AppDbContext(options));
        services.AddScoped<IDbOutboxSequenceAllocator, DbOutboxSequenceAllocator>();
        if (printers is not null)
        {
            services.AddSingleton(printers);
        }

        if (management is not null)
        {
            services.AddSingleton(management);
        }

        return services.BuildServiceProvider();
    }

    private static async Task IgnoreExpectedRaceAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception exception) when (
            exception is DbUpdateConcurrencyException or DbUpdateException)
        {
            // The losing provider transaction proves the concurrency predicate held.
        }
    }

    private static IHubContext<PrinterHub> CreateProviderHub()
    {
        var client = new Mock<IClientProxy>();
        client.Setup(proxy => proxy.SendCoreAsync(
                It.IsAny<string>(),
                It.IsAny<object?[]>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var clients = new Mock<IHubClients>();
        clients.Setup(value => value.Group(It.IsAny<string>())).Returns(client.Object);
        var hub = new Mock<IHubContext<PrinterHub>>();
        hub.SetupGet(value => value.Clients).Returns(clients.Object);
        return hub.Object;
    }

    private sealed record ProviderFixture(
        Guid PrinterId,
        Guid JobId,
        Guid? AttemptId,
        string BackendFileIdentity);

    private static QueueDispatchOutbox CreateTerminalEvent(long sequence) =>
        new()
        {
            Id = Guid.NewGuid(),
            Sequence = sequence,
            AggregateType = nameof(PrintJob),
            AggregateId = Guid.NewGuid(),
            EventType = QueueLifecycleEventWriter.EventTypeJobCompleted,
            SchemaVersion = "1",
            JobStatus = PrintJobStatus.Completed.ToString(),
            JobKind = JobKind.Standard.ToString(),
            PayloadJson = "{}",
            Status = QueueOutboxEventStatus.Pending,
            CreatedAtUtc = DateTime.UtcNow,
        };
}
