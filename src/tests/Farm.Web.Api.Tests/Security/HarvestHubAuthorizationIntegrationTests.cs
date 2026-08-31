using System.Net.Http;
using Farm.Infrastructure.Services.Gcode;
using FluentAssertions;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace Farm.Web.Api.Tests.Security;

/// <summary>
/// End-to-end regression coverage for issue #2300: <c>HarvestHub</c> used to auto-join every
/// authenticated connection to the farm-wide harvest group (and <c>JoinHarvestGroupAsync</c> let
/// any authenticated caller join any per-operation group), so a caller with no
/// <c>gcode_harvest:admin</c> permission would still receive farm-wide and per-operation harvest
/// events — bypassing the same gate <c>GcodeHarvestController</c> enforces on the REST surface.
/// This test drives real hub connections (via <see cref="CustomWebApplicationFactory"/> and the
/// "TestScheme" header-based auth handler) and triggers the real
/// <see cref="IHarvestEventBroadcaster"/> production broadcast paths, then asserts who actually
/// receives the resulting SignalR events — mirroring
/// <see cref="MaintenanceHubAuthorizationIntegrationTests"/>'s farm-join assertions for the
/// equivalent <c>MaintenanceHub</c> fix (issue #1966).
/// </summary>
public sealed class HarvestHubAuthorizationIntegrationTests : IAsyncLifetime, IDisposable
{
    private readonly HarvestHubFactory _factory = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task FarmWideBroadcast_ExcludedUser_NeverReceivesEvent_ButHarvestAdminAndFarmAdminDo()
    {
        await using HubConnection excludedConnection = CreateConnection(Guid.NewGuid(), roles: "operator", permissions: null);
        await using HubConnection harvestAdminConnection = CreateConnection(Guid.NewGuid(), roles: "operator", permissions: "gcode_harvest:admin");
        await using HubConnection farmAdminConnection = CreateConnection(Guid.NewGuid(), roles: "farm_admin", permissions: null);

        var excludedReceived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var harvestAdminReceived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var farmAdminReceived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        excludedConnection.On<object>("singlefileharvestcomplete", _ => excludedReceived.TrySetResult(true));
        harvestAdminConnection.On<object>("singlefileharvestcomplete", _ => harvestAdminReceived.TrySetResult(true));
        farmAdminConnection.On<object>("singlefileharvestcomplete", _ => farmAdminReceived.TrySetResult(true));

        await excludedConnection.StartAsync();
        await harvestAdminConnection.StartAsync();
        await farmAdminConnection.StartAsync();

        // The excluded user holds no gcode_harvest:admin permission, so JoinHarvestGroupAsync
        // must reject it exactly like the farm-wide auto-join rejects it in OnConnectedAsync.
        Func<Task> excludedJoin = async () => await excludedConnection.InvokeAsync(
            "JoinHarvestGroupAsync",
            Guid.NewGuid());
        (await excludedJoin.Should().ThrowAsync<Microsoft.AspNetCore.SignalR.HubException>())
            .Which.Message.Should().Contain("resource_forbidden");

        // Act: broadcast a real "singlefileharvestcomplete" event via the real production
        // broadcaster (IHarvestEventBroadcaster/BroadcastToAllAsync), which targets the farm-wide
        // group — the harvestAdmin/farmAdmin connections auto-joined it in OnConnectedAsync, the
        // excluded connection did not.
        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            IHarvestEventBroadcaster broadcaster = scope.ServiceProvider.GetRequiredService<IHarvestEventBroadcaster>();
            await broadcaster.BroadcastSingleFileHarvestCompleteAsync(
                "wire-contract-fixture.gcode",
                success: true,
                message: "Harvest complete.");
        }

        (await harvestAdminReceived.Task.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue(
            "a gcode_harvest:admin caller must still see farm-wide harvest events");
        (await farmAdminReceived.Task.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue(
            "a farm_admin caller must still see farm-wide harvest events (farm_admin implies gcode_harvest:admin)");

        // Give the excluded connection a bounded window to (wrongly) receive the event; it must not.
        await Task.WhenAny(excludedReceived.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        excludedReceived.Task.IsCompleted.Should().BeFalse(
            "a caller with no gcode_harvest:admin permission must never receive farm-wide harvest events");
    }

    [Fact]
    public async Task PerOperationBroadcast_ExcludedUser_NeverReceivesEvent_ButHarvestAdminJoinerDoes()
    {
        Guid operationId = Guid.NewGuid();

        await using HubConnection excludedConnection = CreateConnection(Guid.NewGuid(), roles: "operator", permissions: null);
        await using HubConnection harvestAdminConnection = CreateConnection(Guid.NewGuid(), roles: "operator", permissions: "gcode_harvest:admin");

        var excludedReceived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var harvestAdminReceived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        excludedConnection.On<object>("harvestfilediscovered", _ => excludedReceived.TrySetResult(true));
        harvestAdminConnection.On<object>("harvestfilediscovered", _ => harvestAdminReceived.TrySetResult(true));

        await excludedConnection.StartAsync();
        await harvestAdminConnection.StartAsync();

        // The excluded user cannot join the per-operation group at all.
        Func<Task> excludedJoin = async () => await excludedConnection.InvokeAsync("JoinHarvestGroupAsync", operationId);
        (await excludedJoin.Should().ThrowAsync<Microsoft.AspNetCore.SignalR.HubException>())
            .Which.Message.Should().Contain("resource_forbidden");

        // The gcode_harvest:admin caller can join.
        await harvestAdminConnection.InvokeAsync("JoinHarvestGroupAsync", operationId);

        // Act: broadcast a real "harvestfilediscovered" event via the real production broadcaster
        // (IHarvestEventBroadcaster.BroadcastToGroupAsync), which targets only the
        // harvest-{operationId} group — mirroring GcodeHarvestService's real call site.
        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            IHarvestEventBroadcaster broadcaster = scope.ServiceProvider.GetRequiredService<IHarvestEventBroadcaster>();
            await broadcaster.BroadcastToGroupAsync(
                operationId,
                "harvestfilediscovered",
                new { fileName = "wire-contract-fixture.gcode" });
        }

        (await harvestAdminReceived.Task.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue(
            "the gcode_harvest:admin caller joined the operation group and must still see its events");

        await Task.WhenAny(excludedReceived.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        excludedReceived.Task.IsCompleted.Should().BeFalse(
            "a caller with no gcode_harvest:admin permission can never join a per-operation harvest group and must never receive its events");
    }

    private HubConnection CreateConnection(Guid userId, string roles, string? permissions)
    {
        return new HubConnectionBuilder()
            .WithUrl(
                new Uri(_factory.Server.BaseAddress, "/hubs/harvest"),
                options =>
                {
                    options.Transports = HttpTransportType.LongPolling;
                    options.HttpMessageHandlerFactory = _ => new TestIdentityHandler(
                        _factory.Server.CreateHandler(),
                        userId,
                        roles,
                        permissions);
                })
            .Build();
    }

    /// <summary>
    /// Wraps every outgoing SignalR HTTP request (negotiate + long-polling send/receive) with the
    /// header shape <see cref="Farm.Testing.Shared.TestAuthHandler"/> expects, so each real HTTP
    /// round-trip authenticates as the given user/roles/permissions — mirroring
    /// <see cref="MaintenanceHubAuthorizationIntegrationTests.TestIdentityHandler"/>.
    /// </summary>
    private sealed class TestIdentityHandler(HttpMessageHandler inner, Guid userId, string roles, string? permissions)
        : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Remove("X-Test-User-Id");
            request.Headers.Remove("X-Test-Roles");
            request.Headers.Remove("X-Test-Permissions");
            request.Headers.Add("X-Test-User-Id", userId.ToString());
            request.Headers.Add("X-Test-Roles", roles);
            if (!string.IsNullOrEmpty(permissions))
            {
                request.Headers.Add("X-Test-Permissions", permissions);
            }

            return base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class HarvestHubFactory() : CustomWebApplicationFactory(
        new Dictionary<string, string?>
        {
            ["Testing:UseTestAuthentication"] = "true",
            ["Security:DevModeBypassAuth"] = "false",
        });
}
