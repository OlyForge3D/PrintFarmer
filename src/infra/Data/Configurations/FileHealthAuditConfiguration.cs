using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

public class FileHealthAuditConfiguration : IEntityTypeConfiguration<FileHealthAudit>
{
    public void Configure(EntityTypeBuilder<FileHealthAudit> builder)
    {
        _ = builder.HasKey(a => a.Id);
        _ = builder.Property(a => a.AuditDate).IsRequired();
        _ = builder.Property(a => a.AuditType).HasConversion<int>();
        _ = builder.Property(a => a.FilesChecked).IsRequired();
        _ = builder.Property(a => a.HealthyFiles).IsRequired();
        _ = builder.Property(a => a.MissingFiles).IsRequired();
        _ = builder.Property(a => a.CorruptedFiles).IsRequired();
        _ = builder.Property(a => a.OrphanedFiles).IsRequired();
        _ = builder.Property(a => a.MissingFileIds).HasColumnType("TEXT"); // JSON array
        _ = builder.Property(a => a.CorruptedFileIds).HasColumnType("TEXT"); // JSON array
        _ = builder.Property(a => a.OrphanedFilePaths).HasColumnType("TEXT"); // JSON array
        _ = builder.Property(a => a.SummaryMessage).HasColumnType("TEXT");
        _ = builder.Property(a => a.HasIssues).IsRequired();
        _ = builder.Property(a => a.CreatedAt).IsRequired();

        // Indexes for efficient querying and dashboard
        _ = builder.HasIndex(a => a.AuditDate).IsDescending(); // Most recent audits first
        _ = builder.HasIndex(a => a.AuditType);
        _ = builder.HasIndex(a => a.HasIssues);
        _ = builder.HasIndex(a => new { a.AuditType, a.AuditDate }).IsDescending(false, true); // Composite for type+recent queries
    }
}
