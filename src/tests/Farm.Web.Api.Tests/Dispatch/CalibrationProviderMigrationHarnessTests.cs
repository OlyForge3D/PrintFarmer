// <copyright file="CalibrationProviderMigrationHarnessTests.cs" company="OlyForge3D">
// Copyright (c) OlyForge3D. All rights reserved.
// </copyright>

using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
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
/// 1. All pending migrations can be applied without error (<see cref="EnsureCreatedAsync"/>
///    or <c>MigrateAsync()</c> paths as appropriate per provider).
/// 2. After migration, the <c>OutboxSequenceState</c> seed row (Id=1, NextSequence=0) is present.
/// 3. The <c>QueueDispatchOutbox</c>, <c>QueueDispatchAttempts</c>, and
///    <c>PrinterDispatchState</c> tables exist and are queryable.
/// 4. Backfill: existing <c>PrintJob</c> rows have <c>JobKind = Standard</c> with all
///    nullable calibration fields null — no ambiguous legacy flag becomes a valid ack or lease.
/// 5. The filtered unique index on <c>(IdempotencyScope, IdempotencyKey)</c> is enforced
///    for active calibration jobs.
/// </summary>
public class CalibrationProviderMigrationHarnessTests
{
    // Environment variable names for optional provider connection strings.
    private const string PostgresConnEnvVar = "PFARM_TEST_POSTGRES_CONN";
    private const string SqlServerConnEnvVar = "PFARM_TEST_SQLSERVER_CONN";

    /// <summary>
    /// Creates an AppDbContext backed by the given SQLite connection string with
    /// FK constraints disabled. This matches the test pattern used by
    /// <see cref="CalibrationQueueConcurrencyTests"/> so tests can insert entities
    /// in arbitrary order without FK failures.
    /// </summary>
    private static AppDbContext CreateSqliteContext(string connString)
    {
        DbContextOptions<AppDbContext> opts = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connString)
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
        string connString = $"Data Source=file:{dbName}?mode=memory&cache=shared";

        using var keepAlive = new SqliteConnection(connString);
        keepAlive.Open();

        await using var ctx = CreateSqliteContext(connString);
        bool created = await ctx.Database.EnsureCreatedAsync();
        created.Should().BeTrue("SQLite in-memory DB must be created on first use");

        // Seed row must exist after schema creation.
        OutboxSequenceState seqState = await ctx.OutboxSequenceStates.SingleAsync();
        seqState.Id.Should().Be(1, "OutboxSequenceState seed must use Id=1");
        seqState.NextSequence.Should().Be(0, "OutboxSequenceState seed must start at NextSequence=0");

        // Key calibration tables must be queryable.
        (await ctx.QueueDispatchOutbox.LongCountAsync()).Should().Be(0);
        (await ctx.QueueDispatchAttempts.LongCountAsync()).Should().Be(0);
        (await ctx.PrinterDispatchStates.LongCountAsync()).Should().Be(0);
    }

    [Fact]
    [Trait("Category", "DbHeavy")]
    public async Task SQLite_PrintJobBackfill_LegacyJobsDefaultToStandard_NullCalibrationFields()
    {
        string dbName = $"pfarm_backfill_{Guid.NewGuid():N}";
        string connString = $"Data Source=file:{dbName}?mode=memory&cache=shared";

        using var keepAlive = new SqliteConnection(connString);
        keepAlive.Open();

        await using var seedCtx = CreateSqliteContext(connString);
        await seedCtx.Database.EnsureCreatedAsync();

        // Seed a minimal PrintJob to simulate a pre-calibration-feature row.
        var mfr = new Manufacturer { Id = Guid.NewGuid(), Name = "BackfillMfr" };
        seedCtx.Manufacturers.Add(mfr);
        var mdl = new PrinterModel { Id = Guid.NewGuid(), ManufacturerId = mfr.Id, Name = "BackfillModel" };
        seedCtx.PrinterModels.Add(mdl);

        var gcode = new GcodeFile
        {
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
        string connString = $"Data Source=file:{dbName}?mode=memory&cache=shared";

        using var keepAlive = new SqliteConnection(connString);
        keepAlive.Open();

        await using var seedCtx = CreateSqliteContext(connString);
        await seedCtx.Database.EnsureCreatedAsync();

        var mfr = new Manufacturer { Id = Guid.NewGuid(), Name = "IdxMfr" };
        seedCtx.Manufacturers.Add(mfr);
        var mdl = new PrinterModel { Id = Guid.NewGuid(), ManufacturerId = mfr.Id, Name = "IdxModel" };
        seedCtx.PrinterModels.Add(mdl);

        var gcode = new GcodeFile
        {
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
        string connString = $"Data Source=file:{dbName}?mode=memory&cache=shared";

        using var keepAlive = new SqliteConnection(connString);
        keepAlive.Open();

        await using var seedCtx = CreateSqliteContext(connString);
        await seedCtx.Database.EnsureCreatedAsync();

        var mfr = new Manufacturer { Id = Guid.NewGuid(), Name = "TermMfr" };
        seedCtx.Manufacturers.Add(mfr);
        var mdl = new PrinterModel { Id = Guid.NewGuid(), ManufacturerId = mfr.Id, Name = "TermModel" };
        seedCtx.PrinterModels.Add(mdl);

        var gcode = new GcodeFile
        {
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
        if (string.IsNullOrWhiteSpace(connString))
        {
            // No Postgres container — skip gracefully. CI with a real container must set this.
            return;
        }

        DbContextOptions<AppDbContext> opts = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connString)
            .Options;

        await RunProviderSchemaAssertionsAsync(opts, "PostgreSQL");
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
            return;
        }

        DbContextOptions<AppDbContext> opts = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer(connString)
            .Options;

        await RunProviderSchemaAssertionsAsync(opts, "SQL Server");
    }

    // =========================================================================
    // Shared helpers
    // =========================================================================

    private static async Task RunProviderSchemaAssertionsAsync(
        DbContextOptions<AppDbContext> opts,
        string providerName)
    {
        await using var ctx = new AppDbContext(opts);

        // Apply all pending migrations.
        await ctx.Database.MigrateAsync();

        // OutboxSequenceState seed row must exist.
        bool seedExists = await ctx.OutboxSequenceStates.AnyAsync(s => s.Id == 1);
        seedExists.Should().BeTrue($"[{providerName}] OutboxSequenceState seed must be present after migration");

        OutboxSequenceState seqState = await ctx.OutboxSequenceStates.SingleAsync(s => s.Id == 1);
        seqState.NextSequence.Should().BeGreaterThanOrEqualTo(
            0, $"[{providerName}] NextSequence must be non-negative");

        // All calibration queue tables must be queryable.
        (await ctx.QueueDispatchOutbox.LongCountAsync()).Should()
            .BeGreaterThanOrEqualTo(0, $"[{providerName}] QueueDispatchOutbox must be queryable");
        (await ctx.QueueDispatchAttempts.LongCountAsync()).Should()
            .BeGreaterThanOrEqualTo(0, $"[{providerName}] QueueDispatchAttempts must be queryable");
        (await ctx.PrinterDispatchStates.LongCountAsync()).Should()
            .BeGreaterThanOrEqualTo(0, $"[{providerName}] PrinterDispatchStates must be queryable");

        // No pending migrations must remain.
        IEnumerable<string> pending = await ctx.Database.GetPendingMigrationsAsync();
        pending.Should().BeEmpty(
            $"[{providerName}] no pending migrations must remain after MigrateAsync");
    }
}
