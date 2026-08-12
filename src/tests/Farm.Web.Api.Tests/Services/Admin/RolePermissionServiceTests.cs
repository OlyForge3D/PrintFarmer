using Farm.Infrastructure.Authorization;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.Repositories.Users;
using Farm.Infrastructure.Services.Authentication;
using Farm.Web.Api.Services.Admin;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Primitives;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services.Admin;

/// <summary>
/// Unit tests for <see cref="RolePermissionService"/> covering every acceptance criterion of
/// #1449: catalog-only validation, farm_admin immutability (D6), optimistic concurrency via
/// <see cref="Role.UpdatedAt"/>, the D9 lockout invariant for <c>roles:admin</c>/<c>users:admin</c>,
/// full-replacement diffing, session revocation counting, and audit logging.
///
/// Uses a real in-memory Sqlite database (not the EF InMemory provider) because
/// <see cref="RolePermissionService"/> opens a real serializable transaction, which the
/// InMemory provider does not support. Mirrors the pattern established by
/// <c>RoleManagementServiceTests</c> (#1448).
/// </summary>
public sealed class RolePermissionServiceTests : IAsyncDisposable
{
    private const string QueueReadPermission = "queue:read";
    private const string QueueWritePermission = "queue:write";

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly Mock<IAuthAuditService> _authAuditServiceMock = new(MockBehavior.Strict);
    private readonly Mock<ITokenRevocationService> _tokenRevocationServiceMock = new(MockBehavior.Strict);
    private readonly IEffectivePermissionsRevocationService _revocationService;
    private readonly List<RouteEndpoint> _endpoints = [];

    public RolePermissionServiceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        _revocationService = new EffectivePermissionsRevocationService(_tokenRevocationServiceMock.Object);
    }

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task GetRolePermissionsAsync_UnknownRole_ReturnsNull()
    {
        await using AppDbContext db = await CreateContextAsync();
        await SeedCatalogAsync(db, "queue", "Calibration Queue", "read", "Read");

        RolePermissionService service = CreateService(db);

        RolePermissionsDto? result = await service.GetRolePermissionsAsync(Guid.NewGuid());

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetRolePermissionsAsync_JoinsCatalogWithCurrentGrants_ReportsAbsentGrantedAndDenied()
    {
        await using AppDbContext db = await CreateContextAsync();
        await SeedCatalogAsync(db, "queue", "Calibration Queue", "read", "Read");
        await SeedCatalogAsync(db, "queue", "Calibration Queue", "write", "Write");
        await SeedCatalogAsync(db, "printers", "Printers", "read", "Read");
        Role role = await CreateRoleAsync(db, "operator");
        await GrantAsync(db, role, "queue", "read", granted: true);
        await GrantAsync(db, role, "queue", "write", granted: false);

        RolePermissionService service = CreateService(db);

        RolePermissionsDto? result = await service.GetRolePermissionsAsync(role.Id);

        result.Should().NotBeNull();
        result!.RoleId.Should().Be(role.Id);
        result.IsEditable.Should().BeTrue();
        result.UpdatedAt.Should().Be(role.UpdatedAt);

        Dictionary<string, RolePermissionGrantStatus> statusByPermission = result.Resources
            .SelectMany(g => g.Permissions)
            .ToDictionary(p => p.Permission, p => p.Status);
        statusByPermission[QueueReadPermission].Should().Be(RolePermissionGrantStatus.Granted);
        statusByPermission[QueueWritePermission].Should().Be(RolePermissionGrantStatus.Denied);
        statusByPermission["printers:read"].Should().Be(RolePermissionGrantStatus.Absent);
    }

    [Fact]
    public async Task UpdateRolePermissionsAsync_UnknownRole_ReturnsRoleNotFound()
    {
        await using AppDbContext db = await CreateContextAsync();
        RolePermissionService service = CreateService(db);

        RolePermissionUpdateResult result = await service.UpdateRolePermissionsAsync(
            Guid.NewGuid(),
            new UpdateRolePermissionsRequestDto { UpdatedAt = DateTime.UtcNow, Permissions = [] },
            Guid.NewGuid(),
            "127.0.0.1");

        result.Should().BeOfType<RolePermissionUpdateResult.RoleNotFound>();
    }

    [Fact]
    public async Task UpdateRolePermissionsAsync_FarmAdminRole_ReturnsFarmAdminImmutable()
    {
        await using AppDbContext db = await CreateContextAsync();
        Role farmAdmin = await CreateRoleAsync(db, "farm_admin");
        RolePermissionService service = CreateService(db);

        RolePermissionUpdateResult result = await service.UpdateRolePermissionsAsync(
            farmAdmin.Id,
            new UpdateRolePermissionsRequestDto { UpdatedAt = farmAdmin.UpdatedAt, Permissions = [] },
            Guid.NewGuid(),
            "127.0.0.1");

        result.Should().BeOfType<RolePermissionUpdateResult.FarmAdminImmutable>();
    }

    [Fact]
    public async Task UpdateRolePermissionsAsync_UnknownPermission_ReturnsInvalidPermissions()
    {
        await using AppDbContext db = await CreateContextAsync();
        await SeedCatalogAsync(db, "queue", "Calibration Queue", "read", "Read");
        Role role = await CreateRoleAsync(db, "operator");
        RolePermissionService service = CreateService(db);

        RolePermissionUpdateResult result = await service.UpdateRolePermissionsAsync(
            role.Id,
            new UpdateRolePermissionsRequestDto { UpdatedAt = role.UpdatedAt, Permissions = ["not_a_real:permission"] },
            Guid.NewGuid(),
            "127.0.0.1");

        result.Should().BeOfType<RolePermissionUpdateResult.InvalidPermissions>()
            .Which.Permissions.Should().ContainSingle().Which.Should().Be("not_a_real:permission");
    }

    [Fact]
    public async Task UpdateRolePermissionsAsync_StaleUpdatedAt_ReturnsConcurrencyConflict()
    {
        await using AppDbContext db = await CreateContextAsync();
        await SeedCatalogAsync(db, "queue", "Calibration Queue", "read", "Read");
        Role role = await CreateRoleAsync(db, "operator");
        RolePermissionService service = CreateService(db);

        RolePermissionUpdateResult result = await service.UpdateRolePermissionsAsync(
            role.Id,
            new UpdateRolePermissionsRequestDto
            {
                UpdatedAt = role.UpdatedAt.AddMinutes(-5),
                Permissions = [QueueReadPermission],
            },
            Guid.NewGuid(),
            "127.0.0.1");

        result.Should().BeOfType<RolePermissionUpdateResult.ConcurrencyConflict>();
    }

    [Fact]
    public async Task UpdateRolePermissionsAsync_RemovingLastActiveRoleHoldingRolesAdmin_ReturnsLockoutViolation()
    {
        await using AppDbContext db = await CreateContextAsync();
        await SeedCatalogAsync(db, "roles", "Roles", "admin", "Admin");
        Role role = await CreateRoleAsync(db, "operator");
        // Simulate the only role (other than the immutable farm_admin) holding roles:admin.
        await GrantAsync(db, role, "roles", "admin", granted: true);
        RolePermissionService service = CreateService(db);

        RolePermissionUpdateResult result = await service.UpdateRolePermissionsAsync(
            role.Id,
            new UpdateRolePermissionsRequestDto { UpdatedAt = role.UpdatedAt, Permissions = [] },
            Guid.NewGuid(),
            "127.0.0.1");

        result.Should().BeOfType<RolePermissionUpdateResult.LockoutViolation>()
            .Which.Permissions.Should().ContainSingle().Which.Should().Be("roles:admin");
    }

    [Fact]
    public async Task UpdateRolePermissionsAsync_RemovingRolesAdmin_WhenAnotherActiveRoleHoldsIt_Succeeds()
    {
        await using AppDbContext db = await CreateContextAsync();
        await SeedCatalogAsync(db, "roles", "Roles", "admin", "Admin");
        Role role = await CreateRoleAsync(db, "operator");
        Role otherRole = await CreateRoleAsync(db, "super_operator");
        await GrantAsync(db, role, "roles", "admin", granted: true);
        await GrantAsync(db, otherRole, "roles", "admin", granted: true);
        SetupNoOpRevocationAndAudit();
        RolePermissionService service = CreateService(db);

        RolePermissionUpdateResult result = await service.UpdateRolePermissionsAsync(
            role.Id,
            new UpdateRolePermissionsRequestDto { UpdatedAt = role.UpdatedAt, Permissions = [] },
            Guid.NewGuid(),
            "127.0.0.1");

        result.Should().BeOfType<RolePermissionUpdateResult.Success>();
    }

    [Fact]
    public async Task UpdateRolePermissionsAsync_RemovingRolesAdmin_WhenOnlyOtherHolderIsInactive_ReturnsLockoutViolation()
    {
        await using AppDbContext db = await CreateContextAsync();
        await SeedCatalogAsync(db, "roles", "Roles", "admin", "Admin");
        Role role = await CreateRoleAsync(db, "operator");
        Role inactiveRole = await CreateRoleAsync(db, "retired_role", isActive: false);
        await GrantAsync(db, role, "roles", "admin", granted: true);
        await GrantAsync(db, inactiveRole, "roles", "admin", granted: true);
        RolePermissionService service = CreateService(db);

        RolePermissionUpdateResult result = await service.UpdateRolePermissionsAsync(
            role.Id,
            new UpdateRolePermissionsRequestDto { UpdatedAt = role.UpdatedAt, Permissions = [] },
            Guid.NewGuid(),
            "127.0.0.1");

        result.Should().BeOfType<RolePermissionUpdateResult.LockoutViolation>();
    }

    [Fact]
    public async Task UpdateRolePermissionsAsync_NoOpRequest_ReturnsSuccessWithoutAuditOrRevocationOrUpdatedAtChange()
    {
        await using AppDbContext db = await CreateContextAsync();
        await SeedCatalogAsync(db, "queue", "Calibration Queue", "read", "Read");
        Role role = await CreateRoleAsync(db, "operator");
        await GrantAsync(db, role, "queue", "read", granted: true);
        DateTime originalUpdatedAt = role.UpdatedAt;
        RolePermissionService service = CreateService(db);

        RolePermissionUpdateResult result = await service.UpdateRolePermissionsAsync(
            role.Id,
            new UpdateRolePermissionsRequestDto { UpdatedAt = role.UpdatedAt, Permissions = [QueueReadPermission] },
            Guid.NewGuid(),
            "127.0.0.1");

        RolePermissionUpdateResult.Success success = result.Should().BeOfType<RolePermissionUpdateResult.Success>().Subject;
        success.Response.RevokedSessionCount.Should().Be(0);

        Role? persisted = await db.Roles.AsNoTracking().FirstOrDefaultAsync(r => r.Id == role.Id);
        persisted!.UpdatedAt.Should().Be(originalUpdatedAt);

        _authAuditServiceMock.VerifyNoOtherCalls();
        _tokenRevocationServiceMock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task UpdateRolePermissionsAsync_HappyPath_AppliesDiff_RevokesSessions_AndAudits()
    {
        await using AppDbContext db = await CreateContextAsync();
        await SeedCatalogAsync(db, "queue", "Calibration Queue", "read", "Read");
        await SeedCatalogAsync(db, "queue", "Calibration Queue", "write", "Write");
        Role role = await CreateRoleAsync(db, "operator");
        await GrantAsync(db, role, "queue", "read", granted: true);

        Guid activeUserWithTokens = Guid.NewGuid();
        Guid activeUserNoTokens = Guid.NewGuid();
        await AssignUserToRoleAsync(db, activeUserWithTokens, role, isActive: true);
        await AssignUserToRoleAsync(db, activeUserNoTokens, role, isActive: true);
        await AssignUserToRoleAsync(db, Guid.NewGuid(), role, isActive: false); // inactive assignment, must not be revoked

        Guid actingUserId = Guid.NewGuid();
        DateTime updatedAtBeforeChange = role.UpdatedAt;

        _tokenRevocationServiceMock
            .Setup(s => s.RevokeAllUserTokensAsync(activeUserWithTokens, actingUserId, It.IsAny<string>(), "10.0.0.1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(2);
        _tokenRevocationServiceMock
            .Setup(s => s.RevokeAllUserTokensAsync(activeUserNoTokens, actingUserId, It.IsAny<string>(), "10.0.0.1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);
        _authAuditServiceMock
            .Setup(s => s.LogRolePermissionsChangedAsync(
                actingUserId,
                role.Id,
                role.Name,
                It.Is<IReadOnlyList<string>>(added => added.SequenceEqual(new[] { QueueWritePermission })),
                It.Is<IReadOnlyList<string>>(removed => removed.SequenceEqual(new[] { QueueReadPermission })),
                1,
                "10.0.0.1",
                null,
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        RolePermissionService service = CreateService(db);

        RolePermissionUpdateResult result = await service.UpdateRolePermissionsAsync(
            role.Id,
            new UpdateRolePermissionsRequestDto { UpdatedAt = updatedAtBeforeChange, Permissions = [QueueWritePermission] },
            actingUserId,
            "10.0.0.1");

        RolePermissionUpdateResult.Success success = result.Should().BeOfType<RolePermissionUpdateResult.Success>().Subject;
        success.Response.RevokedSessionCount.Should().Be(1);

        Dictionary<string, RolePermissionGrantStatus> statusByPermission = success.Response.Role.Resources
            .SelectMany(g => g.Permissions)
            .ToDictionary(p => p.Permission, p => p.Status);
        statusByPermission[QueueReadPermission].Should().Be(RolePermissionGrantStatus.Absent);
        statusByPermission[QueueWritePermission].Should().Be(RolePermissionGrantStatus.Granted);

        Role? persisted = await db.Roles.AsNoTracking().FirstOrDefaultAsync(r => r.Id == role.Id);
        persisted!.UpdatedAt.Should().BeAfter(updatedAtBeforeChange);

        _tokenRevocationServiceMock.Verify(
            s => s.RevokeAllUserTokensAsync(activeUserWithTokens, actingUserId, It.IsAny<string>(), "10.0.0.1", It.IsAny<CancellationToken>()),
            Times.Once);
        _tokenRevocationServiceMock.Verify(
            s => s.RevokeAllUserTokensAsync(activeUserNoTokens, actingUserId, It.IsAny<string>(), "10.0.0.1", It.IsAny<CancellationToken>()),
            Times.Once);
        _authAuditServiceMock.VerifyAll();
    }

    [Fact]
    public async Task UpdateRolePermissionsAsync_HappyPath_ChangeIsVisibleToJwtPermissionDerivation()
    {
        // Proves AC1 ("takes effect on next token issue") against the actual production code
        // path AuthenticationService.GenerateJwtTokenAsync uses to derive JWT permission
        // claims, rather than just asserting RolePermission row state.
        await using AppDbContext db = await CreateContextAsync();
        await SeedCatalogAsync(db, "queue", "Calibration Queue", "read", "Read");
        await SeedCatalogAsync(db, "queue", "Calibration Queue", "write", "Write");
        Role role = await CreateRoleAsync(db, "operator");
        await GrantAsync(db, role, "queue", "read", granted: true);

        Guid userId = Guid.NewGuid();
        await AssignUserToRoleAsync(db, userId, role, isActive: true);
        SetupNoOpRevocationAndAudit();
        RolePermissionService service = CreateService(db);

        RolePermissionUpdateResult result = await service.UpdateRolePermissionsAsync(
            role.Id,
            new UpdateRolePermissionsRequestDto { UpdatedAt = role.UpdatedAt, Permissions = [QueueWritePermission] },
            Guid.NewGuid(),
            "127.0.0.1");

        result.Should().BeOfType<RolePermissionUpdateResult.Success>();

        EfUsersRepository usersRepository = new(db);
        List<(string Resource, string Action)> grantedPermissions = await usersRepository.GetGrantedPermissionsAsync(userId);

        grantedPermissions.Should().ContainSingle(p => p.Resource == "queue" && p.Action == "write");
        grantedPermissions.Should().NotContain(p => p.Resource == "queue" && p.Action == "read");
    }

    [Fact]
    public async Task UpdateRolePermissionsAsync_RemovingLastActiveRoleHoldingUsersAdmin_ReturnsLockoutViolation()
    {
        await using AppDbContext db = await CreateContextAsync();
        await SeedCatalogAsync(db, "users", "Users", "admin", "Admin");
        Role role = await CreateRoleAsync(db, "operator");
        await GrantAsync(db, role, "users", "admin", granted: true);
        RolePermissionService service = CreateService(db);

        RolePermissionUpdateResult result = await service.UpdateRolePermissionsAsync(
            role.Id,
            new UpdateRolePermissionsRequestDto { UpdatedAt = role.UpdatedAt, Permissions = [] },
            Guid.NewGuid(),
            "127.0.0.1");

        result.Should().BeOfType<RolePermissionUpdateResult.LockoutViolation>()
            .Which.Permissions.Should().ContainSingle().Which.Should().Be("users:admin");
    }

    [Fact]
    public async Task UpdateRolePermissionsAsync_RemovingUsersAdmin_WhenAnotherActiveRoleHoldsIt_Succeeds()
    {
        await using AppDbContext db = await CreateContextAsync();
        await SeedCatalogAsync(db, "users", "Users", "admin", "Admin");
        Role role = await CreateRoleAsync(db, "operator");
        Role otherRole = await CreateRoleAsync(db, "super_operator");
        await GrantAsync(db, role, "users", "admin", granted: true);
        await GrantAsync(db, otherRole, "users", "admin", granted: true);
        SetupNoOpRevocationAndAudit();
        RolePermissionService service = CreateService(db);

        RolePermissionUpdateResult result = await service.UpdateRolePermissionsAsync(
            role.Id,
            new UpdateRolePermissionsRequestDto { UpdatedAt = role.UpdatedAt, Permissions = [] },
            Guid.NewGuid(),
            "127.0.0.1");

        result.Should().BeOfType<RolePermissionUpdateResult.Success>();
    }

    [Fact]
    public async Task UpdateRolePermissionsAsync_RemovingUsersAdmin_WhenOnlyOtherHolderIsInactive_ReturnsLockoutViolation()
    {
        await using AppDbContext db = await CreateContextAsync();
        await SeedCatalogAsync(db, "users", "Users", "admin", "Admin");
        Role role = await CreateRoleAsync(db, "operator");
        Role inactiveRole = await CreateRoleAsync(db, "retired_role", isActive: false);
        await GrantAsync(db, role, "users", "admin", granted: true);
        await GrantAsync(db, inactiveRole, "users", "admin", granted: true);
        RolePermissionService service = CreateService(db);

        RolePermissionUpdateResult result = await service.UpdateRolePermissionsAsync(
            role.Id,
            new UpdateRolePermissionsRequestDto { UpdatedAt = role.UpdatedAt, Permissions = [] },
            Guid.NewGuid(),
            "127.0.0.1");

        result.Should().BeOfType<RolePermissionUpdateResult.LockoutViolation>();
    }

    [Fact]
    public async Task UpdateRolePermissionsAsync_NonCatalogGrant_SurvivesUntouchedWhenNotResubmitted()
    {
        // roles:admin/users:admin are not yet catalog-enforced (FR-4 is separate future work),
        // so a client's request payload can never include them. A naive full-replacement diff
        // would delete this row simply because the client didn't (couldn't) resubmit it. This
        // proves the fix: only catalog-visible existing grants participate in the replacement.
        await using AppDbContext db = await CreateContextAsync();
        await SeedCatalogAsync(db, "queue", "Calibration Queue", "read", "Read");
        Role role = await CreateRoleAsync(db, "operator");
        await GrantAsync(db, role, "queue", "read", granted: true);

        // Seed roles:admin/users:admin resource+action rows directly (bypassing SeedCatalogAsync
        // so no enforced endpoint -- and therefore no catalog entry -- is registered for them).
        Resource rolesResource = new() { Id = Guid.NewGuid(), Name = "roles", DisplayName = "Roles", ResourceType = "test", IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        UserAction adminAction = new() { Id = Guid.NewGuid(), Name = "admin", DisplayName = "Admin", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.Resources.Add(rolesResource);
        db.UserActions.Add(adminAction);
        await db.SaveChangesAsync();
        db.RolePermissions.Add(new RolePermission
        {
            Id = Guid.NewGuid(),
            RoleId = role.Id,
            ResourceId = rolesResource.Id,
            ActionId = adminAction.Id,
            Granted = true,
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        SetupNoOpRevocationAndAudit();
        RolePermissionService service = CreateService(db);

        // Client's request never mentions roles:admin -- it isn't in the catalog, so the client
        // has no way to see or resubmit it. Re-request the one catalog-visible grant unchanged.
        RolePermissionUpdateResult result = await service.UpdateRolePermissionsAsync(
            role.Id,
            new UpdateRolePermissionsRequestDto { UpdatedAt = role.UpdatedAt, Permissions = [QueueReadPermission] },
            Guid.NewGuid(),
            "127.0.0.1");

        result.Should().BeOfType<RolePermissionUpdateResult.Success>();

        bool rolesAdminStillGranted = await db.RolePermissions
            .AsNoTracking()
            .AnyAsync(rp => rp.RoleId == role.Id && rp.ResourceId == rolesResource.Id && rp.ActionId == adminAction.Id && rp.Granted);
        rolesAdminStillGranted.Should().BeTrue("a non-catalog grant the client could never resubmit must not be silently stripped");
    }

    [Fact]
    public async Task UpdateRolePermissionsAsync_ConcurrentUpdatesWithSameStaleUpdatedAt_OnlyOneSucceeds()
    {
        // Proves the DB-level concurrency token (Role.UpdatedAt IsConcurrencyToken) actually
        // rejects the second of two writers racing on the same stale UpdatedAt, rather than
        // silently letting the second overwrite the first (Bishop's/Vasquez's review finding).
        await using AppDbContext db = await CreateContextAsync();
        await SeedCatalogAsync(db, "queue", "Calibration Queue", "read", "Read");
        await SeedCatalogAsync(db, "queue", "Calibration Queue", "write", "Write");
        Role role = await CreateRoleAsync(db, "operator");
        DateTime staleUpdatedAt = role.UpdatedAt;
        SetupNoOpRevocationAndAudit();

        RolePermissionService serviceA = CreateService(db);
        await using AppDbContext dbB = await CreateContextAsync();
        RolePermissionService serviceB = new(dbB, new PermissionCatalogService(new FakeEndpointDataSource(_endpoints), dbB), _authAuditServiceMock.Object, _revocationService);

        RolePermissionUpdateResult resultA = await serviceA.UpdateRolePermissionsAsync(
            role.Id,
            new UpdateRolePermissionsRequestDto { UpdatedAt = staleUpdatedAt, Permissions = [QueueReadPermission] },
            Guid.NewGuid(),
            "127.0.0.1");

        RolePermissionUpdateResult resultB = await serviceB.UpdateRolePermissionsAsync(
            role.Id,
            new UpdateRolePermissionsRequestDto { UpdatedAt = staleUpdatedAt, Permissions = [QueueWritePermission] },
            Guid.NewGuid(),
            "127.0.0.1");

        resultA.Should().BeOfType<RolePermissionUpdateResult.Success>("the first writer to commit against the stale UpdatedAt should win");
        resultB.Should().BeOfType<RolePermissionUpdateResult.ConcurrencyConflict>(
            "the second writer read the same stale UpdatedAt already consumed by the first writer's commit");
    }

    private void AddEnforcedPermissionEndpoint(string resourceName, string actionName)
    {
        string template = $"api/test/{resourceName}/{actionName}";
        RouteEndpointBuilder builder = new(
            requestDelegate: _ => Task.CompletedTask,
            routePattern: RoutePatternFactory.Parse(template),
            order: 0);
        builder.Metadata.Add(new HttpMethodMetadata(["GET"]));
        builder.Metadata.Add(new RequirePermissionAttribute(resourceName, actionName));
        _endpoints.Add((RouteEndpoint)builder.Build());
    }

    private void SetupNoOpRevocationAndAudit()
    {
        _tokenRevocationServiceMock
            .Setup(s => s.RevokeAllUserTokensAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);
        _authAuditServiceMock
            .Setup(s => s.LogRolePermissionsChangedAsync(
                It.IsAny<Guid>(),
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<int>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    private RolePermissionService CreateService(AppDbContext db) =>
        new(db, new PermissionCatalogService(new FakeEndpointDataSource(_endpoints), db), _authAuditServiceMock.Object, _revocationService);

    private async Task<AppDbContext> CreateContextAsync()
    {
        AppDbContext context = new(_options);
        _ = await context.Database.EnsureCreatedAsync();
        return context;
    }

    private async Task SeedCatalogAsync(AppDbContext db, string resourceName, string resourceDisplayName, string actionName, string actionDisplayName)
    {
        if (!await db.Resources.AnyAsync(r => r.Name == resourceName))
        {
            db.Resources.Add(new Resource
            {
                Id = Guid.NewGuid(),
                Name = resourceName,
                DisplayName = resourceDisplayName,
                ResourceType = "test",
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
        }

        if (!await db.UserActions.AnyAsync(a => a.Name == actionName))
        {
            db.UserActions.Add(new UserAction
            {
                Id = Guid.NewGuid(),
                Name = actionName,
                DisplayName = actionDisplayName,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
        }

        await db.SaveChangesAsync();

        // The permission catalog only reports permissions actually enforced by a routed
        // endpoint, so register a fake endpoint carrying the matching [RequirePermission]
        // attribute alongside the seeded resource/action display metadata.
        AddEnforcedPermissionEndpoint(resourceName, actionName);
    }

    private static async Task<Role> CreateRoleAsync(AppDbContext db, string name, bool isActive = true)
    {
        Role role = new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            DisplayName = name,
            IsSystemRole = false,
            IsActive = isActive,
            CreatedAt = DateTime.UtcNow.AddMinutes(-1),
            // Backdated so tests asserting UpdatedAt advances aren't flaky against clock resolution.
            UpdatedAt = DateTime.UtcNow.AddMinutes(-1),
        };
        db.Roles.Add(role);
        await db.SaveChangesAsync();
        return role;
    }

    private static async Task GrantAsync(AppDbContext db, Role role, string resourceName, string actionName, bool granted)
    {
        Resource resource = await db.Resources.FirstAsync(r => r.Name == resourceName);
        UserAction action = await db.UserActions.FirstAsync(a => a.Name == actionName);

        db.RolePermissions.Add(new RolePermission
        {
            Id = Guid.NewGuid(),
            RoleId = role.Id,
            ResourceId = resource.Id,
            ActionId = action.Id,
            Granted = granted,
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private static async Task AssignUserToRoleAsync(AppDbContext db, Guid userId, Role role, bool isActive)
    {
        db.Users.Add(new User
        {
            Id = userId,
            Username = $"user-{userId:N}",
            Email = $"{userId:N}@example.test",
            PasswordHash = "hash",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        db.UserRoles.Add(new UserRole
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            RoleId = role.Id,
            AssignedAt = DateTime.UtcNow,
            IsActive = isActive,
        });
        await db.SaveChangesAsync();
    }

    private sealed class FakeEndpointDataSource(IReadOnlyList<Endpoint> endpoints) : EndpointDataSource
    {
        public override IReadOnlyList<Endpoint> Endpoints { get; } = endpoints;

        public override IChangeToken GetChangeToken() => NullChangeToken.Instance;
    }

    private sealed class NullChangeToken : IChangeToken
    {
        public static readonly NullChangeToken Instance = new();

        public bool HasChanged => false;

        public bool ActiveChangeCallbacks => false;

        public IDisposable RegisterChangeCallback(Action<object?> callback, object? state) => NullDisposable.Instance;

        private sealed class NullDisposable : IDisposable
        {
            public static readonly NullDisposable Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
