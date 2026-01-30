using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

public class PasswordPolicyEntityConfiguration : IEntityTypeConfiguration<PasswordPolicyEntity>
{
    public void Configure(EntityTypeBuilder<PasswordPolicyEntity> builder)
    {
        // Keep the existing table name to avoid creating a migration due to the rename
        _ = builder.ToTable("PasswordPolicies");
        _ = builder.HasKey(pp => pp.Id);
        _ = builder.Property(pp => pp.MinLength).IsRequired();
        _ = builder.Property(pp => pp.RequireUppercase);
        _ = builder.Property(pp => pp.RequireLowercase);
        _ = builder.Property(pp => pp.RequireDigit);
        _ = builder.Property(pp => pp.RequireSymbol);
    }
}
