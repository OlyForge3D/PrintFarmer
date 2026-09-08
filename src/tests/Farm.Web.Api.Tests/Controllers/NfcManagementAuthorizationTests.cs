using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.NfcDevices;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Farm.Web.Api.Tests.Controllers;

public sealed class NfcManagementAuthorizationTests(NfcManagementAuthorizationTests.Factory factory)
    : IClassFixture<NfcManagementAuthorizationTests.Factory>, IAsyncLifetime
{
    public sealed class Factory() : CustomWebApplicationFactory(new Dictionary<string, string?>
    {
        ["Testing:UseTestAuthentication"] = "true",
        ["Security:DevModeBypassAuth"] = "false"
    });

    public Task InitializeAsync() => factory.ResetDataAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Theory]
    [InlineData("GET", "/api/nfc-devices")]
    [InlineData("GET", "/api/nfc-devices/{id}")]
    [InlineData("GET", "/api/nfc-devices/{id}/history")]
    [InlineData("POST", "/api/nfc-devices")]
    [InlineData("PUT", "/api/nfc-devices/{id}")]
    [InlineData("DELETE", "/api/nfc-devices/{id}")]
    [InlineData("POST", "/api/nfc-devices/{id}/approve")]
    [InlineData("GET", "/api/nfc/bindings")]
    [InlineData("POST", "/api/nfc/link")]
    [InlineData("DELETE", "/api/nfc/bindings/{bindingId}")]
    public async Task ManagementEndpoint_OrdinaryCaller_DeniesWithoutMutation(string method, string route)
    {
        var seed = await SeedAsync();
        using var client = CreateClient(adminPermission: false);
        using var response = await SendAsync(client, method, route, seed);
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        await AssertUnchangedAsync(seed);
    }

    [Theory]
    [InlineData("GET", "/api/nfc-devices", 200)]
    [InlineData("GET", "/api/nfc-devices/{id}", 200)]
    [InlineData("GET", "/api/nfc-devices/{id}/history", 200)]
    [InlineData("POST", "/api/nfc-devices", 201)]
    [InlineData("PUT", "/api/nfc-devices/{id}", 200)]
    [InlineData("DELETE", "/api/nfc-devices/{id}", 204)]
    [InlineData("POST", "/api/nfc-devices/{id}/approve", 200)]
    [InlineData("GET", "/api/nfc/bindings", 200)]
    [InlineData("POST", "/api/nfc/link", 200)]
    [InlineData("DELETE", "/api/nfc/bindings/{bindingId}", 204)]
    public async Task ManagementEndpoint_NfcAdministrator_PreservesAuthorizedFlow(string method, string route, int status)
    {
        var seed = await SeedAsync();
        using var client = CreateClient();
        using var response = await SendAsync(client, method, route, seed);
        ((int)response.StatusCode).Should().Be(status, await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("GET", "/api/nfc-devices/{id}")]
    [InlineData("GET", "/api/nfc-devices/{id}/history")]
    [InlineData("PUT", "/api/nfc-devices/{id}")]
    [InlineData("DELETE", "/api/nfc-devices/{id}")]
    [InlineData("POST", "/api/nfc-devices/{id}/approve")]
    [InlineData("DELETE", "/api/nfc/bindings/{bindingId}")]
    public async Task ManagementEndpoint_RestrictedResource_MatchesMissingAndLeavesRecordsUnchanged(string method, string route)
    {
        var seed = await SeedAsync(restricted: true);
        using var client = CreateClient();
        using var response = await SendAsync(client, method, route, seed);
        using var missing = await SendAsync(client, method, route,
            seed with { DeviceId = Guid.NewGuid(), BindingId = Guid.NewGuid() });
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        response.StatusCode.Should().Be(missing.StatusCode);
        await AssertUnchangedAsync(seed);
    }

    [Fact]
    public async Task Collections_RestrictedPrinter_ExcludeDevicesAndBindings()
    {
        var restricted = await SeedAsync(restricted: true);
        var open = await SeedAsync();
        using var client = CreateClient();
        var devices = await client.GetFromJsonAsync<NfcDeviceDto[]>("/api/nfc-devices");
        var bindings = await client.GetFromJsonAsync<NfcTagBindingDto[]>("/api/nfc/bindings");
        devices!.Select(d => d.Id).Should().Contain(open.DeviceId).And.NotContain(restricted.DeviceId);
        bindings!.Select(b => b.Id).Should().Contain(open.BindingId).And.NotContain(restricted.BindingId);
    }

    [Fact]
    public async Task Mutations_RestrictedSourceOrDestination_DenyBeforeChanges()
    {
        var restricted = await SeedAsync(restricted: true);
        var open = await SeedAsync();
        using var client = CreateClient();
        var requests = new[]
        {
            ("/api/nfc-devices", HttpMethod.Post, (object)new { name = "Changed", printerId = restricted.PrinterId }),
            ($"/api/nfc-devices/{open.DeviceId}", HttpMethod.Put, new { name = "Changed", printerId = restricted.PrinterId }),
            ("/api/nfc/link", HttpMethod.Post, new { tagUid = open.TagUid, spoolId = 99, printerId = restricted.PrinterId }),
            ("/api/nfc/link", HttpMethod.Post, new { tagUid = restricted.TagUid, spoolId = 99, printerId = open.PrinterId }),
            ("/api/nfc/link", HttpMethod.Post, new { tagUid = restricted.TagUid, spoolId = 99, printerId = (Guid?)null }),
            ("/api/nfc/link", HttpMethod.Post, new { tagUid = "NEW", spoolId = 99, printerId = restricted.PrinterId }),
            ("/api/nfc-devices", HttpMethod.Post, new { name = "Changed", printerId = Guid.NewGuid() })
        };
        foreach (var (route, method, body) in requests)
        {
            using var request = new HttpRequestMessage(method, route) { Content = JsonContent.Create(body) };
            using var response = await client.SendAsync(request);
            response.StatusCode.Should().Be(HttpStatusCode.Forbidden, await response.Content.ReadAsStringAsync());
        }

        await AssertUnchangedAsync(restricted, expectedCount: 2);
        await AssertUnchangedAsync(open, expectedCount: 2);
    }

    [Theory]
    [InlineData(PrinterGroupAccessLevel.View, false)]
    [InlineData(PrinterGroupAccessLevel.Submit, false)]
    [InlineData(PrinterGroupAccessLevel.Manage, true)]
    public async Task ManagementEndpoint_GroupRole_RequiresManageAccess(PrinterGroupAccessLevel access, bool permitted)
    {
        var seed = await SeedAsync(restricted: true, access: access);
        var userId = Guid.NewGuid();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Users.Add(new User { Id = userId, Username = $"nfc-{userId:N}", Email = $"{userId:N}@example.com" });
            db.UserRoles.Add(new UserRole { Id = Guid.NewGuid(), UserId = userId, RoleId = seed.RoleId, IsActive = true });
            await db.SaveChangesAsync();
        }

        using var client = CreateClient(userId: userId);
        using var response = await client.GetAsync($"/api/nfc-devices/{seed.DeviceId}/history");
        response.StatusCode.Should().Be(permitted ? HttpStatusCode.OK : HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ManagementServices_NoPermission_DenyEveryEntryPointWithoutMutation()
    {
        var seed = await SeedAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var accessor = scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>();
        accessor.HttpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())], "Test"))
        };
        try
        {
            var devices = scope.ServiceProvider.GetRequiredService<INfcDeviceService>();
            var tags = scope.ServiceProvider.GetRequiredService<INfcTagService>();
            Func<Task>[] operations =
            [
                async () => await devices.GetAllAsync(default),
                async () => await devices.GetByIdAsync(seed.DeviceId, default),
                async () => await devices.CreateAsync(new CreateNfcDeviceDto { Name = "Changed" }, default),
                async () => await devices.UpdateAsync(seed.DeviceId, new UpdateNfcDeviceDto { Name = "Changed" }, default),
                async () => await devices.DeleteAsync(seed.DeviceId, default),
                async () => await devices.ApproveAsync(seed.DeviceId, default),
                async () => await devices.GetScanHistoryAsync(seed.DeviceId, 50, 0, default),
                async () => await tags.ListBindingsAsync(default),
                async () => await tags.LinkTagAsync(new LinkNfcTagRequest { TagUid = seed.TagUid, SpoolId = 99 }, default),
                async () => await tags.DeleteBindingAsync(seed.BindingId, default)
            ];
            foreach (var operation in operations)
            {
                await operation.Should().ThrowAsync<NfcManagementAccessDeniedException>();
            }
        }
        finally
        {
            accessor.HttpContext = null;
        }

        await AssertUnchangedAsync(seed);
    }

    [Fact]
    public async Task ManagementServices_RestrictedResource_DenyWithoutControllerChecks()
    {
        var seed = await SeedAsync(restricted: true);
        await using var scope = factory.Services.CreateAsyncScope();
        var accessor = scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>();
        accessor.HttpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), new Claim("permission", "nfc_devices:admin")], "Test"))
        };
        try
        {
            var devices = scope.ServiceProvider.GetRequiredService<INfcDeviceService>();
            var tags = scope.ServiceProvider.GetRequiredService<INfcTagService>();
            (await devices.GetByIdAsync(seed.DeviceId, default)).Should().BeNull();
            (await devices.GetScanHistoryAsync(seed.DeviceId, 50, 0, default)).Should().BeEmpty();
            (await devices.UpdateAsync(seed.DeviceId, new UpdateNfcDeviceDto { Name = "Changed" }, default)).Should().BeNull();
            (await devices.ApproveAsync(seed.DeviceId, default)).Should().BeNull();
            (await devices.DeleteAsync(seed.DeviceId, default)).Should().BeFalse();
            (await tags.DeleteBindingAsync(seed.BindingId, default)).Should().BeFalse();
            Func<Task> link = async () => await tags.LinkTagAsync(new LinkNfcTagRequest { TagUid = seed.TagUid, SpoolId = 99 }, default);
            await link.Should().ThrowAsync<NfcManagementAccessDeniedException>();
        }
        finally
        {
            accessor.HttpContext = null;
        }

        await AssertUnchangedAsync(seed);
    }

    private HttpClient CreateClient(bool adminPermission = true, Guid? userId = null)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Id", (userId ?? Guid.NewGuid()).ToString());
        client.DefaultRequestHeaders.Add("X-Test-Roles", "farm_user");
        if (adminPermission)
        {
            client.DefaultRequestHeaders.Add("X-Test-Permissions", "nfc_devices:admin");
        }

        return client;
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, string method, string route, Seed seed)
    {
        route = route.Replace("{id}", seed.DeviceId.ToString()).Replace("{bindingId}", seed.BindingId.ToString());
        var request = new HttpRequestMessage(new HttpMethod(method), route);
        if (method is "POST" or "PUT")
        {
            request.Content = JsonContent.Create(new
            {
                name = "Changed",
                printerId = route == "/api/nfc-devices" ? (Guid?)null : seed.PrinterId,
                tagUid = seed.TagUid,
                spoolId = 99
            });
        }

        return client.SendAsync(request);
    }

    private async Task<Seed> SeedAsync(bool restricted = false, PrinterGroupAccessLevel access = PrinterGroupAccessLevel.Manage)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var manufacturer = new Manufacturer { Id = Guid.NewGuid(), Name = $"NFC maker {Guid.NewGuid():N}" };
        var model = new PrinterModel { Id = Guid.NewGuid(), Name = "NFC model", ManufacturerId = manufacturer.Id };
        var group = new PrinterGroup { Id = Guid.NewGuid(), Name = $"NFC group {Guid.NewGuid():N}" };
        var role = new Role { Id = Guid.NewGuid(), Name = $"nfc-role-{Guid.NewGuid():N}" };
        var printer = new Printer
        {
            Id = Guid.NewGuid(),
            Name = "NFC printer",
            ServerUrl = $"http://nfc-test-{Guid.NewGuid():N}",
            ManufacturerId = manufacturer.Id,
            ModelId = model.Id,
            PrinterGroupId = group.Id
        };
        var device = new NfcDevice { Id = Guid.NewGuid(), Name = "Original", PrinterId = printer.Id };
        var binding = new NfcTagBinding { Id = Guid.NewGuid(), TagUid = Guid.NewGuid().ToString(), SpoolId = 1, PrinterId = printer.Id };
        db.AddRange(manufacturer, model, group, role, printer, device, binding,
            new NfcScanEvent { Id = Guid.NewGuid(), NfcDeviceId = device.Id, SpoolId = 1 });
        if (restricted)
        {
            db.PrinterGroupAccesses.Add(new PrinterGroupAccess
            {
                Id = Guid.NewGuid(),
                PrinterGroupId = group.Id,
                RoleId = role.Id,
                AccessLevel = access
            });
        }

        await db.SaveChangesAsync();
        return new Seed(printer.Id, device.Id, binding.Id, binding.TagUid, role.Id);
    }

    private async Task AssertUnchangedAsync(Seed seed, int expectedCount = 1)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var device = await db.NfcDevices.SingleAsync(d => d.Id == seed.DeviceId);
        device.Name.Should().Be("Original");
        device.PrinterId.Should().Be(seed.PrinterId);
        device.IsApproved.Should().BeFalse();
        device.DeviceTokenHash.Should().BeNull();
        var binding = await db.NfcTagBindings.SingleAsync(b => b.Id == seed.BindingId);
        binding.PrinterId.Should().Be(seed.PrinterId);
        binding.SpoolId.Should().Be(1);
        (await db.NfcDevices.CountAsync()).Should().Be(expectedCount);
        (await db.NfcTagBindings.CountAsync()).Should().Be(expectedCount);
        (await db.NfcScanEvents.CountAsync()).Should().Be(expectedCount);
    }

    private sealed record Seed(Guid PrinterId, Guid DeviceId, Guid BindingId, string TagUid, Guid RoleId);
}
