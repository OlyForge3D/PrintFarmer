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

        // Foreign Keys
        builder.HasOne(g => g.Folder)
            .WithMany(f => f.Files)
            .HasForeignKey(g => g.FolderId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne(g => g.SourcePrinter)
            .WithMany()
            .HasForeignKey(g => g.SourcePrinterId)
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasOne(g => g.PrinterModel)
            .WithMany()
            .HasForeignKey(g => g.PrinterModelId)
            .OnDelete(DeleteBehavior.NoAction);

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
    }
}
