using System.Text.Json;
using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
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

        // Basic properties
        builder.Property(pj => pj.Name).IsRequired().HasMaxLength(255);
        builder.Property(pj => pj.Priority)
            .HasDefaultValue((int)PrintJobPriority.Normal)
            .ValueGeneratedNever();
        builder.ToTable(table => table.HasCheckConstraint(
            "CK_PrintJobs_Priority",
            "\"Priority\" >= 0 AND \"Priority\" <= 3"));
        builder.Property(pj => pj.Status).HasConversion<int>();
        builder.Property(pj => pj.EstimatedPrintTime).HasConversion<long>();
        builder.Property(pj => pj.ActualPrintTime).HasConversion<long>();

        // JSON array properties
        PropertyBuilder<string[]?> requiredCapabilities = builder
            .Property(pj => pj.RequiredCapabilities)
            .HasConversion(
                v => v == null ? null : JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                v => v == null ? null : JsonSerializer.Deserialize<string[]>(v, (JsonSerializerOptions?)null));
        requiredCapabilities.Metadata.SetValueComparer(CreateArrayComparer<string>());

        PropertyBuilder<Guid[]?> preferredPrinterIds = builder
            .Property(pj => pj.PreferredPrinterIds)
            .HasConversion(
                v => v == null ? null : JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                v => v == null ? null : JsonSerializer.Deserialize<Guid[]>(v, (JsonSerializerOptions?)null));
        preferredPrinterIds.Metadata.SetValueComparer(CreateArrayComparer<Guid>());

        PropertyBuilder<Guid[]?> excludedPrinterIds = builder
            .Property(pj => pj.ExcludedPrinterIds)
            .HasConversion(
                v => v == null ? null : JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                v => v == null ? null : JsonSerializer.Deserialize<Guid[]>(v, (JsonSerializerOptions?)null));
        excludedPrinterIds.Metadata.SetValueComparer(CreateArrayComparer<Guid>());

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

        // Printed-part harvest metadata. Harvest is orthogonal to lifecycle status.
        builder.Property(pj => pj.HarvestOperationKey).HasMaxLength(128);
        builder.Property(pj => pj.HarvestedByUserId).HasMaxLength(450);
        builder.HasIndex(pj => pj.HarvestedAt);
        builder.HasIndex(pj => pj.HarvestOperationKey).IsUnique();
        builder.HasOne<Bin>()
            .WithMany()
            .HasForeignKey(pj => pj.HarvestedIntoBinId)
            .OnDelete(DeleteBehavior.SetNull);

        // Per-tool material requirements: RequiredMaterialsPerToolJson is stored as text;
        // the typed RequiredMaterialsPerTool accessor is [NotMapped] but declared explicitly
        // here so the mapping remains obvious to future readers.
        builder.Ignore(pj => pj.RequiredMaterialsPerTool);

        // History seeding fields
        builder.Property(pj => pj.ExternalJobId).HasMaxLength(255);
        builder.Property(pj => pj.WasSeededFromHistory).HasDefaultValue(false);

        // Indexes for common queries
        builder.HasIndex(pj => pj.Status);
        builder.HasIndex(pj => pj.QueuedAt);
        builder.HasIndex(pj => pj.DeadlineAtUtc);
        builder.HasIndex(pj => pj.Priority);
        builder.HasIndex(pj => pj.AssignedPrinterId);
        builder.HasIndex(pj => pj.ActiveExternalPrinterId)
            .IsUnique()
            .HasDatabaseName("UX_PrintJobs_ActiveExternalPrinterId");

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

        // Tags - many-to-many via skip-navigation (auto-creates join table)
        builder.HasMany(pj => pj.Tags).WithMany();
    }

    private static ValueComparer<T[]?> CreateArrayComparer<T>()
        where T : notnull =>
        new(
            (left, right) =>
                left == right ||
                (left != null && right != null && left.SequenceEqual(right)),
            values => values == null
                ? 0
                : values.Aggregate(
                    0,
                    (hash, value) => HashCode.Combine(hash, value)),
            values => values == null ? null : values.ToArray());
}
