using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services;
using Farm.Infrastructure.Services.Authentication;
using Farm.Infrastructure.Services.Discovery;
using Farm.Slicer.Module.Contracts;
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
/// Per issue #2238's acceptance criteria, this file also proves the one PascalCase exception
/// to the lowercase SignalR event-name convention: <c>SlicerHub</c> (the administrator-only
/// worker-registration hub, distinct from <c>SlicerProgressHub</c> covered here) sends
/// <c>SlicerHubEvents.SlicerRegistered</c> — a PascalCase constant (see
/// <c>src/slicer/Farm.Slicer.Module.Api/Hubs/SlicerHub.cs</c>) — with a real producer and
/// consumer: <see cref="SlicerRegister_RealBroadcast_SendsSlicerRegisteredMatchingCorpus"/>
/// connects a real <see cref="HubConnection"/> to <c>/hubs/slicer-registry</c> and drives the
/// production <c>ISlicersService.RegisterAsync</c> call whose <c>SlicersService.RegisterAsync</c>
/// implementation performs the real <c>IHubContext&lt;SlicerHub&gt;.Clients...SendAsync</c>
/// broadcast — the same real-producer/real-consumer pattern used for every other event in this
/// file, not a hand-built object. The token itself remains additionally pinned as a CLR constant
/// by <c>SlicerHubTests.SlicerHubEvents_*_HasCorrectValue</c> in <c>Farm.Slicer.Module.Tests</c>.
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
    /// Same event as <see cref="JoinDiscoveryGroup_CachedProgress_ReplaysDiscoveryProgressEvent"/>
    /// with <c>Message: null</c> — the optional-with-default-null property has no per-property
    /// <c>JsonIgnore</c> override, so it falls through to the hub's global
    /// <c>DefaultIgnoreCondition = WhenWritingNull</c> policy and the key is OMITTED, not
    /// serialized as an explicit JSON null. This is the "missing key" variant for a payload
    /// whose optional field is legal to omit (issue #2238's variant matrix).
    /// </summary>
    [Fact]
    public async Task JoinDiscoveryGroup_CachedProgressNoMessage_OmitsMessageKey()
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
                    ScannedIps: 0,
                    PrintersFound: 0,
                    PrintersExcluded: 0,
                    ProgressPercentage: 0.0,
                    Status: DiscoveryStatus.Scanning,
                    Message: null));
        }

        string token = await CreateFarmAdminTokenAsync();
        var receivedTcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using HubConnection connection = BuildHubConnection("/hubs/printers", token);
        _ = connection.On<JsonElement>("discoveryprogress", payload => receivedTcs.TrySetResult(payload));

        await connection.StartAsync();
        await connection.InvokeAsync("JoinDiscoveryGroupAsync", sessionId);

        JsonElement received = await WaitForEventAsync(receivedTcs);

        JsonContractAssertions.AssertMissingKey(received, "message");

        string json = received.GetRawText();
        var volatilePaths = new HashSet<string> { "$.sessionId" };
        await WireContractFixtureWriter.CaptureOrVerifyAsync(
            WireContractCorpusPaths.ApiRoot,
            "signalr-events/discoveryprogress.missing-message.json",
            endpoint: "SignalR PrinterHub \"discoveryprogress\" (JoinDiscoveryGroupAsync, null Message)",
            producingTest: $"{nameof(SignalREventContractTests)}.{nameof(JoinDiscoveryGroup_CachedProgressNoMessage_OmitsMessageKey)}",
            schemaVersion: "1.0",
            actualJson: json,
            volatilePaths: volatilePaths);
    }

    /// <summary>
    /// <c>SlicerProgressHub</c> <c>"slicejobevent"</c>, real-broadcast via
    /// <see cref="ISliceJobEventService.NotifyJobQueuedAsync"/> to the <c>SlicingMonitors</c>
    /// group a <c>farm_admin</c> connection auto-joins on connect.
    /// </summary>
    /// <remarks>
    /// <see cref="HubConnection.StartAsync"/> completing only proves the client received the
    /// SignalR handshake response — it does NOT guarantee the server's
    /// <c>SlicerProgressHub.OnConnectedAsync</c> (which does the actual
    /// <c>Groups.AddToGroupAsync(..., SlicingMonitors)</c> call this test relies on) has
    /// finished running. Invoking the harmless, idempotent <c>JoinMonitoringGroupAsync</c> hub
    /// method immediately after <c>StartAsync</c> forces a synchronization point: SignalR
    /// guarantees a connection's <c>OnConnectedAsync</c> completes before any of its hub-method
    /// invocations are dispatched, so once this call returns, the group join from
    /// <c>OnConnectedAsync</c> is guaranteed complete. Without this, the broadcast below could
    /// race ahead of the group join under load and the event would never arrive — a flake, not
    /// a production defect.
    /// </remarks>
    [Fact]
    public async Task NotifyJobQueued_RealBroadcast_SendsSliceJobEventMatchingCorpus()
    {
        string token = await CreateFarmAdminTokenAsync();
        var receivedTcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using HubConnection connection = BuildHubConnection("/hubs/slicers", token);
        _ = connection.On<JsonElement>("slicejobevent", payload => receivedTcs.TrySetResult(payload));
        await connection.StartAsync();
        await connection.InvokeAsync("JoinMonitoringGroupAsync");

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
    /// <c>SlicerProgressHub</c> <c>"slicejobevent"</c> completion payload, captured from the real
    /// <see cref="ISliceJobEventService.NotifyJobCompletedAsync"/> producer. The authenticated
    /// artifact-list route is the public contract; the internal result-file path is never emitted.
    /// </summary>
    [Fact]
    public async Task NotifyJobCompleted_RealBroadcast_SendsSliceJobEventMatchingCorpus()
    {
        string token = await CreateFarmAdminTokenAsync();
        var receivedTcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using HubConnection connection = BuildHubConnection("/hubs/slicers", token);
        _ = connection.On<JsonElement>("slicejobevent", payload => receivedTcs.TrySetResult(payload));
        await connection.StartAsync();
        await connection.InvokeAsync("JoinMonitoringGroupAsync");

        var job = new SliceJob
        {
            Id = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            ModelFileUrl = "/api/3d-models/wire-contract-model.3mf",
            ModelFileName = "wire-contract-model.3mf",
            SlicerEngineName = "OrcaSlicer",
            SlicerEngineVersion = "2.3.1",
            Status = SliceJobStatus.Completed,
            ProgressPercent = 100,
            QueuedAt = DateTime.UtcNow.AddMinutes(-2),
            StartedAt = DateTime.UtcNow.AddMinutes(-1),
            CompletedAt = DateTime.UtcNow,
            EstimatedPrintTimeSeconds = 3600,
            FilamentUsedGrams = 18.5m,
            WorkerId = Guid.NewGuid(),
            ResultFileUrl = @"D:\private\artifacts\result.gcode",
        };

        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            ISliceJobEventService events = scope.ServiceProvider.GetRequiredService<ISliceJobEventService>();
            await events.NotifyJobCompletedAsync(job);
        }

        JsonElement received = await WaitForEventAsync(receivedTcs);

        JsonContractAssertions.AssertEnumToken(received, "status", "Completed");
        JsonContractAssertions.AssertMissingKey(received, "resultFileUrl");
        JsonElement artifactsRoute =
            JsonContractAssertions.AssertProperty(received, "artifactsRoute", JsonValueKind.String);
        artifactsRoute.GetString().Should().Be($"/api/artifacts/job/{job.Id}");

        string json = received.GetRawText();
        var volatilePaths = new HashSet<string>
        {
            "$.jobId",
            "$.userId",
            "$.queuedAt",
            "$.startedAt",
            "$.completedAt",
            "$.workerId",
            "$.timestamp",
            "$.artifactsRoute",
        };
        await WireContractFixtureWriter.CaptureOrVerifyAsync(
            WireContractCorpusPaths.ApiRoot,
            "signalr-events/slicejobevent.completed.json",
            endpoint: "SignalR SlicerProgressHub \"slicejobevent\" (NotifyJobCompletedAsync)",
            producingTest: $"{nameof(SignalREventContractTests)}.{nameof(NotifyJobCompleted_RealBroadcast_SendsSliceJobEventMatchingCorpus)}",
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
        await connection.InvokeAsync("JoinMonitoringGroupAsync");

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
    /// Same event as <see cref="NotifyProgress_RealBroadcast_SendsSlicingProgressMatchingCorpus"/>
    /// with <c>CurrentStep: null</c> — the nullable property has no explicit-null override, so it
    /// falls through to the hub's global <c>DefaultIgnoreCondition = WhenWritingNull</c> policy
    /// and the key is OMITTED. This is the "missing key" variant for this payload's one optional
    /// field (issue #2238's variant matrix).
    /// </summary>
    [Fact]
    public async Task NotifyProgress_RealBroadcastNoCurrentStep_OmitsCurrentStepKey()
    {
        string token = await CreateFarmAdminTokenAsync();
        var receivedTcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using HubConnection connection = BuildHubConnection("/hubs/slicers", token);
        _ = connection.On<JsonElement>("slicingprogress", payload => receivedTcs.TrySetResult(payload));
        await connection.StartAsync();
        await connection.InvokeAsync("JoinMonitoringGroupAsync");

        var update = new SlicingProgressUpdate
        {
            JobId = Guid.NewGuid(),
            Progress = 0,
            Status = SlicingJobStatus.Queued,
            CurrentStep = null,
        };

        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            ISlicerProgressNotifier notifier = scope.ServiceProvider.GetRequiredService<ISlicerProgressNotifier>();
            await notifier.NotifyProgressAsync(update);
        }

        JsonElement received = await WaitForEventAsync(receivedTcs);

        JsonContractAssertions.AssertMissingKey(received, "currentStep");

        string json = received.GetRawText();
        var volatilePaths = new HashSet<string> { "$.jobId", "$.timestamp" };
        await WireContractFixtureWriter.CaptureOrVerifyAsync(
            WireContractCorpusPaths.ApiRoot,
            "signalr-events/slicingprogress.missing-currentstep.json",
            endpoint: "SignalR SlicerProgressHub \"slicingprogress\" (NotifyProgressAsync, null CurrentStep)",
            producingTest: $"{nameof(SignalREventContractTests)}.{nameof(NotifyProgress_RealBroadcastNoCurrentStep_OmitsCurrentStepKey)}",
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
        await connection.InvokeAsync("JoinMonitoringGroupAsync");

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
        await connection.InvokeAsync("JoinMonitoringGroupAsync");

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

    /// <summary>
    /// <c>SlicerHub</c> <c>"SlicerRegistered"</c> (PascalCase — the one deliberate exception to
    /// this file's lowercase event-name convention). Real producer and consumer: a real
    /// <see cref="HubConnection"/> joins <c>/hubs/slicer-registry</c> as farm_admin (every
    /// connected client is auto-added to the hub's <c>Administrators</c> group by
    /// <c>SlicerHub.OnConnectedAsync</c>), then the production
    /// <see cref="ISlicersService.RegisterAsync"/> implementation performs the real
    /// <c>IHubContext&lt;SlicerHub&gt;</c> broadcast — never a hand-built object serialized
    /// locally.
    /// </summary>
    [Fact]
    public async Task SlicerRegister_RealBroadcast_SendsSlicerRegisteredMatchingCorpus()
    {
        string token = await CreateFarmAdminTokenAsync();
        var receivedTcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using HubConnection connection = BuildHubConnection("/hubs/slicer-registry", token);
        _ = connection.On<JsonElement>("SlicerRegistered", payload => receivedTcs.TrySetResult(payload));
        await connection.StartAsync();
        await connection.InvokeAsync("JoinProgressGroupAsync");

        var dto = new RegisterSlicerDto
        {
            Name = $"wire-contract-worker-{Guid.NewGuid():N}",
            SlicerType = 1,
            Version = "2.1.0",
            Host = "http://127.0.0.1:9500",
            MaxConcurrentJobs = 2,
        };

        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            ISlicersService slicers = scope.ServiceProvider.GetRequiredService<ISlicersService>();
            _ = await slicers.RegisterAsync(dto, CancellationToken.None);
        }

        JsonElement received = await WaitForEventAsync(receivedTcs);

        _ = JsonContractAssertions.AssertProperty(received, "id", JsonValueKind.String);
        JsonElement name = JsonContractAssertions.AssertProperty(received, "name", JsonValueKind.String);
        Assert.Equal(dto.Name, name.GetString());
        _ = JsonContractAssertions.AssertProperty(received, "slicerType", JsonValueKind.Number);
        _ = JsonContractAssertions.AssertProperty(received, "version", JsonValueKind.String);
        _ = JsonContractAssertions.AssertProperty(received, "maxConcurrentJobs", JsonValueKind.Number);
        _ = JsonContractAssertions.AssertProperty(received, "status", JsonValueKind.String);

        string json = received.GetRawText();
        var volatilePaths = new HashSet<string> { "$.id", "$.name", "$.lastSeen" };
        await WireContractFixtureWriter.CaptureOrVerifyAsync(
            WireContractCorpusPaths.ApiRoot,
            "signalr-events/SlicerRegistered.populated.json",
            endpoint: "SignalR SlicerHub \"SlicerRegistered\" (PascalCase; ISlicersService.RegisterAsync)",
            producingTest: $"{nameof(SignalREventContractTests)}.{nameof(SlicerRegister_RealBroadcast_SendsSlicerRegisteredMatchingCorpus)}",
            schemaVersion: "1.0",
            actualJson: json,
            volatilePaths: volatilePaths);
    }

    /// <summary>
    /// <c>PrinterHub</c> <c>"taskcreated"</c>, real-broadcast via
    /// <c>IUserTaskService.CreateManualTaskAsync</c> (driven end-to-end through the real
    /// <c>POST /api/tasks</c> endpoint, never a hand-built DTO). Issue #2246: proves
    /// <c>UserTaskDto.AnchorKind</c>/<c>SourceKind</c> emit their canonical lowercase camelCase
    /// tokens over the SignalR transport too, not just MVC — the DTO crosses both.
    /// </summary>
    [Fact]
    public async Task CreateManualTask_RealBroadcast_SendsTaskCreatedWithLowercaseEnumTokens()
    {
        string token = await CreateFarmAdminTokenAsync();
        var receivedTcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using HubConnection connection = BuildHubConnection("/hubs/printers", token);
        _ = connection.On<JsonElement>("taskcreated", payload => receivedTcs.TrySetResult(payload));
        await connection.StartAsync();
        // Non-maintenance tasks broadcast to AuthorizedHubGroups.Farm, which (unlike the
        // farm_admin/AdminTaskGroup this connection auto-joined on connect) requires an
        // explicit subscribe.
        await connection.InvokeAsync("SubscribeToFarmAsync");

        using HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var createRequest = new
        {
            title = "SignalR wire contract manual task",
            description = (string?)null,
            priority = "Normal",
        };
        using HttpResponseMessage response = await client.PostAsJsonAsync("/api/tasks", createRequest);
        _ = response.StatusCode.Should().Be(HttpStatusCode.Created);

        JsonElement received = await WaitForEventAsync(receivedTcs);

        // Property-level [JsonConverter] attributes on UserTaskDto.AnchorKind/SourceKind
        // outrank SignalRStartup's global JsonStringEnumConverter, so these are the canonical
        // lowercase camelCase tokens over the wire, not the PascalCase CLR names.
        JsonContractAssertions.AssertEnumToken(received, "anchorKind", "unspecified");
        JsonContractAssertions.AssertEnumToken(received, "sourceKind", "unspecified");
        JsonContractAssertions.AssertEnumToken(received, "status", "Pending");
        JsonContractAssertions.AssertEnumToken(received, "priority", "Normal");
        JsonContractAssertions.AssertEnumToken(received, "taskType", "Custom");

        string json = received.GetRawText();
        var volatilePaths = new HashSet<string> { "$.id", "$.entityId", "$.createdAt" };
        await WireContractFixtureWriter.CaptureOrVerifyAsync(
            WireContractCorpusPaths.ApiRoot,
            "signalr-events/taskcreated.populated.json",
            endpoint: "SignalR PrinterHub \"taskcreated\" (POST /api/tasks)",
            producingTest: $"{nameof(SignalREventContractTests)}.{nameof(CreateManualTask_RealBroadcast_SendsTaskCreatedWithLowercaseEnumTokens)}",
            schemaVersion: "1.0",
            actualJson: json,
            volatilePaths: volatilePaths);
    }

    /// <summary>
    /// <c>PrinterHub</c> <c>"taskupdated"</c>, real-broadcast via
    /// <c>IUserTaskService.CompleteTaskAsync</c> (driven end-to-end through the real
    /// <c>POST /api/tasks/{id}/complete</c> endpoint). Companion to
    /// <see cref="CreateManualTask_RealBroadcast_SendsTaskCreatedWithLowercaseEnumTokens"/> for
    /// the update path (issue #2246).
    /// </summary>
    [Fact]
    public async Task CompleteTask_RealBroadcast_SendsTaskUpdatedWithLowercaseEnumTokens()
    {
        string token = await CreateFarmAdminTokenAsync();
        using HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var createRequest = new
        {
            title = "SignalR wire contract manual task (update)",
            description = (string?)null,
            priority = "Normal",
        };
        using HttpResponseMessage createResponse = await client.PostAsJsonAsync("/api/tasks", createRequest);
        _ = createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        using JsonDocument createdDocument = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync());
        Guid taskId = createdDocument.RootElement.GetProperty("id").GetGuid();

        var receivedTcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using HubConnection connection = BuildHubConnection("/hubs/printers", token);
        _ = connection.On<JsonElement>("taskupdated", payload => receivedTcs.TrySetResult(payload));
        await connection.StartAsync();
        await connection.InvokeAsync("SubscribeToFarmAsync");

        using HttpResponseMessage completeResponse = await client.PostAsync($"/api/tasks/{taskId}/complete", content: null);
        _ = completeResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        JsonElement received = await WaitForEventAsync(receivedTcs);

        JsonContractAssertions.AssertEnumToken(received, "anchorKind", "unspecified");
        JsonContractAssertions.AssertEnumToken(received, "sourceKind", "unspecified");
        JsonContractAssertions.AssertEnumToken(received, "status", "Completed");

        string json = received.GetRawText();
        var volatilePaths = new HashSet<string> { "$.id", "$.entityId", "$.createdAt", "$.completedAt" };
        await WireContractFixtureWriter.CaptureOrVerifyAsync(
            WireContractCorpusPaths.ApiRoot,
            "signalr-events/taskupdated.completed.json",
            endpoint: "SignalR PrinterHub \"taskupdated\" (POST /api/tasks/{id}/complete)",
            producingTest: $"{nameof(SignalREventContractTests)}.{nameof(CompleteTask_RealBroadcast_SendsTaskUpdatedWithLowercaseEnumTokens)}",
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
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
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
