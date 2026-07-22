using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

/// <summary>
/// Entity configuration for <see cref="ModelCollection"/>. Visibility is stored as a string for
/// readability and forward compatibility with the library-sync epic.
/// </summary>
public class ModelCollectionConfiguration : IEntityTypeConfiguration<ModelCollection>
{
    /// <inheritdoc/>
    public void Configure(EntityTypeBuilder<ModelCollection> builder)
    {
        _ = builder.HasKey(c => c.Id);

        _ = builder.Property(c => c.OwnerUserId).IsRequired();
        _ = builder.Property(c => c.Name).IsRequired().HasMaxLength(200);
        _ = builder.Property(c => c.Description).HasMaxLength(2000);
        _ = builder.Property(c => c.Visibility)
            .IsRequired()
            .HasMaxLength(32)
            .HasConversion<string>();

        // Query surfaces filter by owner and visibility.
        _ = builder.HasIndex(c => c.OwnerUserId);
        _ = builder.HasIndex(c => c.Visibility);

        _ = builder.HasMany(c => c.Memberships)
            .WithOne(m => m.Collection!)
            .HasForeignKey(m => m.CollectionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
