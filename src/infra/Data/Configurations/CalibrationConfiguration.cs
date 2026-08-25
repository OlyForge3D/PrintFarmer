using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

/// <summary>Configures calibration aggregates, immutable history, and sync state.</summary>
public sealed class CalibrationProjectConfiguration : IEntityTypeConfiguration<CalibrationProject>
{
    public void Configure(EntityTypeBuilder<CalibrationProject> builder)
    {
        _ = builder.HasKey(project => project.Id);
        _ = builder.Property(project => project.Name).IsRequired().HasMaxLength(200);
        _ = builder.Property(project => project.FilamentProvider).IsRequired().HasMaxLength(64);
        _ = builder.Property(project => project.FilamentProductId).IsRequired().HasMaxLength(256);
        _ = builder.Property(project => project.FilamentSku).HasMaxLength(256);
        _ = builder.Property(project => project.FilamentVendor).HasMaxLength(256);
        _ = builder.Property(project => project.FilamentProductName).IsRequired().HasMaxLength(256);
        _ = builder.Property(project => project.FilamentMaterial).IsRequired().HasMaxLength(64);
        _ = builder.Property(project => project.FilamentColor).HasMaxLength(32);
        _ = builder.Property(project => project.FilamentDiameter).HasPrecision(6, 3);
        _ = builder.Property(project => project.FilamentSnapshotJson).IsRequired();
        _ = builder.Property(project => project.OrderedStepsJson).IsRequired();
        _ = builder.Property(project => project.CurrentSelectionsJson).IsRequired();
        _ = builder.Property(project => project.CurrentStep).HasMaxLength(128);
        _ = builder.Property(project => project.Revision).IsConcurrencyToken().ValueGeneratedNever();
        _ = builder.Property(project => project.CreateRequestId).IsRequired().HasMaxLength(128);
        _ = builder.Property(project => project.CreatedBySubject).IsRequired().HasMaxLength(256);
        _ = builder.Property(project => project.UpdatedBySubject).IsRequired().HasMaxLength(256);
        _ = builder.Property(project => project.DeletedBySubject).HasMaxLength(256);
        _ = builder.HasIndex(project => new { project.OwnerUserId, project.CreateRequestId }).IsUnique();
        _ = builder.HasIndex(project => new { project.OwnerUserId, project.DeletedAtUtc, project.UpdatedAtUtc });

        // Subject/owner identifiers intentionally remain soft references so user
        // deletion or deactivation never erases calibration history.
        _ = builder.HasOne<Printer>().WithMany().HasForeignKey(project => project.PrinterId)
            .OnDelete(DeleteBehavior.Restrict);
        _ = builder.HasOne<Spool>().WithMany().HasForeignKey(project => project.LocalSpoolId)
            .OnDelete(DeleteBehavior.SetNull);
        _ = builder.HasOne<FilamentType>().WithMany().HasForeignKey(project => project.FilamentTypeId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}

/// <summary>Configures editable drafts and their active-lineage uniqueness rule.</summary>
public sealed class CalibrationDraftConfiguration : IEntityTypeConfiguration<CalibrationDraft>
{
    public void Configure(EntityTypeBuilder<CalibrationDraft> builder)
    {
        _ = builder.HasKey(draft => draft.Id);
        _ = builder.Property(draft => draft.StepId).IsRequired().HasMaxLength(128);
        _ = builder.Property(draft => draft.DeviceLineageId).IsRequired().HasMaxLength(128);
        _ = builder.Property(draft => draft.Method).IsRequired().HasMaxLength(128);
        _ = builder.Property(draft => draft.ValuesJson).IsRequired();
        _ = builder.Property(draft => draft.PrerequisitesJson).IsRequired();
        _ = builder.Property(draft => draft.Revision).IsConcurrencyToken().ValueGeneratedNever();
        _ = builder.Property(draft => draft.CreatedBySubject).IsRequired().HasMaxLength(256);
        _ = builder.Property(draft => draft.UpdatedBySubject).IsRequired().HasMaxLength(256);
        _ = builder.HasIndex(draft => new { draft.ProjectId, draft.StepId, draft.DeviceLineageId })
            .IsUnique();
        _ = builder.HasOne<CalibrationProject>().WithMany().HasForeignKey(draft => draft.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary>Configures immutable attempts and append-only attempt history.</summary>
public sealed class CalibrationAttemptConfiguration : IEntityTypeConfiguration<CalibrationAttempt>
{
    public void Configure(EntityTypeBuilder<CalibrationAttempt> builder)
    {
        _ = builder.HasKey(attempt => attempt.Id);
        _ = builder.Property(attempt => attempt.CalibrationKind).IsRequired().HasMaxLength(128);
        _ = builder.Property(attempt => attempt.Method).IsRequired().HasMaxLength(128);
        _ = builder.Property(attempt => attempt.DefinitionVersion).IsRequired().HasMaxLength(64);
        _ = builder.Property(attempt => attempt.InputJson).IsRequired();
        _ = builder.Property(attempt => attempt.SpecificationJson).IsRequired();
        _ = builder.Property(attempt => attempt.SpecificationSha256).IsRequired().HasMaxLength(64);
        _ = builder.Property(attempt => attempt.ProfileSnapshotIdsJson).IsRequired();
        _ = builder.Property(attempt => attempt.AttemptRequestId).IsRequired().HasMaxLength(128);
        _ = builder.Property(attempt => attempt.CreatedBySubject).IsRequired().HasMaxLength(256);
        _ = builder.HasIndex(attempt => new { attempt.ProjectId, attempt.Sequence }).IsUnique();
        _ = builder.HasIndex(attempt => new { attempt.ProjectId, attempt.AttemptRequestId }).IsUnique();
        _ = builder.HasOne<CalibrationProject>().WithMany().HasForeignKey(attempt => attempt.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary>Configures lifecycle facts without mutable status state.</summary>
public sealed class CalibrationAttemptEventConfiguration : IEntityTypeConfiguration<CalibrationAttemptEvent>
{
    public void Configure(EntityTypeBuilder<CalibrationAttemptEvent> builder)
    {
        _ = builder.HasKey(@event => @event.Id);
        _ = builder.Property(@event => @event.EventType).IsRequired().HasMaxLength(128);
        _ = builder.Property(@event => @event.DerivedStatus).IsRequired().HasMaxLength(64);
        _ = builder.Property(@event => @event.ErrorCode).HasMaxLength(128);
        _ = builder.Property(@event => @event.OperationId).IsRequired().HasMaxLength(128);
        _ = builder.Property(@event => @event.ActorSubject).IsRequired().HasMaxLength(256);
        _ = builder.HasIndex(@event => new { @event.AttemptId, @event.OperationId }).IsUnique();
        _ = builder.HasIndex(@event => new { @event.AttemptId, @event.Sequence }).IsUnique();
        _ = builder.HasOne<CalibrationProject>().WithMany().HasForeignKey(@event => @event.ProjectId)
            .OnDelete(DeleteBehavior.Restrict);
        _ = builder.HasOne<CalibrationAttempt>().WithMany().HasForeignKey(@event => @event.AttemptId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary>Configures append-only calibration measurements and selections.</summary>
public sealed class CalibrationObservationConfiguration : IEntityTypeConfiguration<CalibrationObservation>
{
    public void Configure(EntityTypeBuilder<CalibrationObservation> builder)
    {
        _ = builder.HasKey(observation => observation.Id);
        _ = builder.Property(observation => observation.ObservationType).IsRequired().HasMaxLength(128);
        _ = builder.Property(observation => observation.MeasurementsJson).IsRequired();
        _ = builder.Property(observation => observation.ResultJson).IsRequired();
        _ = builder.Property(observation => observation.UnitsJson).IsRequired();
        _ = builder.Property(observation => observation.Confidence).HasPrecision(5, 4);
        _ = builder.Property(observation => observation.SelectionReason).HasMaxLength(512);
        _ = builder.Property(observation => observation.OperationId).IsRequired().HasMaxLength(128);
        _ = builder.Property(observation => observation.ActorSubject).IsRequired().HasMaxLength(256);
        _ = builder.HasIndex(observation => new { observation.AttemptId, observation.OperationId }).IsUnique();
        _ = builder.HasIndex(observation => new { observation.AttemptId, observation.Sequence }).IsUnique();
        _ = builder.HasOne<CalibrationProject>().WithMany().HasForeignKey(observation => observation.ProjectId)
            .OnDelete(DeleteBehavior.Restrict);
        _ = builder.HasOne<CalibrationAttempt>().WithMany().HasForeignKey(observation => observation.AttemptId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary>Configures private photo metadata while keeping opaque storage keys internal.</summary>
public sealed class CalibrationPhotoConfiguration : IEntityTypeConfiguration<CalibrationPhoto>
{
    public void Configure(EntityTypeBuilder<CalibrationPhoto> builder)
    {
        _ = builder.HasKey(photo => photo.Id);
        _ = builder.Property(photo => photo.ClientUploadId).IsRequired().HasMaxLength(128);
        _ = builder.Property(photo => photo.OpaqueStorageKey).IsRequired().HasMaxLength(512);
        _ = builder.Property(photo => photo.OriginalFileName).IsRequired().HasMaxLength(255);
        _ = builder.Property(photo => photo.ContentType).IsRequired().HasMaxLength(128);
        _ = builder.Property(photo => photo.Sha256).IsRequired().HasMaxLength(64);
        _ = builder.Property(photo => photo.Caption).HasMaxLength(1024);
        _ = builder.Property(photo => photo.Revision).IsConcurrencyToken().ValueGeneratedNever();
        _ = builder.Property(photo => photo.CreatedBySubject).IsRequired().HasMaxLength(256);
        _ = builder.Property(photo => photo.DeletedBySubject).HasMaxLength(256);
        _ = builder.HasIndex(photo => new { photo.AttemptId, photo.ClientUploadId }).IsUnique();
        _ = builder.HasIndex(photo => new { photo.ProjectId, photo.DeletedAtUtc });
        _ = builder.HasOne<CalibrationProject>().WithMany().HasForeignKey(photo => photo.ProjectId)
            .OnDelete(DeleteBehavior.Restrict);
        _ = builder.HasOne<CalibrationAttempt>().WithMany().HasForeignKey(photo => photo.AttemptId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary>Configures durable compensation for blobs left by failed metadata writes.</summary>
public sealed class CalibrationBlobCleanupConfiguration : IEntityTypeConfiguration<CalibrationBlobCleanup>
{
    public void Configure(EntityTypeBuilder<CalibrationBlobCleanup> builder)
    {
        _ = builder.HasKey(cleanup => cleanup.Id);
        _ = builder.Property(cleanup => cleanup.OpaqueStorageKey).IsRequired().HasMaxLength(512);
        _ = builder.HasIndex(cleanup => cleanup.OpaqueStorageKey).IsUnique();
        _ = builder.HasIndex(cleanup => cleanup.CreatedAtUtc);
    }
}

/// <summary>Configures exact idempotency replay storage.</summary>
public sealed class CalibrationIdempotencyRecordConfiguration : IEntityTypeConfiguration<CalibrationIdempotencyRecord>
{
    public void Configure(EntityTypeBuilder<CalibrationIdempotencyRecord> builder)
    {
        _ = builder.HasKey(record => record.Id);
        _ = builder.Property(record => record.Scope).IsRequired().HasMaxLength(128);
        _ = builder.Property(record => record.ClientId).IsRequired().HasMaxLength(128);
        _ = builder.Property(record => record.OperationId).IsRequired().HasMaxLength(128);
        _ = builder.Property(record => record.OperationType).IsRequired().HasMaxLength(128);
        _ = builder.Property(record => record.CanonicalRequestSha256).IsRequired().HasMaxLength(64);
        _ = builder.Property(record => record.ResourceType).IsRequired().HasMaxLength(64);
        _ = builder.HasIndex(record => new { record.Scope, record.ClientId, record.OperationId }).IsUnique();
        _ = builder.HasIndex(record => record.ExpiresAtUtc);
        _ = builder.HasOne<CalibrationProject>().WithMany().HasForeignKey(record => record.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary>Configures durable calibration saga checkpoints without cross-context FKs.</summary>
public sealed class CalibrationOrchestrationConfiguration : IEntityTypeConfiguration<CalibrationOrchestration>
{
    public void Configure(EntityTypeBuilder<CalibrationOrchestration> builder)
    {
        _ = builder.HasKey(orchestration => orchestration.Id);
        _ = builder.Property(orchestration => orchestration.CurrentStep).IsRequired().HasMaxLength(128);
        _ = builder.Property(orchestration => orchestration.LastErrorCode).HasMaxLength(128);
        _ = builder.Property(orchestration => orchestration.OperationId).IsRequired().HasMaxLength(128);
        _ = builder.Property(orchestration => orchestration.Revision).IsConcurrencyToken().ValueGeneratedNever();
        _ = builder.Property(orchestration => orchestration.GenerationRequestSha256).HasMaxLength(64);
        _ = builder.Property(orchestration => orchestration.SpecificationSha256).HasMaxLength(64);
        _ = builder.Property(orchestration => orchestration.LeaseOwner).HasMaxLength(128);
        _ = builder.HasIndex(orchestration => orchestration.AttemptId).IsUnique();
        _ = builder.HasIndex(orchestration => new { orchestration.Status, orchestration.NextRetryAtUtc });
        _ = builder.HasIndex(orchestration => orchestration.LeaseExpiresAtUtc);
        _ = builder.HasOne<CalibrationProject>().WithMany().HasForeignKey(orchestration => orchestration.ProjectId)
            .OnDelete(DeleteBehavior.Restrict);
        _ = builder.HasOne<CalibrationAttempt>().WithMany().HasForeignKey(orchestration => orchestration.AttemptId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary>Configures the transaction-serialized calibration journal allocator.</summary>
public sealed class CalibrationChangeFeedStateConfiguration : IEntityTypeConfiguration<CalibrationChangeFeedState>
{
    public void Configure(EntityTypeBuilder<CalibrationChangeFeedState> builder)
    {
        _ = builder.HasKey(state => state.Id);
        _ = builder.Property(state => state.Id).ValueGeneratedNever();
        _ = builder.HasData(new CalibrationChangeFeedState { Id = 1, LastSequence = 0 });
    }
}

/// <summary>Configures the monotonic synchronization journal and opaque cursors.</summary>
public sealed class CalibrationChangeConfiguration : IEntityTypeConfiguration<CalibrationChange>
{
    public void Configure(EntityTypeBuilder<CalibrationChange> builder)
    {
        _ = builder.HasKey(change => change.Sequence);
        _ = builder.Property(change => change.Sequence).ValueGeneratedNever();
        _ = builder.Property(change => change.EntityType).IsRequired().HasMaxLength(64);
        _ = builder.Property(change => change.MutationId).IsRequired().HasMaxLength(128);
        _ = builder.Property(change => change.ActorSubject).IsRequired().HasMaxLength(256);
        _ = builder.HasIndex(change => change.Id).IsUnique();
        _ = builder.HasIndex(change => new { change.OwnerUserId, change.MutationId }).IsUnique();
        _ = builder.HasIndex(change => new { change.OwnerUserId, change.Sequence });
        _ = builder.HasOne<CalibrationProject>().WithMany().HasForeignKey(change => change.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary>Configures scope-isolated durable cursor state.</summary>
public sealed class CalibrationSyncCursorConfiguration : IEntityTypeConfiguration<CalibrationSyncCursor>
{
    public void Configure(EntityTypeBuilder<CalibrationSyncCursor> builder)
    {
        _ = builder.HasKey(cursor => cursor.Id);
        _ = builder.Property(cursor => cursor.Scope).IsRequired().HasMaxLength(128);
        _ = builder.HasIndex(cursor => new { cursor.Scope, cursor.Sequence });
    }
}
