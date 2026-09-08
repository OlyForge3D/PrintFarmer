using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.NfcDevices;
using FluentAssertions;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Farm.Web.Api.Tests.Security;

public sealed class NfcHubAuthorizationIntegrationTests : IAsyncLifetime, IDisposable
{
    private readonly NfcHubFactory _factory = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task KnownTagScan_ExcludedUserNeverReceivesEvent_ButGroupMemberAndAdminDo()
    {
        NfcScope scope = await SeedAsync(knownTag: true, online: true);

        await using HubConnection excludedConnection = CreateConnection(scope.ExcludedUserId, "operator");
        await using HubConnection includedConnection = CreateConnection(scope.IncludedUserId, "operator");
        await using HubConnection adminConnection = CreateConnection(Guid.NewGuid(), "farm_admin");

        var excludedReceived = CreateEventSignal(excludedConnection, "nfctagread");
        var includedReceived = CreateEventSignal(includedConnection, "nfctagread");
        var adminReceived = CreateEventSignal(adminConnection, "nfctagread");

        await excludedConnection.StartAsync();
        await includedConnection.StartAsync();
        await adminConnection.StartAsync();

        await PublishAsync(scope);

        await AssertDeliveryAsync(excludedReceived, includedReceived, adminReceived);
    }

    [Fact]
    public async Task DeferredTagScan_ExcludedUserNeverReceivesEvent_ButGroupMemberAndAdminDo()
    {
        NfcScope scope = await SeedAsync(knownTag: true, online: false);

        await using HubConnection excludedConnection = CreateConnection(scope.ExcludedUserId, "operator");
        await using HubConnection includedConnection = CreateConnection(scope.IncludedUserId, "operator");
        await using HubConnection adminConnection = CreateConnection(Guid.NewGuid(), "farm_admin");

        var excludedReceived = CreateEventSignal(excludedConnection, "nfctagread");
        var includedReceived = CreateEventSignal(includedConnection, "nfctagread");
        var adminReceived = CreateEventSignal(adminConnection, "nfctagread");

        await excludedConnection.StartAsync();
        await includedConnection.StartAsync();
        await adminConnection.StartAsync();

        await PublishAsync(scope);
        includedReceived.Task.IsCompleted.Should().BeFalse("offline NFC scans must be deferred");

        await using (AsyncServiceScope serviceScope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext db = serviceScope.ServiceProvider.GetRequiredService<AppDbContext>();
            NfcDevice device = await db.NfcDevices.SingleAsync(item => item.Id == scope.DeviceId);
            device.LastHeartbeat = DateTime.UtcNow;
            await db.SaveChangesAsync();

            INfcTagService service = serviceScope.ServiceProvider.GetRequiredService<INfcTagService>();
            await service.FlushOfflineQueueAsync(scope.DeviceId, CancellationToken.None);
        }

        await AssertDeliveryAsync(excludedReceived, includedReceived, adminReceived);
    }

    private HubConnection CreateConnection(Guid userId, string roles) =>
        new HubConnectionBuilder()
            .WithUrl(
                new Uri(_factory.Server.BaseAddress, "/hubs/nfc"),
                options =>
                {
                    options.Transports = HttpTransportType.LongPolling;
                    options.HttpMessageHandlerFactory = _ => new TestIdentityHandler(
                        _factory.Server.CreateHandler(),
                        userId,
                        roles);
                })
            .Build();

    private static TaskCompletionSource<bool> CreateEventSignal(HubConnection connection, string eventName)
    {
        var received = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<object>(eventName, _ => received.TrySetResult(true));
        return received;
    }

    private static async Task AssertDeliveryAsync(
        TaskCompletionSource<bool> excludedReceived,
        TaskCompletionSource<bool> includedReceived,
        TaskCompletionSource<bool> adminReceived)
    {
        (await includedReceived.Task.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
        (await adminReceived.Task.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
        await Task.Delay(TimeSpan.FromSeconds(2));
        excludedReceived.Task.IsCompleted.Should().BeFalse(
            "a user without access to the printer's group must receive no NFC scan events");
    }

    private async Task PublishAsync(NfcScope scope)
    {
        await using AsyncServiceScope serviceScope = _factory.Services.CreateAsyncScope();
        INfcTagService service = serviceScope.ServiceProvider.GetRequiredService<INfcTagService>();
        await service.ProcessTagReadAsync(
            scope.TagUid,
            scope.DeviceId,
            scope.PrinterId,
            DateTime.UtcNow,
            CancellationToken.None);
    }

    private async Task<NfcScope> SeedAsync(bool knownTag, bool online)
    {
        DateTime now = DateTime.UtcNow;
        Guid manufacturerId = Guid.NewGuid();
        Guid modelId = Guid.NewGuid();
        Guid groupId = Guid.NewGuid();
        Guid roleId = Guid.NewGuid();
        Guid printerId = Guid.NewGuid();
        Guid deviceId = Guid.NewGuid();
        Guid includedUserId = Guid.NewGuid();
        Guid excludedUserId = Guid.NewGuid();
        string tagUid = $"nfc-acl-{Guid.NewGuid():N}";

        await using AsyncServiceScope serviceScope = _factory.Services.CreateAsyncScope();
        AppDbContext db = serviceScope.ServiceProvider.GetRequiredService<AppDbContext>();

        db.Manufacturers.Add(new Manufacturer { Id = manufacturerId, Name = $"NFC ACL maker {Guid.NewGuid():N}" });
        db.PrinterModels.Add(new PrinterModel { Id = modelId, ManufacturerId = manufacturerId, Name = $"NFC ACL model {Guid.NewGuid():N}" });
        db.PrinterGroups.Add(new PrinterGroup { Id = groupId, Name = $"NFC ACL group {Guid.NewGuid():N}" });
        db.Roles.Add(new Role { Id = roleId, Name = $"nfc-acl-role-{Guid.NewGuid():N}", DisplayName = "NFC ACL role", IsActive = true, CreatedAt = now, UpdatedAt = now });
        db.PrinterGroupAccesses.Add(new PrinterGroupAccess { Id = Guid.NewGuid(), PrinterGroupId = groupId, RoleId = roleId, AccessLevel = PrinterGroupAccessLevel.View, CreatedDate = DateTimeOffset.UtcNow });
        db.Printers.Add(new Printer
        {
            Id = printerId,
            Name = "Restricted NFC printer",
            ServerUrl = $"http://nfc-acl-printer-{Guid.NewGuid():N}",
            ManufacturerId = manufacturerId,
            ModelId = modelId,
            PrinterGroupId = groupId,
            IsEnabled = true,
            IsAvailable = true,
        });
        db.NfcDevices.Add(new NfcDevice
        {
            Id = deviceId,
            Name = "NFC ACL reader",
            PrinterId = printerId,
            LastHeartbeat = online ? now : now.AddMinutes(-4),
            CreatedAt = now,
            UpdatedAt = now,
        });
        if (knownTag)
        {
            db.NfcTagBindings.Add(new NfcTagBinding
            {
                Id = Guid.NewGuid(),
                TagUid = tagUid,
                SpoolId = 42,
                SpoolName = "NFC ACL spool",
                PrinterId = printerId,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }
        db.Users.AddRange(
            new User { Id = includedUserId, Username = $"nfc-included-{Guid.NewGuid():N}", Email = $"nfc-included-{Guid.NewGuid():N}@example.test", PasswordHash = "test-hash", IsActive = true, CreatedAt = now, UpdatedAt = now },
            new User { Id = excludedUserId, Username = $"nfc-excluded-{Guid.NewGuid():N}", Email = $"nfc-excluded-{Guid.NewGuid():N}@example.test", PasswordHash = "test-hash", IsActive = true, CreatedAt = now, UpdatedAt = now });
        db.UserRoles.Add(new UserRole { Id = Guid.NewGuid(), UserId = includedUserId, RoleId = roleId, AssignedAt = now, IsActive = true });

        await db.SaveChangesAsync();
        return new NfcScope(printerId, deviceId, includedUserId, excludedUserId, tagUid);
    }

    private sealed record NfcScope(
        Guid PrinterId,
        Guid DeviceId,
        Guid IncludedUserId,
        Guid ExcludedUserId,
        string TagUid);

    private sealed class TestIdentityHandler(HttpMessageHandler inner, Guid userId, string roles)
        : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            request.Headers.Remove("X-Test-User-Id");
            request.Headers.Remove("X-Test-Roles");
            request.Headers.Add("X-Test-User-Id", userId.ToString());
            request.Headers.Add("X-Test-Roles", roles);
            return base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class NfcHubFactory() : CustomWebApplicationFactory(
        new Dictionary<string, string?>
        {
            ["Testing:UseTestAuthentication"] = "true",
            ["Security:DevModeBypassAuth"] = "false",
        });
}
