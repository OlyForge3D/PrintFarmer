using System.Text.Json;
using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

/// <summary>
/// Entity Framework configuration for the PrintJob entity.
/// Extracted from AppDbContext.OnModelCreating for better maintainability.
/// </summary>
public class PrintJobConfiguration : IEntityTypeConfiguration<PrintJob>
{
    public void Configure(EntityTypeBuilder<PrintJob> builder)
    {
        builder.HasKey(pj => pj.Id);

        // Concurrency token for optimistic locking - critical for job queue operations
        builder.Property(pj => pj.RowVersion).IsRowVersion();

        // Basic properties
        builder.Property(pj => pj.Name).IsRequired().HasMaxLength(255);
        builder.Property(pj => pj.Priority).HasDefaultValue(0);
        builder.Property(pj => pj.Status).HasConversion<int>();
        builder.Property(pj => pj.EstimatedPrintTime).HasConversion<long>();
        builder.Property(pj => pj.ActualPrintTime).HasConversion<long>();

        // JSON array properties
        builder.Property(pj => pj.RequiredCapabilities)
            .HasConversion(
                v => v == null ? null : JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                v => v == null ? null : JsonSerializer.Deserialize<string[]>(v, (JsonSerializerOptions?)null));

        builder.Property(pj => pj.PreferredPrinterIds)
            .HasConversion(
                v => v == null ? null : JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                v => v == null ? null : JsonSerializer.Deserialize<Guid[]>(v, (JsonSerializerOptions?)null));

        builder.Property(pj => pj.ExcludedPrinterIds)
            .HasConversion(
                v => v == null ? null : JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                v => v == null ? null : JsonSerializer.Deserialize<Guid[]>(v, (JsonSerializerOptions?)null));

        // Foreign key to GcodeFile (optional - history-seeded jobs may not have a G-code file)
        builder.HasOne(pj => pj.GcodeFile)
            .WithMany()
            .HasForeignKey(pj => pj.GcodeFileId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.NoAction);

        // Foreign key to Printer (optional - job may be unassigned)
        builder.HasOne(pj => pj.AssignedPrinter)
            .WithMany()
            .HasForeignKey(pj => pj.AssignedPrinterId)
            .OnDelete(DeleteBehavior.NoAction);

        // State history navigation (FK is JobId, not PrintJobId)
        builder.HasMany(pj => pj.StateHistory)
            .WithOne(h => h.PrintJob)
            .HasForeignKey(h => h.JobId)
            .OnDelete(DeleteBehavior.Cascade);

        // Project tracking fields
        builder.Property(pj => pj.ProjectName).HasMaxLength(255);
        builder.Property(pj => pj.FilamentName).HasMaxLength(255);
        builder.Property(pj => pj.FilamentVendor).HasMaxLength(128);
        builder.Property(pj => pj.FilamentColor).HasMaxLength(32);

        // Multi-copy support
        builder.Property(pj => pj.Copies).HasDefaultValue(1);
        builder.Property(pj => pj.CompletedCopies).HasDefaultValue(0);
        builder.Ignore(pj => pj.RemainingCopies);
        builder.Ignore(pj => pj.IsMultiCopy);

        // History seeding fields
        builder.Property(pj => pj.ExternalJobId).HasMaxLength(255);
        builder.Property(pj => pj.WasSeededFromHistory).HasDefaultValue(false);

        // Indexes for common queries
        builder.HasIndex(pj => pj.Status);
        builder.HasIndex(pj => pj.QueuedAt);
        builder.HasIndex(pj => pj.Priority);
        builder.HasIndex(pj => pj.AssignedPrinterId);

        // Composite index for queue overview batch queries (AssignedPrinterId + Status)
        builder.HasIndex(pj => new { pj.AssignedPrinterId, pj.Status })
            .HasDatabaseName("IX_PrintJobs_AssignedPrinterId_Status");

        // Prevents duplicate jobs when seeding from the same printer
        // Note: PostgreSQL syntax differs from SQL Server - using provider-agnostic approach
        // The partial unique index ensures uniqueness only when both fields are NOT NULL
        builder.HasIndex(pj => new { pj.ExternalJobId, pj.SourcePrinterId })
            .IsUnique()
            .HasDatabaseName("IX_PrintJobs_ExternalJobId_SourcePrinterId");

        // Index for efficient lookup by external job ID and source printer (for history seeding)
        builder.HasIndex(pj => pj.SourcePrinterId)
            .HasDatabaseName("IX_PrintJobs_SourcePrinterId");
    }
}
