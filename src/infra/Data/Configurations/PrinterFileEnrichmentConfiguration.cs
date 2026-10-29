using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

public sealed class PrinterFileEnrichmentConfiguration : IEntityTypeConfiguration<PrinterFileEnrichment>
{
    public void Configure(EntityTypeBuilder<PrinterFileEnrichment> builder)
    {
        builder.HasKey(x => x.LocationId);
        builder.Property(x => x.SourceIdentity).HasMaxLength(64).IsUnicode(false);
        builder.Property(x => x.MetadataState).HasConversion<string>().HasMaxLength(32);
        builder.Property(x => x.ThumbnailState).HasConversion<string>().HasMaxLength(32);
        builder.Property(x => x.MetadataReason).HasMaxLength(128);
        builder.Property(x => x.ThumbnailReason).HasMaxLength(128);
        builder.Property(x => x.MetadataSource).HasMaxLength(128);
        builder.HasIndex(x => x.NextAttemptAtUtc);
        builder.HasOne(x => x.Location).WithOne().HasForeignKey<PrinterFileEnrichment>(x => x.LocationId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(x => x.Artifact).WithMany().HasForeignKey(x => x.ArtifactId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}

public sealed class PrinterFileThumbnailArtifactConfiguration : IEntityTypeConfiguration<PrinterFileThumbnailArtifact>
{
    public void Configure(EntityTypeBuilder<PrinterFileThumbnailArtifact> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.ContentSha256).HasMaxLength(64).IsUnicode(false);
        builder.Property(x => x.ContentType).HasMaxLength(32).IsUnicode(false);
        builder.HasIndex(x => x.RetiredAtUtc);
        builder.HasIndex(x => x.ContentSha256);
        foreach (string name in new[]
        {
            nameof(PrinterFileThumbnailArtifact.ContentSha256), nameof(PrinterFileThumbnailArtifact.ContentType),
            nameof(PrinterFileThumbnailArtifact.Width), nameof(PrinterFileThumbnailArtifact.Height),
            nameof(PrinterFileThumbnailArtifact.SizeBytes), nameof(PrinterFileThumbnailArtifact.CreatedAtUtc),
        })
        {
            builder.Property(name).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        }
    }
}

public sealed class PrinterFileThumbnailReservationConfiguration : IEntityTypeConfiguration<PrinterFileThumbnailReservation>
{
    public void Configure(EntityTypeBuilder<PrinterFileThumbnailReservation> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.HasIndex(x => x.ExpiresAtUtc);
    }
}

public sealed class PrinterFileThumbnailStorageLockConfiguration : IEntityTypeConfiguration<PrinterFileThumbnailStorageLock>
{
    public void Configure(EntityTypeBuilder<PrinterFileThumbnailStorageLock> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.HasData(new PrinterFileThumbnailStorageLock { Id = 1, Revision = 0 });
    }
}
