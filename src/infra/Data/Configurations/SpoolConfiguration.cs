using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

/// <summary>
/// Entity configuration for Spool with printer assignment relationship.
/// Includes RowVersion for optimistic concurrency during concurrent printer assignment.
/// </summary>
public class SpoolConfiguration : IEntityTypeConfiguration<Spool>
{
    public void Configure(EntityTypeBuilder<Spool> builder)
    {
        _ = builder.HasKey(s => s.Id);
        _ = builder.Property(s => s.RowVersion).IsRowVersion();
        _ = builder.Property(s => s.Material).IsRequired().HasMaxLength(64);
        _ = builder.Property(s => s.ColorHex).IsRequired().HasMaxLength(16);
        _ = builder.HasOne<Printer>()
            .WithMany()
            .HasForeignKey(s => s.AssignedPrinterId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
