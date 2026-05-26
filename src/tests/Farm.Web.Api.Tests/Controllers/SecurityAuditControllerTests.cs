using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Authentication;
using Farm.Web.Api.Controllers.Admin;
using Farm.Web.Api.Tests.TestInfrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Farm.Web.Api.Tests.Controllers;

/// <summary>
/// Integration tests for <see cref="SecurityAuditController"/>.
/// </summary>
[Trait("Category", "Integration")]
[Collection(IntegrationTestCollection.Name)]
public class SecurityAuditControllerTests : IAsyncLifetime
{
    private readonly CustomWebApplicationFactory _factory;
    private HttpClient? _adminClient;
    private HttpClient? _nonAdminClient;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public SecurityAuditControllerTests()
    {
        // Disable DevModeBypassAuth so auth tests (401/403) behave correctly regardless
        // of the local appsettings.Development.json setting. Production behavior must be verified.
        _factory = new CustomWebApplicationFactory(new Dictionary<string, string?> { ["Security:DevModeBypassAuth"] = "false" });
    }

    public async Task InitializeAsync()
    {
        await _factory.ResetDatabaseAsync();
        _adminClient = await _factory.CreateAdminClientAsync();
        _nonAdminClient = await _factory.CreateAuthenticatedClientAsync(
            username: "test-nonadmin",
            email: "nonadmin@example.com");
    }

    public async Task DisposeAsync()
    {
        _adminClient?.Dispose();
        _nonAdminClient?.Dispose();
        _factory.Dispose();
    }

    // ─── helpers ────────────────────────────────────────────────────────────

    private async Task SeedEntriesAsync(IEnumerable<LoginAuditEntry> entries)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.LoginAuditEntries.AddRange(entries);
        await db.SaveChangesAsync();
    }

    private async Task<LoginAuditPageDto?> GetPageAsync(string query = "")
    {
        HttpResponseMessage resp = await _adminClient!.GetAsync($"/api/admin/security/login-audit{query}");
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<LoginAuditPageDto>(JsonOptions);
    }

    // ─── auth tests ─────────────────────────────────────────────────────────

    [Fact]
    public async Task GetLoginAudit_Unauthenticated_Returns401()
    {
        using HttpClient anon = _factory.CreateClient();
        HttpResponseMessage resp = await anon.GetAsync("/api/admin/security/login-audit");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetLoginAudit_NonAdminRole_Returns403()
    {
        HttpResponseMessage resp = await _nonAdminClient!.GetAsync("/api/admin/security/login-audit");
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetLoginAudit_Admin_Returns200()
    {
        HttpResponseMessage resp = await _adminClient!.GetAsync("/api/admin/security/login-audit");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ─── shape / content tests ───────────────────────────────────────────────

    [Fact]
    public async Task GetLoginAudit_EmptyTable_ReturnsTotalCountZero()
    {
        LoginAuditPageDto? page = await GetPageAsync();

        page.Should().NotBeNull();
        page!.TotalCount.Should().Be(0);
        page.Items.Should().BeEmpty();
        page.Page.Should().Be(1);
        page.PageSize.Should().Be(50);
    }

    [Fact]
    public async Task GetLoginAudit_WithEntries_ReturnsOrderedNewestFirst()
    {
        DateTime now = DateTime.UtcNow;
        await SeedEntriesAsync(
        [
            new LoginAuditEntry { Id = Guid.NewGuid(), Timestamp = now.AddMinutes(-10), Username = "old", Success = true, IpAddress = "1.1.1.1" },
            new LoginAuditEntry { Id = Guid.NewGuid(), Timestamp = now.AddMinutes(-1), Username = "new", Success = true, IpAddress = "2.2.2.2" },
        ]);

        LoginAuditPageDto? page = await GetPageAsync();

        page!.Items.Should().HaveCount(2);
        page.Items[0].Username.Should().Be("new");
        page.Items[1].Username.Should().Be("old");
    }

    [Fact]
    public async Task GetLoginAudit_FilterBySuccess_ReturnsMatchingEntries()
    {
        await SeedEntriesAsync(
        [
            new LoginAuditEntry { Id = Guid.NewGuid(), Timestamp = DateTime.UtcNow, Username = "alice", Success = true, IpAddress = "1.1.1.1" },
            new LoginAuditEntry { Id = Guid.NewGuid(), Timestamp = DateTime.UtcNow, Username = "eve", Success = false, IpAddress = "2.2.2.2" },
        ]);

        LoginAuditPageDto? page = await GetPageAsync("?success=false");

        page!.TotalCount.Should().Be(1);
        page.Items.Should().ContainSingle(i => i.Username == "eve");
    }

    [Fact]
    public async Task GetLoginAudit_FilterByUsername_ReturnsMatchingEntries()
    {
        await SeedEntriesAsync(
        [
            new LoginAuditEntry { Id = Guid.NewGuid(), Timestamp = DateTime.UtcNow, Username = "alice@example.com", Success = true, IpAddress = "1.1.1.1" },
            new LoginAuditEntry { Id = Guid.NewGuid(), Timestamp = DateTime.UtcNow, Username = "bob@example.com", Success = false, IpAddress = "2.2.2.2" },
        ]);

        LoginAuditPageDto? page = await GetPageAsync("?username=alice");

        page!.TotalCount.Should().Be(1);
        page.Items.Single().Username.Should().Be("alice@example.com");
    }

    [Fact]
    public async Task GetLoginAudit_Pagination_RespectsPageAndPageSize()
    {
        DateTime now = DateTime.UtcNow;
        List<LoginAuditEntry> entries = Enumerable.Range(0, 10)
            .Select(i => new LoginAuditEntry
            {
                Id = Guid.NewGuid(),
                Timestamp = now.AddMinutes(-i),
                Username = $"user{i:D2}",
                Success = true,
                IpAddress = "1.2.3.4",
            })
            .ToList();

        await SeedEntriesAsync(entries);

        LoginAuditPageDto? page = await GetPageAsync("?page=2&pageSize=3");

        page!.TotalCount.Should().Be(10);
        page.Items.Should().HaveCount(3);
        page.Page.Should().Be(2);
        page.PageSize.Should().Be(3);
    }

    [Fact]
    public async Task GetLoginAudit_PageSizeClamped_DoesNotExceed200()
    {
        LoginAuditPageDto? page = await GetPageAsync("?pageSize=9999");
        page!.PageSize.Should().Be(200);
    }

    [Fact]
    public async Task GetLoginAudit_ResponseShape_ContainsAllExpectedFields()
    {
        await SeedEntriesAsync(
        [
            new LoginAuditEntry
            {
                Id = Guid.NewGuid(),
                Timestamp = DateTime.UtcNow,
                Username = "tester",
                Success = false,
                IpAddress = "10.0.0.42",
                UserAgent = "TestAgent/1.0",
                FailureReason = "invalid_credentials",
            },
        ]);

        LoginAuditPageDto? page = await GetPageAsync();

        LoginAuditItemDto item = page!.Items.Single();
        item.Id.Should().NotBeEmpty();
        item.Timestamp.Should().NotBe(default);
        item.Username.Should().Be("tester");
        item.Success.Should().BeFalse();
        item.IpAddress.Should().Be("10.0.0.42");
        item.UserAgent.Should().Be("TestAgent/1.0");
        item.FailureReason.Should().Be("invalid_credentials");
    }

    [Fact]
    public async Task GetLoginAudit_FilterByDateRange_ReturnsOnlyEntriesWithinRange()
    {
        DateTime now = DateTime.UtcNow;
        await SeedEntriesAsync(
        [
            new LoginAuditEntry { Id = Guid.NewGuid(), Timestamp = now.AddHours(-2), Username = "too_old", Success = true, IpAddress = "1.1.1.1" },
            new LoginAuditEntry { Id = Guid.NewGuid(), Timestamp = now.AddMinutes(-30), Username = "in_range", Success = true, IpAddress = "2.2.2.2" },
            new LoginAuditEntry { Id = Guid.NewGuid(), Timestamp = now.AddHours(1), Username = "future", Success = true, IpAddress = "3.3.3.3" },
        ]);

        string from = Uri.EscapeDataString(now.AddHours(-1).ToString("O"));
        string to = Uri.EscapeDataString(now.ToString("O"));

        LoginAuditPageDto? page = await GetPageAsync($"?from={from}&to={to}");

        page!.TotalCount.Should().Be(1);
        page.Items.Single().Username.Should().Be("in_range");
    }
}
