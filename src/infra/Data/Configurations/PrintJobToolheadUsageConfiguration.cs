using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

/// <summary>
/// Entity Framework configuration for per-toolhead filament usage records on print jobs.
/// </summary>
public sealed class PrintJobToolheadUsageConfiguration : IEntityTypeConfiguration<PrintJobToolheadUsage>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<PrintJobToolheadUsage> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.HasKey(u => u.Id);

        builder.Property(u => u.ToolheadIndex).IsRequired();
        builder.Property(u => u.FilamentName).HasMaxLength(255);
        builder.Property(u => u.FilamentColor).HasMaxLength(32);

        builder.HasOne(u => u.PrintJob)
            .WithMany(j => j.ToolheadUsages)
            .HasForeignKey(u => u.PrintJobId)
            .OnDelete(DeleteBehavior.Cascade);

        // Unique composite: one record per toolhead per job
        builder.HasIndex(u => new { u.PrintJobId, u.ToolheadIndex })
            .IsUnique()
            .HasDatabaseName("IX_PrintJobToolheadUsages_PrintJobId_ToolheadIndex");
    }
}
