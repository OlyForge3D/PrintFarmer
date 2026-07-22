using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Farm.Infrastructure.Data.Configurations;

public class UserQuotaGroupMembershipConfiguration : IEntityTypeConfiguration<UserQuotaGroupMembership>
{
    public void Configure(EntityTypeBuilder<UserQuotaGroupMembership> builder)
    {
        builder.HasKey(m => m.Id);

        builder.Property(m => m.GroupName).HasMaxLength(200).IsRequired();

        builder.HasOne(m => m.User)
            .WithMany()
            .HasForeignKey(m => m.UserId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(m => new { m.UserId, m.GroupName })
            .IsUnique()
            .HasDatabaseName("IX_UserQuotaGroupMemberships_UserId_GroupName");

        builder.HasIndex(m => m.GroupName)
            .HasDatabaseName("IX_UserQuotaGroupMemberships_GroupName");
    }
}
