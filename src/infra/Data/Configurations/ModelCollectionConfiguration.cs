using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

/// <summary>
/// Entity configuration for <see cref="ModelCollection"/> — a user-owned grouping of
/// 3D model references. Model references are cross-context soft references and carry
/// no EF foreign key (see <see cref="ModelCollectionMembershipConfiguration"/>).
/// </summary>
public class ModelCollectionConfiguration : IEntityTypeConfiguration<ModelCollection>
{
    /// <inheritdoc/>
    public void Configure(EntityTypeBuilder<ModelCollection> builder)
    {
        _ = builder.ToTable("ModelCollections");
        _ = builder.HasKey(c => c.Id);

        _ = builder.Property(c => c.Name).IsRequired().HasMaxLength(200);
        _ = builder.Property(c => c.Description).HasMaxLength(2000);
        _ = builder.Property(c => c.OwnerUserId).IsRequired();
        _ = builder.Property(c => c.IsShared).IsRequired();

        // Sync revision / optimistic-concurrency metadata (#844). Additive columns.
        _ = builder.Property(c => c.Revision).IsRequired().HasDefaultValue(0L);
        _ = builder.Property(c => c.ConcurrencyToken).IsConcurrencyToken();

        // Owner scoping for list queries.
        _ = builder.HasIndex(c => c.OwnerUserId);

        // UpdatedAt is indexed to support cursor-based incremental sync (#845).
        _ = builder.HasIndex(c => c.UpdatedAt);

        _ = builder.HasMany(c => c.Memberships)
            .WithOne(m => m.Collection)
            .HasForeignKey(m => m.CollectionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
