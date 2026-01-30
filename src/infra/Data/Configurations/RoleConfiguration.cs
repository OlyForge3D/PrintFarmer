using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

/// <summary>
/// Entity Framework configuration for the Role entity.
/// Extracted from AppDbContext.OnModelCreating for better maintainability.
/// </summary>
public class RoleConfiguration : IEntityTypeConfiguration<Role>
{
    public void Configure(EntityTypeBuilder<Role> builder)
    {
        builder.HasKey(r => r.Id);

        // Basic properties
        builder.Property(r => r.Name).IsRequired().HasMaxLength(50);
        builder.Property(r => r.DisplayName).IsRequired().HasMaxLength(100);
        builder.Property(r => r.Description).HasColumnType("TEXT");

        // Unique constraints and indexes
        builder.HasIndex(r => r.Name).IsUnique();
        builder.HasIndex(r => r.IsSystemRole);
        builder.HasIndex(r => r.IsActive);
    }
}
