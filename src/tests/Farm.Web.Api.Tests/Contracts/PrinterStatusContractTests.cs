using System.Text.Json;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Authentication;
using Farm.Infrastructure.Services.Printers;
using Farm.Testing.Shared;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Farm.Web.Api.Tests.Contracts;

/// <summary>
/// Wire-contract corpus for the <c>PrinterHub</c> SignalR event family. Issue #2238: the
/// captured payload is the exact bytes the real <c>PrinterHub</c> sends over a real
/// <c>HubConnection</c> against the in-process <c>TestServer</c> — never a hand-built
/// <see cref="PrinterStatusDto"/> serialized locally. The hub's wire method name
/// (<c>"RequestPrinterStatus"</c>, via <c>[HubMethodName]</c>) and its lowercase
/// <c>"printerupdated"</c> event are exercised exactly as a real client would invoke/subscribe
/// to them. (<c>PrinterHub</c> also broadcasts a <c>"printerstatusesreplayed"</c> event on
/// bulk resync, which is NOT covered by this file — that is a candidate gap for a future
/// corpus addition, not claimed here.)
/// </summary>
public sealed class PrinterStatusContractTests : IAsyncLifetime
{
    private readonly CustomWebApplicationFactory _factory = CustomWebApplicationFactory.CreateWithIsolatedDatabase();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    /// <summary>
    /// Populated variant of the single-printer <c>"printerupdated"</c> event, produced by
    /// invoking the hub's real wire method name <c>"RequestPrinterStatus"</c> (not the C#
    /// method name <c>RequestPrinterStatusAsync</c>) against a status seeded through the real
    /// <see cref="IPrinterStatusCacheWriter"/> used by production polling services.
    /// </summary>
    [Fact]
    public async Task RequestPrinterStatus_CachedStatus_SendsPrinterUpdatedEventMatchingCorpus()
    {
        Guid printerId = Guid.NewGuid();

        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            IPrinterStatusCacheWriter statusWriter = scope.ServiceProvider.GetRequiredService<IPrinterStatusCacheWriter>();
            statusWriter.UpdateStatus(new PrinterStatusDto(
                printerId,
                IsOnline: true,
                State: "printing",
                Progress: 42.5,
                JobName: "wire-contract-fixture.gcode",
                HotendTemp: 215.4,
                BedTemp: 60.2,
                HotendTarget: 220,
                BedTarget: 60,
                SpeedMultiplier: 100));
        }

        string token = await CreateFarmAdminTokenAsync();

        var receivedTcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using HubConnection connection = BuildHubConnection(token);
        _ = connection.On<JsonElement>("printerupdated", payload => receivedTcs.TrySetResult(payload));

        await connection.StartAsync();
        await connection.InvokeAsync("RequestPrinterStatus", printerId.ToString());

        JsonElement received = await WaitForEventAsync(receivedTcs);

        JsonContractAssertions.AssertProperty(received, "isOnline", JsonValueKind.True);
        JsonContractAssertions.AssertProperty(received, "state", JsonValueKind.String);
        JsonContractAssertions.AssertProperty(received, "progress", JsonValueKind.Number);
        JsonContractAssertions.AssertMissingKey(received, "thumbnailUrl");
        JsonContractAssertions.AssertMissingKey(received, "cameraStreamUrl");
        JsonContractAssertions.AssertMissingKey(received, "cameraSnapshotUrl");
        JsonContractAssertions.AssertMissingKey(received, "spoolInfo");
        JsonContractAssertions.AssertMissingKey(received, "mmuStatus");

        string json = received.GetRawText();
        var volatilePaths = new HashSet<string> { "$.id" };
        await WireContractFixtureWriter.CaptureOrVerifyAsync(
            WireContractCorpusPaths.ApiRoot,
            "printer-status/printerupdated.populated.json",
            endpoint: "SignalR PrinterHub \"printerupdated\" (RequestPrinterStatus)",
            producingTest: $"{nameof(PrinterStatusContractTests)}.{nameof(RequestPrinterStatus_CachedStatus_SendsPrinterUpdatedEventMatchingCorpus)}",
            schemaVersion: "1.0",
            actualJson: json,
            volatilePaths: volatilePaths);
    }

    /// <summary>
    /// Missing-key variant: only the required <c>Id</c>/<c>IsOnline</c> fields are set on the
    /// cached status, so every optional <see cref="PrinterStatusDto"/> property is null and,
    /// per the real production <c>WhenWritingNull</c> policy, entirely absent from the wire
    /// payload — not an explicit JSON <c>null</c>.
    /// </summary>
    [Fact]
    public async Task RequestPrinterStatus_MinimalStatus_OmitsOptionalKeys()
    {
        Guid printerId = Guid.NewGuid();

        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            IPrinterStatusCacheWriter statusWriter = scope.ServiceProvider.GetRequiredService<IPrinterStatusCacheWriter>();
            statusWriter.UpdateStatus(new PrinterStatusDto(printerId, IsOnline: false, State: null));
        }

        string token = await CreateFarmAdminTokenAsync();
        var receivedTcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using HubConnection connection = BuildHubConnection(token);
        _ = connection.On<JsonElement>("printerupdated", payload => receivedTcs.TrySetResult(payload));

        await connection.StartAsync();
        await connection.InvokeAsync("RequestPrinterStatus", printerId.ToString());

        JsonElement received = await WaitForEventAsync(receivedTcs);

        JsonContractAssertions.AssertProperty(received, "isOnline", JsonValueKind.False);
        JsonContractAssertions.AssertMissingKey(received, "state");
        JsonContractAssertions.AssertMissingKey(received, "progress");
        JsonContractAssertions.AssertMissingKey(received, "jobName");
        JsonContractAssertions.AssertMissingKey(received, "fileName");
        JsonContractAssertions.AssertMissingKey(received, "hotendTemp");
        JsonContractAssertions.AssertMissingKey(received, "bedTemp");

        string json = received.GetRawText();
        var volatilePaths = new HashSet<string> { "$.id" };
        await WireContractFixtureWriter.CaptureOrVerifyAsync(
            WireContractCorpusPaths.ApiRoot,
            "printer-status/printerupdated.missing-key.json",
            endpoint: "SignalR PrinterHub \"printerupdated\" (RequestPrinterStatus)",
            producingTest: $"{nameof(PrinterStatusContractTests)}.{nameof(RequestPrinterStatus_MinimalStatus_OmitsOptionalKeys)}",
            schemaVersion: "1.0",
            actualJson: json,
            volatilePaths: volatilePaths);
    }

    private HubConnection BuildHubConnection(string token) =>
        new HubConnectionBuilder()
            .WithUrl(
                new Uri(_factory.Server.BaseAddress, "/hubs/printers"),
                options =>
                {
                    options.AccessTokenProvider = () => Task.FromResult<string?>(token);
                    options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                    options.Transports = HttpTransportType.WebSockets;
                    options.SkipNegotiation = true;
                    options.WebSocketFactory = async (context, cancellationToken) =>
                    {
                        string? accessToken = await context.Options.AccessTokenProvider!();
                        string authenticatedUrl = QueryHelpers.AddQueryString(
                            context.Uri.ToString(),
                            "access_token",
                            accessToken!);
                        return await _factory.Server
                            .CreateWebSocketClient()
                            .ConnectAsync(new Uri(authenticatedUrl), cancellationToken);
                    };
                })
            .Build();

    private static async Task<JsonElement> WaitForEventAsync(TaskCompletionSource<JsonElement> tcs)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using CancellationTokenRegistration registration = cts.Token.Register(
            () => tcs.TrySetException(new TimeoutException("Timed out waiting for the SignalR event to arrive.")));

#pragma warning disable VSTHRD003 // tcs is a TaskCompletionSource this test controls, completed by a real HubConnection event handler registered above; not a foreign/UI-thread task.
        return await tcs.Task;
#pragma warning restore VSTHRD003
    }

    /// <summary>
    /// Creates a fresh user, assigns the <c>farm_admin</c> role (mirroring
    /// <c>CustomWebApplicationFactory.CreateAdminClientAsync</c>'s role-creation logic, which
    /// only exposes a pre-authenticated <see cref="HttpClient"/> and never the raw JWT a
    /// <see cref="HubConnection"/> needs), and returns a real bearer token via
    /// <see cref="IAuthenticationService"/> — so <c>PrintFarmerPermissions.IsFarmAdmin</c>
    /// bypasses per-printer authorization inside <c>PrinterHub.EnsurePrinterAccessAsync</c>
    /// without seeding printer-group ownership.
    /// </summary>
    private async Task<string> CreateFarmAdminTokenAsync()
    {
        string username = $"wire-contract-printer-admin-{Guid.NewGuid():N}";
        string email = $"{username}@example.test";
        const string password = "WireContractPassword123!";
        Guid userId = Guid.NewGuid();

        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            IPasswordHashingService passwordHasher = scope.ServiceProvider.GetRequiredService<IPasswordHashingService>();

            db.Users.Add(new User
            {
                Id = userId,
                Username = username,
                Email = email,
                PasswordHash = passwordHasher.HashPassword(password),
                FirstName = "Wire",
                LastName = "Contract",
                IsActive = true,
                EmailConfirmed = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            _ = await db.SaveChangesAsync();

            Role? adminRole = await db.Roles.FirstOrDefaultAsync(r => r.Name == "farm_admin");
            if (adminRole is null)
            {
                adminRole = new Role
                {
                    Id = Guid.NewGuid(),
                    Name = "farm_admin",
                    Description = "Farm administrator",
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                };
                db.Roles.Add(adminRole);
                _ = await db.SaveChangesAsync();
            }

            db.UserRoles.Add(new UserRole
            {
                UserId = userId,
                RoleId = adminRole.Id,
                IsActive = true,
                AssignedAt = DateTime.UtcNow,
            });
            _ = await db.SaveChangesAsync();
        }

        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            IAuthenticationService auth = scope.ServiceProvider.GetRequiredService<IAuthenticationService>();
            AuthenticationResult result = await auth.AuthenticateAsync(username, password);
            if (!result.Success || string.IsNullOrWhiteSpace(result.Token))
            {
                throw new InvalidOperationException("Failed to authenticate the wire-contract farm_admin test user.");
            }

            return result.Token;
        }
    }
}
