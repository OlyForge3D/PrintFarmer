using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

public class PrintProjectTemplateConfiguration : IEntityTypeConfiguration<PrintProjectTemplate>
{
    public void Configure(EntityTypeBuilder<PrintProjectTemplate> builder)
    {
        builder.ToTable("PrintProjectTemplates");

        builder.HasKey(t => t.Id);

        builder.Property(t => t.Name)
            .IsRequired()
            .HasMaxLength(255);

        builder.Property(t => t.Description)
            .HasMaxLength(2000);

        builder.Property(t => t.Category)
            .HasMaxLength(100);

        builder.Property(t => t.DefaultNotes)
            .HasMaxLength(2000);

        builder.Property(t => t.RowVersion)
            .IsRowVersion();

        builder.HasIndex(t => t.Name);
        builder.HasIndex(t => t.Category);
        builder.HasIndex(t => t.SortOrder);

        builder.HasMany(t => t.Files)
            .WithOne(f => f.Template)
            .HasForeignKey(f => f.PrintProjectTemplateId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public class PrintProjectTemplateFileConfiguration : IEntityTypeConfiguration<PrintProjectTemplateFile>
{
    public void Configure(EntityTypeBuilder<PrintProjectTemplateFile> builder)
    {
        builder.ToTable("PrintProjectTemplateFiles");

        builder.HasKey(f => f.Id);

        builder.Property(f => f.Name)
            .IsRequired()
            .HasMaxLength(255);

        builder.Property(f => f.FileNamePattern)
            .HasMaxLength(255);

        builder.Property(f => f.MaterialRequirement)
            .HasMaxLength(100);

        builder.Property(f => f.Notes)
            .HasMaxLength(500);

        builder.HasIndex(f => f.PrintProjectTemplateId);
        builder.HasIndex(f => f.SortOrder);
    }
}
