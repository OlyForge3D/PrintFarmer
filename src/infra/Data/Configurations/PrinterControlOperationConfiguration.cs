using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

public sealed class PrinterControlOperationConfiguration : IEntityTypeConfiguration<PrinterControlOperation>
{
    public void Configure(EntityTypeBuilder<PrinterControlOperation> builder)
    {
        builder.HasKey(operation => operation.Id);
        builder.HasIndex(operation => new { operation.PrinterId, operation.CreatedAtUtc });
        builder.HasIndex(operation => new { operation.State, operation.OwnerHeartbeatAtUtc });

        // Keep idempotency records, including after a printer is removed. Never cascade-delete
        // an unresolved operation or permit its UUID to be reused for a second physical send.
        builder.Property(operation => operation.Kind).HasConversion<string>().HasMaxLength(32);
        builder.Property(operation => operation.State).HasConversion<string>().HasMaxLength(32);
        builder.Property(operation => operation.CompletionEvidence).HasConversion<string>().HasMaxLength(32);
        builder.Property(operation => operation.SenderIsolation).HasConversion<string>().HasMaxLength(32);
    }
}
