using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Users;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Farm.Web.Api.Tests.Repositories.Users;

/// <summary>
/// Verifies the grant/deny precedence rule for <see cref="RolePermission.Granted"/> decided in
/// issue #1450: an explicit deny on any of a user's active roles suppresses a permission even
/// when another active role grants it. See docs/ROLE_PERMISSION_PRECEDENCE.md.
/// </summary>
public sealed class EfUsersRepositoryPermissionPrecedenceTests : IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public EfUsersRepositoryPermissionPrecedenceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
    }

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync();
    }

    private async Task<AppDbContext> CreateContextAsync()
    {
        AppDbContext context = new(_options);
        _ = await context.Database.EnsureCreatedAsync();
        return context;
    }

    private static async Task<Guid> CreateUserAsync(AppDbContext context, string username)
    {
        User user = new()
        {
            Id = Guid.NewGuid(),
            Username = username,
            Email = $"{username}@example.test",
            PasswordHash = "hash",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _ = context.Users.Add(user);
        await context.SaveChangesAsync();
        return user.Id;
    }

    private static async Task<Guid> CreateRoleAsync(AppDbContext context, string name)
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
        return role.Id;
    }

    private static async Task<Guid> GetOrCreateResourceAsync(AppDbContext context, string name)
    {
        Resource? resource = await context.Resources.FirstOrDefaultAsync(r => r.Name == name);
        if (resource != null)
        {
            return resource.Id;
        }

        resource = new Resource
        {
            Id = Guid.NewGuid(),
            Name = name,
            DisplayName = name,
            ResourceType = "system",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _ = context.Resources.Add(resource);
        await context.SaveChangesAsync();
        return resource.Id;
    }

    private static async Task<Guid> GetOrCreateActionAsync(AppDbContext context, string name)
    {
        UserAction? action = await context.UserActions.FirstOrDefaultAsync(a => a.Name == name);
        if (action != null)
        {
            return action.Id;
        }

        action = new UserAction
        {
            Id = Guid.NewGuid(),
            Name = name,
            DisplayName = name,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _ = context.UserActions.Add(action);
        await context.SaveChangesAsync();
        return action.Id;
    }

    private static async Task AddRolePermissionAsync(AppDbContext context, Guid roleId, string resourceName, string actionName, bool granted)
    {
        Guid resourceId = await GetOrCreateResourceAsync(context, resourceName);
        Guid actionId = await GetOrCreateActionAsync(context, actionName);
        _ = context.RolePermissions.Add(new RolePermission
        {
            Id = Guid.NewGuid(),
            RoleId = roleId,
            ResourceId = resourceId,
            ActionId = actionId,
            Granted = granted,
            CreatedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();
    }

    private static async Task AssignRoleAsync(AppDbContext context, Guid userId, Guid roleId)
    {
        _ = context.UserRoles.Add(new UserRole
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            RoleId = roleId,
            AssignedAt = DateTime.UtcNow,
            IsActive = true
        });
        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task GetGrantedPermissionsAsync_GrantOnly_IsIncluded()
    {
        await using AppDbContext context = await CreateContextAsync();
        Guid userId = await CreateUserAsync(context, "grant-only-user");
        Guid roleId = await CreateRoleAsync(context, "role_grant_only");
        await AddRolePermissionAsync(context, roleId, "printers", "read", granted: true);
        await AssignRoleAsync(context, userId, roleId);

        EfUsersRepository repository = new(context);
        List<(string Resource, string Action)> permissions = await repository.GetGrantedPermissionsAsync(userId);

        permissions.Should().ContainSingle(p => p.Resource == "printers" && p.Action == "read");
    }

    [Fact]
    public async Task GetGrantedPermissionsAsync_DenyOnly_IsExcluded()
    {
        await using AppDbContext context = await CreateContextAsync();
        Guid userId = await CreateUserAsync(context, "deny-only-user");
        Guid roleId = await CreateRoleAsync(context, "role_deny_only");
        await AddRolePermissionAsync(context, roleId, "printers", "write", granted: false);
        await AssignRoleAsync(context, userId, roleId);

        EfUsersRepository repository = new(context);
        List<(string Resource, string Action)> permissions = await repository.GetGrantedPermissionsAsync(userId);

        permissions.Should().NotContain(p => p.Resource == "printers" && p.Action == "write");
    }

    [Fact]
    public async Task GetGrantedPermissionsAsync_GrantAndDenySamePermission_DenyWins()
    {
        await using AppDbContext context = await CreateContextAsync();
        Guid userId = await CreateUserAsync(context, "conflict-user");
        Guid grantingRoleId = await CreateRoleAsync(context, "role_granter");
        Guid denyingRoleId = await CreateRoleAsync(context, "role_denier");

        await AddRolePermissionAsync(context, grantingRoleId, "printers", "write", granted: true);
        await AddRolePermissionAsync(context, denyingRoleId, "printers", "write", granted: false);

        await AssignRoleAsync(context, userId, grantingRoleId);
        await AssignRoleAsync(context, userId, denyingRoleId);

        EfUsersRepository repository = new(context);
        List<(string Resource, string Action)> permissions = await repository.GetGrantedPermissionsAsync(userId);

        permissions.Should().NotContain(p => p.Resource == "printers" && p.Action == "write");
    }

    [Fact]
    public async Task GetGrantedPermissionsAsync_InheritedRoles_DenyOnOneRoleSuppressesGrantFromAnother()
    {
        await using AppDbContext context = await CreateContextAsync();
        Guid userId = await CreateUserAsync(context, "inherited-user");
        Guid roleA = await CreateRoleAsync(context, "role_a");
        Guid roleB = await CreateRoleAsync(context, "role_b");

        // Role A grants both X (printers:write) and Y (printers:read).
        await AddRolePermissionAsync(context, roleA, "printers", "write", granted: true);
        await AddRolePermissionAsync(context, roleA, "printers", "read", granted: true);

        // Role B explicitly denies X (printers:write) only.
        await AddRolePermissionAsync(context, roleB, "printers", "write", granted: false);

        await AssignRoleAsync(context, userId, roleA);
        await AssignRoleAsync(context, userId, roleB);

        EfUsersRepository repository = new(context);
        List<(string Resource, string Action)> permissions = await repository.GetGrantedPermissionsAsync(userId);

        permissions.Should().ContainSingle(p => p.Resource == "printers" && p.Action == "read");
        permissions.Should().NotContain(p => p.Resource == "printers" && p.Action == "write");
    }

    [Fact]
    public async Task GetGrantedPermissionsAsync_SamePermissionGrantedByTwoRoles_ReturnedExactlyOnce()
    {
        await using AppDbContext context = await CreateContextAsync();
        Guid userId = await CreateUserAsync(context, "double-grant-user");
        Guid roleA = await CreateRoleAsync(context, "role_double_a");
        Guid roleB = await CreateRoleAsync(context, "role_double_b");

        await AddRolePermissionAsync(context, roleA, "printers", "read", granted: true);
        await AddRolePermissionAsync(context, roleB, "printers", "read", granted: true);

        await AssignRoleAsync(context, userId, roleA);
        await AssignRoleAsync(context, userId, roleB);

        EfUsersRepository repository = new(context);
        List<(string Resource, string Action)> permissions = await repository.GetGrantedPermissionsAsync(userId);

        permissions.Should().BeEquivalentTo(new[] { ("printers", "read") });
    }

    [Fact]
    public async Task GetGrantedPermissionsAsync_DenyFromInactiveRole_DoesNotSuppressGrant()
    {
        await using AppDbContext context = await CreateContextAsync();
        Guid userId = await CreateUserAsync(context, "inactive-deny-user");
        Guid grantingRoleId = await CreateRoleAsync(context, "role_active_granter");
        Guid denyingRoleId = await CreateRoleAsync(context, "role_inactive_denier");

        await AddRolePermissionAsync(context, grantingRoleId, "printers", "write", granted: true);
        await AddRolePermissionAsync(context, denyingRoleId, "printers", "write", granted: false);

        await AssignRoleAsync(context, userId, grantingRoleId);

        // Deny comes from a role assignment that is inactive; it must not count.
        context.UserRoles.Add(new UserRole
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            RoleId = denyingRoleId,
            AssignedAt = DateTime.UtcNow,
            IsActive = false
        });
        await context.SaveChangesAsync();

        EfUsersRepository repository = new(context);
        List<(string Resource, string Action)> permissions = await repository.GetGrantedPermissionsAsync(userId);

        permissions.Should().ContainSingle(p => p.Resource == "printers" && p.Action == "write");
    }

    [Fact]
    public async Task GetGrantedPermissionsAsync_DenyFromExpiredRole_DoesNotSuppressGrant()
    {
        await using AppDbContext context = await CreateContextAsync();
        Guid userId = await CreateUserAsync(context, "expired-deny-user");
        Guid grantingRoleId = await CreateRoleAsync(context, "role_active_granter_2");
        Guid denyingRoleId = await CreateRoleAsync(context, "role_expired_denier");

        await AddRolePermissionAsync(context, grantingRoleId, "printers", "write", granted: true);
        await AddRolePermissionAsync(context, denyingRoleId, "printers", "write", granted: false);

        await AssignRoleAsync(context, userId, grantingRoleId);

        // Deny comes from a role assignment that expired in the past; it must not count.
        context.UserRoles.Add(new UserRole
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            RoleId = denyingRoleId,
            AssignedAt = DateTime.UtcNow.AddDays(-30),
            ExpiresAt = DateTime.UtcNow.AddDays(-1),
            IsActive = true
        });
        await context.SaveChangesAsync();

        EfUsersRepository repository = new(context);
        List<(string Resource, string Action)> permissions = await repository.GetGrantedPermissionsAsync(userId);

        permissions.Should().ContainSingle(p => p.Resource == "printers" && p.Action == "write");
    }

    [Fact]
    public async Task GetGrantedPermissionsAsync_UserWithNoRoles_ReturnsEmpty()
    {
        await using AppDbContext context = await CreateContextAsync();
        Guid userId = await CreateUserAsync(context, "no-roles-user");

        EfUsersRepository repository = new(context);
        List<(string Resource, string Action)> permissions = await repository.GetGrantedPermissionsAsync(userId);

        permissions.Should().BeEmpty();
    }

    [Fact]
    public async Task GetDeniedPermissionsAsync_GrantOnly_IsExcluded()
    {
        await using AppDbContext context = await CreateContextAsync();
        Guid userId = await CreateUserAsync(context, "denied-grant-only-user");
        Guid roleId = await CreateRoleAsync(context, "role_denied_grant_only");
        await AddRolePermissionAsync(context, roleId, "printers", "read", granted: true);
        await AssignRoleAsync(context, userId, roleId);

        EfUsersRepository repository = new(context);
        List<(string Resource, string Action)> denied = await repository.GetDeniedPermissionsAsync(userId);

        denied.Should().BeEmpty();
    }

    [Fact]
    public async Task GetDeniedPermissionsAsync_DenyOnly_IsIncluded()
    {
        await using AppDbContext context = await CreateContextAsync();
        Guid userId = await CreateUserAsync(context, "denied-deny-only-user");
        Guid roleId = await CreateRoleAsync(context, "role_denied_deny_only");
        await AddRolePermissionAsync(context, roleId, "printers", "delete", granted: false);
        await AssignRoleAsync(context, userId, roleId);

        EfUsersRepository repository = new(context);
        List<(string Resource, string Action)> denied = await repository.GetDeniedPermissionsAsync(userId);

        denied.Should().BeEquivalentTo(new[] { ("printers", "delete") });
    }

    [Fact]
    public async Task GetDeniedPermissionsAsync_GrantAndDenySamePermission_IsStillIncluded()
    {
        // GetDeniedPermissionsAsync surfaces every pair with at least one deny row, even if
        // another active role also grants the same pair — callers use this list purely to
        // suppress the resource:admin implication, so the deny must always be visible here
        // regardless of what GetGrantedPermissionsAsync ultimately resolves to.
        await using AppDbContext context = await CreateContextAsync();
        Guid userId = await CreateUserAsync(context, "denied-conflict-user");
        Guid grantingRoleId = await CreateRoleAsync(context, "role_denied_granter");
        Guid denyingRoleId = await CreateRoleAsync(context, "role_denied_denier");

        await AddRolePermissionAsync(context, grantingRoleId, "printers", "write", granted: true);
        await AddRolePermissionAsync(context, denyingRoleId, "printers", "write", granted: false);

        await AssignRoleAsync(context, userId, grantingRoleId);
        await AssignRoleAsync(context, userId, denyingRoleId);

        EfUsersRepository repository = new(context);
        List<(string Resource, string Action)> denied = await repository.GetDeniedPermissionsAsync(userId);

        denied.Should().BeEquivalentTo(new[] { ("printers", "write") });
    }

    [Fact]
    public async Task GetDeniedPermissionsAsync_UserWithNoRoles_ReturnsEmpty()
    {
        await using AppDbContext context = await CreateContextAsync();
        Guid userId = await CreateUserAsync(context, "denied-no-roles-user");

        EfUsersRepository repository = new(context);
        List<(string Resource, string Action)> denied = await repository.GetDeniedPermissionsAsync(userId);

        denied.Should().BeEmpty();
    }
}
