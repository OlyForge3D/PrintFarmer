using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

/// <summary>
/// Entity Framework configuration for the PrintProject entity.
/// </summary>
public class PrintProjectConfiguration : IEntityTypeConfiguration<PrintProject>
{
    public void Configure(EntityTypeBuilder<PrintProject> builder)
    {
        builder.HasKey(p => p.Id);

        // Concurrency token for optimistic locking
        builder.Property(p => p.RowVersion).IsRowVersion();

        // Basic properties
        builder.Property(p => p.Name).IsRequired().HasMaxLength(255);
        builder.Property(p => p.Description).HasMaxLength(2000);
        builder.Property(p => p.Notes).HasMaxLength(2000);
        builder.Property(p => p.Status).HasConversion<int>().HasDefaultValue(PrintProjectStatus.Open);
        builder.Property(p => p.Priority).HasDefaultValue(0);

        // Navigation to files
        builder.HasMany(p => p.Files)
            .WithOne(f => f.PrintProject)
            .HasForeignKey(f => f.PrintProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        // Indexes for common queries
        builder.HasIndex(p => p.Status);
        builder.HasIndex(p => p.CreatedAt);
        builder.HasIndex(p => p.DueDate);
        builder.HasIndex(p => p.Priority);
    }
}
