using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

/// <summary>
/// Entity Framework configuration for the UserAction entity.
/// Extracted from AppDbContext.OnModelCreating for better maintainability.
/// </summary>
public class UserActionConfiguration : IEntityTypeConfiguration<UserAction>
{
    public void Configure(EntityTypeBuilder<UserAction> builder)
    {
        builder.HasKey(a => a.Id);

        // Basic properties
        builder.Property(a => a.Name).IsRequired().HasMaxLength(50);
        builder.Property(a => a.DisplayName).IsRequired().HasMaxLength(100);
        builder.Property(a => a.Description).HasColumnType("TEXT");

        // Unique constraints and indexes
        builder.HasIndex(a => a.Name).IsUnique();
    }
}
