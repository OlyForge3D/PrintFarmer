using System.Text.Json;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

/// <summary>Portable catalog keys, concurrency, and noncascading asset boundaries.</summary>
public sealed class PrinterFileLocationConfiguration : IEntityTypeConfiguration<PrinterFileLocation>
{
    public void Configure(EntityTypeBuilder<PrinterFileLocation> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.StorageRoot).HasMaxLength(256);
        builder.Property(x => x.RelativePath).HasMaxLength(2048);
        builder.Property(x => x.IdentityKey).HasMaxLength(64).IsUnicode(false);
        builder.Property(x => x.DisplayName).HasMaxLength(512);
        builder.Property(x => x.Metadata).HasMaxLength(PrinterPrintMetadata.MaximumStoredJsonLength).HasConversion(
            value => JsonSerializer.Serialize(value, PrinterPrintMetadata.StorageJsonOptions),
            value => JsonSerializer.Deserialize<PrinterPrintMetadata>(value, PrinterPrintMetadata.StorageJsonOptions))
            .Metadata.SetValueComparer(new ValueComparer<PrinterPrintMetadata?>(
                (left, right) => JsonSerializer.Serialize(left, PrinterPrintMetadata.StorageJsonOptions) ==
                    JsonSerializer.Serialize(right, PrinterPrintMetadata.StorageJsonOptions),
                value => JsonSerializer.Serialize(value, PrinterPrintMetadata.StorageJsonOptions).GetHashCode(),
                value => JsonSerializer.Deserialize<PrinterPrintMetadata>(
                    JsonSerializer.Serialize(value, PrinterPrintMetadata.StorageJsonOptions),
                    PrinterPrintMetadata.StorageJsonOptions)));
        builder.Property(x => x.ModifiedValue).HasMaxLength(128);
        builder.Property(x => x.ProviderVersion).HasMaxLength(512);
        builder.Property(x => x.ProviderDigest).HasMaxLength(512);
        builder.Property(x => x.Availability).HasConversion<string>().HasMaxLength(32);
        builder.HasIndex(x => new { x.SourcePrinterId, x.IdentityKey }).IsUnique();
        builder.HasIndex(x => new { x.RegisteredPrinterId, x.Availability });
        builder.HasOne(x => x.RegisteredPrinter).WithMany()
            .HasForeignKey(x => x.RegisteredPrinterId).OnDelete(DeleteBehavior.SetNull);
        builder.Property(x => x.SourcePrinterId).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        builder.Property(x => x.StorageRoot).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        builder.Property(x => x.RelativePath).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        builder.Property(x => x.IdentityKey).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        builder.Property(x => x.FirstSeenAtUtc).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
    }
}

/// <summary>One exclusive scan/import/delete owner per registered printer.</summary>
public sealed class PrinterFileScanStateConfiguration : IEntityTypeConfiguration<PrinterFileScanState>
{
    public void Configure(EntityTypeBuilder<PrinterFileScanState> builder)
    {
        builder.HasKey(x => x.Id);
        builder.HasIndex(x => x.SourcePrinterId).IsUnique();
        builder.HasIndex(x => new { x.Enabled, x.NextScanAtUtc });
        builder.Property(x => x.ConfigurationFingerprint).HasMaxLength(64).IsUnicode(false);
        builder.Property(x => x.ReasonCode).HasMaxLength(64);
        builder.Property(x => x.Outcome).HasConversion<string>().HasMaxLength(32);
        builder.HasOne(x => x.RegisteredPrinter).WithMany()
            .HasForeignKey(x => x.RegisteredPrinterId).OnDelete(DeleteBehavior.SetNull);
        builder.Property(x => x.SourcePrinterId).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
    }
}

/// <summary>Only proven covered scopes receive complete-scan timestamps.</summary>
public sealed class PrinterFileRootScanConfiguration : IEntityTypeConfiguration<PrinterFileRootScan>
{
    public void Configure(EntityTypeBuilder<PrinterFileRootScan> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.StorageRoot).HasMaxLength(256);
        builder.Property(x => x.Subtree).HasMaxLength(2048);
        builder.Property(x => x.IdentityKey).HasMaxLength(64).IsUnicode(false);
        builder.HasIndex(x => new { x.ScanStateId, x.IdentityKey }).IsUnique();
        builder.HasOne(x => x.ScanState).WithMany()
            .HasForeignKey(x => x.ScanStateId).OnDelete(DeleteBehavior.NoAction);
        builder.Property(x => x.ScanStateId).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        builder.Property(x => x.StorageRoot).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        builder.Property(x => x.Subtree).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        builder.Property(x => x.IdentityKey).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
    }
}

/// <summary>Historical origins survive removal of remote and local copies.</summary>
public sealed class PrinterFileOriginConfiguration : IEntityTypeConfiguration<PrinterFileOrigin>
{
    public void Configure(EntityTypeBuilder<PrinterFileOrigin> builder)
    {
        builder.HasKey(x => x.Id);
        builder.HasIndex(x => x.OperationId).IsUnique();
        builder.HasIndex(x => x.SourcePrinterId);
        builder.Property(x => x.StorageRoot).HasMaxLength(256);
        builder.Property(x => x.RelativePath).HasMaxLength(2048);
        builder.Property(x => x.ContentSha256).HasMaxLength(64).IsUnicode(false);
        builder.Property(x => x.ProviderVersion).HasMaxLength(512);
        builder.Property(x => x.ProviderDigest).HasMaxLength(512);
        builder.Property(x => x.Verification).HasConversion<string>().HasMaxLength(32);
        builder.HasOne(x => x.Location).WithMany()
            .HasForeignKey(x => x.LocationId).OnDelete(DeleteBehavior.SetNull);
        builder.HasOne(x => x.GcodeFile).WithMany()
            .HasForeignKey(x => x.GcodeFileId).OnDelete(DeleteBehavior.SetNull);

        foreach (IMutableProperty property in builder.Metadata.GetProperties()
                     .Where(x => x.Name is not nameof(PrinterFileOrigin.GcodeFileId)
                         and not nameof(PrinterFileOrigin.LocationId)))
        {
            property.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        }
    }
}

/// <summary>Actor-scoped command identity and separately bound confirmations.</summary>
public sealed class PrinterFileOperationConfiguration : IEntityTypeConfiguration<PrinterFileOperation>
{
    public void Configure(EntityTypeBuilder<PrinterFileOperation> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.CommandKeyHash).HasMaxLength(64).IsUnicode(false);
        builder.Property(x => x.PayloadSha256).HasMaxLength(64).IsUnicode(false);
        builder.HasIndex(x => new { x.ActorId, x.CommandKeyHash }).IsUnique();
        builder.HasIndex(x => new { x.State, x.CreatedAtUtc });
        builder.Property(x => x.Action).HasConversion<string>().HasMaxLength(16);
        builder.Property(x => x.State).HasConversion<string>().HasMaxLength(40);
        builder.Property(x => x.CommandJson).HasMaxLength(16384);
        builder.Property(x => x.ContentSha256).HasMaxLength(64).IsUnicode(false);
        builder.Property(x => x.SnapshotConfirmationSha256).HasMaxLength(64).IsUnicode(false);
        builder.Property(x => x.DeleteConfirmationSha256).HasMaxLength(64).IsUnicode(false);
        builder.Property(x => x.Verification).HasConversion<string>().HasMaxLength(32);
        builder.Property(x => x.ReasonCode).HasMaxLength(64);
        builder.HasOne(x => x.Location).WithMany()
            .HasForeignKey(x => x.LocationId).OnDelete(DeleteBehavior.NoAction);
        builder.HasOne(x => x.GcodeFile).WithMany()
            .HasForeignKey(x => x.GcodeFileId).OnDelete(DeleteBehavior.SetNull);

        string[] commandFields =
        [
            nameof(PrinterFileOperation.ActorId),
            nameof(PrinterFileOperation.CommandKeyHash),
            nameof(PrinterFileOperation.PayloadSha256),
            nameof(PrinterFileOperation.SourcePrinterId),
            nameof(PrinterFileOperation.LocationId),
            nameof(PrinterFileOperation.ObservationRevision),
            nameof(PrinterFileOperation.ConfigurationGeneration),
            nameof(PrinterFileOperation.MutationGeneration),
            nameof(PrinterFileOperation.Action),
            nameof(PrinterFileOperation.CommandJson),
            nameof(PrinterFileOperation.DestinationPrinterId),
            nameof(PrinterFileOperation.CreatedAtUtc),
        ];
        foreach (string field in commandFields)
        {
            builder.Property(field).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        }
    }
}

/// <summary>Durable admission linkage; job IDs survive ordinary queue-history deletion.</summary>
public sealed class PrinterFileOperationJobConfiguration : IEntityTypeConfiguration<PrinterFileOperationJob>
{
    public void Configure(EntityTypeBuilder<PrinterFileOperationJob> builder)
    {
        builder.HasKey(x => new { x.OperationId, x.Ordinal });
        builder.HasIndex(x => x.JobId).IsUnique();
        builder.HasOne(x => x.Operation).WithMany(x => x.Jobs)
            .HasForeignKey(x => x.OperationId).OnDelete(DeleteBehavior.NoAction);
    }
}

/// <summary>Finite numbered slots, acquired and renewed using portable revisions.</summary>
public sealed class PrinterFileIoSlotConfiguration : IEntityTypeConfiguration<PrinterFileIoSlot>
{
    public void Configure(EntityTypeBuilder<PrinterFileIoSlot> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
    }
}
