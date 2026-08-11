using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace Farm.Web.Api.Tests.Security;

/// <summary>
/// Regression coverage for issue #1451: 167 <c>[Authorize(Roles = "farm_admin")]</c> sites were
/// migrated to <c>[RequirePermission("&lt;resource&gt;", "admin")]</c>. This proves, for a
/// representative sample spanning both newly-introduced resources and one already-existing
/// resource reused by a pre-migration site, that:
/// <list type="bullet">
/// <item>a caller without the resource's <c>admin</c> permission is refused (403), and</item>
/// <item>a non-admin caller granted exactly that permission is let through — i.e. a custom
/// role can now reach these endpoints without being named <c>farm_admin</c>, and</item>
/// <item>farm_admin still reaches every one of them, unconditionally.</item>
/// </list>
/// </summary>
public sealed class RoleToPermissionMigrationTests : IAsyncLifetime
{
    private readonly CustomWebApplicationFactory _factory = new(
        new Dictionary<string, string?>
        {
            ["Testing:UseTestAuthentication"] = "true",
            ["Security:DevModeBypassAuth"] = "false",
        });

    public static TheoryData<string, string> MigratedRouteCases => new()
    {
        // resource that already existed pre-migration, but was gated at a method (not
        // class) level by the mechanical migration script — proves the script also
        // handled reused-resource, method-level sites correctly.
        { "/api/quotas", "quota:admin" },

        // newly-introduced resources (class-level [RequirePermission], so the endpoint's
        // gate is unambiguous), one per distinct controller/module gated by this migration.
        { "/api/webhooks", "webhooks:admin" },
        { "/api/services", "background_services:admin" },
        { "/api/maintenance/alerts", "maintenance:admin" },
        { "/api/admin/power-monitors", "power_monitors:admin" },
        { "/api/admin/integrations/home-assistant/settings", "home_assistant:admin" },
        { "/api/admin/integrations/telegram/settings", "telegram:admin" },

        // the AdminDataController quirk fix: previously an unseeded "admin" resource with
        // an "execute" action, now the seeded data_management:admin permission.
        { "/api/admin/data/export/catalog", "data_management:admin" },
    };

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Theory]
    [MemberData(nameof(MigratedRouteCases))]
    public async Task MigratedRoute_NonAdminWithoutPermission_ReturnsPermissionDenied(
        string route,
        string requiredPermission)
    {
        _ = requiredPermission;
        using HttpClient client = CreateOperatorClient();

        HttpResponseMessage response = await client.GetAsync(route);
        string body = await response.Content.ReadAsStringAsync();

        _ = response.StatusCode.Should().Be(HttpStatusCode.Forbidden, body);
        using JsonDocument document = JsonDocument.Parse(body);
        _ = document.RootElement.GetProperty("code").GetString()
            .Should().Be("permission_denied");
    }

    [Theory]
    [MemberData(nameof(MigratedRouteCases))]
    public async Task MigratedRoute_CustomRoleWithExactResourceAdminPermission_PassesAuthorization(
        string route,
        string requiredPermission)
    {
        // A custom role — deliberately not named "farm_admin" — granted only the single
        // resource:admin permission this endpoint requires. This is the behavior D2 exists
        // to unlock: reach without the farm_admin name.
        using HttpClient client = CreateOperatorClient(requiredPermission);

        HttpResponseMessage response = await client.GetAsync(route);

        _ = response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
        _ = response.StatusCode.Should().NotBe(HttpStatusCode.Forbidden);
    }

    [Theory]
    [MemberData(nameof(MigratedRouteCases))]
    public async Task MigratedRoute_FarmAdministrator_BypassesPermission(
        string route,
        string requiredPermission)
    {
        _ = requiredPermission;
        // Default test identity (no X-Test-Roles header) is farm_admin — proves farm_admin's
        // reach is unchanged by the migration.
        using HttpClient client = _factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync(route);

        _ = response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
        _ = response.StatusCode.Should().NotBe(HttpStatusCode.Forbidden);
    }

    private HttpClient CreateOperatorClient(params string[] permissions)
    {
        HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Id", Guid.NewGuid().ToString());
        client.DefaultRequestHeaders.Add("X-Test-Roles", "operator");
        if (permissions.Length > 0)
        {
            client.DefaultRequestHeaders.Add("X-Test-Permissions", string.Join(',', permissions));
        }

        return client;
    }
}
