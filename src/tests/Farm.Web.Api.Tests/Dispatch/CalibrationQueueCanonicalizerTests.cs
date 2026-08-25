// <copyright file="CalibrationQueueCanonicalizerTests.cs" company="OlyForge3D">
// Copyright (c) OlyForge3D. All rights reserved.
// </copyright>

using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Queue;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Farm.Web.Api.Tests.Dispatch;

/// <summary>
/// Regression coverage for issue #1990: PR #1987 (D4) started unconditionally nulling
/// the (now-deleted) <c>CalibrationAttempt.PrinterConfigurationSnapshotId</c> FK for every new
/// attempt with no replacement path. Before the #1990 fix, <see cref="CalibrationQueueCanonicalizer.BuildAsync"/>
/// fell through to a dead lookup on a nonexistent snapshot id and surfaced a generic "not found"
/// exception that read like data corruption. This asserts the explicit, documented short-circuit
/// added for #1990 — which #1989 (D3b) made unconditional after deleting the
/// <c>PrinterConfigurationSnapshot</c> entity/table entirely.
/// </summary>
[Trait("Category", "DbHeavy")]
public sealed class CalibrationQueueCanonicalizerTests : IAsyncDisposable
{
    private readonly SqliteConnection _keepAlive;
    private readonly string _connectionString;
    private static int _dbCounter;

    public CalibrationQueueCanonicalizerTests()
    {
        int id = System.Threading.Interlocked.Increment(ref _dbCounter);
        _connectionString = $"Data Source=file:canonicalizer_{id}?mode=memory&cache=shared;Foreign Keys=False";
        _keepAlive = new SqliteConnection(_connectionString);
        _keepAlive.Open();
    }

    public async ValueTask DisposeAsync()
    {
        await _keepAlive.DisposeAsync();
    }

    private AppDbContext CreateContext()
    {
        DbContextOptions<AppDbContext> opts = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connectionString)
            .Options;
        var ctx = new AppDbContext(opts);
        ctx.Database.ExecuteSqlRaw("PRAGMA foreign_keys = OFF;");
        return ctx;
    }

    [Fact]
    public async Task BuildAsync_AttemptWithoutSnapshotId_ThrowsIncompatible_NotDeadLookup()
    {
        await using AppDbContext seedCtx = CreateContext();
        await seedCtx.Database.EnsureCreatedAsync();

        Guid projectId = Guid.NewGuid();
        Guid attemptId = Guid.NewGuid();
        Guid orchestrationId = Guid.NewGuid();

        seedCtx.CalibrationProjects.Add(new CalibrationProject
        {
            Id = projectId,
            OwnerUserId = Guid.NewGuid(),
            Name = "Canonicalizer regression project",
            PrinterId = Guid.NewGuid(),
            FilamentProvider = "local",
            FilamentProductId = "pla",
            FilamentProductName = "PLA",
            FilamentMaterial = "PLA",
        });
        seedCtx.CalibrationAttempts.Add(new CalibrationAttempt
        {
            Id = attemptId,
            ProjectId = projectId,
            SpecificationSha256 = new string('a', 64),
        });
        seedCtx.CalibrationOrchestrations.Add(new CalibrationOrchestration
        {
            Id = orchestrationId,
            ProjectId = projectId,
            AttemptId = attemptId,
            SpecificationSha256 = new string('a', 64),
        });
        await seedCtx.SaveChangesAsync();

        await using AppDbContext buildCtx = CreateContext();
        var canonicalizer = new CalibrationQueueCanonicalizer(buildCtx);

        var request = new Farm.Infrastructure.QueuePrintJobDto
        {
            JobKind = JobKind.FilamentCalibration,
        };
        var gcode = new GcodeFile
        {
            Id = Guid.NewGuid(),
            Name = "canon.gcode",
            FileName = "canon.gcode",
            FileHash = new string('b', 64),
            FileSizeBytes = 1,
            FilePath = "/canon",
        };
        var classification = new QueueJobClassification(
            JobKind: JobKind.FilamentCalibration,
            CalibrationProjectId: projectId,
            CalibrationAttemptId: attemptId,
            CalibrationOrchestrationId: orchestrationId,
            SourceArtifactId: Guid.NewGuid(),
            SliceJobId: Guid.NewGuid(),
            GcodeContentSha256: new string('b', 64),
            SpecificationSha256: new string('a', 64),
            MachineProfileSha256: new string('c', 64),
            ProcessProfileSha256: new string('d', 64),
            FilamentProfileSha256: new string('e', 64),
            RequiredFirmwareFamily: null,
            RequiredGcodeDialect: null,
            RequiredSlicerEngine: null,
            RequiredSlicerDistribution: null,
            RequiredSlicerVersion: null,
            RequiredSlicerContainerDigest: null);

        Func<Task> act = () => canonicalizer.BuildAsync(
            request, gcode, classification, actorUserId: null, CancellationToken.None);

        (await act.Should().ThrowAsync<CalibrationQueueIncompatibleException>())
            .WithMessage("*known interim limitation*#1990*");
    }
}
