using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Gcode;
using Farm.Infrastructure.Repositories.UnitOfWork;
using Farm.Infrastructure.Services.FileManagement;
using Farm.Infrastructure.Services.FolderManagement;
using Farm.Infrastructure.Services.Gcode;
using Farm.Infrastructure.Services.StorageManagement;
using Farm.Infrastructure.Settings;
using Farm.Infrastructure.Telemetry;
using Farm.Slicer.Module.Api.Services;
using Farm.Slicer.Module.Data;
using Farm.Slicer.Module.Data.Repositories;
using Farm.Slicer.Module.Domain;
using Farm.Slicer.Module.Services;
using Farm.Slicer.Module.Services.Metrics;
using Farm.Web.Api.Services.Calibration;
using Farm.Web.Api.Services.Gcode;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Farm.Web.Api.Tests.Gcode;

/// <summary>
/// Covers the <c>Artifact -&gt; GcodeFile</c> promotion boundary: lineage validation, byte equivalence,
/// database-enforced idempotency, cleanup safety and restart reconciliation.
/// </summary>
public sealed class GcodeArtifactPromotionTests : IAsyncLifetime
{
    private const string GcodeContent = "; promoted calibration output\nG28\nG1 X10 Y10 F3000\n";

    private PromotionHarness _harness = null!;

    public async Task InitializeAsync() => _harness = await PromotionHarness.CreateAsync();

    public Task DisposeAsync()
    {
        _harness.Dispose();
        return Task.CompletedTask;
    }

    [Fact(DisplayName = "A verified artifact is streamed into the library with complete immutable lineage")]
    public async Task PromoteAsync_WithVerifiedArtifact_StoresBytesHashAndLineage()
    {
        PromotionFixture fixture = await _harness.SeedCompletedGcodeArtifactAsync();

        CalibrationApiResult<GcodePromotionDto> result = await _harness.CreatePromoter().PromoteAsync(
            fixture.Request("promotion-1"),
            fixture.Owner,
            CancellationToken.None);

        _ = result.StatusCode.Should().Be(StatusCodes.Status201Created, result.Code);
        GcodeFile promoted = await _harness.GetGcodeFileAsync(result.Value!.GcodeFileId);
        _ = promoted.ContentSha256.Should().Be(fixture.Sha256);
        _ = promoted.FileHash.Should().Be(fixture.Sha256);
        _ = promoted.FileSizeBytes.Should().Be(fixture.SizeBytes);
        _ = promoted.SourceArtifactId.Should().Be(fixture.ArtifactId);
        _ = promoted.SourceSliceJobId.Should().Be(fixture.JobId);
        _ = promoted.SourceWorkerId.Should().Be(fixture.WorkerId);
        _ = promoted.CalibrationProjectId.Should().Be(fixture.ProjectId);
        _ = promoted.CalibrationAttemptId.Should().Be(fixture.AttemptId);
        _ = promoted.CalibrationOrchestrationId.Should().Be(fixture.OrchestrationId);
        _ = promoted.PromotionOperationId.Should().Be("promotion-1");
        _ = promoted.SpecificationSha256.Should().Be(fixture.SpecificationSha256);
        _ = promoted.MachineProfileSha256.Should().Be(fixture.MachineProfileSha256);
        _ = promoted.SlicerEngineName.Should().Be("OrcaSlicer");
        _ = promoted.SlicerDistribution.Should().Be("upstream");
        _ = promoted.PinnedSlicerVersion.Should().Be("2.3.1");
        _ = promoted.SlicerContainerDigest.Should().Be("sha256:pinned-container");
        _ = promoted.FirmwareFamily.Should().Be("Klipper");
        _ = promoted.GcodeDialect.Should().Be("Klipper");
        _ = promoted.IsImmutable.Should().BeTrue();
        _ = promoted.PromotedAtUtc.Should().NotBeNull();
    }

    [Fact(DisplayName = "Promoted bytes match the source artifact byte for byte")]
    public async Task PromoteAsync_WritesBytesIdenticalToSourceArtifact()
    {
        PromotionFixture fixture = await _harness.SeedCompletedGcodeArtifactAsync();

        CalibrationApiResult<GcodePromotionDto> result = await _harness.CreatePromoter().PromoteAsync(
            fixture.Request("promotion-bytes"),
            fixture.Owner,
            CancellationToken.None);

        byte[] promotedBytes = await _harness.ReadPromotedBytesAsync(result.Value!.GcodeFileId);
        _ = promotedBytes.Should().Equal(Encoding.UTF8.GetBytes(GcodeContent));
        _ = Convert.ToHexString(SHA256.HashData(promotedBytes)).Should().Be(fixture.Sha256);
    }

    [Fact(DisplayName = "The promoted manifest records lineage without storage paths")]
    public async Task PromoteAsync_WritesManifestWithLineageAndNoPaths()
    {
        PromotionFixture fixture = await _harness.SeedCompletedGcodeArtifactAsync();

        CalibrationApiResult<GcodePromotionDto> result = await _harness.CreatePromoter().PromoteAsync(
            fixture.Request("promotion-manifest"),
            fixture.Owner,
            CancellationToken.None);

        GcodeFile promoted = await _harness.GetGcodeFileAsync(result.Value!.GcodeFileId);
        using JsonDocument manifest = JsonDocument.Parse(promoted.CalibrationManifestJson!);
        JsonElement root = manifest.RootElement;
        _ = root.GetProperty("contentSha256").GetString().Should().Be(fixture.Sha256);
        _ = root.GetProperty("attemptId").GetGuid().Should().Be(fixture.AttemptId);
        _ = root.GetProperty("specificationSha256").GetString().Should().Be(fixture.SpecificationSha256);
        _ = root.GetProperty("slicerVersion").GetString().Should().Be("2.3.1");
        _ = promoted.CalibrationManifestJson.Should().NotContain(_harness.ArtifactRoot);
    }

    [Fact(DisplayName = "A caller who does not own the slice job cannot promote its artifact")]
    public async Task PromoteAsync_ForForeignOwner_ReturnsForbidden()
    {
        PromotionFixture fixture = await _harness.SeedCompletedGcodeArtifactAsync();
        CalibrationActor intruder = new(Guid.NewGuid(), "intruder", false);

        CalibrationApiResult<GcodePromotionDto> result = await _harness.CreatePromoter().PromoteAsync(
            fixture.Request("promotion-foreign"),
            intruder,
            CancellationToken.None);

        _ = result.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        _ = result.Code.Should().Be("resource_forbidden");
        _ = (await _harness.CountGcodeFilesAsync()).Should().Be(0);
    }

    [Fact(DisplayName = "A declared digest that differs from the stored artifact is rejected")]
    public async Task PromoteAsync_WithMismatchedDigest_ReturnsHashMismatch()
    {
        PromotionFixture fixture = await _harness.SeedCompletedGcodeArtifactAsync();
        GcodeArtifactPromotionRequest request = fixture.Request("promotion-hash") with
        {
            ExpectedSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("other"))),
        };

        CalibrationApiResult<GcodePromotionDto> result = await _harness.CreatePromoter().PromoteAsync(
            request,
            fixture.Owner,
            CancellationToken.None);

        _ = result.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        _ = result.Code.Should().Be("artifact_hash_mismatch");
        _ = (await _harness.CountGcodeFilesAsync()).Should().Be(0);
    }

    [Fact(DisplayName = "A declared size that differs from the stored artifact is rejected")]
    public async Task PromoteAsync_WithMismatchedSize_ReturnsSizeMismatch()
    {
        PromotionFixture fixture = await _harness.SeedCompletedGcodeArtifactAsync();
        GcodeArtifactPromotionRequest request = fixture.Request("promotion-size") with
        {
            ExpectedSizeBytes = fixture.SizeBytes + 1,
        };

        CalibrationApiResult<GcodePromotionDto> result = await _harness.CreatePromoter().PromoteAsync(
            request,
            fixture.Owner,
            CancellationToken.None);

        _ = result.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        _ = result.Code.Should().Be("artifact_size_mismatch");
    }

    [Fact(DisplayName = "Only the canonical gcode artifact kind is promotable")]
    public async Task PromoteAsync_WithNonGcodeKind_ReturnsUnsupportedKind()
    {
        PromotionFixture fixture = await _harness.SeedCompletedGcodeArtifactAsync(
            kind: SlicerArtifactKinds.Log,
            contentType: "text/plain");

        CalibrationApiResult<GcodePromotionDto> result = await _harness.CreatePromoter().PromoteAsync(
            fixture.Request("promotion-kind") with { ArtifactKind = SlicerArtifactKinds.Log },
            fixture.Owner,
            CancellationToken.None);

        _ = result.StatusCode.Should().Be(StatusCodes.Status422UnprocessableEntity);
        _ = result.Code.Should().Be("unsupported_artifact_kind");
    }

    [Fact(DisplayName = "An artifact media type outside the gcode allowlist is rejected")]
    public async Task PromoteAsync_WithUnacceptedMediaType_ReturnsUnsupportedMediaType()
    {
        PromotionFixture fixture = await _harness.SeedCompletedGcodeArtifactAsync(contentType: "image/png");

        CalibrationApiResult<GcodePromotionDto> result = await _harness.CreatePromoter().PromoteAsync(
            fixture.Request("promotion-media"),
            fixture.Owner,
            CancellationToken.None);

        _ = result.StatusCode.Should().Be(StatusCodes.Status422UnprocessableEntity);
        _ = result.Code.Should().Be("unsupported_artifact_media_type");
    }

    [Fact(DisplayName = "An artifact from a job that is not complete cannot be promoted")]
    public async Task PromoteAsync_WithUnfinishedJob_ReturnsConflict()
    {
        PromotionFixture fixture = await _harness.SeedCompletedGcodeArtifactAsync(
            jobStatus: SliceJobStatus.Processing);

        CalibrationApiResult<GcodePromotionDto> result = await _harness.CreatePromoter().PromoteAsync(
            fixture.Request("promotion-unfinished"),
            fixture.Owner,
            CancellationToken.None);

        _ = result.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        _ = result.Code.Should().Be("slice_job_not_completed");
    }

    [Fact(DisplayName = "A worker that did not produce the artifact is rejected")]
    public async Task PromoteAsync_WithWrongWorker_ReturnsConflict()
    {
        PromotionFixture fixture = await _harness.SeedCompletedGcodeArtifactAsync();

        CalibrationApiResult<GcodePromotionDto> result = await _harness.CreatePromoter().PromoteAsync(
            fixture.Request("promotion-worker") with { SourceWorkerId = Guid.NewGuid() },
            fixture.Owner,
            CancellationToken.None);

        _ = result.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        _ = result.Code.Should().Be("artifact_worker_mismatch");
    }

    [Fact(DisplayName = "An attempt outside the job lineage is rejected")]
    public async Task PromoteAsync_WithForeignAttempt_ReturnsLineageMismatch()
    {
        PromotionFixture fixture = await _harness.SeedCompletedGcodeArtifactAsync();

        CalibrationApiResult<GcodePromotionDto> result = await _harness.CreatePromoter().PromoteAsync(
            fixture.Request("promotion-lineage") with { CalibrationAttemptId = Guid.NewGuid() },
            fixture.Owner,
            CancellationToken.None);

        _ = result.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        _ = result.Code.Should().Be("calibration_lineage_mismatch");
    }

    [Fact(DisplayName = "An exact replay returns the original G-code file identity")]
    public async Task PromoteAsync_ExactReplay_ReturnsStableGcodeFileId()
    {
        PromotionFixture fixture = await _harness.SeedCompletedGcodeArtifactAsync();
        GcodeArtifactPromotionRequest request = fixture.Request("promotion-replay");

        CalibrationApiResult<GcodePromotionDto> first = await _harness.CreatePromoter()
            .PromoteAsync(request, fixture.Owner, CancellationToken.None);
        CalibrationApiResult<GcodePromotionDto> second = await _harness.CreatePromoter()
            .PromoteAsync(request, fixture.Owner, CancellationToken.None);

        _ = first.StatusCode.Should().Be(StatusCodes.Status201Created, first.Code);
        _ = second.StatusCode.Should().Be(StatusCodes.Status200OK, second.Code);
        _ = second.Replayed.Should().BeTrue();
        _ = second.Value!.GcodeFileId.Should().Be(first.Value!.GcodeFileId);
        _ = (await _harness.CountGcodeFilesAsync()).Should().Be(1);
    }

    [Fact(DisplayName = "A changed payload under the same operation key conflicts")]
    public async Task PromoteAsync_WithChangedPayloadForSameOperation_ReturnsConflict()
    {
        PromotionFixture first = await _harness.SeedCompletedGcodeArtifactAsync();
        PromotionFixture second = await _harness.SeedCompletedGcodeArtifactAsync(
            content: "; different\nG28\n",
            ownerId: first.Owner.UserId);

        _ = await _harness.CreatePromoter().PromoteAsync(
            first.Request("promotion-conflict"),
            first.Owner,
            CancellationToken.None);
        CalibrationApiResult<GcodePromotionDto> conflict = await _harness.CreatePromoter().PromoteAsync(
            second.Request("promotion-conflict"),
            first.Owner,
            CancellationToken.None);

        _ = conflict.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        _ = conflict.Code.Should().Be("idempotency_payload_mismatch");
        _ = (await _harness.CountGcodeFilesAsync()).Should().Be(1);
    }

    [Fact(DisplayName = "Concurrent promotion of one artifact produces exactly one G-code file")]
    public async Task PromoteAsync_ConcurrentCallers_ProduceSingleGcodeFile()
    {
        PromotionFixture fixture = await _harness.SeedCompletedGcodeArtifactAsync();
        GcodeArtifactPromotionRequest request = fixture.Request("promotion-concurrent");

        CalibrationApiResult<GcodePromotionDto>[] results = await Task.WhenAll(
            Task.Run(() => _harness.CreatePromoter().PromoteAsync(request, fixture.Owner, CancellationToken.None)),
            Task.Run(() => _harness.CreatePromoter().PromoteAsync(request, fixture.Owner, CancellationToken.None)));

        Guid[] successfulFileIds = results
            .Where(result => result.IsSuccess)
            .Select(result => result.Value!.GcodeFileId)
            .Distinct()
            .ToArray();
        _ = successfulFileIds.Should().HaveCount(1, "concurrent promotion must converge on one identity");
        _ = (await _harness.CountGcodeFilesAsync()).Should().Be(1);
    }

    [Fact(DisplayName = "A second operation key for the same artifact content replays the first promotion")]
    public async Task PromoteAsync_WithDifferentOperationForSameContent_ReplaysOriginalPromotion()
    {
        PromotionFixture fixture = await _harness.SeedCompletedGcodeArtifactAsync();

        CalibrationApiResult<GcodePromotionDto> first = await _harness.CreatePromoter().PromoteAsync(
            fixture.Request("promotion-a"),
            fixture.Owner,
            CancellationToken.None);
        CalibrationApiResult<GcodePromotionDto> second = await _harness.CreatePromoter().PromoteAsync(
            fixture.Request("promotion-b"),
            fixture.Owner,
            CancellationToken.None);

        _ = second.StatusCode.Should().Be(StatusCodes.Status200OK, second.Code);
        _ = second.Value!.GcodeFileId.Should().Be(first.Value!.GcodeFileId);
        _ = (await _harness.CountGcodeFilesAsync()).Should().Be(1);
    }

    [Fact(DisplayName = "Promoted G-code survives source artifact cleanup with recoverable lineage")]
    public async Task PromotedGcode_SurvivesSourceArtifactCleanup()
    {
        PromotionFixture fixture = await _harness.SeedCompletedGcodeArtifactAsync();
        CalibrationApiResult<GcodePromotionDto> promotion = await _harness.CreatePromoter().PromoteAsync(
            fixture.Request("promotion-cleanup"),
            fixture.Owner,
            CancellationToken.None);
        await _harness.AgeArtifactAsync(fixture.ArtifactId, TimeSpan.FromDays(30));

        int deleted = await _harness.RunArtifactCleanupAsync();

        _ = deleted.Should().Be(1);
        _ = (await _harness.ArtifactExistsAsync(fixture.ArtifactId)).Should().BeFalse();
        byte[] promotedBytes = await _harness.ReadPromotedBytesAsync(promotion.Value!.GcodeFileId);
        _ = Convert.ToHexString(SHA256.HashData(promotedBytes)).Should().Be(fixture.Sha256);
        GcodeFile promoted = await _harness.GetGcodeFileAsync(promotion.Value.GcodeFileId);
        _ = promoted.SourceArtifactId.Should().Be(fixture.ArtifactId, "lineage stays recoverable after cleanup");
        _ = promoted.CalibrationAttemptId.Should().Be(fixture.AttemptId);
    }

    [Fact(DisplayName = "Cleanup leaves a source artifact whose promotion outcome is unresolved")]
    public async Task ArtifactCleanup_WithUnresolvedPromotion_KeepsSourceBytes()
    {
        PromotionFixture fixture = await _harness.SeedCompletedGcodeArtifactAsync();
        await _harness.PinArtifactForPromotionAsync(fixture.ArtifactId, "promotion-inflight");
        await _harness.AgeArtifactAsync(fixture.ArtifactId, TimeSpan.FromDays(30));

        int deleted = await _harness.RunArtifactCleanupAsync();

        _ = deleted.Should().Be(0);
        _ = (await _harness.ArtifactExistsAsync(fixture.ArtifactId)).Should().BeTrue();
    }

    [Fact(DisplayName = "A promotion pin that wins after cleanup selection keeps the artifact")]
    public async Task ArtifactCleanup_WhenPromotionPinsAfterSelection_KeepsSourceArtifact()
    {
        PromotionFixture fixture = await _harness.SeedCompletedGcodeArtifactAsync();
        await _harness.AgeArtifactAsync(fixture.ArtifactId, TimeSpan.FromDays(30));
        IArtifactsRepository inner = _harness.CreateArtifactsRepository();
        TaskCompletionSource selected =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseSelection =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        Mock<IArtifactsRepository> gated = new(MockBehavior.Strict);
        _ = gated.Setup(repository => repository.GetCleanupInProgressAsync(
                It.IsAny<CancellationToken>()))
            .Returns((CancellationToken cancellationToken) =>
                inner.GetCleanupInProgressAsync(cancellationToken));
        _ = gated.Setup(repository => repository.GetOlderThanAsync(
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()))
            .Returns(async (DateTime cutoff, CancellationToken cancellationToken) =>
            {
                IReadOnlyList<Artifact> candidates =
                    await inner.GetOlderThanAsync(cutoff, cancellationToken);
                selected.TrySetResult();
                await releaseSelection.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
                return candidates;
            });
        _ = gated.Setup(repository => repository.TryReserveForCleanupAsync(
                It.IsAny<Guid>(),
                It.IsAny<Guid?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<Guid>(),
                It.IsAny<DateTime>(),
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()))
            .Returns((
                Guid id,
                Guid? expectedReservationToken,
                DateTime? expectedReservedAtUtc,
                Guid reservationToken,
                DateTime reservedAtUtc,
                DateTime staleBeforeUtc,
                CancellationToken cancellationToken) =>
                inner.TryReserveForCleanupAsync(
                    id,
                    expectedReservationToken,
                    expectedReservedAtUtc,
                    reservationToken,
                    reservedAtUtc,
                    staleBeforeUtc,
                    cancellationToken));

        Task<int> cleanup = _harness.RunArtifactCleanupAsync(gated.Object);
        await selected.Task.WaitAsync(TimeSpan.FromSeconds(10));
        bool pinned = await inner.TryPinForPromotionAsync(
            fixture.ArtifactId,
            Guid.NewGuid(),
            new PromotionOperationIdentity("pin-first-key", "pin-first"),
            DateTime.UtcNow);
        releaseSelection.TrySetResult();
        int deleted = await cleanup;

        _ = pinned.Should().BeTrue();
        _ = deleted.Should().Be(0);
        _ = (await _harness.ArtifactExistsAsync(fixture.ArtifactId)).Should().BeTrue();
    }

    [Fact(DisplayName = "A cleanup reservation that wins before promotion excludes the pin")]
    public async Task ArtifactCleanup_WhenCleanupWinsReservation_RejectsPromotionPin()
    {
        PromotionFixture fixture = await _harness.SeedCompletedGcodeArtifactAsync();
        await _harness.AgeArtifactAsync(fixture.ArtifactId, TimeSpan.FromDays(30));
        IArtifactsRepository inner = _harness.CreateArtifactsRepository();
        TaskCompletionSource deleteReached =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseDelete =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        Mock<IArtifactsRepository> gated = new(MockBehavior.Strict);
        _ = gated.Setup(repository => repository.GetCleanupInProgressAsync(
                It.IsAny<CancellationToken>()))
            .Returns((CancellationToken cancellationToken) =>
                inner.GetCleanupInProgressAsync(cancellationToken));
        _ = gated.Setup(repository => repository.GetOlderThanAsync(
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()))
            .Returns((DateTime cutoff, CancellationToken cancellationToken) =>
                inner.GetOlderThanAsync(cutoff, cancellationToken));
        _ = gated.Setup(repository => repository.TryReserveForCleanupAsync(
                It.IsAny<Guid>(),
                It.IsAny<Guid?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<Guid>(),
                It.IsAny<DateTime>(),
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()))
            .Returns((
                Guid id,
                Guid? expectedReservationToken,
                DateTime? expectedReservedAtUtc,
                Guid reservationToken,
                DateTime reservedAtUtc,
                DateTime staleBeforeUtc,
                CancellationToken cancellationToken) =>
                inner.TryReserveForCleanupAsync(
                    id,
                    expectedReservationToken,
                    expectedReservedAtUtc,
                    reservationToken,
                    reservedAtUtc,
                    staleBeforeUtc,
                    cancellationToken));
        _ = gated.Setup(repository => repository.TryBeginCleanupDeletionAsync(
                It.IsAny<Guid>(),
                It.IsAny<Guid>(),
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()))
            .Returns((
                Guid id,
                Guid reservationToken,
                DateTime startedAtUtc,
                CancellationToken cancellationToken) =>
                inner.TryBeginCleanupDeletionAsync(
                    id,
                    reservationToken,
                    startedAtUtc,
                    cancellationToken));
        _ = gated.Setup(repository => repository.FinalizeCleanupAsync(
                It.IsAny<Guid>(),
                It.IsAny<Guid>(),
                It.IsAny<CancellationToken>()))
            .Returns(async (
                Guid id,
                Guid reservationToken,
                CancellationToken cancellationToken) =>
            {
                deleteReached.TrySetResult();
                await releaseDelete.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
                return await inner.FinalizeCleanupAsync(id, reservationToken, cancellationToken);
            });

        Task<int> cleanup = _harness.RunArtifactCleanupAsync(gated.Object);
        await deleteReached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        bool pinned = await inner.TryPinForPromotionAsync(
            fixture.ArtifactId,
            Guid.NewGuid(),
            new PromotionOperationIdentity("cleanup-first-key", "cleanup-first"),
            DateTime.UtcNow);
        releaseDelete.TrySetResult();
        int deleted = await cleanup;

        _ = deleted.Should().Be(1);
        _ = pinned.Should().BeFalse();
        _ = (await _harness.ArtifactExistsAsync(fixture.ArtifactId)).Should().BeFalse();
    }

    [Fact(DisplayName = "A live cleanup reservation cannot be stolen by another cleanup pass")]
    public async Task ArtifactCleanup_WhenCleanupPassesOverlap_PreservesExclusiveOwnership()
    {
        PromotionFixture fixture = await _harness.SeedCompletedGcodeArtifactAsync();
        await _harness.AgeArtifactAsync(fixture.ArtifactId, TimeSpan.FromDays(30));
        IArtifactsRepository inner = _harness.CreateArtifactsRepository();
        TaskCompletionSource firstReserved =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseFirst =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        Mock<IArtifactsRepository> firstPass = new(MockBehavior.Strict);
        _ = firstPass.Setup(repository => repository.GetCleanupInProgressAsync(
                It.IsAny<CancellationToken>()))
            .Returns((CancellationToken cancellationToken) =>
                inner.GetCleanupInProgressAsync(cancellationToken));
        _ = firstPass.Setup(repository => repository.GetOlderThanAsync(
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()))
            .Returns((DateTime cutoff, CancellationToken cancellationToken) =>
                inner.GetOlderThanAsync(cutoff, cancellationToken));
        _ = firstPass.Setup(repository => repository.TryReserveForCleanupAsync(
                It.IsAny<Guid>(),
                It.IsAny<Guid?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<Guid>(),
                It.IsAny<DateTime>(),
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()))
            .Returns(async (
                Guid id,
                Guid? expectedReservationToken,
                DateTime? expectedReservedAtUtc,
                Guid reservationToken,
                DateTime reservedAtUtc,
                DateTime staleBeforeUtc,
                CancellationToken cancellationToken) =>
            {
                bool reserved = await inner.TryReserveForCleanupAsync(
                    id,
                    expectedReservationToken,
                    expectedReservedAtUtc,
                    reservationToken,
                    reservedAtUtc,
                    staleBeforeUtc,
                    cancellationToken);
                if (reserved)
                {
                    firstReserved.TrySetResult();
                    await releaseFirst.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
                }

                return reserved;
            });
        _ = firstPass.Setup(repository => repository.TryBeginCleanupDeletionAsync(
                It.IsAny<Guid>(),
                It.IsAny<Guid>(),
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()))
            .Returns((
                Guid id,
                Guid reservationToken,
                DateTime startedAtUtc,
                CancellationToken cancellationToken) =>
                inner.TryBeginCleanupDeletionAsync(
                    id,
                    reservationToken,
                    startedAtUtc,
                    cancellationToken));
        _ = firstPass.Setup(repository => repository.FinalizeCleanupAsync(
                It.IsAny<Guid>(),
                It.IsAny<Guid>(),
                It.IsAny<CancellationToken>()))
            .Returns((
                Guid id,
                Guid reservationToken,
                CancellationToken cancellationToken) =>
                inner.FinalizeCleanupAsync(id, reservationToken, cancellationToken));

        Mock<IArtifactsRepository> secondPass = new(MockBehavior.Strict);
        _ = secondPass.Setup(repository => repository.GetCleanupInProgressAsync(
                It.IsAny<CancellationToken>()))
            .Returns((CancellationToken cancellationToken) =>
                inner.GetCleanupInProgressAsync(cancellationToken));
        _ = secondPass.Setup(repository => repository.GetOlderThanAsync(
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()))
            .Returns((DateTime cutoff, CancellationToken cancellationToken) =>
                inner.GetOlderThanAsync(cutoff, cancellationToken));
        _ = secondPass.Setup(repository => repository.TryReserveForCleanupAsync(
                It.IsAny<Guid>(),
                It.IsAny<Guid?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<Guid>(),
                It.IsAny<DateTime>(),
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()))
            .Returns((
                Guid id,
                Guid? expectedReservationToken,
                DateTime? expectedReservedAtUtc,
                Guid reservationToken,
                DateTime reservedAtUtc,
                DateTime staleBeforeUtc,
                CancellationToken cancellationToken) =>
                inner.TryReserveForCleanupAsync(
                    id,
                    expectedReservationToken,
                    expectedReservedAtUtc,
                    reservationToken,
                    reservedAtUtc,
                    staleBeforeUtc,
                    cancellationToken));
        _ = secondPass.Setup(repository => repository.ReleaseCleanupReservationAsync(
                It.IsAny<Guid>(),
                It.IsAny<Guid>(),
                It.IsAny<CancellationToken>()))
            .Returns((
                Guid id,
                Guid reservationToken,
                CancellationToken cancellationToken) =>
                inner.ReleaseCleanupReservationAsync(id, reservationToken, cancellationToken));

        Task<int> firstCleanup = _harness.RunArtifactCleanupAsync(firstPass.Object);
        await firstReserved.Task.WaitAsync(TimeSpan.FromSeconds(10));

        int secondDeleted;
        bool rowSurvivedSecondPass;
        bool bytesSurvivedSecondPass;
        try
        {
            secondDeleted = await _harness.RunArtifactCleanupAsync(secondPass.Object);
            rowSurvivedSecondPass = await _harness.ArtifactExistsAsync(fixture.ArtifactId);
            bytesSurvivedSecondPass = _harness.ArtifactBytesExist(fixture.ArtifactId);
        }
        finally
        {
            releaseFirst.TrySetResult();
        }

        int firstDeleted = await firstCleanup;

        _ = secondDeleted.Should().Be(0);
        _ = rowSurvivedSecondPass.Should().BeTrue();
        _ = bytesSurvivedSecondPass.Should().BeTrue();
        _ = firstDeleted.Should().Be(1);
        _ = (await _harness.ArtifactExistsAsync(fixture.ArtifactId)).Should().BeFalse();
        _ = _harness.ArtifactBytesExist(fixture.ArtifactId).Should().BeFalse();
    }

    [Fact(DisplayName = "Exactly one cleanup pass can take over an observed expired reservation")]
    public async Task CleanupReservation_WhenObservedReservationExpired_AllowsExactlyOneTakeover()
    {
        PromotionFixture fixture = await _harness.SeedCompletedGcodeArtifactAsync();
        Guid expiredToken = Guid.NewGuid();
        Guid firstTakeoverToken = Guid.NewGuid();
        Guid secondTakeoverToken = Guid.NewGuid();
        DateTime expiredAtUtc = new(2020, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        DateTime takeoverAtUtc = expiredAtUtc.AddHours(1);
        await _harness.SetArtifactCleanupReservationAsync(
            fixture.ArtifactId,
            expiredToken,
            expiredAtUtc);
        IArtifactsRepository firstRepository = _harness.CreateArtifactsRepository();
        IArtifactsRepository secondRepository = _harness.CreateArtifactsRepository();
        TaskCompletionSource releaseAttempts =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<bool> AttemptTakeoverAsync(
            IArtifactsRepository repository,
            Guid reservationToken)
        {
            await releaseAttempts.Task.WaitAsync(TimeSpan.FromSeconds(10));
            return await repository.TryReserveForCleanupAsync(
                fixture.ArtifactId,
                expiredToken,
                expiredAtUtc,
                reservationToken,
                takeoverAtUtc,
                takeoverAtUtc.AddMinutes(-30));
        }

        Task<bool> firstAttempt = AttemptTakeoverAsync(firstRepository, firstTakeoverToken);
        Task<bool> secondAttempt = AttemptTakeoverAsync(secondRepository, secondTakeoverToken);
        releaseAttempts.TrySetResult();
        bool[] results = await Task.WhenAll(firstAttempt, secondAttempt);

        _ = results.Count(acquired => acquired).Should().Be(1);
        Guid winnerToken = results[0] ? firstTakeoverToken : secondTakeoverToken;
        _ = (await firstRepository.TryBeginCleanupDeletionAsync(
            fixture.ArtifactId,
            expiredToken,
            takeoverAtUtc)).Should().BeFalse();
        _ = (await firstRepository.FinalizeCleanupAsync(
            fixture.ArtifactId,
            expiredToken)).Should().BeFalse();
        await firstRepository.ReleaseCleanupReservationAsync(fixture.ArtifactId, expiredToken);
        Artifact reserved = await _harness.GetArtifactAsync(fixture.ArtifactId);
        _ = reserved.CleanupReservationToken.Should().Be(winnerToken);
        _ = reserved.CleanupReservedAtUtc.Should().Be(takeoverAtUtc);
        _ = _harness.ArtifactBytesExist(fixture.ArtifactId).Should().BeTrue();
        _ = (await firstRepository.TryPinForPromotionAsync(
            fixture.ArtifactId,
            Guid.NewGuid(),
            new PromotionOperationIdentity("stale-takeover-key", "stale-takeover"),
            takeoverAtUtc)).Should().BeFalse();

        _ = (await firstRepository.TryBeginCleanupDeletionAsync(
            fixture.ArtifactId,
            winnerToken,
            takeoverAtUtc)).Should().BeTrue();
        _harness.DeleteArtifactBytes(fixture.ArtifactId);
        _ = (await firstRepository.FinalizeCleanupAsync(
            fixture.ArtifactId,
            winnerToken)).Should().BeTrue();
        _ = (await _harness.ArtifactExistsAsync(fixture.ArtifactId)).Should().BeFalse();
        _ = _harness.ArtifactBytesExist(fixture.ArtifactId).Should().BeFalse();
    }

    [Fact(DisplayName = "A stale cleanup owner cannot delete bytes after takeover")]
    public async Task ArtifactCleanup_WhenStaleReservationIsTakenOver_FencesOldByteDeletion()
    {
        PromotionFixture fixture = await _harness.SeedCompletedGcodeArtifactAsync();
        await _harness.AgeArtifactAsync(fixture.ArtifactId, TimeSpan.FromDays(30));
        IArtifactsRepository inner = _harness.CreateArtifactsRepository();
        DateTime expiredAtUtc = new(2020, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        TaskCompletionSource firstReserved =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseFirst =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        Mock<IArtifactsRepository> staleOwner = new(MockBehavior.Strict);
        _ = staleOwner.Setup(repository => repository.GetCleanupInProgressAsync(
                It.IsAny<CancellationToken>()))
            .Returns((CancellationToken cancellationToken) =>
                inner.GetCleanupInProgressAsync(cancellationToken));
        _ = staleOwner.Setup(repository => repository.GetOlderThanAsync(
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()))
            .Returns((DateTime cutoff, CancellationToken cancellationToken) =>
                inner.GetOlderThanAsync(cutoff, cancellationToken));
        _ = staleOwner.Setup(repository => repository.TryReserveForCleanupAsync(
                It.IsAny<Guid>(),
                It.IsAny<Guid?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<Guid>(),
                It.IsAny<DateTime>(),
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()))
            .Returns(async (
                Guid id,
                Guid? expectedReservationToken,
                DateTime? expectedReservedAtUtc,
                Guid reservationToken,
                DateTime reservedAtUtc,
                DateTime staleBeforeUtc,
                CancellationToken cancellationToken) =>
            {
                bool reserved = await inner.TryReserveForCleanupAsync(
                    id,
                    expectedReservationToken,
                    expectedReservedAtUtc,
                    reservationToken,
                    reservedAtUtc,
                    staleBeforeUtc,
                    cancellationToken);
                if (reserved)
                {
                    await _harness.SetArtifactCleanupReservationAsync(
                        id,
                        reservationToken,
                        expiredAtUtc);
                    firstReserved.TrySetResult();
                    await releaseFirst.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
                }

                return reserved;
            });
        _ = staleOwner.Setup(repository => repository.TryBeginCleanupDeletionAsync(
                It.IsAny<Guid>(),
                It.IsAny<Guid>(),
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()))
            .Returns((
                Guid id,
                Guid reservationToken,
                DateTime startedAtUtc,
                CancellationToken cancellationToken) =>
                inner.TryBeginCleanupDeletionAsync(
                    id,
                    reservationToken,
                    startedAtUtc,
                    cancellationToken));
        _ = staleOwner.Setup(repository => repository.ReleaseCleanupReservationAsync(
                It.IsAny<Guid>(),
                It.IsAny<Guid>(),
                It.IsAny<CancellationToken>()))
            .Returns((
                Guid id,
                Guid reservationToken,
                CancellationToken cancellationToken) =>
                inner.ReleaseCleanupReservationAsync(id, reservationToken, cancellationToken));

        Mock<IArtifactsRepository> takeover = new(MockBehavior.Strict);
        _ = takeover.Setup(repository => repository.GetCleanupInProgressAsync(
                It.IsAny<CancellationToken>()))
            .Returns((CancellationToken cancellationToken) =>
                inner.GetCleanupInProgressAsync(cancellationToken));
        _ = takeover.Setup(repository => repository.GetOlderThanAsync(
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()))
            .Returns((DateTime cutoff, CancellationToken cancellationToken) =>
                inner.GetOlderThanAsync(cutoff, cancellationToken));
        _ = takeover.Setup(repository => repository.TryReserveForCleanupAsync(
                It.IsAny<Guid>(),
                It.IsAny<Guid?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<Guid>(),
                It.IsAny<DateTime>(),
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()))
            .Returns((
                Guid id,
                Guid? expectedReservationToken,
                DateTime? expectedReservedAtUtc,
                Guid reservationToken,
                DateTime reservedAtUtc,
                DateTime staleBeforeUtc,
                CancellationToken cancellationToken) =>
                inner.TryReserveForCleanupAsync(
                    id,
                    expectedReservationToken,
                    expectedReservedAtUtc,
                    reservationToken,
                    reservedAtUtc,
                    staleBeforeUtc,
                    cancellationToken));
        _ = takeover.Setup(repository => repository.TryBeginCleanupDeletionAsync(
                It.IsAny<Guid>(),
                It.IsAny<Guid>(),
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()))
            .Returns((
                Guid id,
                Guid reservationToken,
                DateTime startedAtUtc,
                CancellationToken cancellationToken) =>
                inner.TryBeginCleanupDeletionAsync(
                    id,
                    reservationToken,
                    startedAtUtc,
                    cancellationToken));

        Task<int> staleCleanup = _harness.RunArtifactCleanupAsync(staleOwner.Object);
        await firstReserved.Task.WaitAsync(TimeSpan.FromSeconds(10));

        int takeoverDeleted;
        try
        {
            takeoverDeleted = await _harness.RunArtifactCleanupAsync(
                takeover.Object,
                _ => throw new IOException("deterministic takeover interruption"));
        }
        finally
        {
            releaseFirst.TrySetResult();
        }

        int staleDeleted = await staleCleanup;

        _ = takeoverDeleted.Should().Be(0);
        _ = staleDeleted.Should().Be(0);
        _ = (await _harness.ArtifactExistsAsync(fixture.ArtifactId)).Should().BeTrue();
        _ = _harness.ArtifactBytesExist(fixture.ArtifactId).Should().BeTrue();
        Artifact preserved = await _harness.GetArtifactAsync(fixture.ArtifactId);
        _ = preserved.CleanupReservationToken.Should().NotBeNull();
        _ = preserved.CleanupDeletionStartedAtUtc.Should().NotBeNull();
        _ = (await inner.TryPinForPromotionAsync(
            fixture.ArtifactId,
            Guid.NewGuid(),
            new PromotionOperationIdentity("stale-cleanup-retry-key", "stale-cleanup-retry"),
            DateTime.UtcNow)).Should().BeFalse();

        int recovered = await _harness.RunArtifactCleanupAsync();
        _ = recovered.Should().Be(1);
        _ = (await _harness.ArtifactExistsAsync(fixture.ArtifactId)).Should().BeFalse();
        _ = _harness.ArtifactBytesExist(fixture.ArtifactId).Should().BeFalse();
    }

    [Theory(DisplayName = "A byte deletion failure leaves recoverable cleanup metadata")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ArtifactCleanup_WhenByteDeletionFails_RetriesWithoutOrphaningBytes(
        bool accessDenied)
    {
        PromotionFixture fixture = await _harness.SeedCompletedGcodeArtifactAsync();
        await _harness.AgeArtifactAsync(fixture.ArtifactId, TimeSpan.FromDays(30));
        Exception failure = accessDenied
            ? new UnauthorizedAccessException("deterministic access denial")
            : new IOException("deterministic I/O failure");

        int interrupted = await _harness.RunArtifactCleanupAsync(
            deleteArtifactFile: _ => throw failure);

        _ = interrupted.Should().Be(0);
        _ = (await _harness.ArtifactExistsAsync(fixture.ArtifactId)).Should().BeTrue();
        _ = _harness.ArtifactBytesExist(fixture.ArtifactId).Should().BeTrue();
        Artifact pending = await _harness.GetArtifactAsync(fixture.ArtifactId);
        _ = pending.CleanupReservationToken.Should().NotBeNull();
        _ = pending.CleanupDeletionStartedAtUtc.Should().NotBeNull();
        _ = (await _harness.CreateArtifactsRepository().TryPinForPromotionAsync(
            fixture.ArtifactId,
            Guid.NewGuid(),
            new PromotionOperationIdentity("cleanup-retry-key", "cleanup-retry"),
            DateTime.UtcNow)).Should().BeFalse();

        int recovered = await _harness.RunArtifactCleanupAsync();

        _ = recovered.Should().Be(1);
        _ = (await _harness.ArtifactExistsAsync(fixture.ArtifactId)).Should().BeFalse();
        _ = _harness.ArtifactBytesExist(fixture.ArtifactId).Should().BeFalse();
    }

    [Fact(DisplayName = "A false existence probe cannot finalize metadata over retained bytes")]
    public async Task ArtifactCleanup_WhenExistenceProbeFails_StillRequiresConfirmedDeletion()
    {
        PromotionFixture fixture = await _harness.SeedCompletedGcodeArtifactAsync();
        await _harness.AgeArtifactAsync(fixture.ArtifactId, TimeSpan.FromDays(30));
        bool deletionAttempted = false;

        int interrupted = await _harness.RunArtifactCleanupAsync(
            deleteArtifactFile: _ =>
            {
                deletionAttempted = true;
                throw new UnauthorizedAccessException("deterministic delete access denial");
            },
            artifactFileExists: _ => false);

        _ = interrupted.Should().Be(0);
        _ = deletionAttempted.Should().BeTrue();
        _ = (await _harness.ArtifactExistsAsync(fixture.ArtifactId)).Should().BeTrue();
        _ = _harness.ArtifactBytesExist(fixture.ArtifactId).Should().BeTrue();
        Artifact pending = await _harness.GetArtifactAsync(fixture.ArtifactId);
        _ = pending.CleanupReservationToken.Should().NotBeNull();
        _ = pending.CleanupDeletionStartedAtUtc.Should().NotBeNull();

        int recovered = await _harness.RunArtifactCleanupAsync();
        _ = recovered.Should().Be(1);
        _ = (await _harness.ArtifactExistsAsync(fixture.ArtifactId)).Should().BeFalse();
        _ = _harness.ArtifactBytesExist(fixture.ArtifactId).Should().BeFalse();
    }

    [Fact(DisplayName = "A restart finalizes metadata after bytes were already deleted")]
    public async Task ArtifactCleanup_AfterByteDeleteBeforeFinalization_Converges()
    {
        PromotionFixture fixture = await _harness.SeedCompletedGcodeArtifactAsync();
        await _harness.AgeArtifactAsync(fixture.ArtifactId, TimeSpan.FromDays(30));
        Guid reservationToken = Guid.NewGuid();
        DateTime startedAtUtc = DateTime.UtcNow;
        await _harness.SetArtifactCleanupReservationAsync(
            fixture.ArtifactId,
            reservationToken,
            startedAtUtc,
            startedAtUtc);
        _harness.DeleteArtifactBytes(fixture.ArtifactId);

        int recovered = await _harness.RunArtifactCleanupAsync();

        _ = recovered.Should().Be(1);
        _ = (await _harness.ArtifactExistsAsync(fixture.ArtifactId)).Should().BeFalse();
        _ = _harness.ArtifactBytesExist(fixture.ArtifactId).Should().BeFalse();
    }

    [Fact(DisplayName = "A restart reconciles an unknown promotion outcome without duplicating the file")]
    public async Task ReconcilePendingAsync_AfterUnknownOutcome_CompletesWithoutDuplicate()
    {
        PromotionFixture fixture = await _harness.SeedCompletedGcodeArtifactAsync();
        Guid checkpointId = await _harness.SeedInterruptedPromotionAsync(fixture, "promotion-crashed");

        int reconciled = await _harness.CreatePromoter().ReconcilePendingAsync(10, CancellationToken.None);

        _ = reconciled.Should().Be(1);
        _ = (await _harness.CountGcodeFilesAsync()).Should().Be(1);
        GcodePromotionCheckpoint checkpoint = await _harness.GetCheckpointAsync(checkpointId);
        _ = checkpoint.State.Should().Be(GcodePromotionState.Completed);
        _ = checkpoint.SourceAcknowledgedAtUtc.Should().NotBeNull();
        GcodeFile promoted = await _harness.GetGcodeFileAsync(checkpoint.GcodeFileId);
        _ = promoted.ContentSha256.Should().Be(fixture.Sha256);
        _ = (await _harness.IsArtifactCleanupEligibleAsync(fixture.ArtifactId)).Should().BeTrue();
    }

    [Fact(DisplayName = "An unresolved promotion keeps its source artifact ineligible for cleanup")]
    public async Task IsSourceArtifactCleanupSafeAsync_TracksDurablePromotionState()
    {
        PromotionFixture fixture = await _harness.SeedCompletedGcodeArtifactAsync();
        Guid checkpointId = await _harness.SeedInterruptedPromotionAsync(fixture, "promotion-guard");

        bool beforeReconcile = await _harness.CreatePromoter()
            .IsSourceArtifactCleanupSafeAsync(fixture.ArtifactId, CancellationToken.None);
        _ = await _harness.CreatePromoter().ReconcileAsync(checkpointId, CancellationToken.None);
        bool afterReconcile = await _harness.CreatePromoter()
            .IsSourceArtifactCleanupSafeAsync(fixture.ArtifactId, CancellationToken.None);

        _ = beforeReconcile.Should().BeFalse();
        _ = afterReconcile.Should().BeTrue();
    }

    [Fact(DisplayName = "A permanently failed promotion releases its source artifact for cleanup")]
    public async Task PromoteAsync_WhenSourceBytesAreMissing_FailsAndReleasesTheArtifact()
    {
        PromotionFixture fixture = await _harness.SeedCompletedGcodeArtifactAsync();
        _harness.DeleteArtifactBytes(fixture.ArtifactId);

        CalibrationApiResult<GcodePromotionDto> result = await _harness.CreatePromoter().PromoteAsync(
            fixture.Request("promotion-missing-bytes"),
            fixture.Owner,
            CancellationToken.None);

        _ = result.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        _ = result.Code.Should().Be("source_artifact_bytes_unavailable");
        _ = (await _harness.CountGcodeFilesAsync()).Should().Be(0);
        _ = (await _harness.IsArtifactCleanupEligibleAsync(fixture.ArtifactId))
            .Should().BeTrue("a permanent failure must not pin the artifact forever");
        bool cleanupSafe = await _harness.CreatePromoter()
            .IsSourceArtifactCleanupSafeAsync(fixture.ArtifactId, CancellationToken.None);
        _ = cleanupSafe.Should().BeTrue();
    }

    [Fact(DisplayName = "The promotion status route returns the caller's own promotion only")]
    public async Task GetPromotionAsync_IsScopedToTheOwningCaller()
    {
        PromotionFixture fixture = await _harness.SeedCompletedGcodeArtifactAsync();
        CalibrationApiResult<GcodePromotionDto> promotion = await _harness.CreatePromoter().PromoteAsync(
            fixture.Request("promotion-status"),
            fixture.Owner,
            CancellationToken.None);
        CalibrationActor intruder = new(Guid.NewGuid(), "intruder", false);

        CalibrationApiResult<GcodePromotionDto> owner = await _harness.CreatePromoter()
            .GetPromotionAsync("promotion-status", fixture.Owner, CancellationToken.None);
        CalibrationApiResult<GcodePromotionDto> foreign = await _harness.CreatePromoter()
            .GetPromotionAsync("promotion-status", intruder, CancellationToken.None);

        _ = owner.Value!.GcodeFileId.Should().Be(promotion.Value!.GcodeFileId);
        _ = owner.Value.SourceAcknowledged.Should().BeTrue();
        _ = foreign.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        _ = foreign.Code.Should().Be("promotion_not_found");
    }

    [Fact(DisplayName = "A monolith with routable artifacts and a healthy reconciler reports promotion operational")]
    public async Task GetCapabilityAsync_WithRoutableArtifacts_ReportsOperational()
    {
        GcodePromotionCapabilityDto capability = await _harness.CreatePromoter()
            .GetCapabilityAsync(CancellationToken.None);

        _ = capability.Operational.Should().BeTrue();
        _ = capability.ArtifactSourceAvailable.Should().BeTrue();
        _ = capability.CheckpointStoreAvailable.Should().BeTrue();
        _ = capability.LibraryStorageWritable.Should().BeTrue();
        _ = capability.ReconcilerHealthy.Should().BeTrue();
        _ = capability.UnavailableCode.Should().BeNull();
    }

    [Fact(DisplayName = "A split host without artifact routing reports promotion unavailable")]
    public async Task GetCapabilityAsync_WithoutArtifactRouting_ReportsUnavailable()
    {
        GcodePromotionCapabilityDto capability = await _harness.CreatePromoter(withArtifactRouting: false)
            .GetCapabilityAsync(CancellationToken.None);

        _ = capability.Operational.Should().BeFalse();
        _ = capability.ArtifactSourceAvailable.Should().BeFalse();
        _ = capability.UnavailableCode.Should().Be("artifact_source_unroutable");
    }

    [Fact(DisplayName = "A split host without artifact routing refuses to promote instead of faking success")]
    public async Task PromoteAsync_WithoutArtifactRouting_ReturnsDependencyUnavailable()
    {
        PromotionFixture fixture = await _harness.SeedCompletedGcodeArtifactAsync();

        CalibrationApiResult<GcodePromotionDto> result = await _harness
            .CreatePromoter(withArtifactRouting: false)
            .PromoteAsync(fixture.Request("promotion-split"), fixture.Owner, CancellationToken.None);

        _ = result.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        _ = result.Code.Should().Be("promotion_dependency_unavailable");
        _ = (await _harness.CountGcodeFilesAsync()).Should().Be(0);
    }

    [Fact(DisplayName = "An unhealthy reconciler keeps promotion capability false")]
    public async Task GetCapabilityAsync_WithFailingReconciler_ReportsUnavailable()
    {
        _harness.ReconcilerState.RecordFailure();
        _harness.ReconcilerState.RecordFailure();
        _harness.ReconcilerState.RecordFailure();

        GcodePromotionCapabilityDto capability = await _harness.CreatePromoter()
            .GetCapabilityAsync(CancellationToken.None);

        _ = capability.Operational.Should().BeFalse();
        _ = capability.ReconcilerHealthy.Should().BeFalse();
        _ = capability.UnavailableCode.Should().Be("promotion_reconciler_unavailable");
    }

    [Fact(DisplayName = "Two owners may promote under the same idempotency key without colliding")]
    public async Task PromoteAsync_ForTwoOwnersWithTheSameOperationKey_PromotesBoth()
    {
        PromotionFixture first = await _harness.SeedCompletedGcodeArtifactAsync();
        PromotionFixture second = await _harness.SeedCompletedGcodeArtifactAsync(
            content: "; a different owner's calibration\nG28\nG1 X5 Y5 F3000\n");

        CalibrationApiResult<GcodePromotionDto> firstResult = await _harness.CreatePromoter().PromoteAsync(
            first.Request("shared-idempotency-key"),
            first.Owner,
            CancellationToken.None);
        CalibrationApiResult<GcodePromotionDto> secondResult = await _harness.CreatePromoter().PromoteAsync(
            second.Request("shared-idempotency-key"),
            second.Owner,
            CancellationToken.None);

        _ = firstResult.StatusCode.Should().Be(StatusCodes.Status201Created, firstResult.Code);
        _ = secondResult.StatusCode.Should().Be(StatusCodes.Status201Created, secondResult.Code);
        _ = secondResult.Value!.GcodeFileId.Should().NotBe(firstResult.Value!.GcodeFileId);
        _ = (await _harness.CountGcodeFilesAsync()).Should().Be(2);
    }

    [Fact(DisplayName = "The same idempotency key is persisted under a different identity per owner")]
    public async Task PromoteAsync_StoresOwnerScopedOperationIdentity()
    {
        PromotionFixture first = await _harness.SeedCompletedGcodeArtifactAsync();
        PromotionFixture second = await _harness.SeedCompletedGcodeArtifactAsync(
            content: "; a different owner's calibration\nG28\nG1 X5 Y5 F3000\n");
        CalibrationApiResult<GcodePromotionDto> firstResult = await _harness.CreatePromoter().PromoteAsync(
            first.Request("shared-identity-key"),
            first.Owner,
            CancellationToken.None);
        CalibrationApiResult<GcodePromotionDto> secondResult = await _harness.CreatePromoter().PromoteAsync(
            second.Request("shared-identity-key"),
            second.Owner,
            CancellationToken.None);

        GcodeFile firstFile = await _harness.GetGcodeFileAsync(firstResult.Value!.GcodeFileId);
        GcodeFile secondFile = await _harness.GetGcodeFileAsync(secondResult.Value!.GcodeFileId);

        _ = firstFile.PromotionOperationId.Should().Be("shared-identity-key");
        _ = secondFile.PromotionOperationId.Should().Be("shared-identity-key");
        _ = firstFile.PromotionOperationKey.Should().Be(
            GcodePromotionOperationKey.Compute(first.Owner.UserId, "shared-identity-key"));
        _ = secondFile.PromotionOperationKey.Should().NotBe(firstFile.PromotionOperationKey);
    }

    [Fact(DisplayName = "The promotion result never exposes the internal owner scope")]
    public async Task PromoteAsync_ResultCarriesOnlyTheCallerSuppliedOperationKey()
    {
        PromotionFixture fixture = await _harness.SeedCompletedGcodeArtifactAsync();

        CalibrationApiResult<GcodePromotionDto> result = await _harness.CreatePromoter().PromoteAsync(
            fixture.Request("promotion-scope-hidden"),
            fixture.Owner,
            CancellationToken.None);

        _ = result.Value!.OperationId.Should().Be("promotion-scope-hidden");
        _ = JsonSerializer.Serialize(result.Value).Should().NotContain("gcode-promotion:user:");
    }

    [Fact(DisplayName = "A durable write conflict during promotion is a 409, not a server fault")]
    public async Task PromoteAsync_WhenAnotherFileHoldsTheOperationIdentity_ReturnsConflict()
    {
        PromotionFixture fixture = await _harness.SeedCompletedGcodeArtifactAsync();
        await _harness.SeedFileHoldingOperationIdentityAsync(fixture.Owner.UserId, "promotion-race");

        CalibrationApiResult<GcodePromotionDto> result = await _harness.CreatePromoter().PromoteAsync(
            fixture.Request("promotion-race"),
            fixture.Owner,
            CancellationToken.None);

        _ = result.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        _ = result.Code.Should().Be("promotion_conflict");
    }

    [Fact(DisplayName = "Reconciliation continues past a checkpoint it cannot resolve")]
    public async Task ReconcilePendingAsync_ContinuesPastAFailedCheckpoint()
    {
        PromotionFixture poisoned = await _harness.SeedCompletedGcodeArtifactAsync();
        PromotionFixture healthy = await _harness.SeedCompletedGcodeArtifactAsync(
            content: "; second pending promotion\nG28\nG1 X20 Y20 F3000\n");
        Guid poisonedCheckpoint = await _harness.SeedInterruptedPromotionAsync(poisoned, "promotion-poisoned");
        Guid healthyCheckpoint = await _harness.SeedInterruptedPromotionAsync(healthy, "promotion-healthy");
        await _harness.DeleteArtifactRowAsync(poisoned.ArtifactId);

        int reconciled = await _harness.CreatePromoter().ReconcilePendingAsync(10, CancellationToken.None);

        _ = reconciled.Should().Be(1, "the unresolvable checkpoint must not abort the pass");
        _ = (await _harness.GetCheckpointAsync(poisonedCheckpoint)).State.Should().Be(GcodePromotionState.Failed);
        _ = (await _harness.GetCheckpointAsync(healthyCheckpoint)).State.Should().Be(GcodePromotionState.Completed);
    }

    [Fact(DisplayName = "An unresolvable checkpoint leaves promotion capability healthy")]
    public async Task ReconcilePendingAsync_WithFailedCheckpoint_KeepsCapabilityOperational()
    {
        PromotionFixture poisoned = await _harness.SeedCompletedGcodeArtifactAsync();
        _ = await _harness.SeedInterruptedPromotionAsync(poisoned, "promotion-poisoned-capability");
        await _harness.DeleteArtifactRowAsync(poisoned.ArtifactId);

        _ = await _harness.CreatePromoter().ReconcilePendingAsync(10, CancellationToken.None);
        GcodePromotionCapabilityDto capability = await _harness.CreatePromoter()
            .GetCapabilityAsync(CancellationToken.None);

        _ = capability.Operational.Should().BeTrue();
        _ = capability.ReconcilerHealthy.Should().BeTrue();
    }

    [Fact(DisplayName = "An operation key several owners reused is ambiguous for a farm administrator")]
    public async Task GetPromotionAsync_ForAdminWithReusedKey_ReturnsAmbiguousConflict()
    {
        PromotionFixture first = await _harness.SeedCompletedGcodeArtifactAsync();
        PromotionFixture second = await _harness.SeedCompletedGcodeArtifactAsync(
            content: "; a different owner's calibration\nG28\nG1 X5 Y5 F3000\n");
        _ = await _harness.CreatePromoter().PromoteAsync(
            first.Request("shared-admin-key"),
            first.Owner,
            CancellationToken.None);
        _ = await _harness.CreatePromoter().PromoteAsync(
            second.Request("shared-admin-key"),
            second.Owner,
            CancellationToken.None);
        CalibrationActor administrator = new(Guid.NewGuid(), "farm-admin", true);

        CalibrationApiResult<GcodePromotionDto> result = await _harness.CreatePromoter()
            .GetPromotionAsync("shared-admin-key", administrator, CancellationToken.None);

        _ = result.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        _ = result.Code.Should().Be("promotion_operation_ambiguous");
    }

    /// <summary>Seeded slice job, artifact and calibration context used by one promotion scenario.</summary>
    private sealed record PromotionFixture(
        Guid ArtifactId,
        Guid JobId,
        Guid WorkerId,
        Guid ProjectId,
        Guid AttemptId,
        Guid OrchestrationId,
        string Sha256,
        long SizeBytes,
        string SpecificationSha256,
        string MachineProfileSha256,
        CalibrationActor Owner)
    {
        public GcodeArtifactPromotionRequest Request(string operationId) => new()
        {
            OperationId = operationId,
            SourceArtifactId = ArtifactId,
            SourceSliceJobId = JobId,
            ExpectedSha256 = Sha256,
            ExpectedSizeBytes = SizeBytes,
            SourceWorkerId = WorkerId,
            CalibrationProjectId = ProjectId,
            CalibrationAttemptId = AttemptId,
            CalibrationOrchestrationId = OrchestrationId,
        };
    }

    /// <summary>
    /// Wires a real SQLite core context, a real SQLite slicer context and the real artifact/library
    /// services so promotion behaviour is exercised through persistence rather than through mocks.
    /// </summary>
    private sealed class PromotionHarness : IDisposable
    {
        private readonly string _rootPath;
        private readonly string _coreConnectionString;
        private readonly string _slicerConnectionString;
        private readonly Guid _folderId = Guid.NewGuid();

        private PromotionHarness(string rootPath)
        {
            _rootPath = rootPath;
            ArtifactRoot = Path.Combine(rootPath, "artifacts");
            GcodeRoot = Path.Combine(rootPath, "gcode");
            _ = Directory.CreateDirectory(ArtifactRoot);
            _ = Directory.CreateDirectory(GcodeRoot);
            _coreConnectionString =
                $"Data Source={Path.Combine(rootPath, "core.db")};Pooling=false;Default Timeout=30";
            _slicerConnectionString =
                $"Data Source={Path.Combine(rootPath, "slicer.db")};Pooling=false;Default Timeout=30";
        }

        public string ArtifactRoot { get; }

        public string GcodeRoot { get; }

        public GcodePromotionReconcilerState ReconcilerState { get; } = new();

        public static async Task<PromotionHarness> CreateAsync()
        {
            PromotionHarness harness = new(Path.Combine(
                Path.GetTempPath(),
                $"pf-promotion-{Guid.NewGuid():N}"));
            await using (AppDbContext core = harness.CreateCoreContext())
            {
                _ = await core.Database.EnsureCreatedAsync();
                _ = core.Set<FolderNode>().Add(new FolderNode
                {
                    Id = harness._folderId,
                    Path = "/",
                    FolderType = "gcode",
                });
                _ = await core.SaveChangesAsync();
            }

            await using SlicerDbContext slicer = harness.CreateSlicerContext();
            _ = await slicer.Database.EnsureCreatedAsync();
            return harness;
        }

        public AppDbContext CreateCoreContext() =>
            new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_coreConnectionString).Options);

        public SlicerDbContext CreateSlicerContext() =>
            new(new DbContextOptionsBuilder<SlicerDbContext>().UseSqlite(_slicerConnectionString).Options);

        public IGcodeArtifactPromoter CreatePromoter(bool withArtifactRouting = true)
        {
            AppDbContext core = CreateCoreContext();
            SlicerContextFactory slicerFactory = new(_slicerConnectionString);
            IArtifactsRepository artifactsRepository = new EfArtifactsRepository(slicerFactory);
            ISliceJobRepository sliceJobs = new EfSliceJobRepository(CreateSlicerContext());
            return new GcodeArtifactPromoter(
                core,
                CreateGcodeFilesService(core),
                CreateStoragePaths(),
                ReconcilerState,
                NullLogger<GcodeArtifactPromoter>.Instance,
                withArtifactRouting ? CreateArtifactsService(artifactsRepository) : null,
                withArtifactRouting ? artifactsRepository : null,
                withArtifactRouting ? sliceJobs : null);
        }

        public IArtifactsRepository CreateArtifactsRepository() =>
            new EfArtifactsRepository(new SlicerContextFactory(_slicerConnectionString));

        public async Task<PromotionFixture> SeedCompletedGcodeArtifactAsync(
            string kind = SlicerArtifactKinds.Gcode,
            string contentType = "text/x.gcode",
            string jobStatus = SliceJobStatus.Completed,
            string content = GcodeContent,
            Guid? ownerId = null)
        {
            Guid owner = ownerId ?? Guid.NewGuid();
            Guid projectId = Guid.NewGuid();
            Guid attemptId = Guid.NewGuid();
            Guid snapshotId = Guid.NewGuid();
            Guid orchestrationId = Guid.NewGuid();
            Guid jobId = Guid.NewGuid();
            Guid artifactId = Guid.NewGuid();
            Guid workerId = Guid.NewGuid();
            byte[] bytes = Encoding.UTF8.GetBytes(content);
            string sha256 = Convert.ToHexString(SHA256.HashData(bytes));
            string specificationSha256 =
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"spec-{attemptId}")));
            string machineProfileSha256 =
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"machine-{attemptId}")));

            await SeedCalibrationContextAsync(
                owner,
                projectId,
                attemptId,
                snapshotId,
                specificationSha256,
                machineProfileSha256);

            string relativePath = $"{artifactId}.gcode";
            await File.WriteAllBytesAsync(Path.Combine(ArtifactRoot, relativePath), bytes);

            await using SlicerDbContext slicer = CreateSlicerContext();
            _ = slicer.SliceJobs.Add(new SliceJob
            {
                Id = jobId,
                UserId = owner,
                ModelFileUrl = "storage://model",
                ModelFileName = "calibration.3mf",
                ModelSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("model"))),
                SlicerEngine = (int)SlicerType.OrcaSlicer,
                SlicerEngineName = "OrcaSlicer",
                SlicerDistribution = "upstream",
                SlicerVersion = "2.3.1",
                SlicerContainerDigest = "sha256:pinned-container",
                MachineProfileSha256 = machineProfileSha256,
                Status = jobStatus,
                CalibrationProjectId = projectId,
                CalibrationAttemptId = attemptId,
                CalibrationOrchestrationId = orchestrationId,
                CorrelationId = Guid.NewGuid(),
                WorkerId = workerId,
                QueuedAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            _ = slicer.Artifacts.Add(new Artifact
            {
                Id = artifactId,
                JobId = jobId,
                WorkerId = workerId,
                Kind = kind,
                FileName = "calibration.gcode",
                RelativePath = relativePath,
                ContentType = contentType,
                SizeBytes = bytes.LongLength,
                Sha256 = sha256,
                DeclaredSha256 = sha256,
                CreatedAt = DateTime.UtcNow,
            });
            _ = await slicer.SaveChangesAsync();

            return new PromotionFixture(
                artifactId,
                jobId,
                workerId,
                projectId,
                attemptId,
                orchestrationId,
                sha256,
                bytes.LongLength,
                specificationSha256,
                machineProfileSha256,
                new CalibrationActor(owner, "owner", false));
        }

        public async Task<Guid> SeedInterruptedPromotionAsync(PromotionFixture fixture, string operationId)
        {
            // Reproduces a crash between accepting the promotion and copying the bytes: the checkpoint
            // and the artifact pin are durable, but no G-code file exists yet.
            Guid checkpointId = Guid.NewGuid();
            DateTime nowUtc = DateTime.UtcNow;
            await using (AppDbContext core = CreateCoreContext())
            {
                _ = core.GcodePromotionCheckpoints.Add(new GcodePromotionCheckpoint
                {
                    Id = checkpointId,
                    OwnerUserId = fixture.Owner.UserId,
                    OperationScope = GcodePromotionOperationKey.ScopeFor(fixture.Owner.UserId),
                    OperationId = operationId,
                    RequestSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(operationId))),
                    SourceArtifactId = fixture.ArtifactId,
                    SourceSliceJobId = fixture.JobId,
                    SourceWorkerId = fixture.WorkerId,
                    SourceContentSha256 = fixture.Sha256,
                    SourceSizeBytes = fixture.SizeBytes,
                    CalibrationProjectId = fixture.ProjectId,
                    CalibrationAttemptId = fixture.AttemptId,
                    CalibrationOrchestrationId = fixture.OrchestrationId,
                    GcodeFileId = Guid.NewGuid(),
                    State = GcodePromotionState.Pending,
                    CreatedAtUtc = nowUtc,
                    UpdatedAtUtc = nowUtc,
                });
                _ = await core.SaveChangesAsync();
            }

            await PinArtifactForPromotionAsync(
                fixture.ArtifactId,
                operationId,
                checkpointId,
                fixture.Owner.UserId);
            return checkpointId;
        }

        public async Task PinArtifactForPromotionAsync(
            Guid artifactId,
            string operationId,
            Guid? checkpointId = null,
            Guid? ownerUserId = null)
        {
            await using SlicerDbContext slicer = CreateSlicerContext();
            Artifact artifact = await slicer.Artifacts.SingleAsync(candidate => candidate.Id == artifactId);
            artifact.PromotionOperationId = operationId;
            artifact.PromotionOperationKey =
                GcodePromotionOperationKey.Compute(ownerUserId ?? Guid.Empty, operationId);
            artifact.PromotionCheckpointId = checkpointId ?? Guid.NewGuid();
            artifact.PromotionStartedAtUtc = DateTime.UtcNow;
            _ = await slicer.SaveChangesAsync();
        }

        /// <summary>Removes an artifact row so its promotion checkpoint can no longer be resolved.</summary>
        /// <param name="artifactId">The artifact identity.</param>
        /// <returns>A task that completes when the row is gone.</returns>
        public async Task DeleteArtifactRowAsync(Guid artifactId)
        {
            await using SlicerDbContext slicer = CreateSlicerContext();
            Artifact artifact = await slicer.Artifacts.SingleAsync(candidate => candidate.Id == artifactId);
            _ = slicer.Artifacts.Remove(artifact);
            _ = await slicer.SaveChangesAsync();
        }

        /// <summary>
        /// Seeds an unrelated library file that already occupies an owner's promotion identity, which
        /// is what a lost race against a concurrent promoter looks like to the database.
        /// </summary>
        /// <param name="ownerUserId">Owner whose operation key is taken.</param>
        /// <param name="operationId">The caller-supplied idempotency key that is taken.</param>
        /// <returns>A task that completes when the row is durable.</returns>
        public async Task SeedFileHoldingOperationIdentityAsync(Guid ownerUserId, string operationId)
        {
            await using AppDbContext core = CreateCoreContext();
            _ = core.GcodeFiles.Add(new GcodeFile
            {
                Id = Guid.NewGuid(),
                FileName = "already-promoted.gcode",
                FileHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"winner-{operationId}"))),
                FileSizeBytes = 42,
                FilePath = Path.Combine(GcodeRoot, "already-promoted.gcode"),
                FolderId = _folderId,
                UploadedAt = DateTime.UtcNow,
                PromotionOperationId = operationId,
                PromotionOperationKey = GcodePromotionOperationKey.Compute(ownerUserId, operationId),
            });
            _ = await core.SaveChangesAsync();
        }

        public void DeleteArtifactBytes(Guid artifactId) =>
            File.Delete(Path.Combine(ArtifactRoot, $"{artifactId}.gcode"));

        public bool ArtifactBytesExist(Guid artifactId) =>
            File.Exists(Path.Combine(ArtifactRoot, $"{artifactId}.gcode"));

        public async Task AgeArtifactAsync(Guid artifactId, TimeSpan age)
        {
            await using SlicerDbContext slicer = CreateSlicerContext();
            Artifact artifact = await slicer.Artifacts.SingleAsync(candidate => candidate.Id == artifactId);
            artifact.CreatedAt = DateTime.UtcNow - age;
            _ = await slicer.SaveChangesAsync();
        }

        public async Task SetArtifactCleanupReservationAsync(
            Guid artifactId,
            Guid reservationToken,
            DateTime reservedAtUtc,
            DateTime? deletionStartedAtUtc = null)
        {
            await using SlicerDbContext slicer = CreateSlicerContext();
            Artifact artifact = await slicer.Artifacts.SingleAsync(candidate => candidate.Id == artifactId);
            artifact.CleanupReservationToken = reservationToken;
            artifact.CleanupReservedAtUtc = reservedAtUtc;
            artifact.CleanupDeletionStartedAtUtc = deletionStartedAtUtc;
            _ = await slicer.SaveChangesAsync();
        }

        public async Task<Artifact> GetArtifactAsync(Guid artifactId)
        {
            await using SlicerDbContext slicer = CreateSlicerContext();
            return await slicer.Artifacts.AsNoTracking()
                .SingleAsync(candidate => candidate.Id == artifactId);
        }

        public async Task<int> RunArtifactCleanupAsync(
            IArtifactsRepository? repository = null,
            Action<string>? deleteArtifactFile = null,
            Func<string, bool>? artifactFileExists = null)
        {
            ArtifactStorageSettings settings = new()
            {
                RootPath = ArtifactRoot,
                MaxAgeDays = 1,
                MaxTotalBytes = null,
                EnableCleanupDryRun = false,
                CleanupReservationTimeoutMinutes = 30,
            };
            IArtifactsRepository resolvedRepository = repository ?? CreateArtifactsRepository();
            ArtifactCleanupService cleanup =
                deleteArtifactFile is null && artifactFileExists is null
                ? new ArtifactCleanupService(
                    resolvedRepository,
                    Options.Create(settings),
                    CreateHostEnvironment(),
                    NullLogger<ArtifactCleanupService>.Instance)
                : new TestArtifactCleanupService(
                    resolvedRepository,
                    Options.Create(settings),
                    CreateHostEnvironment(),
                    NullLogger<ArtifactCleanupService>.Instance,
                    deleteArtifactFile,
                    artifactFileExists);
            return await cleanup.ScanAndCleanupAsync(CancellationToken.None);
        }

        private sealed class TestArtifactCleanupService(
            IArtifactsRepository artifactsRepository,
            IOptions<ArtifactStorageSettings> options,
            IWebHostEnvironment environment,
            ILogger<ArtifactCleanupService> logger,
            Action<string>? deleteArtifactFile,
            Func<string, bool>? artifactFileExists)
            : ArtifactCleanupService(artifactsRepository, options, environment, logger)
        {
            private readonly Action<string>? _deleteArtifactFile = deleteArtifactFile;
            private readonly Func<string, bool>? _artifactFileExists = artifactFileExists;

            protected override bool ArtifactFileExists(string path) =>
                _artifactFileExists?.Invoke(path) ?? base.ArtifactFileExists(path);

            protected override void DeleteArtifactFile(
                string rootPath,
                string path)
            {
                if (_deleteArtifactFile is null)
                {
                    base.DeleteArtifactFile(rootPath, path);
                    return;
                }

                _deleteArtifactFile(path);
            }
        }

        public async Task<bool> ArtifactExistsAsync(Guid artifactId)
        {
            await using SlicerDbContext slicer = CreateSlicerContext();
            return await slicer.Artifacts.AnyAsync(candidate => candidate.Id == artifactId);
        }

        public async Task<bool> IsArtifactCleanupEligibleAsync(Guid artifactId)
        {
            await using SlicerDbContext slicer = CreateSlicerContext();
            Artifact artifact = await slicer.Artifacts.AsNoTracking()
                .SingleAsync(candidate => candidate.Id == artifactId);
            return artifact.IsCleanupEligible();
        }

        public async Task<GcodeFile> GetGcodeFileAsync(Guid fileId)
        {
            await using AppDbContext core = CreateCoreContext();
            return await core.GcodeFiles.AsNoTracking().SingleAsync(file => file.Id == fileId);
        }

        public async Task<GcodePromotionCheckpoint> GetCheckpointAsync(Guid checkpointId)
        {
            await using AppDbContext core = CreateCoreContext();
            return await core.GcodePromotionCheckpoints.AsNoTracking()
                .SingleAsync(checkpoint => checkpoint.Id == checkpointId);
        }

        public async Task<int> CountGcodeFilesAsync()
        {
            await using AppDbContext core = CreateCoreContext();
            return await core.GcodeFiles.CountAsync();
        }

        public async Task<byte[]> ReadPromotedBytesAsync(Guid fileId)
        {
            GcodeFile file = await GetGcodeFileAsync(fileId);
            return await File.ReadAllBytesAsync(Path.Combine(GcodeRoot, file.FileName));
        }

        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (!Directory.Exists(_rootPath))
            {
                return;
            }

            try
            {
                Directory.Delete(_rootPath, recursive: true);
            }
            catch (IOException)
            {
                // Temporary test data is reclaimed by the operating system.
            }
        }

        private async Task SeedCalibrationContextAsync(
            Guid ownerId,
            Guid projectId,
            Guid attemptId,
            Guid snapshotId,
            string specificationSha256,
            string machineProfileSha256)
        {
            await using AppDbContext core = CreateCoreContext();
            Guid manufacturerId = Guid.NewGuid();
            Guid modelId = Guid.NewGuid();
            Guid printerId = Guid.NewGuid();
            DateTime nowUtc = DateTime.UtcNow;
            _ = core.Manufacturers.Add(new Manufacturer { Id = manufacturerId, Name = $"M-{manufacturerId:N}" });
            _ = core.PrinterModels.Add(new PrinterModel
            {
                Id = modelId,
                ManufacturerId = manufacturerId,
                Name = $"Model-{modelId:N}",
            });
            _ = core.Printers.Add(new Printer
            {
                Id = printerId,
                Name = $"Printer-{printerId:N}",
                ServerUrl = $"http://{printerId:N}.test",
                BackendPort = 7125,
                ManufacturerId = manufacturerId,
                ModelId = modelId,
            });
            _ = core.CalibrationProjects.Add(new CalibrationProject
            {
                Id = projectId,
                OwnerUserId = ownerId,
                Name = "Promotion project",
                PrinterId = printerId,
                FilamentProvider = "catalog",
                FilamentProductId = $"product-{projectId:N}",
                FilamentProductName = "PLA",
                FilamentMaterial = "PLA",
                FilamentSnapshotJson = "{}",
                OrderedStepsJson = "[]",
                CurrentSelectionsJson = "{}",
                CreateRequestId = $"seed-{projectId:N}",
                CreatedAtUtc = nowUtc,
                UpdatedAtUtc = nowUtc,
                CreatedBySubject = "seed",
                UpdatedBySubject = "seed",
            });
            _ = core.PrinterConfigurationSnapshots.Add(new PrinterConfigurationSnapshot
            {
                Id = snapshotId,
                ProjectId = projectId,
                AttemptId = attemptId,
                PrinterId = printerId,
                SchemaVersion = "1.0",
                SanitizedSnapshotJson = "{}",
                SnapshotSha256 =
                    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"snapshot-{snapshotId}"))),
                PrinterConfigurationRevision = 1,
                FirmwareFamily = PrinterFirmwareFamily.Klipper,
                GcodeDialect = PrinterGcodeDialect.Klipper,
                FirmwareDetectionSource = FirmwareDetectionSource.Printer,
                SlicerEngine = "OrcaSlicer",
                SlicerDistribution = "upstream",
                SlicerVersion = "2.3.1",
                SlicerContainerDigest = "sha256:pinned-container",
                MachineProfileSha256 = machineProfileSha256,
                CapturedAtUtc = nowUtc,
                CapturedBySubject = "seed",
            });
            _ = core.CalibrationAttempts.Add(new CalibrationAttempt
            {
                Id = attemptId,
                ProjectId = projectId,
                Sequence = 1,
                CalibrationKind = "flow",
                Method = "flow-coarse",
                DefinitionVersion = "1.0",
                SpecificationSha256 = specificationSha256,
                PrinterConfigurationSnapshotId = snapshotId,
                AttemptRequestId = $"attempt-{attemptId:N}",
                CreatedAtUtc = nowUtc,
                CreatedBySubject = "seed",
            });
            _ = await core.SaveChangesAsync();
        }

        private IGcodeFilesService CreateGcodeFilesService(AppDbContext core)
        {
            Mock<IStoragePathService> storagePaths = new(MockBehavior.Loose);
            _ = storagePaths.Setup(service => service.GetGcodeStorageDirectory()).Returns(GcodeRoot);
            _ = storagePaths.Setup(service => service.GetThumbnailDirectory()).Returns(GcodeRoot);

            Mock<IGcodeMetadataExtractorService> metadata = new(MockBehavior.Loose);
            _ = metadata.Setup(service => service.ExtractMetadataAsync(It.IsAny<string>()))
                .ReturnsAsync(new GcodeMetadataExtracted());

            Mock<IGcodeThumbnailExtractorService> thumbnails = new(MockBehavior.Loose);
            _ = thumbnails.Setup(service => service.ExtractAndSaveThumbnailAsync(
                    It.IsAny<Stream>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((string?)null);

            Mock<IFolderManagementService> folders = new(MockBehavior.Loose);
            _ = folders.Setup(service => service.GetOrCreateFolderAsync(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new FolderNode { Id = _folderId, Path = "/", FolderType = "gcode" });

            return new GcodeFilesService(
                new EfGcodeRepository(core),
                new Mock<IUnitOfWork>(MockBehavior.Loose).Object,
                NullLogger<GcodeFilesService>.Instance,
                storagePaths.Object,
                metadata.Object,
                thumbnails.Object,
                folders.Object,
                new Mock<IStoredFileOperationsService>(MockBehavior.Loose).Object,
                new Mock<IPrintFarmerTelemetryService>(MockBehavior.Loose).Object);
        }

        private IStoragePathService CreateStoragePaths()
        {
            Mock<IStoragePathService> storagePaths = new(MockBehavior.Loose);
            _ = storagePaths.Setup(service => service.GetGcodeStorageDirectory()).Returns(GcodeRoot);
            return storagePaths.Object;
        }

        private IArtifactsService CreateArtifactsService(IArtifactsRepository repository) =>
            new ArtifactsService(
                CreateHostEnvironment(),
                repository,
                Options.Create(new ArtifactStorageSettings { RootPath = ArtifactRoot }),
                new ArtifactsMetrics());

        private IWebHostEnvironment CreateHostEnvironment()
        {
            Mock<IWebHostEnvironment> environment = new(MockBehavior.Loose);
            _ = environment.SetupGet(host => host.ContentRootPath).Returns(_rootPath);
            _ = environment.SetupGet(host => host.EnvironmentName).Returns("Testing");
            return environment.Object;
        }

        private sealed class SlicerContextFactory(string connectionString) : IDbContextFactory<SlicerDbContext>
        {
            public SlicerDbContext CreateDbContext() =>
                new(new DbContextOptionsBuilder<SlicerDbContext>().UseSqlite(connectionString).Options);
        }
    }
}
