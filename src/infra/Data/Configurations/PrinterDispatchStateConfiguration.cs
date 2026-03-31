using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

/// <summary>
/// Entity Framework configuration for <see cref="PrinterDispatchState"/>.
/// Uses PrinterId as both PK and FK to enforce a strict 1:1 relationship with Printer.
/// </summary>
public class PrinterDispatchStateConfiguration : IEntityTypeConfiguration<PrinterDispatchState>
{
    public void Configure(EntityTypeBuilder<PrinterDispatchState> builder)
    {
        builder.HasKey(e => e.PrinterId);

        builder.HasOne(e => e.Printer)
            .WithOne(p => p.DispatchState)
            .HasForeignKey<PrinterDispatchState>(e => e.PrinterId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Property(e => e.RowVersion).IsRowVersion();
    }
}
