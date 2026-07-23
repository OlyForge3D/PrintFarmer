using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

public class CustomFieldDefinitionConfiguration : IEntityTypeConfiguration<CustomFieldDefinition>
{
    public void Configure(EntityTypeBuilder<CustomFieldDefinition> builder)
    {
        _ = builder.HasKey(d => d.Id);

        _ = builder.Property(d => d.FieldName)
            .IsRequired()
            .HasMaxLength(200);

        _ = builder.Property(d => d.FieldKey)
            .IsRequired()
            .HasMaxLength(100);

        _ = builder.Property(d => d.Description)
            .HasMaxLength(500);

        _ = builder.Property(d => d.Options)
            .HasMaxLength(4000);

        _ = builder.Property(d => d.DefaultValue)
            .HasMaxLength(1000);

        _ = builder.HasIndex(d => new { d.EntityType, d.FieldKey }).IsUnique();

        _ = builder.HasMany(d => d.Values)
            .WithOne(v => v.Definition)
            .HasForeignKey(v => v.DefinitionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
