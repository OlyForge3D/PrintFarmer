// <copyright file="SliceJobIdempotencyIndexTests.cs" company="PlaceholderCompany">
// SPDX-License-Identifier: AGPL-3.0-only
// </copyright>

using Farm.Slicer.Module.Data;
using Farm.Slicer.Module.Domain;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Farm.Slicer.Module.Tests.SlicerServices;

public sealed class SliceJobIdempotencyIndexTests
{
    [Fact]
    public async Task SaveChanges_RepeatedStandardChecksum_AllowsBothButCalibrationDuplicateFails()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        DbContextOptions<SlicerDbContext> options =
            new DbContextOptionsBuilder<SlicerDbContext>()
                .UseSqlite(connection)
                .Options;
        await using var context = new SlicerDbContext(options);
        await context.Database.EnsureCreatedAsync();
        Guid owner = Guid.NewGuid();
        const string checksum = "same-physical-input";

        context.SliceJobs.AddRange(
            CreateJob(owner, Guid.Empty, checksum),
            CreateJob(owner, Guid.Empty, checksum));
        await context.SaveChangesAsync();

        Guid calibrationScope = Guid.NewGuid();
        context.SliceJobs.Add(CreateJob(owner, calibrationScope, checksum));
        await context.SaveChangesAsync();
        context.SliceJobs.Add(CreateJob(owner, calibrationScope, checksum));

        Func<Task> duplicateCalibration = async () => await context.SaveChangesAsync();
        await duplicateCalibration.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task Migrate_PredecessorToHead_AllowsStandardDuplicatesAndDowngradeFailsSafe()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        DbContextOptions<SlicerDbContext> options =
            new DbContextOptionsBuilder<SlicerDbContext>()
                .UseSqlite(
                    connection,
                    sqlite => sqlite.MigrationsAssembly("Farm.Slicer.Migrations.Sqlite"))
                .Options;
        await using var context = new SlicerDbContext(options);
        IMigrator migrator = context.Database.GetService<IMigrator>();
        const string Predecessor =
            "20260725185010_AddOwnerScopedPromotionOperationKey";
        await migrator.MigrateAsync(Predecessor);

        Guid owner = Guid.NewGuid();
        const string checksum = "upgrade-standard-checksum";

        // Use raw SQL to insert at predecessor schema to avoid EF model/schema mismatch:
        // the current EF model includes columns (e.g. ClaimToken) added by later migrations.
        var jobId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO SliceJobs
                (Id, UserId, ModelFileUrl, ModelFileName, SlicerEngine, Status, Priority, ProgressPercent, RetryCount, QueuedAt, CreatedAt, UpdatedAt, Checksum)
            VALUES
                ({jobId}, {owner}, {"stored-model.stl"}, {"stored-model.stl"}, {1}, {"Queued"}, {1}, {0}, {0}, {now}, {now}, {now}, {checksum})
            """);

        await migrator.MigrateAsync();
        context.SliceJobs.Add(CreateJob(owner, Guid.Empty, checksum));
        await context.SaveChangesAsync();

        Func<Task> downgrade = async () => await migrator.MigrateAsync(Predecessor);
        await downgrade.Should().ThrowAsync<NotSupportedException>()
            .WithMessage("*forward-only*");
    }

    private static SliceJob CreateJob(
        Guid owner,
        Guid idempotencyScope,
        string checksum) =>
        new()
        {
            Id = Guid.NewGuid(),
            UserId = owner,
            IdempotencyScopeId = idempotencyScope,
            ModelFileUrl = "stored-model.stl",
            ModelFileName = "stored-model.stl",
            SlicerEngine = 1,
            Status = "Queued",
            Priority = 1,
            CorrelationId = Guid.NewGuid(),
            Checksum = checksum,
            QueuedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
}
