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

        // Foreign key to GcodeFile (no navigation back from GcodeFile)
        builder.HasOne(pj => pj.GcodeFile)
            .WithMany()
            .HasForeignKey(pj => pj.GcodeFileId)
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

        // Indexes for common queries
        builder.HasIndex(pj => pj.Status);
        builder.HasIndex(pj => pj.QueuedAt);
        builder.HasIndex(pj => pj.Priority);
        builder.HasIndex(pj => pj.AssignedPrinterId);
    }
}
