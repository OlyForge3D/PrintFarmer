using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

/// <summary>
/// Configures the singleton global mutation counter.
/// </summary>
public sealed class MutationCounterConfiguration : IEntityTypeConfiguration<MutationCounter>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<MutationCounter> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        _ = builder.HasKey(counter => counter.Id);
        _ = builder.Property(counter => counter.Value).IsRequired();
        _ = builder.HasData(new MutationCounter());
    }
}
