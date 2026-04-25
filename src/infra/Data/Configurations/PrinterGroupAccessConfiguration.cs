using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

/// <summary>
/// Entity configuration for PrinterGroupAccess (role-based group access control).
/// </summary>
public class PrinterGroupAccessConfiguration : IEntityTypeConfiguration<PrinterGroupAccess>
{
    public void Configure(EntityTypeBuilder<PrinterGroupAccess> builder)
    {
        _ = builder.HasKey(a => a.Id);

        _ = builder.HasOne(a => a.PrinterGroup)
            .WithMany(g => g.AccessRules)
            .HasForeignKey(a => a.PrinterGroupId)
            .OnDelete(DeleteBehavior.Cascade);

        _ = builder.HasOne(a => a.Role)
            .WithMany()
            .HasForeignKey(a => a.RoleId)
            .OnDelete(DeleteBehavior.Cascade);

        _ = builder.Property(a => a.AccessLevel)
            .HasConversion<string>()
            .HasMaxLength(50);

        _ = builder.HasIndex(a => new { a.PrinterGroupId, a.RoleId, a.AccessLevel }).IsUnique();
    }
}
