using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

/// <summary>
/// Entity Framework configuration for the PrintProjectFile junction entity.
/// </summary>
public class PrintProjectFileConfiguration : IEntityTypeConfiguration<PrintProjectFile>
{
    public void Configure(EntityTypeBuilder<PrintProjectFile> builder)
    {
        builder.HasKey(pf => pf.Id);

        // Concurrency token for optimistic locking
        builder.Property(pf => pf.RowVersion).IsRowVersion();

        // Basic properties
        builder.Property(pf => pf.ColorRequirement).HasConversion<int>().HasDefaultValue(PrintColorRequirement.Base);
        builder.Property(pf => pf.MaterialRequirement).HasMaxLength(64);
        builder.Property(pf => pf.Status).HasConversion<int>().HasDefaultValue(PrintProjectFileStatus.Pending);
        builder.Property(pf => pf.PrintCount).HasDefaultValue(1);
        builder.Property(pf => pf.PrintedCount).HasDefaultValue(0);
        builder.Property(pf => pf.SortOrder).HasDefaultValue(0);
        builder.Property(pf => pf.Notes).HasMaxLength(500);

        // Foreign key to PrintProject (required)
        builder.HasOne(pf => pf.PrintProject)
            .WithMany(p => p.Files)
            .HasForeignKey(pf => pf.PrintProjectId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Cascade);

        // Foreign key to GcodeFile (required)
        builder.HasOne(pf => pf.GcodeFile)
            .WithMany()
            .HasForeignKey(pf => pf.GcodeFileId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Cascade);

        // Foreign key to last PrintJob (optional - for tracking)
        builder.HasOne(pf => pf.LastPrintJob)
            .WithMany()
            .HasForeignKey(pf => pf.LastPrintJobId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.SetNull);

        // Unique constraint: same file can only be in a project once
        builder.HasIndex(pf => new { pf.PrintProjectId, pf.GcodeFileId })
            .IsUnique()
            .HasDatabaseName("IX_PrintProjectFiles_ProjectId_GcodeFileId");

        // Indexes for common queries
        builder.HasIndex(pf => pf.PrintProjectId);
        builder.HasIndex(pf => pf.GcodeFileId);
        builder.HasIndex(pf => pf.Status);
        builder.HasIndex(pf => pf.ColorRequirement);
    }
}
