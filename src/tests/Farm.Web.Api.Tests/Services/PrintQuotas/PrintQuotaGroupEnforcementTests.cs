using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.PrintQuotas;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services.PrintQuotas;

public class PrintQuotaGroupEnforcementTests
{
    private static AppDbContext CreateDb() => TestInfrastructure.TestHelpers.CreateSqliteInMemoryDb();

    private static PrintQuotaService CreateService(AppDbContext db)
        => new(db, new Mock<ILogger<PrintQuotaService>>().Object);

    private static User CreateUser(AppDbContext db)
    {
        User user = new()
        {
            Id = Guid.NewGuid(),
            Username = "testuser",
            Email = "test@test.com",
            PasswordHash = "hash",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Users.Add(user);
        return user;
    }

    private static PrintQuota CreateGroupQuota(string groupName, QuotaType type, decimal limit)
        => new()
        {
            Id = Guid.NewGuid(),
            GroupName = groupName,
            QuotaType = type,
            LimitAmount = limit,
            UsedAmount = 0,
            PeriodType = QuotaPeriodType.Monthly,
            PeriodStart = DateTime.UtcNow,
            ResetAt = DateTime.UtcNow.AddMonths(1),
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

    private static PrintQuota CreateUserQuota(Guid userId, QuotaType type, decimal limit)
        => new()
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            QuotaType = type,
            LimitAmount = limit,
            UsedAmount = 0,
            PeriodType = QuotaPeriodType.Monthly,
            PeriodStart = DateTime.UtcNow,
            ResetAt = DateTime.UtcNow.AddMonths(1),
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

    // ── CheckQuotaAsync: group enforcement ────────────────────────────

    [Fact]
    public async Task CheckQuotaAsync_GroupQuotaExceeded_ReturnsDenied()
    {
        await using AppDbContext db = CreateDb();
        User user = CreateUser(db);

        PrintQuota groupQuota = CreateGroupQuota("Students", QuotaType.Cost, 100);
        groupQuota.UsedAmount = 90;
        db.PrintQuotas.Add(groupQuota);

        db.UserQuotaGroupMemberships.Add(new UserQuotaGroupMembership
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            GroupName = "Students",
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        PrintQuotaService svc = CreateService(db);

        QuotaCheckResult result = await svc.CheckQuotaAsync(user.Id, estimatedCost: 20, jobCount: 1, estimatedWeightGrams: 0);

        Assert.False(result.Allowed);
        Assert.Equal(groupQuota.Id, result.DeniedByQuotaId);
        Assert.Contains("group: Students", result.DeniedReason);
    }

    [Fact]
    public async Task CheckQuotaAsync_GroupQuotaWithinLimit_ReturnsAllowed()
    {
        await using AppDbContext db = CreateDb();
        User user = CreateUser(db);

        db.PrintQuotas.Add(CreateGroupQuota("Faculty", QuotaType.Cost, 500));
        db.UserQuotaGroupMemberships.Add(new UserQuotaGroupMembership
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            GroupName = "Faculty",
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        PrintQuotaService svc = CreateService(db);

        QuotaCheckResult result = await svc.CheckQuotaAsync(user.Id, estimatedCost: 50, jobCount: 1, estimatedWeightGrams: 0);

        Assert.True(result.Allowed);
    }

    [Fact]
    public async Task CheckQuotaAsync_UserQuotaAllowed_GroupQuotaDenied_ReturnsDenied()
    {
        await using AppDbContext db = CreateDb();
        User user = CreateUser(db);

        db.PrintQuotas.Add(CreateUserQuota(user.Id, QuotaType.Cost, 200));

        PrintQuota groupQuota = CreateGroupQuota("Students", QuotaType.Cost, 50);
        groupQuota.UsedAmount = 45;
        db.PrintQuotas.Add(groupQuota);

        db.UserQuotaGroupMemberships.Add(new UserQuotaGroupMembership
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            GroupName = "Students",
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        PrintQuotaService svc = CreateService(db);

        QuotaCheckResult result = await svc.CheckQuotaAsync(user.Id, estimatedCost: 10, jobCount: 1, estimatedWeightGrams: 0);

        Assert.False(result.Allowed);
        Assert.Equal(groupQuota.Id, result.DeniedByQuotaId);
    }

    [Fact]
    public async Task CheckQuotaAsync_NoGroupMembership_IgnoresGroupQuotas()
    {
        await using AppDbContext db = CreateDb();
        User user = CreateUser(db);

        PrintQuota groupQuota = CreateGroupQuota("Students", QuotaType.Cost, 10);
        groupQuota.UsedAmount = 9;
        db.PrintQuotas.Add(groupQuota);
        await db.SaveChangesAsync();

        PrintQuotaService svc = CreateService(db);

        QuotaCheckResult result = await svc.CheckQuotaAsync(user.Id, estimatedCost: 5, jobCount: 1, estimatedWeightGrams: 0);

        Assert.True(result.Allowed);
    }

    [Fact]
    public async Task CheckQuotaAsync_MultipleGroups_ChecksAll()
    {
        await using AppDbContext db = CreateDb();
        User user = CreateUser(db);

        db.PrintQuotas.Add(CreateGroupQuota("Students", QuotaType.Cost, 1000));

        PrintQuota labQuota = CreateGroupQuota("Lab101", QuotaType.Count, 5);
        labQuota.UsedAmount = 5;
        db.PrintQuotas.Add(labQuota);

        db.UserQuotaGroupMemberships.AddRange(
            new UserQuotaGroupMembership { Id = Guid.NewGuid(), UserId = user.Id, GroupName = "Students", CreatedAt = DateTime.UtcNow },
            new UserQuotaGroupMembership { Id = Guid.NewGuid(), UserId = user.Id, GroupName = "Lab101", CreatedAt = DateTime.UtcNow }
        );
        await db.SaveChangesAsync();

        PrintQuotaService svc = CreateService(db);

        QuotaCheckResult result = await svc.CheckQuotaAsync(user.Id, estimatedCost: 1, jobCount: 1, estimatedWeightGrams: 0);

        Assert.False(result.Allowed);
        Assert.Equal(labQuota.Id, result.DeniedByQuotaId);
    }

    // ── DeductQuotaUsageAsync: group enforcement ──────────────────────

    [Fact]
    public async Task DeductQuotaUsageAsync_DeductsFromGroupQuotas()
    {
        await using AppDbContext db = CreateDb();
        User user = CreateUser(db);

        PrintQuota groupQuota = CreateGroupQuota("Students", QuotaType.Cost, 100);
        db.PrintQuotas.Add(groupQuota);

        db.UserQuotaGroupMemberships.Add(new UserQuotaGroupMembership
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            GroupName = "Students",
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        PrintQuotaService svc = CreateService(db);

        await svc.DeductQuotaUsageAsync(user.Id, actualCost: 25, actualWeightGrams: 0);

        await db.Entry(groupQuota).ReloadAsync();
        Assert.Equal(25m, groupQuota.UsedAmount);
    }

    [Fact]
    public async Task DeductQuotaUsageAsync_DeductsFromBothUserAndGroupQuotas()
    {
        await using AppDbContext db = CreateDb();
        User user = CreateUser(db);

        PrintQuota userQuota = CreateUserQuota(user.Id, QuotaType.Weight, 1000);
        PrintQuota groupQuota = CreateGroupQuota("Lab", QuotaType.Weight, 5000);
        db.PrintQuotas.AddRange(userQuota, groupQuota);

        db.UserQuotaGroupMemberships.Add(new UserQuotaGroupMembership
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            GroupName = "Lab",
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        PrintQuotaService svc = CreateService(db);

        await svc.DeductQuotaUsageAsync(user.Id, actualCost: 0, actualWeightGrams: 150);

        await db.Entry(userQuota).ReloadAsync();
        await db.Entry(groupQuota).ReloadAsync();
        Assert.Equal(150m, userQuota.UsedAmount);
        Assert.Equal(150m, groupQuota.UsedAmount);
    }

    // ── RefundQuotaUsageAsync: group enforcement ──────────────────────

    [Fact]
    public async Task RefundQuotaUsageAsync_RefundsGroupQuotas()
    {
        await using AppDbContext db = CreateDb();
        User user = CreateUser(db);

        PrintQuota groupQuota = CreateGroupQuota("Students", QuotaType.Cost, 100);
        groupQuota.UsedAmount = 50;
        db.PrintQuotas.Add(groupQuota);

        db.UserQuotaGroupMemberships.Add(new UserQuotaGroupMembership
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            GroupName = "Students",
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        PrintQuotaService svc = CreateService(db);

        await svc.RefundQuotaUsageAsync(user.Id, refundCost: 20, refundWeightGrams: 0);

        await db.Entry(groupQuota).ReloadAsync();
        Assert.Equal(30m, groupQuota.UsedAmount);
    }

    [Fact]
    public async Task RefundQuotaUsageAsync_DoesNotGoNegative()
    {
        await using AppDbContext db = CreateDb();
        User user = CreateUser(db);

        PrintQuota groupQuota = CreateGroupQuota("Students", QuotaType.Cost, 100);
        groupQuota.UsedAmount = 5;
        db.PrintQuotas.Add(groupQuota);

        db.UserQuotaGroupMemberships.Add(new UserQuotaGroupMembership
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            GroupName = "Students",
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        PrintQuotaService svc = CreateService(db);

        await svc.RefundQuotaUsageAsync(user.Id, refundCost: 50, refundWeightGrams: 0);

        await db.Entry(groupQuota).ReloadAsync();
        Assert.Equal(0m, groupQuota.UsedAmount);
    }

    // ── Membership CRUD ───────────────────────────────────────────────

    [Fact]
    public async Task AddUserToGroupAsync_CreatesNewMembership()
    {
        await using AppDbContext db = CreateDb();
        User user = CreateUser(db);
        await db.SaveChangesAsync();

        PrintQuotaService svc = CreateService(db);

        UserQuotaGroupMembership membership = await svc.AddUserToGroupAsync(user.Id, "Students");

        Assert.Equal(user.Id, membership.UserId);
        Assert.Equal("Students", membership.GroupName);
        Assert.NotEqual(Guid.Empty, membership.Id);
    }

    [Fact]
    public async Task GetUserGroupsAsync_ReturnsGroupNames()
    {
        await using AppDbContext db = CreateDb();
        User user = CreateUser(db);
        db.UserQuotaGroupMemberships.AddRange(
            new UserQuotaGroupMembership { Id = Guid.NewGuid(), UserId = user.Id, GroupName = "Zeta", CreatedAt = DateTime.UtcNow },
            new UserQuotaGroupMembership { Id = Guid.NewGuid(), UserId = user.Id, GroupName = "Alpha", CreatedAt = DateTime.UtcNow }
        );
        await db.SaveChangesAsync();

        PrintQuotaService svc = CreateService(db);

        string[] groups = await svc.GetUserGroupsAsync(user.Id);

        Assert.Equal(2, groups.Length);
        Assert.Equal("Alpha", groups[0]);
        Assert.Equal("Zeta", groups[1]);
    }

    [Fact]
    public async Task RemoveUserFromGroupAsync_ExistingMembership_ReturnsTrue()
    {
        await using AppDbContext db = CreateDb();
        User user = CreateUser(db);
        db.UserQuotaGroupMemberships.Add(new UserQuotaGroupMembership
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            GroupName = "Students",
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        PrintQuotaService svc = CreateService(db);

        bool removed = await svc.RemoveUserFromGroupAsync(user.Id, "Students");

        Assert.True(removed);

        string[] groups = await svc.GetUserGroupsAsync(user.Id);
        Assert.Empty(groups);
    }

    [Fact]
    public async Task RemoveUserFromGroupAsync_NonExistentMembership_ReturnsFalse()
    {
        await using AppDbContext db = CreateDb();
        User user = CreateUser(db);
        await db.SaveChangesAsync();

        PrintQuotaService svc = CreateService(db);

        bool removed = await svc.RemoveUserFromGroupAsync(user.Id, "NonExistent");

        Assert.False(removed);
    }

    [Fact]
    public async Task GetGroupMembersAsync_ReturnsAllMembers()
    {
        await using AppDbContext db = CreateDb();
        User user1 = CreateUser(db);
        user1.Username = "user1";
        User user2 = new()
        {
            Id = Guid.NewGuid(),
            Username = "user2",
            Email = "user2@test.com",
            PasswordHash = "hash",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Users.Add(user2);

        db.UserQuotaGroupMemberships.AddRange(
            new UserQuotaGroupMembership { Id = Guid.NewGuid(), UserId = user1.Id, GroupName = "Students", CreatedAt = DateTime.UtcNow },
            new UserQuotaGroupMembership { Id = Guid.NewGuid(), UserId = user2.Id, GroupName = "Students", CreatedAt = DateTime.UtcNow }
        );
        await db.SaveChangesAsync();

        PrintQuotaService svc = CreateService(db);

        UserQuotaGroupMembership[] members = await svc.GetGroupMembersAsync("Students");

        Assert.Equal(2, members.Length);
        Assert.All(members, m => Assert.Equal("Students", m.GroupName));
    }
}
