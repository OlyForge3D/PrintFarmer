using System.Text.Json;
using Farm.Infrastructure.Settings;
using Farm.Web.Api.Services;
using Farm.Web.Api.Services.Interfaces;
using Farm.Web.Api.Tests.Infrastructure;
using Farm.Web.Shared;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.TestHost;

namespace Farm.Web.Api.Tests;

[Trait("Category", "DbHeavy")]
[Collection("DbHeavySerialWithSharedFixture")]
[TestTiming]
public class DiscoverySignalRIntegrationTests : DbHeavyTestBase<Program>
{
    public DiscoverySignalRIntegrationTests(WebApplicationFactory<Program> factory)
        : base(factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                // Replace network discovery settings with a tiny pretend network (/30 gives 2 usable hosts)
                // Use SettingsService or direct configuration for network discovery settings
                // Suppress verbose EF Core SQL command logs during tests (set global minimum to Warning)
                services.AddLogging(lb => lb.SetMinimumLevel(LogLevel.Warning));
            });
        }))
    {
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
            builder.ConfigureTestServices(services =>
            {
                // Use SettingsService or direct configuration for network discovery settings
                services.AddLogging(lb => lb.SetMinimumLevel(LogLevel.Warning));

                // Replace ISettingsService with a test implementation that returns
                // an empty NetworkDiscoverySettings so the auto-detection path runs.
                try
                {
                    var existing = services.SingleOrDefault(d => d.ServiceType == typeof(Farm.Infrastructure.Settings.ISettingsService));
                    if (existing != null)
                    {
                        services.Remove(existing);
                    }
                }
                catch { }

                services.AddSingleton<Farm.Infrastructure.Settings.ISettingsService>(new TestOnlySettingsService());
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

internal class TestOnlySettingsService : Farm.Infrastructure.Settings.ISettingsService
{
    public IEnumerable<object> All => Enumerable.Empty<object>();

    public T Get<T>() where T : class
    {
        if (typeof(T) == typeof(Farm.Infrastructure.Settings.NetworkDiscoverySettings))
        {
            var ns = new Farm.Infrastructure.Settings.NetworkDiscoverySettings();
            // Ensure no configured subnets so auto-detection triggers
            ns.DiscoverySubnets = new List<string>();
            return (T)(object)ns;
        }

        // Provide a default instance for other settings types if possible.
        // If we can't instantiate the requested settings type, throw a clear exception
        // so tests fail fast instead of returning null.
        try
        {
            return Activator.CreateInstance<T>();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"TestOnlySettingsService cannot create an instance of settings type {typeof(T).FullName}. Add handling to TestOnlySettingsService for this type.", ex);
        }
    }

    public object GetByKey(string key) => throw new NotSupportedException();
    public IEnumerable<Farm.Infrastructure.Settings.SettingMetadata> GetAllMetadata() => Enumerable.Empty<Farm.Infrastructure.Settings.SettingMetadata>();
    public void Reload(Microsoft.Extensions.Configuration.IConfiguration config) { }
    public void Save<T>(T settings) where T : class, Farm.Infrastructure.Settings.IAppSetting => throw new NotSupportedException();
}


// Removed obsolete IAppSettingsService test mocks
