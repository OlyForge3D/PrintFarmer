using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

/// <summary>
/// Entity configuration for <see cref="Model3DTagMapping"/> — replaces the original
/// skip-navigation join table that was auto-managed when Model3D lived in infra.
/// Maps to the existing <c>Model3DTag</c> table with no schema changes.
/// </summary>
public class Model3DTagMappingConfiguration : IEntityTypeConfiguration<Model3DTagMapping>
{
    /// <inheritdoc/>
    public void Configure(EntityTypeBuilder<Model3DTagMapping> builder)
    {
        _ = builder.ToTable("Model3DTag");
        _ = builder.HasKey(x => new { x.Model3DId, x.TagsId });

        _ = builder.HasOne(x => x.Tag)
            .WithMany()
            .HasForeignKey(x => x.TagsId);
    }
}
