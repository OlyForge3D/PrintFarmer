using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

/// <summary>
/// Entity Framework configuration for the Resource entity.
/// Extracted from AppDbContext.OnModelCreating for better maintainability.
/// </summary>
public class ResourceConfiguration : IEntityTypeConfiguration<Resource>
{
    public void Configure(EntityTypeBuilder<Resource> builder)
    {
        builder.HasKey(r => r.Id);

        // Basic properties
        builder.Property(r => r.Name).IsRequired().HasMaxLength(100);
        builder.Property(r => r.DisplayName).IsRequired().HasMaxLength(100);
        builder.Property(r => r.Description).HasColumnType("TEXT");
        builder.Property(r => r.ResourceType).IsRequired().HasMaxLength(50);

        // Unique constraints and indexes
        builder.HasIndex(r => r.Name).IsUnique();
        builder.HasIndex(r => r.ResourceType);
        builder.HasIndex(r => r.IsActive);
    }
}
