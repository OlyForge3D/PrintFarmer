using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

/// <summary>
/// Entity Framework configuration for the GcodeFile entity.
/// Extracted from AppDbContext.OnModelCreating for better maintainability.
/// </summary>
public class GcodeFileConfiguration : IEntityTypeConfiguration<GcodeFile>
{
    public void Configure(EntityTypeBuilder<GcodeFile> builder)
    {
        builder.HasKey(g => g.Id);
        builder.Property(g => g.RowVersion).IsRowVersion();

        // Basic properties
        builder.Property(g => g.FileName).IsRequired().HasMaxLength(255);
        builder.Property(g => g.FileHash).IsRequired().HasMaxLength(64);
        builder.Property(g => g.FileSizeBytes).IsRequired();
        builder.Property(g => g.FilePath).IsRequired().HasMaxLength(512);
        builder.Property(g => g.ThumbnailFileName).HasMaxLength(255);
        builder.Property(g => g.SlicerName).HasMaxLength(128);
        builder.Property(g => g.SlicerVersion).HasMaxLength(64);
        builder.Property(g => g.RequiredMaterial).HasMaxLength(64);
        builder.Property(g => g.HealthStatus).HasConversion<int>().HasDefaultValue(FileHealthStatus.Unknown);
        builder.Property(g => g.LastVerificationResult).HasColumnType("TEXT");

        // Promotion lineage — identifiers, hashes and versions only. Never paths or private URLs.
        builder.Property(g => g.PromotionOperationId).HasMaxLength(128);
        builder.Property(g => g.PromotionOperationKey).HasMaxLength(64);
        builder.Property(g => g.ContentSha256).HasMaxLength(64);
        builder.Property(g => g.SpecificationSha256).HasMaxLength(64);
        builder.Property(g => g.SourceModelSha256).HasMaxLength(64);
        builder.Property(g => g.MachineProfileSha256).HasMaxLength(64);
        builder.Property(g => g.ProcessProfileSha256).HasMaxLength(64);
        builder.Property(g => g.FilamentProfileSha256).HasMaxLength(64);
        builder.Property(g => g.CalibrationManifestSha256).HasMaxLength(64);
        builder.Property(g => g.SlicerEngineName).HasMaxLength(32);
        builder.Property(g => g.SlicerDistribution).HasMaxLength(64);
        builder.Property(g => g.PinnedSlicerVersion).HasMaxLength(64);
        builder.Property(g => g.SlicerContainerDigest).HasMaxLength(128);
        builder.Property(g => g.FirmwareFamily).HasMaxLength(64);
        builder.Property(g => g.GcodeDialect).HasMaxLength(64);
        builder.Property(g => g.GeneratorName).HasMaxLength(128);
        builder.Property(g => g.GeneratorVersion).HasMaxLength(64);
        builder.Property(g => g.IsImmutable).HasDefaultValue(false);

        // Foreign Keys
        // StoredFile.FolderId is required (comment on StoredFile.cs says REQUIRED). Deleting a
        // folder that still has files is prohibited (Restrict). Callers must move files to a
        // different folder or soft-delete via FolderNode.DeletedAt before removing the folder.
        // Previous SetNull was incompatible with the required column on SQL Server
        // (fails CREATE TABLE with error 1761).
        builder.HasOne(g => g.Folder)
            .WithMany(f => f.Files)
            .HasForeignKey(g => g.FolderId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(g => g.SourcePrinter)
            .WithMany()
            .HasForeignKey(g => g.SourcePrinterId)
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasOne(g => g.PrinterModel)
            .WithMany()
            .HasForeignKey(g => g.PrinterModelId)
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasOne(g => g.PrinterGroup)
            .WithMany()
            .HasForeignKey(g => g.PrinterGroupId)
            .OnDelete(DeleteBehavior.SetNull);

        // Skip-navigation: GcodeFile.Tags - join table managed by EF Core
        builder.HasMany(g => g.Tags)
            .WithMany();

        // Indexes for common queries
        builder.HasIndex(g => g.FileHash).IsUnique();
        builder.HasIndex(g => g.UploadedAt);
        builder.HasIndex(g => g.FolderId);
        builder.HasIndex(g => g.RequiredNozzleDiameter);
        builder.HasIndex(g => g.RequiredMaterial);
        builder.HasIndex(g => g.SourcePrinterId);
        builder.HasIndex(g => g.HealthStatus);
        builder.HasIndex(g => g.LastHealthCheckDate);
        builder.HasIndex(g => g.PrinterGroupId);

        // Promotion uniqueness: one promoted file per source artifact content and per owner-scoped
        // operation key. The raw idempotency key is only unique inside its owner scope, so it is
        // indexed for lookups but never enforced as globally unique.
        builder.HasIndex(g => new { g.SourceArtifactId, g.ContentSha256 }).IsUnique();
        builder.HasIndex(g => g.PromotionOperationKey).IsUnique();
        builder.HasIndex(g => g.PromotionOperationId);
        builder.HasIndex(g => g.CalibrationAttemptId);
        builder.HasIndex(g => g.CalibrationOrchestrationId);
    }
}
