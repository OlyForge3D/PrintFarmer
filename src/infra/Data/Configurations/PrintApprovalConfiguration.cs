using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

public class PrintApprovalConfiguration : IEntityTypeConfiguration<PrintApproval>
{
    public void Configure(EntityTypeBuilder<PrintApproval> builder)
    {
        _ = builder.HasKey(a => a.Id);
        _ = builder.Property(a => a.PrintJobId).IsRequired();
        _ = builder.Property(a => a.PrinterId).IsRequired(false);
        _ = builder.Property(a => a.RequestedBy).HasMaxLength(256);
        _ = builder.Property(a => a.CreatedAt).IsRequired();

        // Foreign keys
        _ = builder.HasOne<PrintJob>()
            .WithMany()
            .HasForeignKey(a => a.PrintJobId)
            .OnDelete(DeleteBehavior.Cascade);

        // Indexes for efficient querying
        _ = builder.HasIndex(a => a.PrintJobId);
        _ = builder.HasIndex(a => a.CreatedAt).IsDescending(); // Most recent first
    }
}
