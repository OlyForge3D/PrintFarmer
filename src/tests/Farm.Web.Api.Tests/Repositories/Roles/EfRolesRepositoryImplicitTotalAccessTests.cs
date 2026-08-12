using Farm.Infrastructure.Contracts.Roles;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Roles;
using Farm.Infrastructure.Security;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Farm.Web.Api.Tests.Repositories.Roles;

/// <summary>
/// Covers <see cref="RoleSummaryDto.HasImplicitTotalAccess"/> on both repository read paths
/// (issue #1490).
///
/// The role list previously rendered <c>PermissionCount</c> — a raw count of granted
/// <c>RolePermission</c> rows — for every role. For <c>farm_admin</c> that is one row per resource
/// (<c>{resource}:admin</c>), so a role that effectively holds every permission displayed a small
/// partial number, reading as though the administrator were missing access.
///
/// These tests exist because the flag is otherwise unverified end to end: unlike
/// <c>RolePermissionsDto</c>, <see cref="RoleSummaryDto"/> uses plain settable properties rather
/// than <c>required</c> init, so the compiler cannot catch a construction site that omits the
/// field — it silently defaults to <c>false</c>, which is exactly the bug state. Without a test at
/// this layer, deleting the assignment in <see cref="EfRolesRepository"/> leaves the whole suite
/// green while the original user-visible symptom returns.
/// </summary>
public sealed class EfRolesRepositoryImplicitTotalAccessTests : IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public EfRolesRepositoryImplicitTotalAccessTests()
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

    private static async Task<Guid> CreateRoleAsync(AppDbContext context, string name, bool isSystemRole)
    {
        Role role = new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            DisplayName = name,
            IsSystemRole = isSystemRole,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _ = context.Roles.Add(role);
        await context.SaveChangesAsync();
        return role.Id;
    }

    [Fact]
    public async Task GetRoleSummariesAsync_SetsImplicitTotalAccess_OnlyForFarmAdmin()
    {
        await using AppDbContext context = await CreateContextAsync();
        _ = await CreateRoleAsync(context, PrintFarmerPermissions.FarmAdminRole, isSystemRole: true);
        _ = await CreateRoleAsync(context, "farm_user", isSystemRole: true);
        _ = await CreateRoleAsync(context, "shift_lead", isSystemRole: false);

        EfRolesRepository repository = new(context);

        List<RoleSummaryDto> summaries = await repository.GetRoleSummariesAsync();

        summaries.Single(r => r.Name == PrintFarmerPermissions.FarmAdminRole)
            .HasImplicitTotalAccess.Should().BeTrue();

        // A second system role must NOT inherit the flag: implicit total access is specific to
        // farm_admin's hard-coded role bypass, not a property of system roles in general.
        summaries.Single(r => r.Name == "farm_user").HasImplicitTotalAccess.Should().BeFalse();
        summaries.Single(r => r.Name == "shift_lead").HasImplicitTotalAccess.Should().BeFalse();
    }

    [Fact]
    public async Task GetRoleDetailAsync_SetsImplicitTotalAccess_OnlyForFarmAdmin()
    {
        await using AppDbContext context = await CreateContextAsync();
        Guid adminRoleId = await CreateRoleAsync(context, PrintFarmerPermissions.FarmAdminRole, isSystemRole: true);
        Guid customRoleId = await CreateRoleAsync(context, "shift_lead", isSystemRole: false);

        EfRolesRepository repository = new(context);

        RoleDetailDto? admin = await repository.GetRoleDetailAsync(adminRoleId);
        RoleDetailDto? custom = await repository.GetRoleDetailAsync(customRoleId);

        admin.Should().NotBeNull();
        admin!.HasImplicitTotalAccess.Should().BeTrue();

        custom.Should().NotBeNull();
        custom!.HasImplicitTotalAccess.Should().BeFalse();
    }

    [Fact]
    public async Task BothReadPaths_AgreeOnImplicitTotalAccess_ForTheSameRole()
    {
        // GetRoleSummariesAsync computes the flag in an EF projection translated to SQL equality,
        // while GetRoleDetailAsync evaluates it in memory with StringComparison.Ordinal. Pin the
        // two together so the list badge and the detail view can never disagree about a role.
        await using AppDbContext context = await CreateContextAsync();
        Guid adminRoleId = await CreateRoleAsync(context, PrintFarmerPermissions.FarmAdminRole, isSystemRole: true);
        Guid customRoleId = await CreateRoleAsync(context, "shift_lead", isSystemRole: false);

        EfRolesRepository repository = new(context);

        List<RoleSummaryDto> summaries = await repository.GetRoleSummariesAsync();

        foreach (Guid roleId in new[] { adminRoleId, customRoleId })
        {
            RoleDetailDto? detail = await repository.GetRoleDetailAsync(roleId);
            detail.Should().NotBeNull();
            summaries.Single(s => s.Id == roleId)
                .HasImplicitTotalAccess.Should().Be(detail!.HasImplicitTotalAccess);
        }
    }
}
