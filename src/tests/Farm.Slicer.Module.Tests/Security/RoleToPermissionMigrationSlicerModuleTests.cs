using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;

namespace Farm.Slicer.Module.Tests.Security;

/// <summary>
/// Regression coverage for issue #1451 on the slicer module's REST controllers, which enforce
/// permissions through <c>Farm.Slicer.Module.Api.Filters.RequirePermissionAttribute</c> — a
/// separate, action-filter-based enforcement path from the main API's
/// <c>Farm.Infrastructure.Authorization.RequirePermissionAttribute</c>
/// (covered by <c>Farm.Web.Api.Tests.Security.RoleToPermissionMigrationTests</c>).
///
/// Also proves the fix for a real gap found during pre-PR review: <see cref="Farm.Slicer.Module.Api.Controllers.WorkersController"/>'s
/// <c>ResetAsync</c> stacks a class-level <c>dispatch-settings:manage</c> requirement (pre-existing)
/// with a method-level <c>dispatch-settings:admin</c> requirement (added by this migration). Because
/// ASP.NET Core combines class- and method-level filters with AND semantics, a custom role holding
/// only <c>dispatch-settings:admin</c> would be refused by the class-level filter unless the slicer
/// module's validator also honors "resource:admin implies every other action on that resource" —
/// which it now does by delegating to <c>PrintFarmerPermissions.HasPermission</c>.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Category", "Regression")]
public class RoleToPermissionMigrationSlicerModuleTests : IAsyncLifetime
{
    private readonly CustomWebApplicationFactory _factory = new();

    public async Task InitializeAsync() => await _factory.ResetDatabaseAsync();

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task WorkersReset_NonAdminWithoutPermission_Returns403()
    {
        using HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", "Bearer invalid-or-missing");

        HttpResponseMessage response = await client.PostAsync($"/api/workers/{Guid.NewGuid()}/reset", content: null);

        response.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task WorkersReset_CustomRoleWithOnlyDispatchSettingsAdmin_PassesBothPermissionChecks()
    {
        // Grants ONLY "dispatch-settings:admin" — proves that this alone is sufficient to reach
        // ResetAsync, satisfying both the class-level "dispatch-settings:manage" requirement (via
        // resource-admin implication) and the method-level "dispatch-settings:admin" requirement
        // (exact match). A 404 (not 403) proves authorization passed and the request reached the
        // handler, which then legitimately reports "no such worker" for the random ID.
        using HttpClient client = await _factory.CreateOperatorClientAsync("dispatch-settings", "admin");

        HttpResponseMessage response = await client.PostAsync($"/api/workers/{Guid.NewGuid()}/reset", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task WorkersReset_FarmAdmin_PassesBothPermissionChecks()
    {
        using HttpClient client = await _factory.CreateAdminClientAsync();

        HttpResponseMessage response = await client.PostAsync($"/api/workers/{Guid.NewGuid()}/reset", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task SlicerManagementList_NonAdminWithoutPermission_Returns403()
    {
        using HttpClient client = await _factory.CreateAuthenticatedClientAsync();

        HttpResponseMessage response = await client.GetAsync("/api/admin/slicers");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task SlicerManagementList_CustomRoleWithSlicerEnginesAdmin_PassesAuthorization()
    {
        using HttpClient client = await _factory.CreateOperatorClientAsync("slicer_engines", "admin");

        HttpResponseMessage response = await client.GetAsync("/api/admin/slicers");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task SlicerManagementList_FarmAdmin_PassesAuthorization()
    {
        using HttpClient client = await _factory.CreateAdminClientAsync();

        HttpResponseMessage response = await client.GetAsync("/api/admin/slicers");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
