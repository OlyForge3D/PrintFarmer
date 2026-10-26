using System.Security.Cryptography;
using System.Text;
using Farm.Infrastructure.Contracts.Printers;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Printers;
using Farm.Infrastructure.Services.FileManagement;
using Farm.Infrastructure.Services.FolderManagement;
using Farm.Infrastructure.Services.Gcode.PrinterFiles;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.StorageManagement;
using Farm.Infrastructure.Settings;
using Farm.Modules.Gcode.Controllers;
using Farm.Modules.Gcode.Services.Gcode;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Farm.Modules.Gcode.Tests.Services.Gcode;

public sealed class PrinterFileCopyServiceTests
{
    [Fact]
    public async Task PublicationFailure_StopsAfterThreeLocalAttemptsAndRefundsWithoutRedownloadAsync()
    {
        PublicationFailureInterceptor failure = new();
        await using Fixture test = await Fixture.CreateAsync(interceptor: failure);
        await test.CreateCommandAsync("poison-publication");
        await test.ProcessAsync();
        await using AsyncServiceScope scope = test.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        PrinterFileOperation saved = await db.PrinterFileOperations.SingleAsync();
        Assert.Equal(3, failure.Attempts);
        Assert.Equal(PrinterFileOperationState.Failed, saved.State);
        Assert.Equal("publication_failed", saved.ReasonCode);
        Assert.Equal(0, saved.ReservedBytes);
        Assert.Null(saved.WorkerId);
        Assert.Empty(await db.GcodeFiles.ToArrayAsync());
        Assert.Empty(await db.PrinterFileOrigins.ToArrayAsync());
        Assert.False(await scope.ServiceProvider.GetRequiredService<PrinterFileCopyProcessor>()
            .ProcessNextAsync(CancellationToken.None));
        Assert.Equal(1, test.Backend.Copies);
        Assert.Empty(Directory.EnumerateFiles(test.Root, "*.gcode"));
        Assert.Empty(Directory.EnumerateFiles(Path.Join(test.Root, ".printer-file-staging")));
    }

    [Fact]
    public async Task Dedup_HeartbeatsWhileSlowLocalValidationOutlastsRemainingWorkerLeaseAsync()
    {
        await using Fixture test = await Fixture.CreateAsync();
        await test.CreateCommandAsync("winner");
        await test.ProcessAsync();
        PrinterFileCopyOperation second = await test.CreateCommandAsync("slow-dedup");
        test.Access.BeforeLocalCheck = async () =>
        {
            test.Access.BeforeLocalCheck = null;
            await using AsyncServiceScope scope = test.Services.CreateAsyncScope();
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.PrinterFileOperations.Where(x => x.Id == second.Id)
                .ExecuteUpdateAsync(update => update.SetProperty(x => x.WorkerLeaseExpiresAtUtc, DateTime.UtcNow.AddSeconds(1)));
            await Task.Delay(TimeSpan.FromSeconds(5));
        };
        await test.ProcessAsync();
        await using AsyncServiceScope verify = test.Services.CreateAsyncScope();
        AppDbContext current = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        PrinterFileOperation saved = await current.PrinterFileOperations.SingleAsync(x => x.Id == second.Id);
        Assert.Equal(PrinterFileOperationState.LocalReady, saved.State);
        Assert.Equal(0, saved.ReservedBytes);
        Assert.Equal(1, saved.WorkerFence);
        Assert.Equal(2, test.Backend.Copies);
        Assert.Single(await current.GcodeFiles.ToArrayAsync());
        Assert.Equal(2, await current.PrinterFileOrigins.CountAsync());
    }

    [Fact]
    public async Task Copy_PublishesActualBytesAndIndependentOrigin_WithoutJobsAsync()
    {
        await using Fixture test = await Fixture.CreateAsync();
        PrinterFileCopyOperation operation = await test.CreateCommandAsync("copy");
        Assert.Equal(PrinterFileOperationState.Pending, operation.State);
        Assert.Equal(0, test.Backend.Copies);

        await test.ProcessAsync();
        await using AsyncServiceScope scope = test.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        PrinterFileOperation saved = await db.PrinterFileOperations.SingleAsync();
        GcodeFile file = await db.GcodeFiles.SingleAsync();
        PrinterFileOrigin origin = await db.PrinterFileOrigins.SingleAsync();
        Assert.Equal(operation.Id, file.Id);
        Assert.Equal(PrinterFileOperationState.LocalReady, saved.State);
        Assert.Equal(test.Digest, saved.ContentSha256);
        Assert.Equal(test.Content.Length, saved.ReservedBytes);
        Assert.Equal(PrinterFileVerification.UnverifiedSnapshot, saved.Verification);
        Assert.Null(file.SourcePrinterId);
        Assert.Null(file.SourceArtifactId);
        Assert.Null(file.PromotionOperationId);
        Assert.False(file.IsImmutable);
        Assert.Equal(file.Id, origin.GcodeFileId);
        Assert.Equal(test.SourceId, origin.SourcePrinterId);
        Assert.Equal(test.Digest, origin.ContentSha256);
        Assert.Empty(await db.Set<PrintJob>().ToListAsync());
        Assert.Empty(await db.PrinterFileOperationJobs.ToListAsync());
        Assert.Equal(2, test.Backend.Inventories);
        Assert.Equal(1, test.Lease.Fences);
        Assert.Equal("credential", test.Backend.ReceivedCredential?.ApiKey);
        Assert.Equal(test.Content, await File.ReadAllBytesAsync(test.FullPath(file)));
    }

    [Fact]
    public async Task Queue_StopsAtLocalReadyForAdmissionValidation_AndCannotRecaptureBoundBytesAsync()
    {
        await using Fixture test = await Fixture.CreateAsync();
        PrinterFileCopyOperation operation = await test.CreateCommandAsync("queue", PrinterFileAction.Queue);
        await test.ProcessAsync();
        await using AsyncServiceScope scope = test.Services.CreateAsyncScope();
        IPrinterFileCopyService commands = scope.ServiceProvider.GetRequiredService<IPrinterFileCopyService>();
        PrinterFileCopyOperation saved = (await commands.GetAsync(test.Actor, operation.Id))!;
        Assert.Equal(PrinterFileOperationState.LocalReady, saved.State);
        Assert.Equal(PrinterFileVerification.UnverifiedSnapshot, saved.Verification);
        Assert.NotNull(saved.GcodeFileId);
        await Assert.ThrowsAsync<PrinterFileCommandException>(() => commands.RetryAsync(test.Actor, operation.Id));
        Assert.Equal(1, test.Backend.Copies);
    }

    [Fact]
    public async Task Replay_ReusesOperation_ChangedPayloadConflictsAsync()
    {
        await using Fixture test = await Fixture.CreateAsync();
        PrinterFileCopyOperation original = await test.CreateCommandAsync("key");
        PrinterFileCopyOperation replay = await test.CreateCommandAsync("key");
        Assert.Equal(original.Id, replay.Id);
        await using AsyncServiceScope scope = test.Services.CreateAsyncScope();
        IPrinterFileCopyService commands = scope.ServiceProvider.GetRequiredService<IPrinterFileCopyService>();
        PrinterFileCommandException error = await Assert.ThrowsAsync<PrinterFileCommandException>(() =>
            commands.CreateAsync(test.Actor, "key", new(test.LocationId, 1, "/other")));
        Assert.Equal("idempotency_payload_mismatch", error.Reason);
        Assert.Equal(409, error.StatusCode);
        Assert.Equal(0, test.Backend.Copies);
    }

    [Fact]
    public async Task Dedup_ChargesZeroAndPreservesWinnerMetadataAndPromotionLineageAsync()
    {
        await using Fixture test = await Fixture.CreateAsync();
        await test.CreateCommandAsync("first");
        await test.ProcessAsync();
        Guid assetId;
        await using (AsyncServiceScope scope = test.Services.CreateAsyncScope())
        {
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            GcodeFile winner = await db.GcodeFiles.SingleAsync();
            assetId = winner.Id;
            winner.Name = "Existing name";
            winner.Description = "Existing SKU/model association";
            winner.SourceArtifactId = Guid.NewGuid();
            winner.IsImmutable = true;
            await db.SaveChangesAsync();
        }

        PrinterFileCopyOperation second = await test.CreateCommandAsync("second");
        await test.ProcessAsync();
        await using AsyncServiceScope verify = test.Services.CreateAsyncScope();
        AppDbContext current = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        GcodeFile existing = await current.GcodeFiles.SingleAsync();
        PrinterFileOperation saved = await current.PrinterFileOperations.SingleAsync(x => x.Id == second.Id);
        Assert.Equal(assetId, saved.GcodeFileId);
        Assert.Equal(0, saved.ReservedBytes);
        Assert.Equal("Existing name", existing.Name);
        Assert.Equal("Existing SKU/model association", existing.Description);
        Assert.NotNull(existing.SourceArtifactId);
        Assert.True(existing.IsImmutable);
        Assert.Equal(2, await current.PrinterFileOrigins.CountAsync());
        Assert.Single(Directory.EnumerateFiles(test.Root, "*.gcode"));
    }

    [Theory]
    [InlineData(PrinterFileAction.Copy, PrinterGroupAccessLevel.View)]
    [InlineData(PrinterFileAction.Queue, PrinterGroupAccessLevel.Submit)]
    public async Task LocalRevocation_HidesGetListReplayAndMutationsDespiteSourceAuthorityAsync(
        PrinterFileAction action, PrinterGroupAccessLevel minimumAccess)
    {
        await using Fixture test = await Fixture.CreateAsync();
        PrinterFileCopyOperation original = await test.CreateCommandAsync("local-revoke", action);
        await test.ProcessAsync();
        await using AsyncServiceScope scope = test.Services.CreateAsyncScope();
        IPrinterFileCopyService commands = scope.ServiceProvider.GetRequiredService<IPrinterFileCopyService>();
        PrinterFileCopyOperation ready = (await commands.GetAsync(test.Actor, original.Id))!;
        Guid assetId = ready.GcodeFileId!.Value;
        await test.RestrictAssetAsync(assetId);
        test.Access.LocalAssetGrants[assetId] = null;
        test.Access.LocalChecks.Clear();

        Assert.True(await test.Access.CanReadAsync(test.Actor, test.SourceId));
        Assert.True(await test.Access.CanActAsync(test.Actor, test.SourceId, action, original.DestinationPrinterId));
        Assert.Null(await commands.GetAsync(test.Actor, original.Id));
        Assert.Empty(await commands.ListAsync(test.Actor));
        Assert.Null(await commands.CancelAsync(test.Actor, original.Id));
        Assert.Null(await commands.RetryAsync(test.Actor, original.Id));
        PrinterFileCopyCommand replay = new(test.LocationId, 1, "/", action, original.DestinationPrinterId);
        foreach (PrinterFileCopyCommand payload in new[] { replay, replay with { VirtualDirectory = "/changed" } })
        {
            PrinterFileCommandException error = await Assert.ThrowsAsync<PrinterFileCommandException>(() =>
                commands.CreateAsync(test.Actor, "local-revoke", payload));
            Assert.Equal(404, error.StatusCode);
            Assert.Equal("operation_not_found", error.Reason);
        }

        Assert.NotEmpty(test.Access.LocalChecks);
        Assert.All(test.Access.LocalChecks, check =>
        {
            Assert.Equal(assetId, check.AssetId);
            Assert.Equal(minimumAccess, check.MinimumAccess);
        });
    }

    [Theory]
    [InlineData(PrinterFileAction.Copy, PrinterGroupAccessLevel.View)]
    [InlineData(PrinterFileAction.Queue, PrinterGroupAccessLevel.Submit)]
    public async Task GroupRestrictedDedup_DeniesBeforeReadingWinnerAndDoesNotBindOrAddOriginAsync(
        PrinterFileAction action, PrinterGroupAccessLevel minimumAccess)
    {
        await using Fixture test = await Fixture.CreateAsync();
        PrinterFileCopyOperation first = await test.CreateCommandAsync("winner");
        await test.ProcessAsync();
        Guid groupId = await test.RestrictAssetAsync(first.Id);
        test.Access.LocalAssetGrants[first.Id] = null;
        test.Access.LocalChecks.Clear();
        int pathReads = test.LocalPathResolutions;

        PrinterFileCopyOperation second = await test.CreateCommandAsync("restricted", action);
        await test.ProcessAsync();
        await using AsyncServiceScope scope = test.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        PrinterFileOperation blocked = await db.PrinterFileOperations.SingleAsync(x => x.Id == second.Id);
        Assert.Equal(PrinterFileOperationState.Blocked, blocked.State);
        Assert.Equal("local_asset_access_denied", blocked.ReasonCode);
        Assert.Null(blocked.GcodeFileId);
        Assert.Null(blocked.ContentSha256);
        Assert.Equal(0, blocked.ReservedBytes);
        Assert.Equal(pathReads, test.LocalPathResolutions);
        Assert.Single(await db.PrinterFileOrigins.ToArrayAsync());
        GcodeFile winner = await db.GcodeFiles.SingleAsync();
        Assert.Equal(groupId, winner.PrinterGroupId);
        Assert.Equal(first.Id, winner.Id);
        Assert.Single(Directory.EnumerateFiles(test.Root, "*.gcode"));
        Assert.Contains(test.Access.LocalChecks,
            check => check.AssetId == first.Id && check.MinimumAccess == minimumAccess);
    }

    [Fact]
    public async Task ViewOnlyLocalGroupGrant_AllowsCopyDedupButNotQueueDedupAsync()
    {
        await using Fixture test = await Fixture.CreateAsync();
        PrinterFileCopyOperation first = await test.CreateCommandAsync("winner");
        await test.ProcessAsync();
        await test.RestrictAssetAsync(first.Id);
        test.Access.LocalAssetGrants[first.Id] = PrinterGroupAccessLevel.View;
        test.Access.LocalChecks.Clear();
        PrinterFileCopyOperation copy = await test.CreateCommandAsync("view-copy");
        await test.ProcessAsync();
        PrinterFileCopyOperation queue = await test.CreateCommandAsync("view-queue", PrinterFileAction.Queue);
        await test.ProcessAsync();

        await using AsyncServiceScope scope = test.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        PrinterFileOperation copied = await db.PrinterFileOperations.SingleAsync(x => x.Id == copy.Id);
        PrinterFileOperation blocked = await db.PrinterFileOperations.SingleAsync(x => x.Id == queue.Id);
        Assert.Equal(first.Id, copied.GcodeFileId);
        Assert.Equal(PrinterFileOperationState.LocalReady, copied.State);
        Assert.Null(blocked.GcodeFileId);
        Assert.Equal("local_asset_access_denied", blocked.ReasonCode);
        Assert.Equal(2, await db.PrinterFileOrigins.CountAsync());
        Assert.Contains(test.Access.LocalChecks, check => check.MinimumAccess == PrinterGroupAccessLevel.View);
        Assert.Contains(test.Access.LocalChecks, check => check.MinimumAccess == PrinterGroupAccessLevel.Submit);
    }

    [Theory]
    [InlineData(PrinterFileAction.Copy, PrinterGroupAccessLevel.View)]
    [InlineData(PrinterFileAction.Queue, PrinterGroupAccessLevel.Submit)]
    public async Task LocalDedupRevocationAtPublication_RechecksFreshAuthorityAndRollsBackBindingAsync(
        PrinterFileAction action, PrinterGroupAccessLevel minimumAccess)
    {
        await using Fixture test = await Fixture.CreateAsync();
        PrinterFileCopyOperation first = await test.CreateCommandAsync("winner");
        await test.ProcessAsync();
        await test.RestrictAssetAsync(first.Id);
        test.Access.LocalChecks.Clear();
        test.Lease.BeforeFence = () => test.Access.LocalAssetGrants[first.Id] = null;
        PrinterFileCopyOperation second = await test.CreateCommandAsync("revoke-at-fence", action);
        await test.ProcessAsync();

        await using AsyncServiceScope scope = test.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        PrinterFileOperation blocked = await db.PrinterFileOperations.SingleAsync(x => x.Id == second.Id);
        Assert.Equal("local_asset_access_denied", blocked.ReasonCode);
        Assert.Null(blocked.GcodeFileId);
        Assert.Null(blocked.ContentSha256);
        Assert.Equal(0, blocked.ReservedBytes);
        Assert.Single(await db.GcodeFiles.ToArrayAsync());
        Assert.Single(await db.PrinterFileOrigins.ToArrayAsync());
        Assert.True(test.Access.LocalChecks.Count >= 2);
        Assert.All(test.Access.LocalChecks, check =>
        {
            Assert.Equal(first.Id, check.AssetId);
            Assert.Equal(minimumAccess, check.MinimumAccess);
        });
    }

    [Fact]
    public async Task LocalAuthorityDeniedForNewAsset_RollsBackAssetOriginAndOperationBindingAsync()
    {
        await using Fixture test = await Fixture.CreateAsync();
        PrinterFileCopyOperation original = await test.CreateCommandAsync("deny-new");
        test.Access.LocalAllowed = false;
        await test.ProcessAsync();

        await using AsyncServiceScope scope = test.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        PrinterFileOperation blocked = await db.PrinterFileOperations.SingleAsync(x => x.Id == original.Id);
        Assert.Equal("local_asset_access_denied", blocked.ReasonCode);
        Assert.Null(blocked.GcodeFileId);
        Assert.Null(blocked.ContentSha256);
        Assert.Equal(0, blocked.ReservedBytes);
        Assert.Empty(await db.GcodeFiles.ToArrayAsync());
        Assert.Empty(await db.PrinterFileOrigins.ToArrayAsync());
        Assert.Empty(Directory.EnumerateFiles(test.Root, "*.gcode"));
        Assert.Contains(test.Access.LocalChecks, check => check.AssetId == original.Id);
    }

    [Theory]
    [InlineData("before", "source_changed", 0)]
    [InlineData("after", "source_changed", 1)]
    [InlineData("missing", "source_missing", 0)]
    [InlineData("offline", "source_restat_unavailable", 0)]
    [InlineData("size", "source_size_changed", 1)]
    [InlineData("fence", "source_ownership_lost", 1)]
    [InlineData("revoke", "source_authority_changed", 1)]
    public async Task UnsafeCapture_BlocksAndReleasesQuotaAndStagingAsync(string scenario, string reason, int copies)
    {
        await using Fixture test = await Fixture.CreateAsync();
        PrinterFileCopyOperation operation = await test.CreateCommandAsync("unsafe");
        switch (scenario)
        {
            case "before":
                test.Backend.Modified = "2";
                break;
            case "after":
                test.Backend.AfterCopy = () => test.Backend.Modified = "2";
                break;
            case "missing":
                test.Backend.Missing = true;
                break;
            case "offline":
                test.Backend.Offline = true;
                break;
            case "size":
                test.Backend.ReportedReceivedSize = test.Content.Length - 1;
                break;
            case "fence":
                test.Backend.AfterCopy = () => test.Lease.AllowPublication = false;
                break;
            case "revoke":
                test.Backend.AfterCopy = () => test.Access.Allowed = false;
                break;
        }

        await test.ProcessAsync();
        await using AsyncServiceScope scope = test.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        PrinterFileOperation saved = await db.PrinterFileOperations.SingleAsync(x => x.Id == operation.Id);
        Assert.Equal(reason, saved.ReasonCode);
        Assert.Equal(PrinterFileOperationState.Blocked, saved.State);
        Assert.Equal(0, saved.ReservedBytes);
        Assert.Equal(copies, test.Backend.Copies);
        Assert.Empty(await db.GcodeFiles.ToListAsync());
        Assert.Empty(await db.PrinterFileOrigins.ToListAsync());
        Assert.DoesNotContain(Directory.EnumerateFiles(test.Root, "*", SearchOption.AllDirectories),
            x => !x.EndsWith(".db", StringComparison.Ordinal) &&
                !x.Contains(".db-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RevokedBeforeWork_DeniesIoAndActorScopedPollAsync()
    {
        await using Fixture test = await Fixture.CreateAsync();
        PrinterFileCopyOperation operation = await test.CreateCommandAsync("revoke");
        test.Access.Allowed = false;
        await test.ProcessAsync();
        Assert.Equal(0, test.Backend.Copies);
        await using AsyncServiceScope scope = test.Services.CreateAsyncScope();
        IPrinterFileCopyService commands = scope.ServiceProvider.GetRequiredService<IPrinterFileCopyService>();
        Assert.Null(await commands.GetAsync(test.Actor, operation.Id));
        Assert.Null(await commands.GetAsync(Guid.NewGuid(), operation.Id));
        await Assert.ThrowsAsync<PrinterFileCommandException>(() =>
            commands.CreateAsync(test.Actor, "other", new(test.LocationId, 1)));
    }

    [Fact]
    public async Task UnknownSize_ReservesBound_AndCancelReleasesWithoutIoAsync()
    {
        await using Fixture test = await Fixture.CreateAsync(unknownSize: true);
        PrinterFileCopyOperation operation = await test.CreateCommandAsync("unknown");
        await using AsyncServiceScope scope = test.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(PrinterFileCopyService.MaximumBytes, (await db.PrinterFileOperations.SingleAsync()).ReservedBytes);
        IPrinterFileCopyService commands = scope.ServiceProvider.GetRequiredService<IPrinterFileCopyService>();
        await commands.CancelAsync(test.Actor, operation.Id);
        db.ChangeTracker.Clear();
        PrinterFileOperation cancelled = await db.PrinterFileOperations.SingleAsync();
        Assert.Equal(0, cancelled.ReservedBytes);
        Assert.Equal(PrinterFileOperationState.Cancelled, cancelled.State);
        Assert.Equal(0, test.Backend.Copies);
    }

    [Fact]
    public async Task Quota_DeniesSecondReservation_AndCancelMakesCapacityReusableAsync()
    {
        await using Fixture test = await Fixture.CreateAsync();
        test.UploadPolicy.DailyUploadLimitBytes = test.Content.Length;
        PrinterFileCopyOperation first = await test.CreateCommandAsync("first");
        PrinterFileCommandException error = await Assert.ThrowsAsync<PrinterFileCommandException>(() =>
            test.CreateCommandAsync("second"));
        Assert.Equal("daily_quota_exceeded", error.Reason);
        await using (AsyncServiceScope scope = test.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IPrinterFileCopyService>()
                .CancelAsync(test.Actor, first.Id);
        }

        await test.CreateCommandAsync("second");
        Assert.Equal(0, test.Backend.Copies);
    }

    [Fact]
    public async Task Restart_ExpiredWorkerReclaimsOriginalOperation_AndCleansOldAttemptAsync()
    {
        await using Fixture test = await Fixture.CreateAsync();
        PrinterFileCopyOperation original = await test.CreateCommandAsync("restart");
        string stage = Path.Join(test.Root, ".printer-file-staging");
        Directory.CreateDirectory(stage);
        string abandoned = Path.Join(stage, $"{original.Id:N}-1-abandoned.part");
        await File.WriteAllTextAsync(abandoned, "partial");
        await using (AsyncServiceScope scope = test.Services.CreateAsyncScope())
        {
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            PrinterFileOperation interrupted = await db.PrinterFileOperations.SingleAsync();
            interrupted.State = PrinterFileOperationState.Copying;
            interrupted.WorkerId = Guid.NewGuid();
            interrupted.WorkerFence = 1;
            interrupted.WorkerLeaseExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }

        await test.ProcessAsync();
        Assert.False(File.Exists(abandoned));
        await using AsyncServiceScope verify = test.Services.CreateAsyncScope();
        PrinterFileOperation saved = await verify.ServiceProvider.GetRequiredService<AppDbContext>()
            .PrinterFileOperations.SingleAsync();
        Assert.Equal(original.Id, saved.GcodeFileId);
        Assert.Equal(2, saved.WorkerFence);
        Assert.Equal(PrinterFileOperationState.LocalReady, saved.State);
    }

    [Fact]
    public async Task SameNameDifferentBytes_CreatesDistinctUnverifiedOwnedAssetsAsync()
    {
        await using Fixture test = await Fixture.CreateAsync();
        await test.CreateCommandAsync("first");
        await test.ProcessAsync();
        test.Backend.SubstituteContent = Encoding.UTF8.GetBytes("G28\nG1 X2 Y2\n");
        Assert.Equal(test.Content.Length, test.Backend.SubstituteContent.Length);
        await test.CreateCommandAsync("second");
        await test.ProcessAsync();
        await using AsyncServiceScope scope = test.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        GcodeFile[] files = await db.GcodeFiles.ToArrayAsync();
        Assert.Equal(2, files.Length);
        Assert.All(files, file => Assert.Equal("part.gcode", file.Name));
        Assert.Equal(2, files.Select(x => x.FileHash).Distinct().Count());
        Assert.All(await db.PrinterFileOperations.ToListAsync(),
            operation => Assert.Equal(PrinterFileVerification.UnverifiedSnapshot, operation.Verification));
    }

    [Fact]
    public async Task UnknownSize_SettlesActualBytesInsteadOfReservationCeilingAsync()
    {
        await using Fixture test = await Fixture.CreateAsync(unknownSize: true);
        await test.CreateCommandAsync("unknown");
        await test.ProcessAsync();
        await using AsyncServiceScope scope = test.Services.CreateAsyncScope();
        PrinterFileOperation operation = await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .PrinterFileOperations.SingleAsync();
        Assert.Equal(PrinterFileOperationState.LocalReady, operation.State);
        Assert.Equal(test.Content.Length, operation.ReservedBytes);
        Assert.Equal(test.Content.Length, operation.SizeBytes);
    }

    [Fact]
    public async Task PriorDayReservation_MustRebookAgainstTodaysBudgetBeforeIoAsync()
    {
        await using Fixture test = await Fixture.CreateAsync();
        test.UploadPolicy.DailyUploadLimitBytes = test.Content.Length;
        PrinterFileCopyOperation old = await test.CreateCommandAsync("yesterday");
        await using (AsyncServiceScope scope = test.Services.CreateAsyncScope())
        {
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            PrinterFileOperation operation = await db.PrinterFileOperations.SingleAsync();
            operation.UpdatedAtUtc = DateTime.UtcNow.AddDays(-1);
            await db.SaveChangesAsync();
        }

        await test.CreateCommandAsync("today");
        await test.ProcessAsync();
        await using AsyncServiceScope verify = test.Services.CreateAsyncScope();
        PrinterFileOperation priorDay = await verify.ServiceProvider.GetRequiredService<AppDbContext>()
            .PrinterFileOperations.SingleAsync(x => x.Id == old.Id);
        Assert.Equal(PrinterFileOperationState.Blocked, priorDay.State);
        Assert.Equal("daily_quota_exceeded", priorDay.ReasonCode);
        Assert.Equal(0, priorDay.ReservedBytes);
        Assert.Equal(0, test.Backend.Copies);
    }

    [Fact]
    public async Task CurrentPersistedQuotaOverridesStaleProcessPolicyBeforeIoAsync()
    {
        await using Fixture test = await Fixture.CreateAsync();
        PrinterFileCopyOperation original = await test.CreateCommandAsync("quota");
        await using (AsyncServiceScope scope = test.Services.CreateAsyncScope())
        {
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.AppSettingsEntities.Add(new()
            {
                Key = GcodeUploadSettings.SectionName,
                SettingsJson = System.Text.Json.JsonSerializer.Serialize(new GcodeUploadSettings
                {
                    DailyUploadLimitBytes = 1,
                }),
            });
            await db.SaveChangesAsync();
        }

        await test.ProcessAsync();
        await using AsyncServiceScope verify = test.Services.CreateAsyncScope();
        PrinterFileOperation operation = await verify.ServiceProvider.GetRequiredService<AppDbContext>()
            .PrinterFileOperations.SingleAsync(x => x.Id == original.Id);
        Assert.Equal("daily_quota_exceeded", operation.ReasonCode);
        Assert.Equal(0, operation.ReservedBytes);
        Assert.Equal(0, test.Backend.Copies);
    }

    [Theory]
    [InlineData(false, "dedup_asset_missing")]
    [InlineData(true, "dedup_asset_integrity_failed")]
    public async Task Dedup_DoesNotAdoptMissingOrCorruptLocalBytesAsync(bool corrupt, string reason)
    {
        await using Fixture test = await Fixture.CreateAsync();
        await test.CreateCommandAsync("first");
        await test.ProcessAsync();
        await using (AsyncServiceScope scope = test.Services.CreateAsyncScope())
        {
            GcodeFile file = await scope.ServiceProvider.GetRequiredService<AppDbContext>().GcodeFiles.SingleAsync();
            if (corrupt)
            {
                await File.WriteAllBytesAsync(test.FullPath(file), new byte[test.Content.Length]);
            }
            else
            {
                File.Delete(test.FullPath(file));
            }
        }

        PrinterFileCopyOperation second = await test.CreateCommandAsync("second");
        await test.ProcessAsync();
        await using AsyncServiceScope verify = test.Services.CreateAsyncScope();
        PrinterFileOperation operation = await verify.ServiceProvider.GetRequiredService<AppDbContext>()
            .PrinterFileOperations.SingleAsync(x => x.Id == second.Id);
        Assert.Equal(PrinterFileOperationState.Blocked, operation.State);
        Assert.Equal(reason, operation.ReasonCode);
        Assert.Equal(0, operation.ReservedBytes);
        Assert.Null(operation.GcodeFileId);
    }

    [Fact]
    public async Task Cleanup_ReconcilesPostCommitLeftoversWithoutDeletingOwnedBytesAsync()
    {
        await using Fixture test = await Fixture.CreateAsync();
        PrinterFileCopyOperation operation = await test.CreateCommandAsync("first");
        await test.ProcessAsync();
        string stage = Path.Join(test.Root, ".printer-file-staging");
        string stageLeftover = Path.Join(stage, $"{operation.Id:N}-0-abandoned.part");
        string candidateLeftover = Path.Join(test.Root, $"{operation.Id:N}-0-abandoned.gcode");
        await File.WriteAllTextAsync(stageLeftover, "partial");
        await File.WriteAllTextAsync(candidateLeftover, "unbound");
        await using AsyncServiceScope scope = test.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<PrinterFileCopyProcessor>()
            .CleanupCompletedAttemptsAsync(CancellationToken.None);
        Assert.False(File.Exists(stageLeftover));
        Assert.False(File.Exists(candidateLeftover));
        GcodeFile owned = await scope.ServiceProvider.GetRequiredService<AppDbContext>().GcodeFiles.SingleAsync();
        Assert.Equal(test.Content, await File.ReadAllBytesAsync(test.FullPath(owned)));
    }

    [Fact]
    public void Controller_RequiresInteractiveReadAndWriteForMutations()
    {
        var policies = typeof(PrinterFileCopyController).GetCustomAttributes(typeof(AuthorizeAttribute), true)
            .Cast<AuthorizeAttribute>().Select(x => x.Policy).ToArray();
        Assert.Contains("RequireInteractiveSession", policies);
        Assert.Contains("ModelRead", policies);
        foreach (string method in new[] { "CopyAsync", "RetryAsync", "CancelAsync" })
        {
            Assert.Contains(typeof(PrinterFileCopyController).GetMethod(method)!
                .GetCustomAttributes(typeof(AuthorizeAttribute), true).Cast<AuthorizeAttribute>(),
                x => x.Policy == "ModelWrite");
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public string Root { get; } = Path.Join(Path.GetTempPath(), "printer-copy-tests", Guid.NewGuid().ToString("N"));
        public Guid Actor { get; } = Guid.NewGuid();
        public Guid SourceId { get; } = Guid.NewGuid();
        public Guid LocationId { get; } = Guid.NewGuid();
        public byte[] Content { get; } = Encoding.UTF8.GetBytes("G28\nG1 X1 Y1\n");
        public string Digest => Convert.ToHexString(SHA256.HashData(Content));
        public ServiceProvider Services { get; private set; } = null!;
        public Backend Backend { get; private set; } = null!;
        public Access Access { get; private set; } = null!;
        public Lease Lease { get; private set; } = null!;
        public GcodeUploadSettings UploadPolicy { get; } = new() { DailyUploadLimitBytes = 500_000_000 };
        public int LocalPathResolutions { get; private set; }

        public static async Task<Fixture> CreateAsync(bool unknownSize = false, IInterceptor? interceptor = null)
        {
            Fixture test = new();
            Directory.CreateDirectory(test.Root);
            test.Access = new(test.Actor, test.SourceId);
            test.Lease = new(test.SourceId);
            test.Backend = new(test.Content, unknownSize);
            Manufacturer manufacturer = new() { Id = Guid.NewGuid(), Name = "test" };
            PrinterModel model = new() { Id = Guid.NewGuid(), Name = "test", Manufacturer = manufacturer };
            Printer printer = new()
            {
                Id = test.SourceId,
                Name = "source",
                Manufacturer = manufacturer,
                Model = model,
                ServerUrl = "http://printer.invalid",
                BackendPort = 80,
                IsEnabled = true,
                Credential = PrinterCredential.FromApiKey("credential"),
            };
            FolderNode folder = new() { Id = Guid.NewGuid(), Path = "/", FolderType = "gcode" };
            var settings = new Mock<ISettingsService>();
            settings.Setup(x => x.Get<GcodeUploadSettings>()).Returns(test.UploadPolicy);
            var storage = new Mock<IStoragePathService>();
            storage.Setup(x => x.GetGcodeStorageDirectory()).Returns(test.Root);
            var factory = new Mock<IBackendClientFactory>();
            factory.Setup(x => x.IsBackendSupported(It.IsAny<PrinterBackend>())).Returns(true);
            factory.Setup(x => x.GetClient(It.IsAny<int>())).Returns(test.Backend);
            var leases = new Mock<IPrinterFileLeaseCoordinator>();
            leases.Setup(x => x.TryAcquireAsync(test.SourceId, true, It.IsAny<CancellationToken>()))
                .ReturnsAsync(test.Lease);
            var printers = new Mock<IPrintersRepository>();
            printers.Setup(x => x.FindByIdWithIncludesAsync(test.SourceId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(printer);
            var folders = new Mock<IFolderManagementService>();
            folders.Setup(x => x.GetOrCreateFolderAsync("/", "gcode", It.IsAny<CancellationToken>()))
                .ReturnsAsync(folder);
            var files = new Mock<IStoredFileOperationsService>();
            files.Setup(x => x.GetFullFilePath(It.IsAny<StoredFile>()))
                .Callback<StoredFile>(_ => test.LocalPathResolutions++)
                .Returns<StoredFile>(test.FullPath);
            var ingest = new Mock<IPrinterFileLibraryIngest>();
            ingest.Setup(x => x.PrepareAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Guid id, string _, string name, string directory, string digest, long bytes, CancellationToken _) =>
                    new GcodeFile
                    {
                        Id = id,
                        Name = name,
                        FilePath = directory,
                        FileName = $"{id}.gcode",
                        FileHash = digest,
                        FileSizeBytes = bytes,
                        Source = GcodeSource.Upload,
                    });
            ServiceCollection services = new();
            services.AddDbContext<AppDbContext>(options =>
            {
                options.UseSqlite($"Data Source={Path.Join(test.Root, "copy.db")}");
                if (interceptor is not null)
                {
                    options.AddInterceptors(interceptor);
                }
            });
            services.AddSingleton<IPrinterFileAccessService>(test.Access);
            services.AddSingleton(settings.Object);
            services.AddSingleton(storage.Object);
            services.AddSingleton(factory.Object);
            services.AddSingleton(leases.Object);
            services.AddSingleton(printers.Object);
            services.AddSingleton(folders.Object);
            services.AddSingleton(files.Object);
            services.AddSingleton(ingest.Object);
            services.AddOptions<BackendTimeoutSettings>();
            services.AddScoped<PrinterFileCopyService>();
            services.AddScoped<IPrinterFileCopyService>(sp => sp.GetRequiredService<PrinterFileCopyService>());
            services.AddScoped<PrinterFileCopyProcessor>();
            test.Services = services.BuildServiceProvider();
            await using AsyncServiceScope scope = test.Services.CreateAsyncScope();
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.EnsureCreatedAsync();
            db.AddRange(printer, folder);
            db.PrinterFileLocations.Add(new()
            {
                Id = test.LocationId,
                RegisteredPrinterId = test.SourceId,
                SourcePrinterId = test.SourceId,
                StorageRoot = "gcodes",
                RelativePath = "part.gcode",
                DisplayName = "part.gcode",
                IdentityKey = PrinterInventoryPath.ComputeIdentity("gcodes", "part.gcode"),
                ObservationRevision = 1,
                ConfigurationGeneration = 1,
                MutationGeneration = 0,
                SizeBytes = unknownSize ? null : test.Content.Length,
                ModifiedValue = "1",
                Availability = PrinterFileAvailability.Available,
                FirstSeenAtUtc = DateTime.UtcNow,
                LastSeenAtUtc = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
            return test;
        }

        public async Task<PrinterFileCopyOperation> CreateCommandAsync(string key, PrinterFileAction action = PrinterFileAction.Copy)
        {
            await using AsyncServiceScope scope = Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IPrinterFileCopyService>()
                .CreateAsync(Actor, key, new(LocationId, 1, "/", action,
                    action == PrinterFileAction.Queue ? Guid.NewGuid() : null));
        }

        public async Task ProcessAsync()
        {
            await using AsyncServiceScope scope = Services.CreateAsyncScope();
            Assert.True(await scope.ServiceProvider.GetRequiredService<PrinterFileCopyProcessor>()
                .ProcessNextAsync(CancellationToken.None));
        }

        public async Task<Guid> RestrictAssetAsync(Guid assetId)
        {
            await using AsyncServiceScope scope = Services.CreateAsyncScope();
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            GcodeFile file = await db.GcodeFiles.SingleAsync(x => x.Id == assetId);
            PrinterGroup group = new() { Id = Guid.NewGuid(), Name = "Restricted local group" };
            db.PrinterGroups.Add(group);
            file.PrinterGroupId = group.Id;
            await db.SaveChangesAsync();
            return group.Id;
        }

        public string FullPath(StoredFile file) => Path.Join(Root, file.FilePath.Trim('/'), file.FileName);

        public async ValueTask DisposeAsync()
        {
            await Services.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class Access(Guid actor, Guid source) : IPrinterFileAccessService
    {
        public bool Allowed { get; set; } = true;
        public bool LocalAllowed { get; set; } = true;
        public Dictionary<Guid, PrinterGroupAccessLevel?> LocalAssetGrants { get; } = [];
        public List<(Guid AssetId, PrinterGroupAccessLevel MinimumAccess)> LocalChecks { get; } = [];
        public Func<Task>? BeforeLocalCheck { get; set; }
        public Task<bool> CanReadAsync(Guid actorId, Guid printerId, CancellationToken ct = default) =>
            Task.FromResult(Allowed && actorId == actor && printerId == source);
        public Task<bool> CanActAsync(Guid actorId, Guid printerId, PrinterFileAction action,
            Guid? destinationPrinterId = null, CancellationToken ct = default) => CanReadAsync(actorId, printerId, ct);
        public async Task<bool> CanAccessLocalAsync(Guid actorId, Guid gcodeFileId,
            PrinterGroupAccessLevel minimumAccess = PrinterGroupAccessLevel.View, CancellationToken ct = default)
        {
            if (BeforeLocalCheck is not null)
            {
                await BeforeLocalCheck();
            }

            LocalChecks.Add((gcodeFileId, minimumAccess));
            bool groupAllowed = !LocalAssetGrants.TryGetValue(gcodeFileId, out PrinterGroupAccessLevel? granted) ||
                (granted.HasValue && granted.Value >= minimumAccess);
            return Allowed && LocalAllowed && actorId == actor && groupAllowed;
        }
    }

    private sealed class PublicationFailureInterceptor : SaveChangesInterceptor
    {
        public int Attempts { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<PrinterFileOrigin>().Any(x => x.State == EntityState.Added))
            {
                Attempts++;
                throw new DbUpdateException("simulated deterministic publication failure");
            }

            return ValueTask.FromResult(result);
        }
    }

    private sealed class Lease(Guid printerId) : IPrinterFileLease
    {
        public Guid PrinterId => printerId;
        public Guid OwnerId { get; } = Guid.NewGuid();
        public long Fence => 1;
        public long ConfigurationGeneration => 1;
        public long MutationGeneration => 0;
        public CancellationToken Token => CancellationToken.None;
        public bool AllowPublication { get; set; } = true;
        public Action? BeforeFence { get; set; }
        public int Fences { get; private set; }
        public Task<bool> IsCurrentAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> FencePublicationAsync(AppDbContext db, CancellationToken ct = default)
        {
            Assert.NotNull(db.Database.CurrentTransaction);
            Assert.Empty(db.ChangeTracker.Entries());
            BeforeFence?.Invoke();
            Fences++;
            return Task.FromResult(AllowPublication);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Backend(byte[] bytes, bool unknownSize) : IBackendClient, ISupportsFileInventory, ISupportsPrinterFileCopy
    {
        public int Copies { get; private set; }
        public int Inventories { get; private set; }
        public string Modified { get; set; } = "1";
        public bool Missing { get; set; }
        public bool Offline { get; set; }
        public Action? AfterCopy { get; set; }
        public long? ReportedReceivedSize { get; set; }
        public byte[]? SubstituteContent { get; set; }
        public PrinterCredential? ReceivedCredential { get; private set; }

        public Task<PrinterFileInventoryResult> GetFileInventoryAsync(string baseUrl,
            PrinterCredential? credential = null, PrinterFileInventoryOptions? options = null, CancellationToken ct = default)
        {
            Inventories++;
            return Task.FromResult(Offline
                ? new PrinterFileInventoryResult(PrinterFileInventoryOutcome.Unavailable,
                    PrinterFileInventoryReason.TransportFailure, [], [])
                : new(PrinterFileInventoryOutcome.Complete, PrinterFileInventoryReason.None,
                    Missing ? [] : [new("gcodes", "part.gcode", unknownSize ? null : bytes.Length, ModifiedValue: Modified)],
                    [new("gcodes", "", true, PrinterFileInventoryEvidence.NativeRecursiveListing)]));
        }

        public async Task<PrinterFileCopyBytes> CopyFileToAsync(string baseUrl, string storageRoot,
            string relativePath, PrinterCredential? credential, Stream destination, long maximumBytes, CancellationToken ct)
        {
            Copies++;
            ReceivedCredential = credential;
            await using MemoryStream input = new(SubstituteContent ?? bytes);
            PrinterFileCopyBytes result = await PrinterFileCopyTransport.CopyBoundedAsync(
                input, destination, maximumBytes, null, ct);
            AfterCopy?.Invoke();
            return result with { SizeBytes = ReportedReceivedSize ?? result.SizeBytes };
        }
    }
}
