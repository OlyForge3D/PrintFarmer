using System.Text.Json;
using Farm.Web.Api.Data;
using Farm.Web.Api.Services.Interfaces;
using Farm.Web.Shared;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
namespace Farm.Web.Api.Tests;

[Trait("Category", "DbHeavy")]
[Collection("DbHeavySerial")]
[TestTiming]
public class DiscoverySignalRIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public DiscoverySignalRIntegrationTests(WebApplicationFactory<Program> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                // Replace network discovery settings with a tiny pretend network (/30 gives 2 usable hosts)
                services.AddSingleton<INetworkDiscoverySettingsService>(sp => new TestDiscoverySettingsService());

                // Swap the AppDbContext to an in-memory SQLite database to guarantee fresh schema per test run.
                // Remove existing context registration
                var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(DbContextOptions<AppDbContext>));
                if (descriptor != null)
                {
                    services.Remove(descriptor);
                }

                // Create and open shared in-memory connection (must stay open for lifetime of factory)
                var connection = new SqliteConnection("DataSource=:memory:");
                connection.Open();
                services.AddSingleton(connection); // dispose with container

                services.AddDbContext<AppDbContext>(options =>
                {
                    options.UseSqlite(connection);
                });

                // Suppress verbose EF Core SQL command logs during tests (set global minimum to Warning)
                services.AddLogging(lb => lb.SetMinimumLevel(LogLevel.Warning));
            });
        });
    }

    [Fact(Timeout = 15000)]
    public async Task DiscoveryProgress_event_should_include_new_fieldsAsync()
    {
        using var client = _factory.CreateClient();

        // 1. Establish hub connection first so we can join group immediately after obtaining the session id
        string? sessionId = null; // declare early for closure

        var hubConnection = new HubConnectionBuilder()
            .WithUrl(client.BaseAddress!.ToString().TrimEnd('/') + "/hubs/printers", options =>
            {
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
            })
            .WithAutomaticReconnect()
            .Build();

        var tcs = new TaskCompletionSource<DiscoveryProgressDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        hubConnection.On<DiscoveryProgressDto>("DiscoveryProgress", prog =>
        {
            if (sessionId != null && prog.SessionId == sessionId)
            {
                tcs.TrySetResult(prog);
            }
        });

        await hubConnection.StartAsync();

        // 2. Start discovery to get session id
        var startResponse = await client.PostAsync("/api/printers/discover/stream", new StringContent(""));
        startResponse.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await startResponse.Content.ReadAsStringAsync());
        sessionId = doc.RootElement.GetProperty("sessionId").GetString();
        sessionId.Should().NotBeNull();

        // Join group after session id issuance (initial Starting event may still be in-flight, so we allow for a second progress event if missed)
        await hubConnection.InvokeAsync("JoinDiscoveryGroupAsync", sessionId!);

        // 3. Wait (with generous timeout) for first progress message corresponding to the session
        // Bounded wait to avoid potential hang if cancellation token not triggered
        var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        completed.Should().Be(tcs.Task, "a DiscoveryProgress event for the session should arrive");

        var progressDto = await tcs.Task;
        progressDto.NetworkRanges.Should().NotBeNull();
        progressDto.NetworkRanges!.Count.Should().BeGreaterThan(0);
        progressDto.AutoDetectedNetworks.Should().BeFalse();
        progressDto.TotalIps.Should().BeGreaterThanOrEqualTo(0);

        await hubConnection.DisposeAsync();
    }

    [Fact(Timeout = 20000)]
    public async Task DiscoveryProgress_event_should_set_autoDetected_true_when_networks_auto_detectedAsync()
    {
        // Create a new factory instance that returns no configured networks so auto-detection path executes
        var autoFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                // Remove prior explicit TestDiscoverySettingsService registration
                var existing = services.FirstOrDefault(d => d.ServiceType == typeof(INetworkDiscoverySettingsService));
                if (existing != null)
                {
                    services.Remove(existing);
                }
                services.AddSingleton<INetworkDiscoverySettingsService>(sp => new EmptyDiscoverySettingsService());
                services.AddLogging(lb => lb.SetMinimumLevel(LogLevel.Warning));
            });
        });

        using var client = autoFactory.CreateClient();

        string? sessionId = null;
        var hubConnection = new HubConnectionBuilder()
            .WithUrl(client.BaseAddress!.ToString().TrimEnd('/') + "/hubs/printers", options =>
            {
                options.HttpMessageHandlerFactory = _ => autoFactory.Server.CreateHandler();
            })
            .WithAutomaticReconnect()
            .Build();

        var tcs = new TaskCompletionSource<DiscoveryProgressDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        hubConnection.On<DiscoveryProgressDto>("DiscoveryProgress", prog =>
        {
            if (sessionId != null && prog.SessionId == sessionId)
            {
                tcs.TrySetResult(prog);
            }
        });

        await hubConnection.StartAsync();

        var startResponse = await client.PostAsync("/api/printers/discover/stream", new StringContent(""));
        startResponse.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await startResponse.Content.ReadAsStringAsync());
        sessionId = doc.RootElement.GetProperty("sessionId").GetString();
        sessionId.Should().NotBeNull();

        await hubConnection.InvokeAsync("JoinDiscoveryGroupAsync", sessionId!);

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(12)));
        completed.Should().Be(tcs.Task, "a DiscoveryProgress event for the session should arrive");

        var progressDto = await tcs.Task;
        progressDto.NetworkRanges.Should().NotBeNull();
        progressDto.NetworkRanges!.Count.Should().BeGreaterThan(0); // Auto-detected at least one network
        progressDto.AutoDetectedNetworks.Should().BeTrue();

        await hubConnection.DisposeAsync();
    }
}

file sealed class TestDiscoverySettingsService : INetworkDiscoverySettingsService
{
    public NetworkDiscoverySettingsDto GetSettings() => new([
        "127.0.0.0/30" // Deterministic tiny network (2 usable hosts) so progress event(s) are fast & predictable
    ], 50, 2, [65535]);
    public void SaveSettings(NetworkDiscoverySettingsDto settings) { }
}

file sealed class EmptyDiscoverySettingsService : INetworkDiscoverySettingsService
{
    public NetworkDiscoverySettingsDto GetSettings() => new([], 50, 2, [65535]);
    public void SaveSettings(NetworkDiscoverySettingsDto settings) { }
}
