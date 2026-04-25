using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

public class CustomFieldValueConfiguration : IEntityTypeConfiguration<CustomFieldValue>
{
    public void Configure(EntityTypeBuilder<CustomFieldValue> builder)
    {
        _ = builder.HasKey(v => v.Id);

        _ = builder.HasOne(v => v.Definition)
            .WithMany(d => d.Values)
            .HasForeignKey(v => v.DefinitionId)
            .OnDelete(DeleteBehavior.Cascade);

        _ = builder.HasIndex(v => new { v.DefinitionId, v.EntityId }).IsUnique();

        _ = builder.Property(v => v.Value)
            .HasMaxLength(4000);
    }
}
