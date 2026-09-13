using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

public sealed class PrinterEmergencyStopAttemptConfiguration : IEntityTypeConfiguration<PrinterEmergencyStopAttempt>
{
    public void Configure(EntityTypeBuilder<PrinterEmergencyStopAttempt> builder)
    {
        builder.HasKey(attempt => attempt.Id);
        builder.HasIndex(attempt => new { attempt.OperationId, attempt.Delivery });
        builder.Property(attempt => attempt.Delivery).HasConversion<string>().HasMaxLength(32);
        builder.HasOne<PrinterControlOperation>().WithMany().HasForeignKey(attempt => attempt.OperationId).OnDelete(DeleteBehavior.Restrict);
    }
}
