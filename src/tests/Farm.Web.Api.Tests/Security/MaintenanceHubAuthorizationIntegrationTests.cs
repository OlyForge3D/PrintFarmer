using System.Net.Http;
using System.Net.Http.Json;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Security;
using Farm.Infrastructure.Services.Maintenance;
using FluentAssertions;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Farm.Web.Api.Tests.Security;

/// <summary>
/// End-to-end regression coverage for issue #1966: <c>MaintenanceHub</c> used to auto-join every
/// authenticated connection to the farm-wide maintenance group, so a caller with no
/// <c>maintenance:admin</c> permission and no <see cref="PrinterGroupAccess"/> to a printer would
/// still receive that printer's maintenance alert/status/completion events. This test drives real
/// hub connections (via <see cref="CustomWebApplicationFactory"/>, the "TestScheme" header-based
/// auth handler, and long-polling transport) against a real database-seeded
/// <see cref="PrinterGroup"/>/<see cref="PrinterGroupAccess"/>/<see cref="Role"/>/<see cref="UserRole"/>
/// setup, then transitions a real <see cref="MaintenanceAlert"/> via
/// <see cref="IMaintenanceAlertService.ResolveAlertAsync"/> and asserts who actually receives the
/// resulting SignalR event.
/// </summary>
public sealed class MaintenanceHubAuthorizationIntegrationTests : IAsyncLifetime
{
    private readonly MaintenanceHubFactory _factory = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task ResolvedAlert_ExcludedUser_NeverReceivesEvent_ButAdminAndGroupMemberDo()
    {
        (Guid printerId, Guid alertId, Guid includedUserId, Guid excludedUserId) = await SeedAsync();

        await using HubConnection excludedConnection = CreateConnection(excludedUserId, roles: "operator");
        await using HubConnection includedConnection = CreateConnection(includedUserId, roles: "operator");
        await using HubConnection adminConnection = CreateConnection(Guid.NewGuid(), roles: "farm_admin");

        var excludedReceived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var includedReceived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var adminReceived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        excludedConnection.On<object>("alertstatuschanged", _ => excludedReceived.TrySetResult(true));
        includedConnection.On<object>("alertstatuschanged", _ => includedReceived.TrySetResult(true));
        adminConnection.On<object>("alertstatuschanged", _ => adminReceived.TrySetResult(true));

        await excludedConnection.StartAsync();
        await includedConnection.StartAsync();
        await adminConnection.StartAsync();

        // The excluded user has no PrinterGroupAccess rule granting this printer's group, so
        // subscribing must be rejected with resource_forbidden and must NOT join the group.
        Func<Task> excludedSubscribe = () => excludedConnection.InvokeAsync(
            "SubscribeToPrinterAsync",
            printerId.ToString());
        (await excludedSubscribe.Should().ThrowAsync<Microsoft.AspNetCore.SignalR.HubException>())
            .Which.Message.Should().Contain("resource_forbidden");

        // The included user holds the role granted access to this printer's group.
        await includedConnection.InvokeAsync("SubscribeToPrinterAsync", printerId.ToString());

        // The admin auto-joined the farm-wide group in OnConnectedAsync; no explicit subscribe.

        // Act: resolve the alert for real via the production engine, exercising both the
        // repository update and the (now scoped) SignalR broadcast.
        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            IMaintenanceAlertService engine = scope.ServiceProvider.GetRequiredService<IMaintenanceAlertService>();
            await engine.ResolveAlertAsync(alertId, "integration-test");
        }

        (await includedReceived.Task.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue(
            "the included user subscribed to the printer's maintenance group and must still see the event");
        (await adminReceived.Task.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue(
            "a maintenance:admin/farm_admin caller must still see every maintenance event");

        // Give the excluded connection a bounded window to (wrongly) receive the event; it must not.
        await Task.WhenAny(excludedReceived.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        excludedReceived.Task.IsCompleted.Should().BeFalse(
            "a caller with no maintenance:admin permission and no PrinterGroupAccess to this printer must never receive its maintenance events");
    }

    [Fact]
    public async Task ManualMaintenanceLog_ExcludedUser_NeverReceivesCompletedEvent_ButAdminAndGroupMemberDo()
    {
        // Regression coverage for the reviewer-flagged gap: MaintenanceController's own
        // "maintenancecompleted" broadcast (a controller-driven path, not the alert-engine path
        // covered above) previously targeted the farm-only group. This exercises the real HTTP
        // controller endpoint end-to-end, not just the engine/notifier services directly.
        (Guid printerId, Guid alertId, Guid includedUserId, Guid excludedUserId) = await SeedAsync();

        await using HubConnection excludedConnection = CreateConnection(excludedUserId, roles: "operator");
        await using HubConnection includedConnection = CreateConnection(includedUserId, roles: "operator");
        await using HubConnection adminConnection = CreateConnection(Guid.NewGuid(), roles: "farm_admin");

        var excludedReceived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var includedReceived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var adminReceived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        excludedConnection.On<object>("maintenancecompleted", _ => excludedReceived.TrySetResult(true));
        includedConnection.On<object>("maintenancecompleted", _ => includedReceived.TrySetResult(true));
        adminConnection.On<object>("maintenancecompleted", _ => adminReceived.TrySetResult(true));

        await excludedConnection.StartAsync();
        await includedConnection.StartAsync();
        await adminConnection.StartAsync();

        await includedConnection.InvokeAsync("SubscribeToPrinterAsync", printerId.ToString());

        // Act: create a manual maintenance log via the real HTTP controller endpoint, authenticated
        // as a farm_admin caller (the controller requires maintenance:admin for every action).
        using HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Id", Guid.NewGuid().ToString());
        client.DefaultRequestHeaders.Add("X-Test-Roles", "farm_admin");
        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/maintenance/logs",
            new
            {
                printerId,
                taskName = "Manual lubrication",
                performedBy = "integration-test"
            });
        response.IsSuccessStatusCode.Should().BeTrue(
            $"the manual maintenance log endpoint must succeed for a farm_admin caller (got {response.StatusCode})");

        (await includedReceived.Task.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue(
            "the included user subscribed to the printer's maintenance group and must still see the controller-driven completion event");
        (await adminReceived.Task.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue(
            "a maintenance:admin/farm_admin caller must still see the controller-driven completion event");

        await Task.WhenAny(excludedReceived.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        excludedReceived.Task.IsCompleted.Should().BeFalse(
            "a caller with no maintenance:admin permission and no PrinterGroupAccess to this printer must never receive its maintenance events, including the controller-driven completion event");
    }

    private HubConnection CreateConnection(Guid userId, string roles)
    {
        return new HubConnectionBuilder()
            .WithUrl(
                new Uri(_factory.Server.BaseAddress, "/hubs/maintenance"),
                options =>
                {
                    options.Transports = HttpTransportType.LongPolling;
                    options.HttpMessageHandlerFactory = _ => new TestIdentityHandler(
                        _factory.Server.CreateHandler(),
                        userId,
                        roles);
                })
            .Build();
    }

    private async Task<(Guid PrinterId, Guid AlertId, Guid IncludedUserId, Guid ExcludedUserId)> SeedAsync()
    {
        DateTime now = DateTime.UtcNow;
        Guid manufacturerId = Guid.NewGuid();
        Guid modelId = Guid.NewGuid();
        Guid groupId = Guid.NewGuid();
        Guid roleId = Guid.NewGuid();
        Guid printerId = Guid.NewGuid();
        Guid alertId = Guid.NewGuid();
        Guid includedUserId = Guid.NewGuid();
        Guid excludedUserId = Guid.NewGuid();

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        db.Manufacturers.Add(new Manufacturer { Id = manufacturerId, Name = $"Maint-ACL maker {Guid.NewGuid():N}" });
        db.PrinterModels.Add(new PrinterModel { Id = modelId, ManufacturerId = manufacturerId, Name = $"Maint-ACL model {Guid.NewGuid():N}" });
        db.PrinterGroups.Add(new PrinterGroup { Id = groupId, Name = $"Maint-ACL group {Guid.NewGuid():N}" });
        db.Roles.Add(new Role
        {
            Id = roleId,
            Name = $"maint-acl-role-{Guid.NewGuid():N}",
            DisplayName = "Maintenance ACL role",
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        });
        db.PrinterGroupAccesses.Add(new PrinterGroupAccess
        {
            Id = Guid.NewGuid(),
            PrinterGroupId = groupId,
            RoleId = roleId,
            AccessLevel = PrinterGroupAccessLevel.View,
            CreatedDate = DateTimeOffset.UtcNow,
        });
        db.Printers.Add(new Printer
        {
            Id = printerId,
            Name = "Restricted maintenance printer",
            ServerUrl = $"http://maint-acl-printer-{Guid.NewGuid():N}",
            ManufacturerId = manufacturerId,
            ModelId = modelId,
            PrinterGroupId = groupId,
            IsEnabled = true,
            IsAvailable = true,
        });
        db.Users.AddRange(
            new User
            {
                Id = includedUserId,
                Username = $"maint-included-{Guid.NewGuid():N}",
                Email = $"maint-included-{Guid.NewGuid():N}@example.test",
                PasswordHash = "test-hash",
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now,
            },
            new User
            {
                Id = excludedUserId,
                Username = $"maint-excluded-{Guid.NewGuid():N}",
                Email = $"maint-excluded-{Guid.NewGuid():N}@example.test",
                PasswordHash = "test-hash",
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now,
            });
        db.UserRoles.Add(new UserRole
        {
            Id = Guid.NewGuid(),
            UserId = includedUserId,
            RoleId = roleId,
            AssignedAt = now,
            IsActive = true,
        });
        db.MaintenanceAlerts.Add(new MaintenanceAlert
        {
            Id = alertId,
            PrinterId = printerId,
            Title = "Lubricate rails",
            Message = "Scheduled lubrication is due.",
            Severity = 2,
            Status = MaintenanceAlertStatus.Active,
            PrinterHoursAtTrigger = 100,
            CreatedAt = now,
            UpdatedAt = now,
        });

        await db.SaveChangesAsync();

        return (printerId, alertId, includedUserId, excludedUserId);
    }

    /// <summary>
    /// Wraps every outgoing SignalR HTTP request (negotiate + long-polling send/receive) with the
    /// header shape <see cref="TestInfrastructure.TestAuthHandler"/> expects, so each real HTTP
    /// round-trip authenticates as the given user/roles - mirroring how a real browser would carry
    /// its auth on every request, without needing a real JWT/cookie for this scenario.
    /// </summary>
    private sealed class TestIdentityHandler(HttpMessageHandler inner, Guid userId, string roles) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Remove("X-Test-User-Id");
            request.Headers.Remove("X-Test-Roles");
            request.Headers.Add("X-Test-User-Id", userId.ToString());
            request.Headers.Add("X-Test-Roles", roles);
            return base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class MaintenanceHubFactory() : CustomWebApplicationFactory(
        new Dictionary<string, string?>
        {
            ["Testing:UseTestAuthentication"] = "true",
            ["Security:DevModeBypassAuth"] = "false",
        });
}
