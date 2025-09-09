using System.Text.Json;
using Farm.Web.Api.Data;
using Farm.Web.Api.Domain;
using Farm.Web.Api.Services.Interfaces;
using Farm.Web.Shared;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Farm.Web.Api.Tests;

public class DiscoveryExclusionIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public DiscoveryExclusionIntegrationTests(WebApplicationFactory<Program> factory)
    {
        if (factory is null)
        {
            throw new ArgumentNullException(nameof(factory));
        }
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                // Replace discovery settings with a deterministic /30 that includes 10.10.0.1 and 10.10.0.2
                services.AddSingleton<INetworkDiscoverySettingsService>(sp => new FixedRangeDiscoverySettingsService());

                // Swap AppDbContext to in-memory SQLite (shared connection) so we can seed
                var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(DbContextOptions<AppDbContext>));
                if (descriptor != null)
                {
                    services.Remove(descriptor);
                }
                var connection = new SqliteConnection("DataSource=:memory:");
                connection.Open();
                services.AddSingleton(connection);
                services.AddDbContext<AppDbContext>(options => options.UseSqlite(connection));

                // Reconfigure the HttpClient used by MoonrakerClient so that requests to 10.10.0.2 return a valid printer/info payload
                // Remove existing HttpClient registration for MoonrakerClient
                var httpClientDescriptors = services.Where(d => d.ServiceType == typeof(IHttpClientFactory) ||
                                                                (d.ImplementationType?.Name.Contains("MoonrakerClient") ?? false))
                                                     .ToList();
                foreach (var d in httpClientDescriptors)
                {
                    services.Remove(d);
                }
                services.AddHttpClient<Farm.Web.Api.Services.MoonrakerClient>()
                    .ConfigurePrimaryHttpMessageHandler(() => new StubMoonrakerHandler());
                services.AddScoped<IMoonrakerClient, Farm.Web.Api.Services.MoonrakerClient>();

                services.AddLogging(lb => lb.SetMinimumLevel(LogLevel.Warning));
            });
        });
    }

    [Fact(Timeout = 20000)]
    public async Task Streaming_discovery_should_exclude_already_added_printerAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.EnsureCreatedAsync();

        // Seed a printer that matches what discovery would find (10.10.0.2 on Moonraker port 7125)
        var seeded = new Printer
        {
            Id = Guid.NewGuid(),
            Name = "Seeded Printer",
            Backend = (int)PrinterBackend.Moonraker,
            ServerUrl = "http://10.10.0.2:7125"
        };
        db.Printers.Add(seeded);
        await db.SaveChangesAsync();

        using var client = _factory.CreateClient();

        string? sessionId = null;
        var hubConnection = new HubConnectionBuilder()
            .WithUrl(client.BaseAddress!.ToString().TrimEnd('/') + "/hubs/printers", options =>
            {
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
            })
            .WithAutomaticReconnect()
            .Build();

        var foundTcs = new TaskCompletionSource<DiscoveryPrinterFoundDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completedTcs = new TaskCompletionSource<DiscoveryCompletedDto>(TaskCreationOptions.RunContinuationsAsynchronously);

        hubConnection.On<DiscoveryPrinterFoundDto>("DiscoveryPrinterFound", dto =>
        {
            if (sessionId != null && dto.SessionId == sessionId)
            {
                // If we get a found event, that's a failure because it should have been excluded
                foundTcs.TrySetResult(dto);
            }
        });
        hubConnection.On<DiscoveryCompletedDto>("DiscoveryCompleted", dto =>
        {
            if (sessionId != null && dto.SessionId == sessionId)
            {
                completedTcs.TrySetResult(dto);
            }
        });

        await hubConnection.StartAsync();

        var startResponse = await client.PostAsync("/api/printers/discover/stream", new StringContent(""));
        startResponse.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await startResponse.Content.ReadAsStringAsync());
        sessionId = json.RootElement.GetProperty("sessionId").GetString();
        sessionId.Should().NotBeNull();

        await hubConnection.InvokeAsync("JoinDiscoveryGroupAsync", sessionId!);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var completed = await Task.WhenAny(completedTcs.Task, Task.Delay(-1, cts.Token));
        completed.Should().Be(completedTcs.Task, "discovery should complete");

        // Ensure no found printer event happened
        foundTcs.Task.IsCompleted.Should().BeFalse("already added printer must be excluded");
        var completion = await completedTcs.Task;
        completion.TotalPrintersFound.Should().Be(0, "excluded printer should not be counted");

        await hubConnection.DisposeAsync();
    }
}

file sealed class FixedRangeDiscoverySettingsService : INetworkDiscoverySettingsService
{
    public NetworkDiscoverySettingsDto GetSettings() => new([
        "10.10.0.0/30" // hosts: 10.10.0.1, 10.10.0.2
    ], 50, 2, [7125]);
    public void SaveSettings(NetworkDiscoverySettingsDto settings) { }
}

// Test Moonraker client returns info only for 10.10.0.2 (the seeded printer) to simulate discoverable host
file sealed class StubMoonrakerHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Respond only to printer/info for 10.10.0.2; other hosts 404
        if (request.RequestUri != null && request.RequestUri.Host == "10.10.0.2" && request.RequestUri.AbsolutePath.EndsWith("/printer/info", StringComparison.OrdinalIgnoreCase))
        {
            var json = "{\"hostname\":\"seeded-host\",\"state\":\"ready\",\"software_version\":\"v1\"}";
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            });
        }
        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
    }
}
