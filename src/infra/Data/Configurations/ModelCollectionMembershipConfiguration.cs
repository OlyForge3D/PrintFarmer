using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

/// <summary>
/// Entity configuration for <see cref="ModelCollectionMembership"/>. The model identifier is a
/// cross-context reference with no foreign key; a unique index prevents duplicate membership rows.
/// </summary>
public class ModelCollectionMembershipConfiguration : IEntityTypeConfiguration<ModelCollectionMembership>
{
    /// <inheritdoc/>
    public void Configure(EntityTypeBuilder<ModelCollectionMembership> builder)
    {
        _ = builder.HasKey(m => m.Id);

        _ = builder.Property(m => m.CollectionId).IsRequired();
        _ = builder.Property(m => m.ModelId).IsRequired();

        // A model may appear at most once per collection.
        _ = builder.HasIndex(m => new { m.CollectionId, m.ModelId }).IsUnique();

        // Support membership lookups by model across collections (cross-context reference).
        _ = builder.HasIndex(m => m.ModelId);
    }
}
