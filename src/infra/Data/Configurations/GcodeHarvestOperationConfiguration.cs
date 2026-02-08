using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

/// <summary>
/// Entity Framework configuration for the GcodeHarvestOperation entity.
/// Extracted from AppDbContext.OnModelCreating for better maintainability.
/// </summary>
public class GcodeHarvestOperationConfiguration : IEntityTypeConfiguration<GcodeHarvestOperation>
{
    public void Configure(EntityTypeBuilder<GcodeHarvestOperation> builder)
    {
        builder.HasKey(h => h.Id);
        builder.Property(h => h.RowVersion).IsRowVersion();

        // Basic properties
        builder.Property(h => h.Status).HasConversion<int>();

        // Foreign Key
        builder.HasOne(h => h.Printer)
            .WithMany()
            .HasForeignKey(h => h.PrinterId)
            .OnDelete(DeleteBehavior.NoAction);

        // Indexes
        builder.HasIndex(h => h.PrinterId);
        builder.HasIndex(h => h.StartedAt);
        builder.HasIndex(h => h.Status);
    }
}
