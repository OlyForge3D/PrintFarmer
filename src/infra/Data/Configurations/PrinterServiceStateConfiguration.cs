using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

/// <summary>
/// Entity Framework configuration for <see cref="PrinterServiceState"/>.
/// Uses PrinterId as both PK and FK to enforce a strict 1:1 relationship with Printer.
/// </summary>
public class PrinterServiceStateConfiguration : IEntityTypeConfiguration<PrinterServiceState>
{
    public void Configure(EntityTypeBuilder<PrinterServiceState> builder)
    {
        builder.ToTable("PrinterServiceState");

        builder.HasKey(e => e.PrinterId);

        builder.HasOne(e => e.Printer)
            .WithOne(p => p.ServiceState)
            .HasForeignKey<PrinterServiceState>(e => e.PrinterId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(e => e.ObicoServer)
            .WithMany(o => o.PrinterServiceStates)
            .HasForeignKey(e => e.ObicoServerId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.Property(e => e.RowVersion).IsRowVersion();
    }
}
