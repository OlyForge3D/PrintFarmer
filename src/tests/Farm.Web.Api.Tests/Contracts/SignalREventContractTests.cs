using System.Net;
using System.Text.Json;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services;
using Farm.Infrastructure.Services.Authentication;
using Farm.Infrastructure.Services.Discovery;
using Farm.Slicer.Module.Domain;
using Farm.Slicer.Module.Dtos;
using Farm.Slicer.Module.Models;
using Farm.Slicer.Module.Services;
using Farm.Testing.Shared;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Farm.Web.Api.Tests.Contracts;

/// <summary>
/// Wire-contract corpus for the remaining lowercase SignalR event families beyond
/// <c>"printerupdated"</c> (covered by <see cref="PrinterStatusContractTests"/>): the
/// discovery-progress family on <c>PrinterHub</c> (<c>"discoveryprogress"</c>), and the
/// slice-job families on <c>SlicerProgressHub</c> (<c>"slicejobevent"</c>,
/// <c>"slicingprogress"</c>, <c>"slicingcompleted"</c>, <c>"slicingfailed"</c>). Every payload
/// is captured from a real connected <see cref="HubConnection"/> against the in-process
/// <c>TestServer</c>, receiving whatever the real application service pushed via
/// <c>IHubContext&lt;T&gt;.Clients...SendAsync</c> — never a hand-built DTO serialized locally.
/// </summary>
/// <remarks>
/// Per issue #2238's acceptance criteria, this file also documents the one PascalCase
/// exception to the lowercase SignalR event-name convention: <c>SlicerHub</c> (the
/// administrator-only worker-registration hub, distinct from <c>SlicerProgressHub</c> covered
/// here) sends <c>SlicerHubEvents.SlicerRegistered</c>/<c>SlicerHeartbeat</c>/
/// <c>SlicerDeregistered</c>/<c>SlicerApiKeyRotated</c> — all PascalCase constants (see
/// <c>src/slicer/Farm.Slicer.Module.Api/Hubs/SlicerHub.cs</c>). Driving that hub's full
/// worker-registration flow through a real HTTP/SignalR round trip requires standing up a
/// registered slicer-service identity and worker heartbeat plumbing that is out of proportion
/// to what this corpus needs to prove; the exact PascalCase tokens are already pinned as CLR
/// constants by the existing <c>SlicerHubTests.SlicerHubEvents_*_HasCorrectValue</c> unit tests
/// in <c>Farm.Slicer.Module.Tests</c>, and since the event names are literal C# string
/// constants sent verbatim (no enum conversion, no camelCase-ing applied to event/method
/// names by the SignalR protocol), those tests are equivalent proof that the wire token is
/// exactly the PascalCase constant value.
/// </remarks>
public sealed class SignalREventContractTests : IAsyncLifetime
{
    private readonly CustomWebApplicationFactory _factory = CustomWebApplicationFactory.CreateWithIsolatedDatabase();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    /// <summary>
    /// <c>PrinterHub</c> <c>"discoveryprogress"</c>, replayed on <c>JoinDiscoveryGroupAsync</c>
    /// from a real cached <see cref="DiscoveryProgressDto"/>. Confirms the two
    /// <c>JsonIgnore(Condition = JsonIgnoreCondition.WhenWriting)</c> fields
    /// (<c>CurrentNetwork</c>/<c>CurrentIp</c>) are unconditionally absent from the wire
    /// payload — a real production redaction distinct from the ordinary null-omission policy.
    /// </summary>
    [Fact]
    public async Task JoinDiscoveryGroup_CachedProgress_ReplaysDiscoveryProgressEvent()
    {
        string sessionId = $"wire-contract-session-{Guid.NewGuid():N}";
        Guid userId;

        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            IDiscoverySessionRegistry sessions = scope.ServiceProvider.GetRequiredService<IDiscoverySessionRegistry>();
            IDiscoveryProgressCache cache = scope.ServiceProvider.GetRequiredService<IDiscoveryProgressCache>();
            userId = Guid.NewGuid();
            sessions.RegisterSession(sessionId, userId);
            cache.Set(
                sessionId,
                new DiscoveryProgressDto(
                    SessionId: sessionId,
                    CurrentNetwork: "192.168.1.0/24",
                    CurrentIp: "192.168.1.42",
                    TotalIps: 254,
                    ScannedIps: 128,
                    PrintersFound: 2,
                    PrintersExcluded: 1,
                    ProgressPercentage: 50.0,
                    Status: DiscoveryStatus.Scanning,
                    Message: "Scanning network"));
        }

        string token = await CreateFarmAdminTokenAsync();
        var receivedTcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using HubConnection connection = BuildHubConnection("/hubs/printers", token);
        _ = connection.On<JsonElement>("discoveryprogress", payload => receivedTcs.TrySetResult(payload));

        await connection.StartAsync();
        await connection.InvokeAsync("JoinDiscoveryGroupAsync", sessionId);

        JsonElement received = await WaitForEventAsync(receivedTcs);

        JsonContractAssertions.AssertEnumToken(received, "status", "Scanning");
        _ = JsonContractAssertions.AssertProperty(received, "progressPercentage", JsonValueKind.Number);
        _ = JsonContractAssertions.AssertProperty(received, "message", JsonValueKind.String);
        JsonContractAssertions.AssertMissingKey(received, "currentNetwork");
        JsonContractAssertions.AssertMissingKey(received, "currentIp");
        JsonContractAssertions.AssertMissingKey(received, "networkRanges");

        string json = received.GetRawText();
        var volatilePaths = new HashSet<string> { "$.sessionId" };
        await WireContractFixtureWriter.CaptureOrVerifyAsync(
            WireContractCorpusPaths.ApiRoot,
            "signalr-events/discoveryprogress.populated.json",
            endpoint: "SignalR PrinterHub \"discoveryprogress\" (JoinDiscoveryGroupAsync)",
            producingTest: $"{nameof(SignalREventContractTests)}.{nameof(JoinDiscoveryGroup_CachedProgress_ReplaysDiscoveryProgressEvent)}",
            schemaVersion: "1.0",
            actualJson: json,
            volatilePaths: volatilePaths);
    }

    /// <summary>
    /// <c>SlicerProgressHub</c> <c>"slicejobevent"</c>, real-broadcast via
    /// <see cref="ISliceJobEventService.NotifyJobQueuedAsync"/> to the <c>SlicingMonitors</c>
    /// group a <c>farm_admin</c> connection auto-joins on connect.
    /// </summary>
    [Fact]
    public async Task NotifyJobQueued_RealBroadcast_SendsSliceJobEventMatchingCorpus()
    {
        string token = await CreateFarmAdminTokenAsync();
        var receivedTcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using HubConnection connection = BuildHubConnection("/hubs/slicers", token);
        _ = connection.On<JsonElement>("slicejobevent", payload => receivedTcs.TrySetResult(payload));
        await connection.StartAsync();

        var job = new SliceJob
        {
            Id = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            ModelFileUrl = "/api/3d-models/wire-contract-model.3mf",
            ModelFileName = "wire-contract-model.3mf",
            SlicerEngineName = "OrcaSlicer",
            Status = SliceJobStatus.Queued,
            QueuedAt = DateTime.UtcNow,
        };

        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            ISliceJobEventService events = scope.ServiceProvider.GetRequiredService<ISliceJobEventService>();
            await events.NotifyJobQueuedAsync(job);
        }

        JsonElement received = await WaitForEventAsync(receivedTcs);

        JsonContractAssertions.AssertProperty(received, "eventType", JsonValueKind.String);
        JsonContractAssertions.AssertEnumToken(received, "status", "Queued");
        JsonContractAssertions.AssertMissingKey(received, "startedAt");
        JsonContractAssertions.AssertMissingKey(received, "workerId");

        string json = received.GetRawText();
        var volatilePaths = new HashSet<string> { "$.jobId", "$.userId", "$.queuedAt", "$.timestamp" };
        await WireContractFixtureWriter.CaptureOrVerifyAsync(
            WireContractCorpusPaths.ApiRoot,
            "signalr-events/slicejobevent.queued.json",
            endpoint: "SignalR SlicerProgressHub \"slicejobevent\" (NotifyJobQueuedAsync)",
            producingTest: $"{nameof(SignalREventContractTests)}.{nameof(NotifyJobQueued_RealBroadcast_SendsSliceJobEventMatchingCorpus)}",
            schemaVersion: "1.0",
            actualJson: json,
            volatilePaths: volatilePaths);
    }

    /// <summary>
    /// <c>SlicerProgressHub</c> <c>"slicingprogress"</c>, real-broadcast via
    /// <see cref="ISlicerProgressNotifier.NotifyProgressAsync"/>.
    /// </summary>
    [Fact]
    public async Task NotifyProgress_RealBroadcast_SendsSlicingProgressMatchingCorpus()
    {
        string token = await CreateFarmAdminTokenAsync();
        var receivedTcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using HubConnection connection = BuildHubConnection("/hubs/slicers", token);
        _ = connection.On<JsonElement>("slicingprogress", payload => receivedTcs.TrySetResult(payload));
        await connection.StartAsync();

        var update = new SlicingProgressUpdate
        {
            JobId = Guid.NewGuid(),
            Progress = 63,
            Status = SlicingJobStatus.Slicing,
            CurrentStep = "Generating toolpaths",
        };

        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            ISlicerProgressNotifier notifier = scope.ServiceProvider.GetRequiredService<ISlicerProgressNotifier>();
            await notifier.NotifyProgressAsync(update);
        }

        JsonElement received = await WaitForEventAsync(receivedTcs);

        JsonContractAssertions.AssertEnumToken(received, "status", "Slicing");
        _ = JsonContractAssertions.AssertProperty(received, "progress", JsonValueKind.Number);
        _ = JsonContractAssertions.AssertProperty(received, "currentStep", JsonValueKind.String);

        string json = received.GetRawText();
        var volatilePaths = new HashSet<string> { "$.jobId", "$.timestamp" };
        await WireContractFixtureWriter.CaptureOrVerifyAsync(
            WireContractCorpusPaths.ApiRoot,
            "signalr-events/slicingprogress.populated.json",
            endpoint: "SignalR SlicerProgressHub \"slicingprogress\" (NotifyProgressAsync)",
            producingTest: $"{nameof(SignalREventContractTests)}.{nameof(NotifyProgress_RealBroadcast_SendsSlicingProgressMatchingCorpus)}",
            schemaVersion: "1.0",
            actualJson: json,
            volatilePaths: volatilePaths);
    }

    /// <summary>
    /// <c>SlicerProgressHub</c> <c>"slicingcompleted"</c>, real-broadcast via
    /// <see cref="ISlicerProgressNotifier.NotifyCompletionAsync"/>.
    /// </summary>
    [Fact]
    public async Task NotifyCompletion_RealBroadcast_SendsSlicingCompletedMatchingCorpus()
    {
        string token = await CreateFarmAdminTokenAsync();
        var receivedTcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using HubConnection connection = BuildHubConnection("/hubs/slicers", token);
        _ = connection.On<JsonElement>("slicingcompleted", payload => receivedTcs.TrySetResult(payload));
        await connection.StartAsync();

        var job = new DistributedSlicingJob
        {
            Id = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            Status = SlicingJobStatus.Completed,
            CompletedAt = DateTime.UtcNow,
        };
        var result = new SlicingResult
        {
            Success = true,
            ProcessingTimeSeconds = 12.5,
            EstimatedPrintTimeSeconds = 5400,
            EstimatedFilamentUsageGrams = 42.75,
            LayerCount = 180,
        };

        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            ISlicerProgressNotifier notifier = scope.ServiceProvider.GetRequiredService<ISlicerProgressNotifier>();
            await notifier.NotifyCompletionAsync(job, result);
        }

        JsonElement received = await WaitForEventAsync(receivedTcs);

        JsonContractAssertions.AssertEnumToken(received, "status", "Completed");
        _ = JsonContractAssertions.AssertProperty(received, "success", JsonValueKind.True);
        _ = JsonContractAssertions.AssertProperty(received, "processingTimeSeconds", JsonValueKind.Number);
        JsonContractAssertions.AssertMissingKey(received, "errorMessage");

        string json = received.GetRawText();
        var volatilePaths = new HashSet<string> { "$.jobId", "$.userId", "$.completedAt", "$.artifactsRoute" };
        await WireContractFixtureWriter.CaptureOrVerifyAsync(
            WireContractCorpusPaths.ApiRoot,
            "signalr-events/slicingcompleted.populated.json",
            endpoint: "SignalR SlicerProgressHub \"slicingcompleted\" (NotifyCompletionAsync)",
            producingTest: $"{nameof(SignalREventContractTests)}.{nameof(NotifyCompletion_RealBroadcast_SendsSlicingCompletedMatchingCorpus)}",
            schemaVersion: "1.0",
            actualJson: json,
            volatilePaths: volatilePaths);
    }

    /// <summary>
    /// <c>SlicerProgressHub</c> <c>"slicingfailed"</c>, real-broadcast via
    /// <see cref="ISlicerProgressNotifier.NotifyFailureAsync"/>. The redacted, constant
    /// <c>"Slicing failed."</c> message (never the raw worker error) is a real production
    /// redaction, not an assumption.
    /// </summary>
    [Fact]
    public async Task NotifyFailure_RealBroadcast_SendsSlicingFailedMatchingCorpus()
    {
        string token = await CreateFarmAdminTokenAsync();
        var receivedTcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using HubConnection connection = BuildHubConnection("/hubs/slicers", token);
        _ = connection.On<JsonElement>("slicingfailed", payload => receivedTcs.TrySetResult(payload));
        await connection.StartAsync();

        var job = new DistributedSlicingJob
        {
            Id = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            Status = SlicingJobStatus.Error,
            RetryCount = 1,
        };

        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            ISlicerProgressNotifier notifier = scope.ServiceProvider.GetRequiredService<ISlicerProgressNotifier>();
            await notifier.NotifyFailureAsync(job, "worker: internal CLI_SLICING_ERROR at /var/worker/model.3mf");
        }

        JsonElement received = await WaitForEventAsync(receivedTcs);

        JsonContractAssertions.AssertEnumToken(received, "status", "Error");
        JsonElement errorMessage = JsonContractAssertions.AssertProperty(received, "errorMessage", JsonValueKind.String);
        Assert.Equal("Slicing failed.", errorMessage.GetString());
        _ = JsonContractAssertions.AssertProperty(received, "canRetry", JsonValueKind.True);

        string json = received.GetRawText();
        var volatilePaths = new HashSet<string> { "$.jobId", "$.userId", "$.failedAt" };
        await WireContractFixtureWriter.CaptureOrVerifyAsync(
            WireContractCorpusPaths.ApiRoot,
            "signalr-events/slicingfailed.populated.json",
            endpoint: "SignalR SlicerProgressHub \"slicingfailed\" (NotifyFailureAsync)",
            producingTest: $"{nameof(SignalREventContractTests)}.{nameof(NotifyFailure_RealBroadcast_SendsSlicingFailedMatchingCorpus)}",
            schemaVersion: "1.0",
            actualJson: json,
            volatilePaths: volatilePaths);
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
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using CancellationTokenRegistration registration = cts.Token.Register(
            () => tcs.TrySetException(new TimeoutException("Timed out waiting for the SignalR event to arrive.")));

#pragma warning disable VSTHRD003 // tcs is a TaskCompletionSource this test controls, completed by a real HubConnection event handler registered above; not a foreign/UI-thread task.
        return await tcs.Task;
#pragma warning restore VSTHRD003
    }

    /// <summary>
    /// Creates a fresh farm_admin bearer token. <c>SlicerProgressHub.OnConnectedAsync</c> only
    /// auto-joins the <c>SlicingMonitors</c> group for callers with <c>PrintFarmerPermissions
    /// .IsFarmAdmin</c>, and <c>PrinterHub.JoinDiscoveryGroupAsync</c> bypasses per-session
    /// ownership for the same role — mirroring <see cref="PrinterStatusContractTests"/>'s
    /// equivalent helper.
    /// </summary>
    private async Task<string> CreateFarmAdminTokenAsync()
    {
        string username = $"wire-contract-signalr-admin-{Guid.NewGuid():N}";
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
