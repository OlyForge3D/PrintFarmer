using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

public class WorkerConfiguration : IEntityTypeConfiguration<Worker>
{
    public void Configure(EntityTypeBuilder<Worker> builder)
    {
        _ = builder.HasKey(w => w.Id);
        _ = builder.Property(w => w.ServiceId).IsRequired().HasMaxLength(256);
        _ = builder.Property(w => w.Name).IsRequired().HasMaxLength(256);
        _ = builder.Property(w => w.EndpointUrl).IsRequired().HasMaxLength(2048);
        _ = builder.Property(w => w.CapabilitiesJson).HasColumnType("TEXT");
        _ = builder.Property(w => w.Status).IsRequired().HasMaxLength(50);
        _ = builder.Ignore(w => w.FreeSlots); // FreeSlots is calculated property
        _ = builder.Property(w => w.TotalSlots).IsRequired();
        _ = builder.Property(w => w.RegisteredAt).IsRequired();
        _ = builder.Property(w => w.ApiKey).HasMaxLength(512);
        _ = builder.Property(w => w.Version).HasMaxLength(50);
        _ = builder.Property(w => w.MetadataJson).HasColumnType("TEXT");
        _ = builder.Property(w => w.CreatedAt).IsRequired();
        _ = builder.Property(w => w.UpdatedAt).IsRequired();
        _ = builder.Property(w => w.DisabledReason).HasMaxLength(1024);

        // Indexes for efficient querying
        _ = builder.HasIndex(w => w.ServiceId).IsUnique();
        _ = builder.HasIndex(w => w.Status);
        _ = builder.HasIndex(w => w.LastHeartbeat);
    }
}
