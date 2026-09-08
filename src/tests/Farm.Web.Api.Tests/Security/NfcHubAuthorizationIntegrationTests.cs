using System.Net.Http;
using System.Text.Json;
using System.Threading.Channels;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.NfcDevices;
using Farm.Infrastructure.Services.SignalR;
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

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ProcessTagReadAsync_SeparatePrinterGroups_OnlyAuthorizedClientsReceive(bool known, bool offline)
    {
        ScanFixture fixture = await SeedAsync(known, offline);
        await using HubConnection included = CreateConnection(fixture.FirstUser);
        await using HubConnection excluded = CreateConnection(fixture.SecondUser);
        await using HubConnection admin = CreateConnection(Guid.NewGuid(), "farm_admin");
        Channel<JsonElement> includedEvents = Listen(included, known);
        Channel<JsonElement> excludedEvents = Listen(excluded, known);
        Channel<JsonElement> adminEvents = Listen(admin, known);
        await Task.WhenAll(included.StartAsync(), excluded.StartAsync(), admin.StartAsync());

        // A rejected invocation also establishes that OnConnectedAsync finished.
        await AssertForbiddenAsync(included, fixture.SecondPrinter);
        await AssertForbiddenAsync(excluded, fixture.FirstPrinter);
        await admin.InvokeAsync("SubscribeToPrinterAsync", fixture.FirstPrinter.ToString());
        await excluded.InvokeAsync("SubscribeToPrinterAsync", fixture.SecondPrinter.ToString());

        INfcTagService service = _factory.Services.GetRequiredService<INfcTagService>();
        DateTime readAt = DateTime.UtcNow;
        await service.ProcessTagReadAsync("NFC-ISOLATION", fixture.Device, fixture.FirstPrinter, readAt, CancellationToken.None);

        if (offline)
        {
            includedEvents.Reader.TryPeek(out _).Should().BeFalse();
            adminEvents.Reader.TryPeek(out _).Should().BeFalse();
            await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            NfcDevice device = await db.NfcDevices.SingleAsync(d => d.Id == fixture.Device);
            device.PrinterId = fixture.SecondPrinter;
            device.LastHeartbeat = DateTime.UtcNow;
            if (known)
            {
                NfcTagBinding binding = await db.NfcTagBindings.SingleAsync(b => b.TagUid == "NFC-ISOLATION");
                binding.PrinterId = fixture.SecondPrinter;
            }
            await db.SaveChangesAsync();
            await service.FlushOfflineQueueAsync(fixture.Device, CancellationToken.None);
            await service.FlushOfflineQueueAsync(fixture.Device, CancellationToken.None);
        }

        JsonElement payload = await includedEvents.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        JsonElement adminPayload = await adminEvents.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        payload.GetRawText().Should().Be(adminPayload.GetRawText());
        payload.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(known
            ? ["tagUid", "spoolId", "spoolName", "printerId", "trayId", "readAt"]
            : ["tagUid", "printerId", "readAt"]);
        payload.GetProperty("tagUid").GetString().Should().Be("NFC-ISOLATION");
        payload.GetProperty("printerId").GetGuid().Should().Be(fixture.FirstPrinter);
        payload.GetProperty("readAt").GetDateTime().Should().Be(readAt);
        if (known)
        {
            payload.GetProperty("spoolId").GetInt32().Should().Be(42);
            payload.GetProperty("spoolName").GetString().Should().Be("Restricted spool");
            payload.GetProperty("trayId").GetString().Should().Be("A1");
        }

        await Task.Delay(TimeSpan.FromMilliseconds(500));
        excludedEvents.Reader.TryRead(out _).Should().BeFalse("another printer group's member must never receive the scan");
        includedEvents.Reader.TryRead(out _).Should().BeFalse("replay must deliver once");
        adminEvents.Reader.TryRead(out _).Should().BeFalse("membership in both groups must not duplicate delivery");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ProcessTagReadAsync_UnscopedOrConflictingResources_OnlyFarmAdminReceives(bool conflict, bool offline)
    {
        ScanFixture fixture = await SeedAsync(known: true, offline);
        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            NfcTagBinding binding = await db.NfcTagBindings.SingleAsync(b => b.TagUid == "NFC-ISOLATION");
            binding.PrinterId = conflict ? fixture.SecondPrinter : null;
            if (!conflict)
            {
                NfcDevice device = await db.NfcDevices.SingleAsync(d => d.Id == fixture.Device);
                device.PrinterId = null;
            }
            await db.SaveChangesAsync();
        }

        await using HubConnection first = CreateConnection(fixture.FirstUser);
        await using HubConnection second = CreateConnection(fixture.SecondUser);
        await using HubConnection admin = CreateConnection(Guid.NewGuid(), "farm_admin");
        Channel<JsonElement> firstEvents = Listen(first, known: true);
        Channel<JsonElement> secondEvents = Listen(second, known: true);
        Channel<JsonElement> adminEvents = Listen(admin, known: true);
        await Task.WhenAll(first.StartAsync(), second.StartAsync(), admin.StartAsync());
        await AssertForbiddenAsync(first, fixture.SecondPrinter);
        await AssertForbiddenAsync(second, fixture.FirstPrinter);
        await admin.InvokeAsync("SubscribeToPrinterAsync", fixture.FirstPrinter.ToString());

        INfcTagService service = _factory.Services.GetRequiredService<INfcTagService>();
        await service.ProcessTagReadAsync("NFC-ISOLATION", fixture.Device, null, DateTime.UtcNow, CancellationToken.None);
        if (offline)
        {
            await service.FlushOfflineQueueAsync(fixture.Device, CancellationToken.None);
        }

        await adminEvents.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        firstEvents.Reader.TryRead(out _).Should().BeFalse();
        secondEvents.Reader.TryRead(out _).Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProcessTagReadAsync_ForgedResourceOrMissingDevice_DoesNotPublish(bool missingDevice)
    {
        ScanFixture fixture = await SeedAsync(known: false, offline: false);
        await using HubConnection admin = CreateConnection(Guid.NewGuid(), "farm_admin");
        Channel<JsonElement> events = Listen(admin, known: false);
        await admin.StartAsync();
        await admin.InvokeAsync("SubscribeToPrinterAsync", fixture.FirstPrinter.ToString());
        INfcTagService service = _factory.Services.GetRequiredService<INfcTagService>();
        Guid deviceId = missingDevice ? Guid.NewGuid() : fixture.Device;
        await service.ProcessTagReadAsync("FORGED", deviceId, fixture.SecondPrinter, DateTime.UtcNow, CancellationToken.None);
        await service.FlushOfflineQueueAsync(deviceId, CancellationToken.None);
        await service.ProcessTagReadAsync("VALID", fixture.Device, null, DateTime.UtcNow, CancellationToken.None);

        JsonElement payload = await events.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        payload.GetProperty("tagUid").GetString().Should().Be("VALID");
        payload.GetProperty("printerId").GetGuid().Should().Be(fixture.FirstPrinter);
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        events.Reader.TryRead(out _).Should().BeFalse();
    }

    private static Channel<JsonElement> Listen(HubConnection connection, bool known)
    {
        Channel<JsonElement> events = Channel.CreateUnbounded<JsonElement>();
        connection.On<JsonElement>(known ? NfcHubEvents.TagRead : NfcHubEvents.TagUnknown,
            payload => events.Writer.TryWrite(payload));
        return events;
    }

    private static async Task AssertForbiddenAsync(HubConnection connection, Guid printerId)
    {
        Func<Task> subscribe = () => connection.InvokeAsync("SubscribeToPrinterAsync", printerId.ToString());
        (await subscribe.Should().ThrowAsync<Microsoft.AspNetCore.SignalR.HubException>())
            .Which.Message.Should().Contain("resource_forbidden");
    }

    private HubConnection CreateConnection(Guid userId, string roles = "operator") =>
        new HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress, "/hubs/nfc"), options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ =>
                    new TestIdentityHandler(_factory.Server.CreateHandler(), userId, roles);
            })
            .Build();

    private async Task<ScanFixture> SeedAsync(bool known, bool offline)
    {
        var fixture = new ScanFixture(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Guid manufacturerId = Guid.NewGuid();
        Guid modelId = Guid.NewGuid();
        db.Manufacturers.Add(new Manufacturer { Id = manufacturerId, Name = $"NFC maker {manufacturerId}" });
        db.PrinterModels.Add(new PrinterModel { Id = modelId, ManufacturerId = manufacturerId, Name = $"NFC model {modelId}" });
        foreach ((Guid printerId, Guid userId) in new[]
        {
            (fixture.FirstPrinter, fixture.FirstUser), (fixture.SecondPrinter, fixture.SecondUser)
        })
        {
            Guid groupId = Guid.NewGuid();
            Guid roleId = Guid.NewGuid();
            db.PrinterGroups.Add(new PrinterGroup { Id = groupId, Name = $"NFC group {groupId}" });
            db.Roles.Add(new Role { Id = roleId, Name = $"nfc-role-{roleId}", DisplayName = "NFC viewer", IsActive = true });
            db.Users.Add(new User
            {
                Id = userId,
                Username = $"nfc-{userId}",
                Email = $"{userId}@example.test",
                PasswordHash = "test-hash",
                IsActive = true
            });
            db.UserRoles.Add(new UserRole { Id = Guid.NewGuid(), UserId = userId, RoleId = roleId, IsActive = true });
            db.PrinterGroupAccesses.Add(new PrinterGroupAccess
            {
                Id = Guid.NewGuid(),
                PrinterGroupId = groupId,
                RoleId = roleId,
                AccessLevel = PrinterGroupAccessLevel.View
            });
            db.Printers.Add(new Printer
            {
                Id = printerId,
                Name = $"NFC printer {printerId}",
                ServerUrl = $"http://nfc-{printerId}",
                ManufacturerId = manufacturerId,
                ModelId = modelId,
                PrinterGroupId = groupId,
                IsEnabled = true,
                IsAvailable = true
            });
        }

        db.NfcDevices.Add(new NfcDevice
        {
            Id = fixture.Device,
            Name = "NFC reader",
            PrinterId = fixture.FirstPrinter,
            LastHeartbeat = offline ? DateTime.UtcNow.AddMinutes(-10) : DateTime.UtcNow
        });
        if (known)
        {
            db.NfcTagBindings.Add(new NfcTagBinding
            {
                Id = Guid.NewGuid(),
                TagUid = "NFC-ISOLATION",
                SpoolId = 42,
                SpoolName = "Restricted spool",
                PrinterId = fixture.FirstPrinter,
                TrayId = "A1"
            });
        }
        await db.SaveChangesAsync();
        return fixture;
    }

    private sealed record ScanFixture(Guid FirstPrinter, Guid SecondPrinter, Guid FirstUser, Guid SecondUser, Guid Device);

    private sealed class TestIdentityHandler(HttpMessageHandler inner, Guid userId, string roles) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Add("X-Test-User-Id", userId.ToString());
            request.Headers.Add("X-Test-Roles", roles);
            request.Headers.Add("X-Test-Permissions", "nfc_devices:admin");
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
