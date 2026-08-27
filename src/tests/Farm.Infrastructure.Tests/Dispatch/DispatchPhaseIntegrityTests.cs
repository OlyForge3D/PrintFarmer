using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.Queue;
using Farm.Infrastructure.Services.Queue.Dispatch;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Farm.Infrastructure.Tests.Dispatch;

public sealed class DispatchPhaseIntegrityTests
{
    [Fact]
    public async Task DispatchExceptions_AreClassifiedByDurablePhaseAndRedacted()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        DbContextOptions<AppDbContext> options =
            new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(connection)
                .Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        ProviderFixture preCall = await SeedFixtureAsync(db, "pre");
        ProviderFixture backendCall = await SeedFixtureAsync(db, "backend");
        ProviderFixture accepted = await SeedFixtureAsync(db, "accepted");
        var snapshots = new Mock<IPrinterStatusSnapshotReader>();
        snapshots.Setup(reader => reader.GetStatusSnapshot(It.IsAny<Guid>()))
            .Returns((Guid printerId) => new PrinterStatusSnapshot(
                new PrinterStatusDto(
                    printerId,
                    IsOnline: true,
                    State: "idle"),
                DateTime.UtcNow.AddSeconds(-1),
                DateTime.UtcNow.AddSeconds(-1),
                "test"));
        var service = new DispatchClaimService(
            db,
            snapshots.Object,
            new DbOutboxSequenceAllocator(),
            NullLogger<DispatchClaimService>.Instance,
            DispatchTestDoubles.TelemetryFreshnessPolicy());

        DispatchClaimResult preClaim = await service.AcquireClaimAsync(
            Request(preCall));
        preClaim.Success.Should().BeTrue(preClaim.ErrorDetail);
        DispatchExceptionDisposition preDisposition =
            await service.RecordDispatchExceptionAsync(
                preClaim.Attempt!.Id,
                "secret=/private/path?token=abc");

        DispatchClaimResult backendClaim = await service.AcquireClaimAsync(
            Request(backendCall));
        backendClaim.Success.Should().BeTrue(backendClaim.ErrorDetail);
        (await service.RecordBackendCallStartedAsync(
            backendClaim.Attempt!.Id)).Should().BeTrue();
        DispatchExceptionDisposition backendDisposition =
            await service.RecordDispatchExceptionAsync(
                backendClaim.Attempt.Id,
                "secret=/private/path?token=abc");

        DispatchClaimResult acceptedClaim = await service.AcquireClaimAsync(
            Request(accepted));
        acceptedClaim.Success.Should().BeTrue(acceptedClaim.ErrorDetail);
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
        QueueDispatchAttempt preAttempt = await db.QueueDispatchAttempts
            .SingleAsync(attempt => attempt.Id == preClaim.Attempt.Id);
        QueueDispatchAttempt backendAttempt = await db.QueueDispatchAttempts
            .SingleAsync(attempt => attempt.Id == backendClaim.Attempt.Id);
        QueueDispatchAttempt acceptedAttempt = await db.QueueDispatchAttempts
            .SingleAsync(attempt => attempt.Id == acceptedClaim.Attempt.Id);

        preDisposition.Should().Be(
            DispatchExceptionDisposition.ReleasedBeforeStart);
        preAttempt.Outcome.Should().Be(
            DispatchAttemptOutcome.FailedBeforeStart);
        preAttempt.BackendCallPhase.Should().Be(
            DispatchBackendCallPhase.Terminal);
        backendDisposition.Should().Be(
            DispatchExceptionDisposition.AwaitingReconciliation);
        backendAttempt.Outcome.Should().Be(DispatchAttemptOutcome.Unknown);
        backendAttempt.BackendCallPhase.Should().Be(
            DispatchBackendCallPhase.AwaitingReconciliation);
        acceptedDisposition.Should().Be(
            DispatchExceptionDisposition.Accepted);
        acceptedAttempt.Outcome.Should().Be(DispatchAttemptOutcome.Accepted);
        acceptedAttempt.BackendCallPhase.Should().Be(
            DispatchBackendCallPhase.PostAccept);
        new[]
        {
            preAttempt.ErrorDetail,
            backendAttempt.ErrorDetail,
            acceptedAttempt.ErrorDetail,
        }.Should().NotContain(detail => detail != null &&
            (detail.Contains("private", StringComparison.OrdinalIgnoreCase) ||
             detail.Contains("token", StringComparison.OrdinalIgnoreCase)));
    }

    private static DispatchClaimRequest Request(ProviderFixture fixture) =>
        new(
            fixture.JobId,
            fixture.PrinterId,
            Guid.NewGuid().ToString(),
            "PhaseInjection",
            null,
            null,
            null);

    private static async Task<ProviderFixture> SeedFixtureAsync(
        AppDbContext db,
        string suffix)
    {
        DateTime now = DateTime.UtcNow;
        var manufacturer = new Manufacturer
        {
            Id = Guid.NewGuid(),
            Name = $"Phase maker {suffix} {Guid.NewGuid():N}",
        };
        var model = new PrinterModel
        {
            Id = Guid.NewGuid(),
            ManufacturerId = manufacturer.Id,
            Name = $"Phase model {suffix} {Guid.NewGuid():N}",
        };
        var printer = new Printer
        {
            Id = Guid.NewGuid(),
            Name = $"Phase printer {suffix}",
            ServerUrl = $"http://phase-{suffix}-{Guid.NewGuid():N}",
            ManufacturerId = manufacturer.Id,
            ModelId = model.Id,
            IsEnabled = true,
            IsAvailable = true,
            ConfigurationRevision = 1,
        };
        var folder = new FolderNode
        {
            Id = Guid.NewGuid(),
            Path = $"/phase-{suffix}-{Guid.NewGuid():N}",
            FolderType = "gcode",
            CreatedAt = now,
        };
        var gcode = new GcodeFile
        {
            Id = Guid.NewGuid(),
            FolderId = folder.Id,
            Name = $"phase-{suffix}.gcode",
            FileName = $"phase-{suffix}-{Guid.NewGuid():N}.gcode",
            FilePath = folder.Path,
            FileHash = $"{Guid.NewGuid():N}{Guid.NewGuid():N}",
            UploadedAt = now,
        };
        var job = new PrintJob
        {
            Id = Guid.NewGuid(),
            Name = $"Phase job {suffix}",
            GcodeFileId = gcode.Id,
            AssignedPrinterId = printer.Id,
            Status = PrintJobStatus.Assigned,
            JobKind = JobKind.Standard,
            Priority = (int)PrintJobPriority.Normal,
            QueuePosition = Math.Abs(Guid.NewGuid().GetHashCode()),
            CreatedAt = now,
            UpdatedAt = now,
            QueuedAt = now,
        };
        db.AddRange(
            manufacturer,
            model,
            printer,
            folder,
            gcode,
            job,
            new PrinterDispatchState { PrinterId = printer.Id });
        await db.SaveChangesAsync();
        return new ProviderFixture(printer.Id, job.Id);
    }

    private sealed record ProviderFixture(Guid PrinterId, Guid JobId);
}
