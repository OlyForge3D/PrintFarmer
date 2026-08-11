using Farm.Infrastructure.Contracts.Roles;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Roles;
using Farm.Infrastructure.Services.Authentication;
using Farm.Infrastructure.Services.Roles;
using Farm.Web.Api.Services.Startup;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Farm.Web.Api.Tests.Services.Roles;

/// <summary>
/// Exercises the role CRUD business logic in <see cref="RoleManagementService"/> against a
/// real (in-memory Sqlite) EF Core database, proving each D6 (system-role protection), D7
/// (name immutability/slug rules), D8 (no silent orphan deletion), and D9 (no self/global
/// admin lockout) invariant from issue #1448.
/// </summary>
public sealed class RoleManagementServiceTests : IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly Mock<IAuthAuditService> _authAuditService = new(MockBehavior.Loose);

    public RoleManagementServiceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
    }

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync();
    }

    private async Task<AppDbContext> CreateSeededContextAsync()
    {
        AppDbContext context = new(_options);
        _ = await context.Database.EnsureCreatedAsync();
        var dataSeedService = new Mock<Farm.Infrastructure.Services.DataManagement.IDataSeedService>(MockBehavior.Loose);
        DatabaseInitializer initializer = new(context, NullLogger<DatabaseInitializer>.Instance, dataSeedService.Object);
        await initializer.SeedAllAsync();
        return context;
    }

    private RoleManagementService CreateService(AppDbContext context)
    {
        EfRolesRepository repository = new(context);
        return new RoleManagementService(repository, _authAuditService.Object);
    }

    private static async Task<Guid> CreateUserAsync(AppDbContext context, string username, bool isActive = true)
    {
        User user = new()
        {
            Id = Guid.NewGuid(),
            Username = username,
            Email = $"{username}@example.test",
            PasswordHash = "hash",
            IsActive = isActive,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _ = context.Users.Add(user);
        await context.SaveChangesAsync();
        return user.Id;
    }

    private static async Task AssignRoleAsync(AppDbContext context, Guid userId, Guid roleId, DateTime? expiresAt = null)
    {
        _ = context.UserRoles.Add(new UserRole
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            RoleId = roleId,
            AssignedAt = DateTime.UtcNow,
            ExpiresAt = expiresAt,
            IsActive = true
        });
        await context.SaveChangesAsync();
    }

    private static async Task<Guid> GetRoleIdAsync(AppDbContext context, string name)
    {
        return await context.Roles.Where(r => r.Name == name).Select(r => r.Id).SingleAsync();
    }

    /// <summary>
    /// Creates a second, active, admin-equivalent custom role directly against the DB (bypassing
    /// the service under test), so lockout scenarios have "other admin coverage" to compare against.
    /// </summary>
    private static async Task<Guid> CreateAdminEquivalentRoleAsync(AppDbContext context, string name)
    {
        Role role = new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            DisplayName = name,
            IsSystemRole = false,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _ = context.Roles.Add(role);
        await context.SaveChangesAsync();

        foreach (string resourceName in new[] { "roles", "users" })
        {
            Guid resourceId = await context.Resources.Where(r => r.Name == resourceName).Select(r => r.Id).SingleAsync();
            Guid actionId = await context.UserActions.Where(a => a.Name == "admin").Select(a => a.Id).SingleAsync();
            _ = context.RolePermissions.Add(new RolePermission
            {
                Id = Guid.NewGuid(),
                RoleId = role.Id,
                ResourceId = resourceId,
                ActionId = actionId,
                Granted = true,
                CreatedAt = DateTime.UtcNow
            });
        }

        await context.SaveChangesAsync();
        return role.Id;
    }

    // ---- D7: name validation & immutability ----

    [Theory]
    [InlineData("1abc")] // must start with a letter
    [InlineData("ab")] // shorter than minimum length (3)
    [InlineData("has space")]
    [InlineData("Has-Dash")]
    public async Task CreateRoleAsync_RejectsNamesViolatingSlugPattern(string invalidName)
    {
        await using AppDbContext context = await CreateSeededContextAsync();
        RoleManagementService service = CreateService(context);

        Func<Task> act = () => service.CreateRoleAsync(
            new CreateCustomRoleRequest { Name = invalidName, DisplayName = "Test Role" },
            Guid.NewGuid(),
            "127.0.0.1");

        (await act.Should().ThrowAsync<RoleManagementException>())
            .Which.ErrorCode.Should().Be(RoleManagementErrorCode.InvalidName);
    }

    [Fact]
    public async Task CreateRoleAsync_RejectsReservedFarmPrefix()
    {
        await using AppDbContext context = await CreateSeededContextAsync();
        RoleManagementService service = CreateService(context);

        Func<Task> act = () => service.CreateRoleAsync(
            new CreateCustomRoleRequest { Name = "farm_custom", DisplayName = "Test Role" },
            Guid.NewGuid(),
            "127.0.0.1");

        (await act.Should().ThrowAsync<RoleManagementException>())
            .Which.ErrorCode.Should().Be(RoleManagementErrorCode.InvalidName);
    }

    [Fact]
    public async Task CreateRoleAsync_RejectsDuplicateNameCaseInsensitively()
    {
        await using AppDbContext context = await CreateSeededContextAsync();
        RoleManagementService service = CreateService(context);
        Guid actor = Guid.NewGuid();

        _ = await service.CreateRoleAsync(new CreateCustomRoleRequest { Name = "operators", DisplayName = "Operators" }, actor, null);

        Func<Task> act = () => service.CreateRoleAsync(new CreateCustomRoleRequest { Name = "OPERATORS", DisplayName = "Dup" }, actor, null);

        (await act.Should().ThrowAsync<RoleManagementException>())
            .Which.ErrorCode.Should().Be(RoleManagementErrorCode.InvalidName);
    }

    [Fact]
    public async Task CreateRoleAsync_CreatesCustomRoleAndAuditsIt()
    {
        await using AppDbContext context = await CreateSeededContextAsync();
        RoleManagementService service = CreateService(context);
        Guid actor = Guid.NewGuid();

        RoleDetailDto created = await service.CreateRoleAsync(
            new CreateCustomRoleRequest { Name = "operators", DisplayName = "Operators", Description = "Floor operators" },
            actor,
            "10.0.0.5");

        created.IsSystemRole.Should().BeFalse();
        created.Name.Should().Be("operators");

        _authAuditService.Verify(
            svc => svc.LogRoleManagementEventAsync(
                actor,
                created.Id,
                "operators",
                AuthEventType.RoleCreated,
                null,
                It.IsAny<string>(),
                "10.0.0.5",
                null,
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task UpdateRoleAsync_RejectsAttemptToRenameRole()
    {
        await using AppDbContext context = await CreateSeededContextAsync();
        RoleManagementService service = CreateService(context);
        Guid actor = Guid.NewGuid();
        RoleDetailDto created = await service.CreateRoleAsync(new CreateCustomRoleRequest { Name = "operators", DisplayName = "Operators" }, actor, null);

        Func<Task> act = () => service.UpdateRoleAsync(created.Id, new UpdateCustomRoleRequest { Name = "renamed_operators" }, actor, null);

        (await act.Should().ThrowAsync<RoleManagementException>())
            .Which.ErrorCode.Should().Be(RoleManagementErrorCode.NameIsImmutable);
    }

    // ---- D6: system role protection ----

    [Fact]
    public async Task UpdateRoleAsync_RejectsDeactivatingSystemRole()
    {
        await using AppDbContext context = await CreateSeededContextAsync();
        RoleManagementService service = CreateService(context);
        Guid farmAdminId = await GetRoleIdAsync(context, "farm_admin");
        Guid actor = Guid.NewGuid();

        Func<Task> act = () => service.UpdateRoleAsync(farmAdminId, new UpdateCustomRoleRequest { IsActive = false }, actor, null);

        (await act.Should().ThrowAsync<RoleManagementException>())
            .Which.ErrorCode.Should().Be(RoleManagementErrorCode.SystemRoleProtected);
    }

    [Fact]
    public async Task UpdateRoleAsync_AllowsEditingDisplayNameOfSystemRole()
    {
        await using AppDbContext context = await CreateSeededContextAsync();
        RoleManagementService service = CreateService(context);
        Guid farmAdminId = await GetRoleIdAsync(context, "farm_admin");
        Guid actor = Guid.NewGuid();

        RoleDetailDto updated = await service.UpdateRoleAsync(
            farmAdminId,
            new UpdateCustomRoleRequest { DisplayName = "Farm Administrator" },
            actor,
            null);

        updated.DisplayName.Should().Be("Farm Administrator");
        updated.IsSystemRole.Should().BeTrue();
    }

    [Fact]
    public async Task DeleteRoleAsync_RejectsDeletingSystemRole()
    {
        await using AppDbContext context = await CreateSeededContextAsync();
        RoleManagementService service = CreateService(context);
        Guid farmUserId = await GetRoleIdAsync(context, "farm_user");
        Guid actor = Guid.NewGuid();

        Func<Task> act = () => service.DeleteRoleAsync(farmUserId, reassignToRoleId: null, cascade: false, actor, null);

        (await act.Should().ThrowAsync<RoleManagementException>())
            .Which.ErrorCode.Should().Be(RoleManagementErrorCode.SystemRoleProtected);
    }

    // ---- D8: no silent orphan deletion ----

    [Fact]
    public async Task DeleteRoleAsync_RejectsDeletingRoleWithMembersWithoutReassignmentOrCascade()
    {
        await using AppDbContext context = await CreateSeededContextAsync();
        RoleManagementService service = CreateService(context);
        Guid actor = Guid.NewGuid();
        RoleDetailDto role = await service.CreateRoleAsync(new CreateCustomRoleRequest { Name = "operators", DisplayName = "Operators" }, actor, null);
        Guid memberId = await CreateUserAsync(context, "operator1");
        await AssignRoleAsync(context, memberId, role.Id);

        Func<Task> act = () => service.DeleteRoleAsync(role.Id, reassignToRoleId: null, cascade: false, actor, null);

        (await act.Should().ThrowAsync<RoleManagementException>())
            .Which.ErrorCode.Should().Be(RoleManagementErrorCode.HasMembers);
    }

    [Fact]
    public async Task DeleteRoleAsync_ReassignsMembersPreservingExpiresAt()
    {
        await using AppDbContext context = await CreateSeededContextAsync();
        RoleManagementService service = CreateService(context);
        Guid actor = Guid.NewGuid();
        RoleDetailDto source = await service.CreateRoleAsync(new CreateCustomRoleRequest { Name = "operators", DisplayName = "Operators" }, actor, null);
        RoleDetailDto target = await service.CreateRoleAsync(new CreateCustomRoleRequest { Name = "supervisors", DisplayName = "Supervisors" }, actor, null);
        Guid memberId = await CreateUserAsync(context, "operator1");
        DateTime expiry = DateTime.UtcNow.AddDays(30);
        await AssignRoleAsync(context, memberId, source.Id, expiry);

        await service.DeleteRoleAsync(source.Id, reassignToRoleId: target.Id, cascade: false, actor, null);

        UserRole? membership = await context.UserRoles.SingleOrDefaultAsync(ur => ur.UserId == memberId);
        membership.Should().NotBeNull();
        membership!.RoleId.Should().Be(target.Id);
        membership.ExpiresAt.Should().BeCloseTo(expiry, TimeSpan.FromSeconds(1));
        (await context.Roles.AnyAsync(r => r.Id == source.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task DeleteRoleAsync_CascadeRemovesMembersWhenExplicitlyRequested()
    {
        await using AppDbContext context = await CreateSeededContextAsync();
        RoleManagementService service = CreateService(context);
        Guid actor = Guid.NewGuid();
        RoleDetailDto role = await service.CreateRoleAsync(new CreateCustomRoleRequest { Name = "operators", DisplayName = "Operators" }, actor, null);
        Guid memberId = await CreateUserAsync(context, "operator1");
        await AssignRoleAsync(context, memberId, role.Id);

        await service.DeleteRoleAsync(role.Id, reassignToRoleId: null, cascade: true, actor, null);

        (await context.UserRoles.AnyAsync(ur => ur.UserId == memberId)).Should().BeFalse();
        (await context.Roles.AnyAsync(r => r.Id == role.Id)).Should().BeFalse();
    }

    // ---- D9: admin lockout guardrails ----

    [Fact]
    public async Task DeleteRoleAsync_RejectsRemovingRoleWhenNoOtherActiveAdminCoverageExists()
    {
        await using AppDbContext context = await CreateSeededContextAsync();
        RoleManagementService service = CreateService(context);
        Guid actor = Guid.NewGuid();

        // A single custom admin-equivalent role with an active member, and no other
        // admin-equivalent role has any active member (farm_admin is unassigned in a fresh
        // seed), so removing it must be refused.
        Guid customAdminRoleId = await CreateAdminEquivalentRoleAsync(context, "super_admins");
        Guid memberId = await CreateUserAsync(context, "super-admin-1");
        await AssignRoleAsync(context, memberId, customAdminRoleId);

        Func<Task> act = () => service.DeleteRoleAsync(customAdminRoleId, reassignToRoleId: null, cascade: true, actor, null);

        (await act.Should().ThrowAsync<RoleManagementException>())
            .Which.ErrorCode.Should().Be(RoleManagementErrorCode.LastAdminRole);
    }

    [Fact]
    public async Task DeleteRoleAsync_AllowsRemovalWhenAnotherActiveAdminEquivalentRoleHasAMember()
    {
        await using AppDbContext context = await CreateSeededContextAsync();
        RoleManagementService service = CreateService(context);
        Guid actor = Guid.NewGuid();
        Guid farmAdminId = await GetRoleIdAsync(context, "farm_admin");

        Guid otherAdminUserId = await CreateUserAsync(context, "root-admin");
        await AssignRoleAsync(context, otherAdminUserId, farmAdminId);

        Guid customAdminRoleId = await CreateAdminEquivalentRoleAsync(context, "super_admins");
        Guid memberId = await CreateUserAsync(context, "super-admin-1");
        await AssignRoleAsync(context, memberId, customAdminRoleId);

        await service.DeleteRoleAsync(customAdminRoleId, reassignToRoleId: null, cascade: true, actor, null);

        (await context.Roles.AnyAsync(r => r.Id == customAdminRoleId)).Should().BeFalse();
    }

    [Fact]
    public async Task DeleteRoleAsync_RejectsWhenActingAdminWouldLoseTheirOwnLastAdminRole()
    {
        await using AppDbContext context = await CreateSeededContextAsync();
        RoleManagementService service = CreateService(context);

        Guid customAdminRoleId = await CreateAdminEquivalentRoleAsync(context, "super_admins");
        Guid actorUserId = await CreateUserAsync(context, "acting-admin");
        await AssignRoleAsync(context, actorUserId, customAdminRoleId);

        Guid farmAdminId = await GetRoleIdAsync(context, "farm_admin");
        Guid otherAdminUserId = await CreateUserAsync(context, "other-admin");
        await AssignRoleAsync(context, otherAdminUserId, farmAdminId);

        // Global coverage exists (farm_admin has a member), but the acting admin is themself the
        // sole member of the role being removed and holds no other admin-equivalent role.
        Func<Task> act = () => service.DeleteRoleAsync(customAdminRoleId, reassignToRoleId: null, cascade: true, actorUserId, null);

        (await act.Should().ThrowAsync<RoleManagementException>())
            .Which.ErrorCode.Should().Be(RoleManagementErrorCode.SelfLockout);
    }

    [Fact]
    public async Task DeleteRoleAsync_AllowsActingAdminToRemoveRoleWhenTheyHoldAnotherAdminEquivalentRole()
    {
        await using AppDbContext context = await CreateSeededContextAsync();
        RoleManagementService service = CreateService(context);

        Guid customAdminRoleId = await CreateAdminEquivalentRoleAsync(context, "super_admins");
        Guid farmAdminId = await GetRoleIdAsync(context, "farm_admin");
        Guid actorUserId = await CreateUserAsync(context, "acting-admin");
        await AssignRoleAsync(context, actorUserId, customAdminRoleId);
        await AssignRoleAsync(context, actorUserId, farmAdminId);

        await service.DeleteRoleAsync(customAdminRoleId, reassignToRoleId: null, cascade: true, actorUserId, null);

        (await context.Roles.AnyAsync(r => r.Id == customAdminRoleId)).Should().BeFalse();
    }

    [Fact]
    public async Task DeleteRoleAsync_AllowsSelfLockoutRoleRemovalWhenReassignTargetIsAdminEquivalent()
    {
        await using AppDbContext context = await CreateSeededContextAsync();
        RoleManagementService service = CreateService(context);

        // The actor's only role is the one being deleted, and they hold no other
        // admin-equivalent role — but since reassignTo points to another admin-equivalent
        // role, the actor (and any other members) retain admin-equivalent access after the
        // move, so this must NOT be treated as a self-lockout.
        Guid customAdminRoleId = await CreateAdminEquivalentRoleAsync(context, "super_admins");
        Guid targetAdminRoleId = await CreateAdminEquivalentRoleAsync(context, "root_admins");
        Guid actorUserId = await CreateUserAsync(context, "acting-admin");
        await AssignRoleAsync(context, actorUserId, customAdminRoleId);

        await service.DeleteRoleAsync(customAdminRoleId, reassignToRoleId: targetAdminRoleId, cascade: false, actorUserId, null);

        (await context.Roles.AnyAsync(r => r.Id == customAdminRoleId)).Should().BeFalse();
        (await context.UserRoles.AnyAsync(ur => ur.UserId == actorUserId && ur.RoleId == targetAdminRoleId && ur.IsActive)).Should().BeTrue();
    }

    [Fact]
    public async Task DeleteRoleAsync_RejectsRemovalWhenReassignTargetIsNotAdminEquivalentAndActorHasNoOtherCoverage()
    {
        await using AppDbContext context = await CreateSeededContextAsync();
        RoleManagementService service = CreateService(context);
        Guid farmUserId = await GetRoleIdAsync(context, "farm_user");

        Guid customAdminRoleId = await CreateAdminEquivalentRoleAsync(context, "super_admins");
        Guid actorUserId = await CreateUserAsync(context, "acting-admin");
        await AssignRoleAsync(context, actorUserId, customAdminRoleId);

        // farm_user is not admin-equivalent, so reassigning to it does not spare the actor
        // from a self-lockout.
        Func<Task> act = () => service.DeleteRoleAsync(customAdminRoleId, reassignToRoleId: farmUserId, cascade: false, actorUserId, null);

        (await act.Should().ThrowAsync<RoleManagementException>())
            .Which.ErrorCode.Should().Be(RoleManagementErrorCode.SelfLockout);
    }

    [Fact]
    public async Task DeleteRoleAsync_ExpiredAdminEquivalentAssignmentDoesNotSpareActorFromSelfLockout()
    {
        await using AppDbContext context = await CreateSeededContextAsync();
        RoleManagementService service = CreateService(context);

        Guid customAdminRoleId = await CreateAdminEquivalentRoleAsync(context, "super_admins");
        Guid farmAdminId = await GetRoleIdAsync(context, "farm_admin");
        Guid actorUserId = await CreateUserAsync(context, "acting-admin");
        await AssignRoleAsync(context, actorUserId, customAdminRoleId);

        // The actor also holds farm_admin, but that assignment already expired — it must not
        // count as "another admin-equivalent role" that would spare them from self-lockout.
        await AssignRoleAsync(context, actorUserId, farmAdminId, expiresAt: DateTime.UtcNow.AddDays(-1));

        Func<Task> act = () => service.DeleteRoleAsync(customAdminRoleId, reassignToRoleId: null, cascade: true, actorUserId, null);

        (await act.Should().ThrowAsync<RoleManagementException>())
            .Which.ErrorCode.Should().Be(RoleManagementErrorCode.SelfLockout);
    }

    [Fact]
    public async Task DeleteRoleAsync_ExpiredAdminEquivalentMembershipDoesNotCountAsGlobalCoverage()
    {
        await using AppDbContext context = await CreateSeededContextAsync();
        RoleManagementService service = CreateService(context);
        Guid actor = Guid.NewGuid();
        Guid farmAdminId = await GetRoleIdAsync(context, "farm_admin");

        // farm_admin has a member, but their assignment already expired, so it does not count
        // as active global admin coverage.
        Guid expiredAdminUserId = await CreateUserAsync(context, "expired-admin");
        await AssignRoleAsync(context, expiredAdminUserId, farmAdminId, expiresAt: DateTime.UtcNow.AddDays(-1));

        Guid customAdminRoleId = await CreateAdminEquivalentRoleAsync(context, "super_admins");
        Guid memberId = await CreateUserAsync(context, "super-admin-1");
        await AssignRoleAsync(context, memberId, customAdminRoleId);

        Func<Task> act = () => service.DeleteRoleAsync(customAdminRoleId, reassignToRoleId: null, cascade: true, actor, null);

        (await act.Should().ThrowAsync<RoleManagementException>())
            .Which.ErrorCode.Should().Be(RoleManagementErrorCode.LastAdminRole);
    }

    [Fact]
    public async Task ReloadRoleAsync_PicksUpAnIsActiveChangeCommittedByAConcurrentContext()
    {
        // Proves the mechanism DeleteRoleAsync/UpdateRoleAsync rely on to avoid making a D9
        // guardrail decision from a role.IsActive value read before their serializable
        // transaction started: without a reload, the tracked `role` instance below would keep
        // reporting the value it had at load time even after another context commits a change.
        await using AppDbContext context = await CreateSeededContextAsync();
        EfRolesRepository repository = new(context);

        Guid customRoleId = await CreateAdminEquivalentRoleAsync(context, "super_admins");
        Role role = (await repository.GetRoleEntityAsync(customRoleId))!;
        role.IsActive.Should().BeTrue();

        await using (AppDbContext concurrentContext = new(_options))
        {
            Role concurrentRole = await concurrentContext.Roles.SingleAsync(r => r.Id == customRoleId);
            concurrentRole.IsActive = false;
            await concurrentContext.SaveChangesAsync();
        }

        bool stillExists = await repository.ReloadRoleAsync(role);

        stillExists.Should().BeTrue();
        role.IsActive.Should().BeFalse("ReloadRoleAsync must overwrite the stale tracked value with the concurrently committed state");
    }

    [Fact]
    public async Task ReloadRoleAsync_ReturnsFalse_WhenTheRoleWasDeletedByAConcurrentContext()
    {
        await using AppDbContext context = await CreateSeededContextAsync();
        EfRolesRepository repository = new(context);

        Guid customRoleId = await CreateAdminEquivalentRoleAsync(context, "super_admins");
        Role role = (await repository.GetRoleEntityAsync(customRoleId))!;

        await using (AppDbContext concurrentContext = new(_options))
        {
            Role concurrentRole = await concurrentContext.Roles.SingleAsync(r => r.Id == customRoleId);
            concurrentContext.Roles.Remove(concurrentRole);
            await concurrentContext.SaveChangesAsync();
        }

        bool stillExists = await repository.ReloadRoleAsync(role);

        stillExists.Should().BeFalse();
    }
}
