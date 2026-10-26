using Farm.Infrastructure;
using Farm.Infrastructure.Contracts.Printers;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Printers;
using Farm.Infrastructure.Services.Gcode.PrinterFiles;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.Queue;
using Farm.Modules.Gcode.Services.Gcode;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Modules.Gcode.Tests.Services.Gcode;

/// <summary>Only fake providers: no real printer calls or mutations.</summary>
public sealed class PrinterFileDeleteServiceTests
{
    [Fact]
    public async Task Confirm_DeletesOnlySelectedLocation_PreservesOwnedAssetsOriginsAndWaitingJobsAsync()
    {
        await using Fixture test = await Fixture.CreateAsync();
        await using AppDbContext db = test.Open();
        PrinterFileDeleteService service = test.Service(db);
        PrinterFileDeleteOperation intent = await test.IntentAsync(service);
        Assert.Equal(0, test.Backend.Sends);
        PrinterFileDeleteOperation result = (await service.ConfirmAsync(test.Actor, intent.Id, Consent(intent)))!;
        Assert.Equal("Acknowledged", result.Outcome);
        Assert.Equal(PrinterFileOperationState.Deleted, result.State);
        Assert.Equal(1, test.Backend.Sends);
        Assert.True(test.Backend.BoundaryWasDurable);
        Assert.Equal("credential", test.Backend.Credential?.ApiKey);
        db.ChangeTracker.Clear();
        PrinterFileLocation location = await db.PrinterFileLocations.SingleAsync(x => x.Id == test.Location);
        Assert.Equal(PrinterFileAvailability.Deleted, location.Availability);
        Assert.Equal(2, location.ObservationRevision);
        Assert.Equal(1, location.MutationGeneration);
        Assert.Equal(PrinterFileAvailability.Available,
            (await db.PrinterFileLocations.SingleAsync(x => x.Id != test.Location)).Availability);
        Assert.Equal("owned bytes remain", (await db.GcodeFiles.SingleAsync()).Description);
        Assert.Single(await db.PrinterFileOrigins.ToListAsync());
        Assert.Single(await db.PrintJobs.ToListAsync());
        Assert.Equal(test.Job, (await db.PrintJobs.SingleAsync()).Id);
        Assert.Contains(await db.QueueOperationAudits.ToListAsync(), x =>
            x.Operation == QueueAuditOperations.PrinterFileDelete && x.ReasonCode == "delete_send_boundary");
        test.Physical.Verify(x => x.CompleteDirectAsync(It.IsAny<PrinterActuationLease>(), true, null,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DurableReplayAndChangedPayload_NeverSendAgainAsync()
    {
        await using Fixture test = await Fixture.CreateAsync();
        await using (AppDbContext db = test.Open())
        {
            PrinterFileDeleteService service = test.Service(db);
            PrinterFileDeleteOperation intent = await test.IntentAsync(service);
            await service.ConfirmAsync(test.Actor, intent.Id, Consent(intent));
        }

        await using AppDbContext restarted = test.Open();
        PrinterFileDeleteService successor = test.Service(restarted);
        PrinterFileDeleteOperation replay = await test.IntentAsync(successor);
        Assert.Equal("Acknowledged", replay.Outcome);
        await successor.ConfirmAsync(test.Actor, replay.Id, Consent(replay));
        PrinterFileCommandException error = await Assert.ThrowsAsync<PrinterFileCommandException>(() =>
            successor.CreateAsync(test.Actor, "key", new(test.Location, 2)));
        Assert.Equal("idempotency_payload_mismatch", error.Reason);
        Assert.Equal(1, test.Backend.Sends);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task WeakDeletion_RequiresSeparateIrreversibleAndRaceConsentAsync(bool irreversible, bool race)
    {
        await using Fixture test = await Fixture.CreateAsync();
        await using AppDbContext db = test.Open();
        PrinterFileDeleteService service = test.Service(db);
        PrinterFileDeleteOperation intent = await test.IntentAsync(service);
        await Assert.ThrowsAsync<PrinterFileCommandException>(() => service.ConfirmAsync(test.Actor, intent.Id,
            new(intent.ConfirmationBinding, irreversible, race)));
        Assert.Equal(0, test.Backend.Sends);
        Assert.Null((await db.PrinterFileOperations.SingleAsync()).DeleteSentAtUtc);
    }

    [Fact]
    public async Task Consent_IsIntentActorAndObservationBound_NotQueueConsentAsync()
    {
        await using Fixture test = await Fixture.CreateAsync();
        await using AppDbContext db = test.Open();
        PrinterFileDeleteService service = test.Service(db);
        PrinterFileDeleteOperation first = await test.IntentAsync(service);
        PrinterFileDeleteOperation second = await service.CreateAsync(test.Actor, "other", new(test.Location, 1));
        await Assert.ThrowsAsync<PrinterFileCommandException>(() =>
            service.ConfirmAsync(test.Actor, second.Id, Consent(first)));
        Assert.Null(await service.GetAsync(Guid.NewGuid(), first.Id));
        Assert.Null(await service.ConfirmAsync(Guid.NewGuid(), first.Id, Consent(first)));
        await Assert.ThrowsAsync<PrinterFileCommandException>(() =>
            service.ConfirmAsync(test.Actor, first.Id, new("queue-snapshot-confirmation", true, true)));
        Assert.Equal(0, test.Backend.Sends);
    }

    [Fact]
    public async Task ConfirmAsync_RevokedAuthorityWithValidConsent_NeverAcquiresLeaseOrSendsAsync()
    {
        await using Fixture test = await Fixture.CreateAsync();
        await using AppDbContext db = test.Open();
        PrinterFileDeleteService service = test.Service(db);
        PrinterFileDeleteOperation intent = await test.IntentAsync(service);
        test.Allowed = false;
        bool acquired = false;
        test.Leases.AfterAcquire = () =>
        {
            acquired = true;
            return Task.CompletedTask;
        };

        Assert.Null(await service.ConfirmAsync(test.Actor, intent.Id, Consent(intent)));
        Assert.False(acquired);
        Assert.Equal(0, test.Backend.Sends);
        Assert.Null((await db.PrinterFileOperations.SingleAsync()).DeleteSentAtUtc);
    }

    [Theory]
    [InlineData(PrinterFileOperationState.SendingDelete, false)]
    [InlineData(PrinterFileOperationState.AwaitingDeleteConfirmation, true)]
    public async Task ConfirmAsync_OperationChangesDuringLeaseAcquisition_NeverSendsAsync(
        PrinterFileOperationState state, bool sent)
    {
        await using Fixture test = await Fixture.CreateAsync();
        await using AppDbContext db = test.Open();
        PrinterFileDeleteService service = test.Service(db);
        PrinterFileDeleteOperation intent = await test.IntentAsync(service);
        test.Leases.AfterAcquire = async () =>
        {
            await using AppDbContext concurrent = test.Open();
            PrinterFileOperation operation = await concurrent.PrinterFileOperations.SingleAsync();
            operation.State = state;
            operation.DeleteSentAtUtc = sent ? DateTime.UtcNow : null;
            await concurrent.SaveChangesAsync();
        };

        PrinterFileDeleteOperation result = (await service.ConfirmAsync(test.Actor, intent.Id, Consent(intent)))!;
        Assert.Equal(state, result.State);
        Assert.Equal(sent, result.DeleteSentAtUtc.HasValue);
        Assert.Equal(0, test.Backend.Sends);
        test.Physical.Verify(x => x.AcquireDirectAsync(It.IsAny<Guid>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task NativeConditional_PassesExactVersionWithoutRequiringWeakConsentAsync()
    {
        await using Fixture test = await Fixture.CreateAsync();
        test.Backend.Guarantee = PrinterFileDeleteGuarantee.NativeConditional;
        test.Backend.Version = "version-7";
        await using AppDbContext db = test.Open();
        (await db.PrinterFileLocations.SingleAsync(x => x.Id == test.Location)).ProviderVersion = "version-7";
        await db.SaveChangesAsync();
        PrinterFileDeleteService service = test.Service(db);
        PrinterFileDeleteOperation intent = await test.IntentAsync(service);
        PrinterFileDeleteOperation result = (await service.ConfirmAsync(test.Actor, intent.Id,
            new(intent.ConfirmationBinding, true, false)))!;
        Assert.Equal("Acknowledged", result.Outcome);
        Assert.Equal("version-7", test.Backend.DeletedVersion);
    }

    [Theory]
    [InlineData("printing")]
    [InlineData("paused")]
    [InlineData("busy")]
    [InlineData("unknown")]
    [InlineData("")]
    [InlineData("offline")]
    [InlineData("path_in_use")]
    public async Task NonIdleMissingOrContradictoryStatus_BlocksWithoutDeleteAsync(string activity)
    {
        await using Fixture test = await Fixture.CreateAsync();
        test.Status = new(test.Source, activity != "offline", "idle",
            JobName: activity == "path_in_use" ? "Part.gcode" : null)
        { RawActivityState = activity == "path_in_use" ? "idle" : activity };
        await using AppDbContext db = test.Open();
        PrinterFileDeleteService service = test.Service(db);
        PrinterFileDeleteOperation intent = await test.IntentAsync(service);
        PrinterFileDeleteOperation result = (await service.ConfirmAsync(test.Actor, intent.Id, Consent(intent)))!;
        Assert.Equal("Blocked", result.Outcome);
        Assert.Equal(0, test.Backend.Sends);
        Assert.Null(result.DeleteSentAtUtc);
    }

    [Theory]
    [InlineData("metadata", "source_changed_reconfirm")]
    [InlineData("partial", "source_observation_unavailable")]
    [InlineData("missing", "observed_absent_no_send")]
    [InlineData("barrier", "physical_fence_conflict")]
    [InlineData("revoke", "location_not_found")]
    [InlineData("fence", "source_ownership_lost")]
    public async Task FreshGuards_BlockMutationAsync(string scenario, string reason)
    {
        await using Fixture test = await Fixture.CreateAsync();
        await using AppDbContext db = test.Open();
        PrinterFileDeleteService service = test.Service(db);
        PrinterFileDeleteOperation intent = await test.IntentAsync(service);
        switch (scenario)
        {
            case "metadata":
                test.Backend.Modified = "new";
                break;
            case "partial":
                test.Backend.Partial = true;
                break;
            case "missing":
                test.Backend.Missing = true;
                break;
            case "barrier":
                test.Physical.Setup(x => x.AcquireDirectAsync(It.IsAny<Guid>(), It.IsAny<string>(),
                        It.IsAny<string>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new PrinterActuationResult(PrinterActuationResultCode.PrinterBusy));
                break;
            case "revoke":
                test.AfterStatus = () => test.Allowed = false;
                break;
            case "fence":
                test.AfterStatus = () => test.Leases.Current = false;
                break;
        }

        PrinterFileDeleteOperation result = (await service.ConfirmAsync(test.Actor, intent.Id, Consent(intent)))!;
        Assert.Equal(reason, result.ReasonCode);
        Assert.Equal(0, test.Backend.Sends);
        Assert.Null(result.DeleteSentAtUtc);
    }

    [Fact]
    public async Task SharedCopyOrScanLease_BlocksConfirmationWithoutAcquiringPhysicalBarrierAsync()
    {
        await using Fixture test = await Fixture.CreateAsync();
        await using AppDbContext db = test.Open();
        PrinterFileDeleteService service = test.Service(db);
        PrinterFileDeleteOperation intent = await test.IntentAsync(service);
        await using IPrinterFileLease? copying = await test.Leases.TryAcquireAsync(test.Source, true);
        Assert.NotNull(copying);
        await Assert.ThrowsAsync<PrinterFileCommandException>(() =>
            service.ConfirmAsync(test.Actor, intent.Id, Consent(intent)));
        test.Physical.Verify(x => x.AcquireDirectAsync(It.IsAny<Guid>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal(0, test.Backend.Sends);
    }

    [Fact]
    public async Task TimeoutAfterPossibleSend_RemainsUnknownAcrossRestart_ReconcileNeverReplaysAsync()
    {
        await using Fixture test = await Fixture.CreateAsync();
        test.Backend.Outcome = PrinterFileDeleteOutcome.OutcomeUnknown;
        Guid id;
        await using (AppDbContext db = test.Open())
        {
            PrinterFileDeleteService service = test.Service(db);
            PrinterFileDeleteOperation intent = await test.IntentAsync(service);
            id = intent.Id;
            Assert.Equal("OutcomeUnknown",
                (await service.ConfirmAsync(test.Actor, id, Consent(intent)))!.Outcome);
        }

        test.Backend.Missing = true;
        await using AppDbContext restarted = test.Open();
        PrinterFileDeleteService successor = test.Service(restarted);
        PrinterFileDeleteOperation replay = (await successor.GetAsync(test.Actor, id))!;
        await successor.ConfirmAsync(test.Actor, id, Consent(replay));
        PrinterFileDeleteOperation absent = (await successor.ReconcileAsync(test.Actor, id))!;
        Assert.Equal("OutcomeUnknown", absent.Outcome);
        Assert.Equal("outcome_unknown_observed_absent", absent.ReasonCode);
        Assert.Equal(PrinterFileAvailability.Available,
            (await restarted.PrinterFileLocations.SingleAsync(x => x.Id == test.Location)).Availability);
        test.Backend.Missing = false; // Another client recreated exactly the same metadata/path.
        PrinterFileDeleteOperation present = (await successor.ReconcileAsync(test.Actor, id))!;
        Assert.Equal("outcome_unknown_observed_present", present.ReasonCode);
        await successor.ConfirmAsync(test.Actor, id, Consent(replay));
        Assert.Equal(1, test.Backend.Sends);
        Assert.Equal(2, (await restarted.PrinterFileLocations.SingleAsync(x => x.Id == test.Location)).ObservationRevision);
        await Assert.ThrowsAsync<PrinterFileCommandException>(() =>
            successor.CreateAsync(test.Actor, "stale", new(test.Location, 1)));
        PrinterFileDeleteOperation fresh = await successor.CreateAsync(test.Actor, "fresh", new(test.Location, 2));
        Assert.NotEqual(replay.ConfirmationBinding, fresh.ConfirmationBinding);
        test.Physical.Verify(x => x.MarkDirectUnknownAsync(It.IsAny<PrinterActuationLease>(),
            "delete_outcome_unknown", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CrashAtSendBoundary_RecoveryIsReadOnlyEvenIfNoMutationWasActuallySentAsync()
    {
        await using Fixture test = await Fixture.CreateAsync();
        Guid id;
        await using (AppDbContext db = test.Open())
        {
            PrinterFileDeleteOperation intent = await test.IntentAsync(test.Service(db));
            id = intent.Id;
            PrinterFileOperation operation = await db.PrinterFileOperations.SingleAsync();
            operation.State = PrinterFileOperationState.SendingDelete;
            operation.DeleteSentAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        await using AppDbContext restarted = test.Open();
        PrinterFileDeleteService successor = test.Service(restarted);
        PrinterFileDeleteOperation unknown = (await successor.GetAsync(test.Actor, id))!;
        Assert.Equal("OutcomeUnknown", unknown.Outcome);
        await successor.ConfirmAsync(test.Actor, id, Consent(unknown));
        await Assert.ThrowsAsync<PrinterFileCommandException>(() =>
            successor.CreateAsync(test.Actor, "new-key-with-old-observation", new(test.Location, 1)));
        await successor.ReconcileAsync(test.Actor, id);
        Assert.Equal(0, test.Backend.Sends);
    }

    [Fact]
    public async Task AckFollowedByLostPublicationClaim_IsUnknownAfterRestart_NotAFalseTombstoneAsync()
    {
        await using Fixture test = await Fixture.CreateAsync();
        Guid id;
        await using (AppDbContext db = test.Open())
        {
            PrinterFileDeleteService service = test.Service(db);
            PrinterFileDeleteOperation intent = await test.IntentAsync(service);
            id = intent.Id;
            test.Backend.AfterSend = () => test.Leases.Current = false;
            await Assert.ThrowsAsync<PrinterFileCommandException>(() =>
                service.ConfirmAsync(test.Actor, id, Consent(intent)));
        }

        await using AppDbContext restarted = test.Open();
        PrinterFileDeleteService successor = test.Service(restarted);
        PrinterFileDeleteOperation unknown = (await successor.GetAsync(test.Actor, id))!;
        Assert.Equal("OutcomeUnknown", unknown.Outcome);
        await successor.ConfirmAsync(test.Actor, id, Consent(unknown));
        Assert.Equal(1, test.Backend.Sends);
        Assert.Equal(PrinterFileAvailability.Available,
            (await restarted.PrinterFileLocations.SingleAsync(x => x.Id == test.Location)).Availability);
    }

    [Theory]
    [InlineData(PrinterFileDeleteOutcome.Rejected, "Blocked")]
    [InlineData(PrinterFileDeleteOutcome.ObservedAbsent, "ObservedAbsent")]
    public async Task NonAcknowledgedOutcome_IsNeverSuccessfulOrAutomaticallyRetriedAsync(
        PrinterFileDeleteOutcome outcome, string expected)
    {
        await using Fixture test = await Fixture.CreateAsync();
        test.Backend.Outcome = outcome;
        await using AppDbContext db = test.Open();
        PrinterFileDeleteService service = test.Service(db);
        PrinterFileDeleteOperation intent = await test.IntentAsync(service);
        PrinterFileDeleteOperation result = (await service.ConfirmAsync(test.Actor, intent.Id, Consent(intent)))!;
        Assert.Equal(expected, result.Outcome);
        await service.ConfirmAsync(test.Actor, intent.Id, Consent(intent));
        Assert.Equal(1, test.Backend.Sends);
        Assert.Equal(PrinterFileAvailability.Available,
            (await db.PrinterFileLocations.SingleAsync(x => x.Id == test.Location)).Availability);
    }

    [Fact]
    public async Task LatePreDeleteScan_CannotResurrectTombstoneAsync()
    {
        await using Fixture test = await Fixture.CreateAsync();
        await using AppDbContext db = test.Open();
        var cache = new Mock<IPrinterStatusCacheReader>();
        cache.Setup(x => x.GetSnapshot(test.Source)).Returns(() =>
            new PrinterStatusCacheSnapshot(test.Status, DateTime.UtcNow));
        var realLeases = new PrinterFileLeaseCoordinator(test.Factory, cache.Object,
            NullLogger<PrinterFileLeaseCoordinator>.Instance);
        PrinterFileDeleteService service = test.Service(db, realLeases);
        PrinterFileDeleteOperation intent = await test.IntentAsync(service);
        await using IPrinterFileLease stale = (await realLeases.TryAcquireAsync(test.Source, true))!;
        PrinterFileInventoryResult oldResult = test.Backend.Inventory();
        // Simulate a stalled scanner whose database claim expired while its read response
        // remained in flight. The old lease object still exists, but a successor owns the fence.
        await db.PrinterFileScanStates.Where(x => x.SourcePrinterId == test.Source)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.LeaseExpiresAtUtc, DateTime.UtcNow.AddMinutes(-1)));
        await db.PrinterFileIoSlots.Where(x => x.SourcePrinterId == test.Source)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.LeaseExpiresAtUtc, DateTime.UtcNow.AddMinutes(-1)));
        await service.ConfirmAsync(test.Actor, intent.Id, Consent(intent));
        var catalog = new PrinterFileCatalogService(test.Factory, test.Printers.Object,
            Mock.Of<IBackendCapabilityFactory>(), realLeases, cache.Object,
            NullLogger<PrinterFileCatalogService>.Instance);
        Assert.False(await catalog.PublishAsync(stale, oldResult));
        db.ChangeTracker.Clear();
        Assert.Equal(PrinterFileAvailability.Deleted,
            (await db.PrinterFileLocations.SingleAsync(x => x.Id == test.Location)).Availability);
    }

    [Fact]
    public async Task CompletePostDeleteScan_IdenticalRecreatedPathIsNewObservation_NotOldConsentAsync()
    {
        await using Fixture test = await Fixture.CreateAsync();
        await using AppDbContext db = test.Open();
        var cache = new Mock<IPrinterStatusCacheReader>();
        cache.Setup(x => x.GetSnapshot(test.Source)).Returns(() =>
            new PrinterStatusCacheSnapshot(test.Status, DateTime.UtcNow));
        var realLeases = new PrinterFileLeaseCoordinator(test.Factory, cache.Object,
            NullLogger<PrinterFileLeaseCoordinator>.Instance);
        PrinterFileDeleteService service = test.Service(db, realLeases);
        PrinterFileDeleteOperation intent = await test.IntentAsync(service);
        await service.ConfirmAsync(test.Actor, intent.Id, Consent(intent));
        await using IPrinterFileLease? postDelete = await realLeases.TryAcquireAsync(test.Source, true);
        Assert.NotNull(postDelete);
        var catalog = new PrinterFileCatalogService(test.Factory, test.Printers.Object,
            Mock.Of<IBackendCapabilityFactory>(), realLeases, cache.Object,
            NullLogger<PrinterFileCatalogService>.Instance);
        Assert.True(await catalog.PublishAsync(postDelete, test.Backend.Inventory()));
        db.ChangeTracker.Clear();
        PrinterFileLocation recreated = await db.PrinterFileLocations.SingleAsync(x => x.Id == test.Location);
        Assert.Equal(PrinterFileAvailability.Available, recreated.Availability);
        Assert.True(recreated.ObservationRevision > 2);
        await service.ConfirmAsync(test.Actor, intent.Id, Consent(intent));
        Assert.Equal(1, test.Backend.Sends);
        PrinterFileDeleteOperation fresh = await service.CreateAsync(test.Actor, "recreated",
            new(test.Location, recreated.ObservationRevision));
        Assert.NotEqual(intent.ConfirmationBinding, fresh.ConfirmationBinding);
    }

    private static PrinterFileDeleteConfirmation Consent(PrinterFileDeleteOperation operation) =>
        new(operation.ConfirmationBinding, true, true);

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection = new("Data Source=:memory:");
        private DbContextOptions<AppDbContext> options = null!;
        public Guid Actor { get; } = Guid.NewGuid();
        public Guid Source { get; } = Guid.NewGuid();
        public Guid Location { get; } = Guid.NewGuid();
        public Guid Job { get; } = Guid.NewGuid();
        public Backend Backend { get; } = new();
        public bool Allowed { get; set; } = true;
        public Action? AfterStatus { get; set; }
        public PrinterStatusDto Status { get; set; } = null!;
        public Mock<IPrintersRepository> Printers { get; } = new();
        public Mock<IPrinterPhysicalActuationService> Physical { get; } = new();
        public Mock<IPrinterFileAccessService> Access { get; } = new();
        public Mock<IBackendClientFactory> Backends { get; } = new();
        public Mock<IPrinterStatusClientFactory> Statuses { get; } = new();
        public LeaseCoordinator Leases { get; } = new();
        public IDbContextFactory<AppDbContext> Factory { get; private set; } = null!;

        public AppDbContext Open() => new(options);

        public PrinterFileDeleteService Service(AppDbContext db, IPrinterFileLeaseCoordinator? coordinator = null) => new(
            db, Access.Object, coordinator ?? Leases, Backends.Object, Printers.Object, Statuses.Object, Physical.Object);

        public Task<PrinterFileDeleteOperation> IntentAsync(PrinterFileDeleteService service) =>
            service.CreateAsync(Actor, "key", new(Location, 1));

        public static async Task<Fixture> CreateAsync()
        {
            Fixture test = new();
            await test.connection.OpenAsync();
            test.options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(test.connection).Options;
            var factory = new Mock<IDbContextFactory<AppDbContext>>();
            factory.Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => test.Open());
            test.Factory = factory.Object;
            test.Status = new(test.Source, true, "idle") { RawActivityState = "idle" };
            test.Access.Setup(x => x.CanActAsync(test.Actor, test.Source, PrinterFileAction.Delete,
                null, It.IsAny<CancellationToken>())).ReturnsAsync(() => test.Allowed);
            test.Backends.Setup(x => x.IsBackendSupported(It.IsAny<PrinterBackend>())).Returns(true);
            test.Backends.Setup(x => x.GetClient(It.IsAny<int>())).Returns(test.Backend);
            var status = new Mock<IPrinterStatusClient>();
            status.Setup(x => x.GetPrinterStatusAsync(It.IsAny<Printer>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() =>
                {
                    test.AfterStatus?.Invoke();
                    return test.Status;
                });
            test.Statuses.Setup(x => x.GetStatusClient(It.IsAny<int>())).Returns(status.Object);
            var physicalLease = new PrinterActuationLease(Guid.NewGuid(), test.Source, null,
                "printer_file_delete", test.Actor.ToString());
            test.Physical.Setup(x => x.AcquireDirectAsync(test.Source, test.Actor.ToString(),
                    "printer_file_delete", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PrinterActuationResult(PrinterActuationResultCode.Accepted, physicalLease));
            test.Physical.Setup(x => x.RevalidateDirectAsync(It.IsAny<PrinterActuationLease>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PrinterActuationResult(PrinterActuationResultCode.Accepted, physicalLease));
            test.Physical.Setup(x => x.CompleteDirectAsync(It.IsAny<PrinterActuationLease>(),
                It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
            test.Physical.Setup(x => x.MarkDirectUnknownAsync(It.IsAny<PrinterActuationLease>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
            await using AppDbContext db = test.Open();
            await db.Database.EnsureCreatedAsync();
            Manufacturer manufacturer = new() { Id = Guid.NewGuid(), Name = "test" };
            PrinterModel model = new() { Id = Guid.NewGuid(), Name = "test", Manufacturer = manufacturer };
            Printer printer = new()
            {
                Id = test.Source,
                Name = "source",
                Manufacturer = manufacturer,
                Model = model,
                ServerUrl = "http://printer.invalid",
                IsEnabled = true,
                Credential = PrinterCredential.FromApiKey("credential"),
            };
            test.Printers.Setup(x => x.FindByIdWithIncludesAsync(test.Source, It.IsAny<CancellationToken>()))
                .ReturnsAsync(printer);
            FolderNode folder = new() { Id = Guid.NewGuid(), Path = "/", FolderType = "gcode" };
            GcodeFile asset = new()
            {
                Id = Guid.NewGuid(),
                Name = "owned",
                FileName = "owned.gcode",
                FilePath = "/",
                Description = "owned bytes remain",
                FolderId = folder.Id,
            };
            db.AddRange(printer, folder, asset, new PrintJob { Id = test.Job, GcodeFile = asset });
            db.PrinterFileLocations.AddRange(new PrinterFileLocation
            {
                Id = test.Location,
                SourcePrinterId = test.Source,
                RegisteredPrinterId = test.Source,
                StorageRoot = "gcodes",
                RelativePath = "Part.gcode",
                SizeBytes = 10,
                ModifiedValue = "1",
                IdentityKey = PrinterInventoryPath.ComputeIdentity("gcodes", "Part.gcode"),
                Availability = PrinterFileAvailability.Available,
            }, new PrinterFileLocation
            {
                Id = Guid.NewGuid(),
                SourcePrinterId = test.Source,
                RegisteredPrinterId = test.Source,
                StorageRoot = "gcodes",
                RelativePath = "Other.gcode",
                IdentityKey = PrinterInventoryPath.ComputeIdentity("gcodes", "Other.gcode"),
                Availability = PrinterFileAvailability.Available,
            });
            db.PrinterFileOrigins.Add(new()
            {
                Id = Guid.NewGuid(),
                OperationId = Guid.NewGuid(),
                SourcePrinterId = test.Source,
                ActorId = test.Actor,
                LocationId = test.Location,
                GcodeFileId = asset.Id,
                StorageRoot = "gcodes",
                RelativePath = "Part.gcode",
                ContentSha256 = new('A', 64),
            });
            db.PrinterFileScanStates.Add(new()
            {
                Id = test.Source,
                SourcePrinterId = test.Source,
                RegisteredPrinterId = test.Source,
                ConfigurationFingerprint = PrinterFileIndexPolicy.ConfigurationFingerprint(printer),
            });
            await db.SaveChangesAsync();
            test.Backend.BeforeSend = async () =>
            {
                await using AppDbContext persisted = test.Open();
                PrinterFileOperation operation = await persisted.PrinterFileOperations
                    .SingleAsync(x => x.State == PrinterFileOperationState.SendingDelete);
                test.Backend.BoundaryWasDurable = operation.DeleteSentAtUtc.HasValue &&
                    operation.DeleteConfirmedAtUtc.HasValue && operation.DeleteConfirmationSha256 is not null;
            };
            return test;
        }

        public ValueTask DisposeAsync() => connection.DisposeAsync();
    }

    private sealed class Backend : IBackendClient, ISupportsSafePrinterFileDelete, ISupportsFileInventory
    {
        public int Sends { get; private set; }
        public bool BoundaryWasDurable { get; set; }
        public bool Missing { get; set; }
        public bool Partial { get; set; }
        public string Modified { get; set; } = "1";
        public string? Version { get; set; }
        public string? DeletedVersion { get; private set; }
        public PrinterCredential? Credential { get; private set; }
        public Func<Task>? BeforeSend { get; set; }
        public Action? AfterSend { get; set; }
        public PrinterFileDeleteOutcome Outcome { get; set; } = PrinterFileDeleteOutcome.Acknowledged;
        public PrinterFileDeleteGuarantee Guarantee { get; set; } = PrinterFileDeleteGuarantee.CurrentExactPath;

        public PrinterFileDeleteGuarantee GetDeleteGuarantee(string storageRoot, string relativePath) =>
            storageRoot == "gcodes" ? Guarantee : PrinterFileDeleteGuarantee.Unsupported;

        public async Task<PrinterFileDeleteResult> DeleteExactFileAsync(string baseUrl, string storageRoot,
            string relativePath, string? providerVersion, PrinterCredential? credential, CancellationToken ct)
        {
            if (BeforeSend is not null)
            {
                await BeforeSend();
            }

            Assert.Equal("gcodes", storageRoot);
            Assert.Equal("Part.gcode", relativePath);
            Sends++;
            Credential = credential;
            DeletedVersion = providerVersion;
            AfterSend?.Invoke();
            return new(Outcome);
        }

        public PrinterFileInventoryResult Inventory() => new(
            Partial ? PrinterFileInventoryOutcome.Partial : PrinterFileInventoryOutcome.Complete,
            PrinterFileInventoryReason.None,
            Missing ? [] : [new("gcodes", "Part.gcode", 10, ProviderVersion: Version, ModifiedValue: Modified)],
            [new("gcodes", "", !Partial, PrinterFileInventoryEvidence.NativeRecursiveListing)]);

        public Task<PrinterFileInventoryResult> GetFileInventoryAsync(string baseUrl,
            PrinterCredential? credential = null, PrinterFileInventoryOptions? options = null,
            CancellationToken ct = default) => Task.FromResult(Inventory());
    }

    private sealed class LeaseCoordinator : IPrinterFileLeaseCoordinator
    {
        private bool occupied;
        private long fence;
        public bool Current { get; set; } = true;
        public Func<Task>? AfterAcquire { get; set; }

        public async Task<IPrinterFileLease?> TryAcquireAsync(Guid printerId, bool interactive, CancellationToken ct = default)
        {
            if (occupied)
            {
                return null;
            }

            occupied = true;
            if (AfterAcquire is not null)
            {
                await AfterAcquire();
            }

            return new Lease(this, printerId, ++fence);
        }

        private sealed class Lease(LeaseCoordinator owner, Guid printer, long fence) : IPrinterFileLease
        {
            private bool disposed;
            public Guid PrinterId => printer;
            public Guid OwnerId { get; } = Guid.NewGuid();
            public long Fence => fence;
            public long ConfigurationGeneration => 1;
            public long MutationGeneration => 0;
            public CancellationToken Token => CancellationToken.None;
            public Task<bool> IsCurrentAsync(CancellationToken ct = default) =>
                Task.FromResult(!disposed && owner.Current && owner.fence == fence);
            public Task<bool> FencePublicationAsync(AppDbContext db, CancellationToken ct = default) => IsCurrentAsync(ct);
            public ValueTask DisposeAsync()
            {
                disposed = true;
                owner.occupied = false;
                return ValueTask.CompletedTask;
            }
        }
    }
}
