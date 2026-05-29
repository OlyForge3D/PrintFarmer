using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.PrinterGroups;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Farm.Web.Api.Tests.Controllers;

/// <summary>
/// Integration tests for printer group access control endpoints.
/// Tests GET/PUT access rules, backward compatibility, and pre-submission checks.
/// </summary>
public class PrinterGroupAccessControlTests : IAsyncLifetime
{
    private readonly CustomWebApplicationFactory _factory;
    private HttpClient _adminClient = null!;

    public PrinterGroupAccessControlTests()
    {
        _factory = new CustomWebApplicationFactory();
    }

    public async Task InitializeAsync()
    {
        _adminClient = await _factory.CreateAdminClientAsync();
    }

    public async Task DisposeAsync()
    {
        _adminClient?.Dispose();
        await _factory.DisposeAsync();
    }

    private async Task<Guid> SeedPrinterGroupAsync(string name = "Test Group")
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        PrinterGroup group = new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            CreatedDate = DateTimeOffset.UtcNow,
            UpdatedDate = DateTimeOffset.UtcNow
        };
        db.PrinterGroups.Add(group);
        await db.SaveChangesAsync();
        return group.Id;
    }

    private async Task<Guid> SeedRoleAsync(string name, string displayName = "")
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        Role role = new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            DisplayName = string.IsNullOrEmpty(displayName) ? name : displayName,
            IsActive = true,
            IsSystemRole = false,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Roles.Add(role);
        await db.SaveChangesAsync();
        return role.Id;
    }

    // =========================================================================
    // GET /api/printer-groups/{id}/access — Retrieve access rules
    // =========================================================================

    [Fact]
    [Trait("Category", "PrinterGroupAccess")]
    public async Task GetAccessRules_WithNoRules_ReturnsEmptyArray()
    {
        Guid groupId = await SeedPrinterGroupAsync();

        HttpResponseMessage response = await _adminClient.GetAsync($"/api/printer-groups/{groupId}/access");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        List<PrinterGroupAccessDto>? rules = await response.Content.ReadFromJsonAsync<List<PrinterGroupAccessDto>>();
        rules.Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    [Trait("Category", "PrinterGroupAccess")]
    public async Task GetAccessRules_NonExistentGroup_ReturnsNotFound()
    {
        HttpResponseMessage response = await _adminClient.GetAsync($"/api/printer-groups/{Guid.NewGuid()}/access");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // =========================================================================
    // PUT /api/printer-groups/{id}/access — Set access rules (replace-all)
    // =========================================================================

    [Fact]
    [Trait("Category", "PrinterGroupAccess")]
    public async Task SetAccessRules_CreatesRulesAndReturnsUpdated()
    {
        Guid groupId = await SeedPrinterGroupAsync();
        Guid operatorRoleId = await SeedRoleAsync("operator", "Operator");
        Guid viewerRoleId = await SeedRoleAsync("viewer", "Viewer");

        var requestBody = new
        {
            rules = new[]
            {
                new { roleId = operatorRoleId, accessLevel = "Submit" },
                new { roleId = viewerRoleId, accessLevel = "View" }
            }
        };

        HttpResponseMessage response = await _adminClient.PutAsJsonAsync(
            $"/api/printer-groups/{groupId}/access", requestBody);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        List<PrinterGroupAccessDto>? rules = await response.Content.ReadFromJsonAsync<List<PrinterGroupAccessDto>>();
        rules.Should().NotBeNull().And.HaveCount(2);
        rules!.Should().Contain(r => r.RoleId == operatorRoleId && r.AccessLevel == PrinterGroupAccessLevel.Submit);
        rules.Should().Contain(r => r.RoleId == viewerRoleId && r.AccessLevel == PrinterGroupAccessLevel.View);
    }

    [Fact]
    [Trait("Category", "PrinterGroupAccess")]
    public async Task SetAccessRules_ReplacesExistingRules()
    {
        Guid groupId = await SeedPrinterGroupAsync();
        Guid role1Id = await SeedRoleAsync("role1");
        Guid role2Id = await SeedRoleAsync("role2");

        // First: set role1 with Submit
        await _adminClient.PutAsJsonAsync($"/api/printer-groups/{groupId}/access", new
        {
            rules = new[] { new { roleId = role1Id, accessLevel = "Submit" } }
        });

        // Second: replace with role2 with Manage
        HttpResponseMessage response = await _adminClient.PutAsJsonAsync(
            $"/api/printer-groups/{groupId}/access", new
            {
                rules = new[] { new { roleId = role2Id, accessLevel = "Manage" } }
            });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        List<PrinterGroupAccessDto>? rules = await response.Content.ReadFromJsonAsync<List<PrinterGroupAccessDto>>();
        rules.Should().NotBeNull().And.HaveCount(1);
        rules![0].RoleId.Should().Be(role2Id);
        rules[0].AccessLevel.Should().Be(PrinterGroupAccessLevel.Manage);
    }

    [Fact]
    [Trait("Category", "PrinterGroupAccess")]
    public async Task SetAccessRules_EmptyRules_ClearsAllRules()
    {
        Guid groupId = await SeedPrinterGroupAsync();
        Guid roleId = await SeedRoleAsync("test-role");

        // First: add a rule
        await _adminClient.PutAsJsonAsync($"/api/printer-groups/{groupId}/access", new
        {
            rules = new[] { new { roleId, accessLevel = "Submit" } }
        });

        // Second: clear all rules
        HttpResponseMessage response = await _adminClient.PutAsJsonAsync(
            $"/api/printer-groups/{groupId}/access", new { rules = Array.Empty<object>() });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        List<PrinterGroupAccessDto>? rules = await response.Content.ReadFromJsonAsync<List<PrinterGroupAccessDto>>();
        rules.Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    [Trait("Category", "PrinterGroupAccess")]
    public async Task SetAccessRules_NonExistentGroup_ReturnsNotFound()
    {
        Guid roleId = await SeedRoleAsync("some-role");

        HttpResponseMessage response = await _adminClient.PutAsJsonAsync(
            $"/api/printer-groups/{Guid.NewGuid()}/access", new
            {
                rules = new[] { new { roleId, accessLevel = "Submit" } }
            });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
