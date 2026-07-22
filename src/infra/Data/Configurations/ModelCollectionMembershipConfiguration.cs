using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

/// <summary>
/// Entity configuration for <see cref="ModelCollectionMembership"/>. The <c>ModelId</c>
/// column is a cross-context soft reference to <c>Model3D</c> and intentionally has no
/// foreign key — existence is validated at the service layer through
/// <see cref="Farm.Infrastructure.Services.IModel3DQueryProvider"/>.
/// </summary>
public class ModelCollectionMembershipConfiguration : IEntityTypeConfiguration<ModelCollectionMembership>
{
    /// <inheritdoc/>
    public void Configure(EntityTypeBuilder<ModelCollectionMembership> builder)
    {
        _ = builder.ToTable("ModelCollectionMemberships");
        _ = builder.HasKey(m => m.Id);

        _ = builder.Property(m => m.CollectionId).IsRequired();
        _ = builder.Property(m => m.ModelId).IsRequired();

        // A model may appear at most once per collection.
        _ = builder.HasIndex(m => new { m.CollectionId, m.ModelId }).IsUnique();

        // Soft reference lookups by model id (which collections contain a model).
        _ = builder.HasIndex(m => m.ModelId);

        // Supports cursor-based incremental sync of membership changes (#845).
        _ = builder.HasIndex(m => m.UpdatedAt);
    }
}
