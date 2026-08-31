using System.Net.Http.Headers;
using System.Text.Json;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Authentication;
using Farm.Infrastructure.Services.Gcode;
using Farm.Infrastructure.Services.Maintenance;
using Farm.Testing.Shared;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Farm.Web.Api.Tests.Contracts;

/// <summary>
/// Wire-contract corpus for the <c>HarvestHub</c> and <c>MaintenanceHub</c> SignalR event
/// families (issue #2257 — a follow-up finding from #2260/#2238). Every payload captured here
/// is the exact bytes a real production code path pushed over a real connected
/// <see cref="HubConnection"/> against the in-process <c>TestServer</c> — never a hand-built DTO
/// serialized locally, matching the pattern established by <see cref="PrinterStatusContractTests"/>
/// and <see cref="SignalREventContractTests"/>.
/// </summary>
/// <remarks>
/// Two events <c>harvest-signalr.ts</c> subscribes to — <c>"harvestupdate"</c> and
/// <c>"harvestoperationcompleted"</c> — have no production broadcaster anywhere in this
/// codebase (confirmed by repo-wide search for a matching <c>SendAsync</c> call). They are
/// dead/unreachable client-side listeners and are intentionally NOT given fixtures here: doing
/// so would require hand-authoring a payload, which is exactly what issue #2238's real-production
/// -serialization requirement forbids. This is a documented, accepted gap, not an oversight.
/// </remarks>
public sealed class HarvestAndMaintenanceSignalREventContractTests : IAsyncLifetime
{
    private readonly CustomWebApplicationFactory _factory = CustomWebApplicationFactory.CreateWithIsolatedDatabase();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    /// <summary>
    /// <c>HarvestHub</c> <c>"harvestfileprogress"</c>, broadcast by the real
    /// <c>BroadcastFileProgressAsync</c> hub method (called by the backend during a real file
    /// download) to every client that joined the operation's <c>harvest-{operationId}</c> group.
    /// This hub method takes no DB state, so no seeding is required beyond a fresh operation id.
    /// </summary>
    [Fact]
    public async Task BroadcastFileProgress_RealHubMethod_SendsHarvestFileProgressEventMatchingCorpus()
    {
        Guid operationId = Guid.NewGuid();
        string token = await CreateFarmAdminTokenAsync();

        var receivedTcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using HubConnection connection = BuildHubConnection("/hubs/harvest", token);
        _ = connection.On<JsonElement>("harvestfileprogress", payload => receivedTcs.TrySetResult(payload));

        await connection.StartAsync();
        await connection.InvokeAsync("JoinHarvestGroupAsync", operationId);
        await connection.InvokeAsync(
            "BroadcastFileProgressAsync",
            operationId,
            "wire-contract-fixture.gcode",
            2_500_000L,
            10_000_000L);

        JsonElement received = await WaitForEventAsync(receivedTcs);

        _ = JsonContractAssertions.AssertProperty(received, "operationId", JsonValueKind.String);
        _ = JsonContractAssertions.AssertProperty(received, "fileName", JsonValueKind.String);
        _ = JsonContractAssertions.AssertProperty(received, "bytesCopied", JsonValueKind.Number);
        _ = JsonContractAssertions.AssertProperty(received, "totalBytes", JsonValueKind.Number);
        _ = JsonContractAssertions.AssertProperty(received, "percent", JsonValueKind.Number);

        string json = received.GetRawText();
        var volatilePaths = new HashSet<string> { "$.operationId" };
        await WireContractFixtureWriter.CaptureOrVerifyAsync(
            WireContractCorpusPaths.ApiRoot,
            "signalr-events/harvestfileprogress.populated.json",
            endpoint: "SignalR HarvestHub \"harvestfileprogress\" (BroadcastFileProgressAsync)",
            producingTest: $"{nameof(HarvestAndMaintenanceSignalREventContractTests)}.{nameof(BroadcastFileProgress_RealHubMethod_SendsHarvestFileProgressEventMatchingCorpus)}",
            schemaVersion: "1.0",
            actualJson: json,
            volatilePaths: volatilePaths);
    }

    /// <summary>
    /// <c>HarvestHub</c> <c>"harvestfileupdated"</c>, broadcast by the real
    /// <see cref="IGcodeHarvestService.SkipDiscoveredFileAsync"/> production call after it marks a
    /// seeded <see cref="HarvestDiscoveredFile"/> as <see cref="HarvestFileStatus.Skipped"/> — the
    /// real <c>MapToEventDto</c> mapping, not a hand-built <c>DiscoveredGcodeFileDto</c>.
    /// </summary>
    [Fact]
    public async Task SkipDiscoveredFile_RealServiceCall_SendsHarvestFileUpdatedEventMatchingCorpus()
    {
        Guid printerId = Guid.NewGuid();
        Guid operationId = Guid.NewGuid();
        Guid fileId = Guid.NewGuid();

        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await SeedPrinterAsync(db, printerId);

            db.GcodeHarvestOperations.Add(new GcodeHarvestOperation
            {
                Id = operationId,
                PrinterId = printerId,
                StartedAt = DateTime.UtcNow.AddMinutes(-5),
                Status = GcodeHarvestStatus.Running,
            });
            db.HarvestDiscoveredFiles.Add(new HarvestDiscoveredFile
            {
                Id = fileId,
                HarvestOperationId = operationId,
                FilePath = "/gcodes/wire-contract-fixture.gcode",
                FileName = "wire-contract-fixture.gcode",
                Size = 1_048_576,
                Status = HarvestFileStatus.Pending,
                DiscoveredAt = DateTime.UtcNow.AddMinutes(-4),
            });
            _ = await db.SaveChangesAsync();
        }

        string token = await CreateFarmAdminTokenAsync();
        var receivedTcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using HubConnection connection = BuildHubConnection("/hubs/harvest", token);
        _ = connection.On<JsonElement>("harvestfileupdated", payload => receivedTcs.TrySetResult(payload));

        await connection.StartAsync();
        await connection.InvokeAsync("JoinHarvestGroupAsync", operationId);

        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            IGcodeHarvestService harvestService = scope.ServiceProvider.GetRequiredService<IGcodeHarvestService>();
            bool skipped = await harvestService.SkipDiscoveredFileAsync(operationId, fileId);
            Assert.True(skipped, "SkipDiscoveredFileAsync should find the seeded discovered file and succeed.");
        }

        JsonElement received = await WaitForEventAsync(receivedTcs);

        JsonContractAssertions.AssertEnumToken(received, "status", "Skipped");
        _ = JsonContractAssertions.AssertProperty(received, "errorMessage", JsonValueKind.String);
        _ = JsonContractAssertions.AssertProperty(received, "printerPath", JsonValueKind.String);
        _ = JsonContractAssertions.AssertProperty(received, "fileName", JsonValueKind.String);
        JsonContractAssertions.AssertMissingKey(received, "extractedSlicerName");

        string json = received.GetRawText();
        var volatilePaths = new HashSet<string> { "$.id", "$.harvestOperationId" };
        await WireContractFixtureWriter.CaptureOrVerifyAsync(
            WireContractCorpusPaths.ApiRoot,
            "signalr-events/harvestfileupdated.skipped.json",
            endpoint: "SignalR HarvestHub \"harvestfileupdated\" (GcodeHarvestService.SkipDiscoveredFileAsync)",
            producingTest: $"{nameof(HarvestAndMaintenanceSignalREventContractTests)}.{nameof(SkipDiscoveredFile_RealServiceCall_SendsHarvestFileUpdatedEventMatchingCorpus)}",
            schemaVersion: "1.0",
            actualJson: json,
            volatilePaths: volatilePaths);
    }

    /// <summary>
    /// <c>MaintenanceHub</c> <c>"alertcreated"</c>, broadcast by the real
    /// <see cref="IMaintenanceAlertService.EvaluatePrinterMaintenanceAsync"/> production call after
    /// it determines a seeded day-based maintenance task is overdue for a real deployed
    /// <see cref="PrinterMaintenanceSchedule"/>.
    /// </summary>
    [Fact]
    public async Task EvaluatePrinterMaintenance_OverdueDeployment_SendsAlertCreatedEventMatchingCorpus()
    {
        Guid printerId = Guid.NewGuid();

        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await SeedPrinterAsync(db, printerId);

            db.PrinterStatisticsSet.Add(new PrinterStatistics
            {
                Id = Guid.NewGuid(),
                PrinterId = printerId,
                TotalPrintHours = 120.5,
            });

            var task = new MaintenanceTask
            {
                Id = Guid.NewGuid(),
                TaskName = "Wire Contract Nozzle Inspection",
                Category = "Hotend",
                IntervalDays = 10,
                Priority = 2,
                IsActive = true,
            };
            var plan = new MaintenancePlan
            {
                Id = Guid.NewGuid(),
                Name = "Wire Contract Maintenance Plan",
                IsActive = true,
            };
            var planTask = new PlanTask
            {
                Id = Guid.NewGuid(),
                MaintenancePlanId = plan.Id,
                MaintenanceTaskId = task.Id,
            };
            var schedule = new PrinterMaintenanceSchedule
            {
                Id = Guid.NewGuid(),
                MaintenancePlanId = plan.Id,
                PrinterId = printerId,
                IsActive = true,
                DeployedAt = DateTime.UtcNow.AddDays(-30),
            };

            db.MaintenanceTasks.Add(task);
            db.MaintenancePlans.Add(plan);
            db.PlanTasks.Add(planTask);
            db.PrinterMaintenanceSchedules.Add(schedule);
            _ = await db.SaveChangesAsync();
        }

        string token = await CreateFarmAdminTokenAsync();
        var receivedTcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using HubConnection connection = BuildHubConnection("/hubs/maintenance", token);
        _ = connection.On<JsonElement>("alertcreated", payload => receivedTcs.TrySetResult(payload));

        await connection.StartAsync();

        // HubConnection.StartAsync completing only proves the client received the SignalR
        // handshake response — it does NOT guarantee the server's MaintenanceHub.OnConnectedAsync
        // (which does the actual Groups.AddToGroupAsync(..., Farm) call this test relies on) has
        // finished running. Invoking the harmless, idempotent RequestAlertsUpdateAsync hub method
        // immediately after StartAsync forces a synchronization point: SignalR guarantees a
        // connection's OnConnectedAsync completes before any of its hub-method invocations are
        // dispatched, so once this call returns, the group join from OnConnectedAsync is
        // guaranteed complete. Without this, the broadcast below could race ahead of the group
        // join under load and the event would never arrive — a flake, not a production defect.
        // Mirrors the identical barrier pattern in SignalREventContractTests.cs.
        await connection.InvokeAsync("RequestAlertsUpdateAsync");

        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            IMaintenanceAlertService alertService = scope.ServiceProvider.GetRequiredService<IMaintenanceAlertService>();
            int generated = await alertService.EvaluatePrinterMaintenanceAsync(printerId);
            Assert.Equal(1, generated);
        }

        JsonElement received = await WaitForEventAsync(receivedTcs);

        _ = JsonContractAssertions.AssertProperty(received, "id", JsonValueKind.String);
        _ = JsonContractAssertions.AssertProperty(received, "printerId", JsonValueKind.String);
        _ = JsonContractAssertions.AssertProperty(received, "title", JsonValueKind.String);
        _ = JsonContractAssertions.AssertProperty(received, "message", JsonValueKind.String);

        string json = received.GetRawText();
        var volatilePaths = new HashSet<string> { "$.id", "$.printerId", "$.createdAt" };
        await WireContractFixtureWriter.CaptureOrVerifyAsync(
            WireContractCorpusPaths.ApiRoot,
            "signalr-events/alertcreated.populated.json",
            endpoint: "SignalR MaintenanceHub \"alertcreated\" (MaintenanceAlertEngine.EvaluatePrinterMaintenanceAsync)",
            producingTest: $"{nameof(HarvestAndMaintenanceSignalREventContractTests)}.{nameof(EvaluatePrinterMaintenance_OverdueDeployment_SendsAlertCreatedEventMatchingCorpus)}",
            schemaVersion: "1.0",
            actualJson: json,
            volatilePaths: volatilePaths);
    }

    /// <summary>
    /// <c>MaintenanceHub</c> <c>"alertstatuschanged"</c> and <c>"maintenancecompleted"</c>, both
    /// broadcast by the single real <see cref="IMaintenanceAlertResolutionService.ResolveAlertWithCompletionLogAsync"/>
    /// production call — <c>MaintenanceResolutionNotifier.NotifyCreatedAsync</c> sends both events
    /// from one atomic resolution, so one real call yields both fixtures.
    /// </summary>
    [Fact]
    public async Task ResolveAlertWithCompletionLog_RealServiceCall_SendsAlertStatusChangedAndMaintenanceCompletedEventsMatchingCorpus()
    {
        Guid printerId = Guid.NewGuid();
        Guid alertId = Guid.NewGuid();

        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await SeedPrinterAsync(db, printerId);

            db.MaintenanceAlerts.Add(new MaintenanceAlert
            {
                Id = alertId,
                PrinterId = printerId,
                Title = "Maintenance Due: Wire Contract Nozzle Inspection",
                Message = "Wire-contract fixture alert awaiting resolution.",
                Severity = 2,
                Status = MaintenanceAlertStatus.Active,
                PrinterHoursAtTrigger = 120.5,
            });
            _ = await db.SaveChangesAsync();
        }

        string token = await CreateFarmAdminTokenAsync();
        var alertStatusChangedTcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var maintenanceCompletedTcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using HubConnection connection = BuildHubConnection("/hubs/maintenance", token);
        _ = connection.On<JsonElement>("alertstatuschanged", payload => alertStatusChangedTcs.TrySetResult(payload));
        _ = connection.On<JsonElement>("maintenancecompleted", payload => maintenanceCompletedTcs.TrySetResult(payload));

        await connection.StartAsync();

        // See the identical barrier rationale on the alertcreated test above: force
        // MaintenanceHub.OnConnectedAsync's group join to complete before triggering the
        // broadcast, so this fixture-capture test cannot race and flake under load.
        await connection.InvokeAsync("RequestAlertsUpdateAsync");

        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            IMaintenanceAlertResolutionService resolutionService = scope.ServiceProvider.GetRequiredService<IMaintenanceAlertResolutionService>();
            MaintenanceAlertResolutionResult? result = await resolutionService.ResolveAlertWithCompletionLogAsync(
                alertId,
                resolvedBy: "wire-contract-operator",
                notes: "Resolved via wire-contract fixture test.");
            Assert.NotNull(result);
        }

        JsonElement alertStatusChanged = await WaitForEventAsync(alertStatusChangedTcs);
        JsonElement maintenanceCompleted = await WaitForEventAsync(maintenanceCompletedTcs);

        JsonContractAssertions.AssertEnumToken(alertStatusChanged, "status", "Resolved");
        _ = JsonContractAssertions.AssertProperty(alertStatusChanged, "resolvedAt", JsonValueKind.String);
        _ = JsonContractAssertions.AssertProperty(alertStatusChanged, "resolvedBy", JsonValueKind.String);
        JsonContractAssertions.AssertMissingKey(alertStatusChanged, "acknowledgedAt");

        _ = JsonContractAssertions.AssertProperty(maintenanceCompleted, "logId", JsonValueKind.String);
        _ = JsonContractAssertions.AssertProperty(maintenanceCompleted, "printerId", JsonValueKind.String);
        _ = JsonContractAssertions.AssertProperty(maintenanceCompleted, "performedAt", JsonValueKind.String);
        _ = JsonContractAssertions.AssertProperty(maintenanceCompleted, "performedBy", JsonValueKind.String);

        var alertStatusChangedVolatilePaths = new HashSet<string> { "$.id", "$.printerId", "$.resolvedAt" };
        await WireContractFixtureWriter.CaptureOrVerifyAsync(
            WireContractCorpusPaths.ApiRoot,
            "signalr-events/alertstatuschanged.resolved.json",
            endpoint: "SignalR MaintenanceHub \"alertstatuschanged\" (MaintenanceAlertResolutionService.ResolveAlertWithCompletionLogAsync)",
            producingTest: $"{nameof(HarvestAndMaintenanceSignalREventContractTests)}.{nameof(ResolveAlertWithCompletionLog_RealServiceCall_SendsAlertStatusChangedAndMaintenanceCompletedEventsMatchingCorpus)}",
            schemaVersion: "1.0",
            actualJson: alertStatusChanged.GetRawText(),
            volatilePaths: alertStatusChangedVolatilePaths);

        var maintenanceCompletedVolatilePaths = new HashSet<string> { "$.logId", "$.printerId", "$.deploymentId", "$.performedAt" };
        await WireContractFixtureWriter.CaptureOrVerifyAsync(
            WireContractCorpusPaths.ApiRoot,
            "signalr-events/maintenancecompleted.populated.json",
            endpoint: "SignalR MaintenanceHub \"maintenancecompleted\" (MaintenanceAlertResolutionService.ResolveAlertWithCompletionLogAsync)",
            producingTest: $"{nameof(HarvestAndMaintenanceSignalREventContractTests)}.{nameof(ResolveAlertWithCompletionLog_RealServiceCall_SendsAlertStatusChangedAndMaintenanceCompletedEventsMatchingCorpus)}",
            schemaVersion: "1.0",
            actualJson: maintenanceCompleted.GetRawText(),
            volatilePaths: maintenanceCompletedVolatilePaths);
    }

    /// <summary>
    /// Seeds the minimal Manufacturer → PrinterModel → Printer chain required to satisfy the
    /// foreign-key constraints enforced by the isolated SQLite test database, mirroring the
    /// pattern already used across <c>Farm.Web.Api.Tests</c> (e.g. <c>PrinterListDtoRowVersionTests</c>).
    /// </summary>
    private static async Task SeedPrinterAsync(AppDbContext db, Guid printerId)
    {
        Guid manufacturerId = Guid.NewGuid();
        Guid modelId = Guid.NewGuid();

        db.Manufacturers.Add(new Manufacturer { Id = manufacturerId, Name = $"Wire Contract Mfg {printerId:N}" });
        db.PrinterModels.Add(new PrinterModel { Id = modelId, ManufacturerId = manufacturerId, Name = "Wire Contract Model" });
        db.Printers.Add(new Printer
        {
            Id = printerId,
            Name = "Wire Contract Printer",
            ServerUrl = "http://192.168.1.99",
            BackendPort = 7125,
            Backend = (int)PrinterBackend.Moonraker,
            ManufacturerId = manufacturerId,
            ModelId = modelId,
        });
        _ = await db.SaveChangesAsync();
    }

    private HubConnection BuildHubConnection(string path, string token) =>
        new HubConnectionBuilder()
            .WithUrl(
                new Uri(_factory.Server.BaseAddress, path),
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
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using CancellationTokenRegistration registration = cts.Token.Register(
            () => tcs.TrySetException(new TimeoutException("Timed out waiting for the SignalR event to arrive.")));

#pragma warning disable VSTHRD003 // tcs is a TaskCompletionSource this test controls, completed by a real HubConnection event handler registered above; not a foreign/UI-thread task.
        return await tcs.Task;
#pragma warning restore VSTHRD003
    }

    /// <summary>
    /// Creates a fresh farm_admin bearer token. <c>HarvestHub.BroadcastFileProgressAsync</c>
    /// requires <c>gcode_harvest:admin</c> (farm_admin implies it), and <c>MaintenanceHub
    /// .OnConnectedAsync</c> only auto-joins the farm-wide group for callers with
    /// <c>maintenance:admin</c> (farm_admin implies it too) — mirroring
    /// <see cref="SignalREventContractTests"/>'s equivalent helper.
    /// </summary>
    private async Task<string> CreateFarmAdminTokenAsync()
    {
        string username = $"wire-contract-harvest-maint-admin-{Guid.NewGuid():N}";
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
