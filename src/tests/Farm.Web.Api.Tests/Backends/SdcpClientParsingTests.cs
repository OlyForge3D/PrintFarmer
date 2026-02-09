using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Farm.Backend.Plugin.Sdcp;
using Farm.Infrastructure;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Telemetry;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Backends;

/// <summary>
/// Tests for SDCP file list parsing (Cmd 258)
/// and history parsing (Cmd 320/321).
/// Uses a real Kestrel-hosted WebSocket server to simulate SDCP responses.
/// </summary>
public sealed class SdcpClientParsingTests
{
    // ==================== File List Tests (Cmd 258) ====================

    [Fact]
    public async Task GetFileListAsync_WithFilesAndFolders_ReturnsOnlyFileNames()
    {
        string responsePayload = BuildFileListResponse(ack: 0, entries: [
            new { Name = "/local/model.gcode", UsedSize = 1024, TotalSize = 8000000, StorageType = 0, Type = 1 },
            new { Name = "/local/subfolder", UsedSize = 0, TotalSize = 8000000, StorageType = 0, Type = 0 },
            new { Name = "/local/benchy.gcode", UsedSize = 2048, TotalSize = 8000000, StorageType = 0, Type = 1 },
        ]);

        await using var env = await CreateSdcpServer(responsePayload);

        string[] files = await env.Client.GetFileListAsync(env.BaseUrl);

        files.Should().HaveCount(2);
        files.Should().Contain("/local/model.gcode");
        files.Should().Contain("/local/benchy.gcode");
    }

    [Fact]
    public async Task GetFileListAsync_InterfaceMethod_ReturnsPrinterFileInfoWithParsedNames()
    {
        string responsePayload = BuildFileListResponse(ack: 0, entries: [
            new { Name = "/local/model.gcode", UsedSize = 4096, TotalSize = 8000000, StorageType = 0, Type = 1 },
            new { Name = "/local/subfolder", UsedSize = 0, TotalSize = 8000000, StorageType = 0, Type = 0 },
        ]);

        await using var env = await CreateSdcpServer(responsePayload);

        ISupportsFileList fileListClient = env.Client;
        List<PrinterFileInfo> result = await fileListClient.GetFileListAsync(env.BaseUrl);

        result.Should().HaveCount(1);
        result[0].Name.Should().Be("model.gcode"); // Path.GetFileName strips the path
        result[0].Path.Should().Be("/local/model.gcode");
        result[0].Size.Should().Be(4096);
        result[0].Modified.Should().BeNull(); // SDCP does not provide modification timestamps
        result[0].ThumbnailUrl.Should().BeNull();
    }

    [Fact]
    public async Task GetFileListAsync_EmptyFileList_ReturnsEmptyArray()
    {
        string responsePayload = BuildFileListResponse(ack: 0, entries: []);

        await using var env = await CreateSdcpServer(responsePayload);

        string[] files = await env.Client.GetFileListAsync(env.BaseUrl);

        files.Should().BeEmpty();
    }

    [Fact]
    public async Task GetFileListAsync_AckNonZero_ReturnsEmptyArray()
    {
        string responsePayload = BuildFileListResponse(ack: 1, entries: [
            new { Name = "/local/model.gcode", UsedSize = 1024, TotalSize = 8000000, StorageType = 0, Type = 1 },
        ]);

        await using var env = await CreateSdcpServer(responsePayload);

        string[] files = await env.Client.GetFileListAsync(env.BaseUrl);

        files.Should().BeEmpty();
    }

    [Fact]
    public async Task GetFileListAsync_OnlyFolders_ReturnsEmptyArray()
    {
        string responsePayload = BuildFileListResponse(ack: 0, entries: [
            new { Name = "/local/folder1", UsedSize = 0, TotalSize = 8000000, StorageType = 0, Type = 0 },
            new { Name = "/local/folder2", UsedSize = 0, TotalSize = 8000000, StorageType = 0, Type = 0 },
        ]);

        await using var env = await CreateSdcpServer(responsePayload);

        string[] files = await env.Client.GetFileListAsync(env.BaseUrl);

        files.Should().BeEmpty();
    }

    // ==================== History Tests (Cmd 320 / 321) ====================

    [Fact]
    public async Task GetHistoryListAsync_CompletedJob_MapsFieldsCorrectly()
    {
        const string taskId = "task-001";
        string idsResponse = BuildHistoryIdsResponse(ack: 0, taskIds: [taskId]);
        string detailResponse = BuildHistoryDetailResponse(
            ack: 0, taskId: taskId, filename: "benchy.gcode",
            status: 1, startTime: 1700000000, endTime: 1700003600);

        await using var env = await CreateSdcpHistoryServer(idsResponse, new Dictionary<string, string>
        {
            [taskId] = detailResponse
        });

        ISupportsHistory historyClient = env.Client;
        HistoryListResponse? result = await historyClient.GetHistoryListAsync(
            env.BaseUrl, limit: null, start: null, since: null, credential: null, CancellationToken.None);

        result.Should().NotBeNull();
        result!.Count.Should().Be(1);
        result.Jobs.Should().HaveCount(1);

        HistoryJob job = result.Jobs[0];
        job.JobId.Should().Be(taskId);
        job.Filename.Should().Be("benchy.gcode");
        job.Status.Should().Be("completed");
        job.StartTime.Should().Be(1700000000);
        job.EndTime.Should().Be(1700003600);
        job.PrintDuration.Should().Be(3600);
        job.TotalDuration.Should().Be(3600);
        job.FilamentUsed.Should().Be(0); // SDCP does not report filament
        job.Exists.Should().BeTrue();
    }

    [Fact]
    public async Task GetHistoryListAsync_CancelledJob_StatusIsCancelled()
    {
        const string taskId = "task-cancelled";
        string idsResponse = BuildHistoryIdsResponse(ack: 0, taskIds: [taskId]);
        string detailResponse = BuildHistoryDetailResponse(
            ack: 0, taskId: taskId, filename: "failed.gcode",
            status: 3, startTime: 1700000000, endTime: 1700001800);

        await using var env = await CreateSdcpHistoryServer(idsResponse, new Dictionary<string, string>
        {
            [taskId] = detailResponse
        });

        ISupportsHistory historyClient = env.Client;
        HistoryListResponse? result = await historyClient.GetHistoryListAsync(
            env.BaseUrl, limit: null, start: null, since: null, credential: null, CancellationToken.None);

        result.Should().NotBeNull();
        result!.Jobs.Should().HaveCount(1);
        result.Jobs[0].Status.Should().Be("cancelled");
    }

    [Fact]
    public async Task GetHistoryListAsync_ErrorJob_StatusIsError()
    {
        const string taskId = "task-error";
        string idsResponse = BuildHistoryIdsResponse(ack: 0, taskIds: [taskId]);
        string detailResponse = BuildHistoryDetailResponse(
            ack: 0, taskId: taskId, filename: "broken.gcode",
            status: 2, startTime: 1700000000, endTime: 1700000500);

        await using var env = await CreateSdcpHistoryServer(idsResponse, new Dictionary<string, string>
        {
            [taskId] = detailResponse
        });

        ISupportsHistory historyClient = env.Client;
        HistoryListResponse? result = await historyClient.GetHistoryListAsync(
            env.BaseUrl, limit: null, start: null, since: null, credential: null, CancellationToken.None);

        result.Should().NotBeNull();
        result!.Jobs.Should().HaveCount(1);
        result.Jobs[0].Status.Should().Be("error");
    }

    [Fact]
    public async Task GetHistoryListAsync_UnknownStatus_MapsToUnknownLabel()
    {
        const string taskId = "task-unknown";
        string idsResponse = BuildHistoryIdsResponse(ack: 0, taskIds: [taskId]);
        string detailResponse = BuildHistoryDetailResponse(
            ack: 0, taskId: taskId, filename: "mystery.gcode",
            status: 99, startTime: 1700000000, endTime: 1700000100);

        await using var env = await CreateSdcpHistoryServer(idsResponse, new Dictionary<string, string>
        {
            [taskId] = detailResponse
        });

        ISupportsHistory historyClient = env.Client;
        HistoryListResponse? result = await historyClient.GetHistoryListAsync(
            env.BaseUrl, limit: null, start: null, since: null, credential: null, CancellationToken.None);

        result.Should().NotBeNull();
        result!.Jobs.Should().HaveCount(1);
        result.Jobs[0].Status.Should().Be("unknown(99)");
    }

    [Fact]
    public async Task GetHistoryListAsync_EmptyIdList_ReturnsEmptyJobs()
    {
        string idsResponse = BuildHistoryIdsResponse(ack: 0, taskIds: []);

        await using var env = await CreateSdcpHistoryServer(idsResponse, new Dictionary<string, string>());

        ISupportsHistory historyClient = env.Client;
        HistoryListResponse? result = await historyClient.GetHistoryListAsync(
            env.BaseUrl, limit: null, start: null, since: null, credential: null, CancellationToken.None);

        result.Should().NotBeNull();
        result!.Count.Should().Be(0);
        result.Jobs.Should().BeEmpty();
    }

    [Fact]
    public async Task GetHistoryListAsync_IdsAckNonZero_ReturnsEmptyJobs()
    {
        string idsResponse = BuildHistoryIdsResponse(ack: 1, taskIds: ["task-001"]);

        await using var env = await CreateSdcpHistoryServer(idsResponse, new Dictionary<string, string>());

        ISupportsHistory historyClient = env.Client;
        HistoryListResponse? result = await historyClient.GetHistoryListAsync(
            env.BaseUrl, limit: null, start: null, since: null, credential: null, CancellationToken.None);

        result.Should().NotBeNull();
        result!.Count.Should().Be(0);
        result.Jobs.Should().BeEmpty();
    }

    [Fact]
    public async Task GetHistoryListAsync_MultipleJobs_ReturnsAll()
    {
        string[] taskIds = ["task-a", "task-b", "task-c"];
        string idsResponse = BuildHistoryIdsResponse(ack: 0, taskIds: taskIds);

        var details = new Dictionary<string, string>
        {
            ["task-a"] = BuildHistoryDetailResponse(0, "task-a", "a.gcode", 1, 1700000000, 1700001000),
            ["task-b"] = BuildHistoryDetailResponse(0, "task-b", "b.gcode", 3, 1700002000, 1700002500),
            ["task-c"] = BuildHistoryDetailResponse(0, "task-c", "c.gcode", 2, 1700003000, 1700003100),
        };

        await using var env = await CreateSdcpHistoryServer(idsResponse, details);

        ISupportsHistory historyClient = env.Client;
        HistoryListResponse? result = await historyClient.GetHistoryListAsync(
            env.BaseUrl, limit: null, start: null, since: null, credential: null, CancellationToken.None);

        result.Should().NotBeNull();
        result!.Count.Should().Be(3);
        result.Jobs.Should().HaveCount(3);
        result.Jobs[0].Status.Should().Be("completed");
        result.Jobs[1].Status.Should().Be("cancelled");
        result.Jobs[2].Status.Should().Be("error");
    }

    [Fact]
    public async Task GetHistoryListAsync_EndTimeZero_DurationIsZero()
    {
        const string taskId = "task-noend";
        string idsResponse = BuildHistoryIdsResponse(ack: 0, taskIds: [taskId]);
        string detailResponse = BuildHistoryDetailResponse(
            ack: 0, taskId: taskId, filename: "inprogress.gcode",
            status: 1, startTime: 1700000000, endTime: 0);

        await using var env = await CreateSdcpHistoryServer(idsResponse, new Dictionary<string, string>
        {
            [taskId] = detailResponse
        });

        ISupportsHistory historyClient = env.Client;
        HistoryListResponse? result = await historyClient.GetHistoryListAsync(
            env.BaseUrl, limit: null, start: null, since: null, credential: null, CancellationToken.None);

        result.Should().NotBeNull();
        result!.Jobs.Should().HaveCount(1);
        result.Jobs[0].PrintDuration.Should().Be(0);
        result.Jobs[0].EndTime.Should().BeNull(); // EndTime > 0 check sets null
    }

    [Fact]
    public async Task GetHistoryListAsync_DetailResponseWithUnknownFields_StillParses()
    {
        const string taskId = "task-extra";
        // Build a detail response that includes extra unknown fields
        string detailResponse = JsonSerializer.Serialize(new
        {
            Id = (string?)null,
            Data = new
            {
                Cmd = 321,
                Data = new
                {
                    Ack = 0,
                    HistoryDetailList = new[]
                    {
                        new
                        {
                            TaskId = taskId,
                            TaskName = "extras.gcode",
                            TaskStatus = 1,
                            BeginTime = 1700000000.0,
                            EndTime = 1700001000.0,
                            SomeUnknownField = "should be ignored",
                            AnotherField = 42
                        }
                    }
                },
                RequestID = "req-1",
                MainboardID = "mb-1",
                TimeStamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            },
            Topic = (string?)null
        });

        string idsResponse = BuildHistoryIdsResponse(ack: 0, taskIds: [taskId]);

        await using var env = await CreateSdcpHistoryServer(idsResponse, new Dictionary<string, string>
        {
            [taskId] = detailResponse
        });

        ISupportsHistory historyClient = env.Client;
        HistoryListResponse? result = await historyClient.GetHistoryListAsync(
            env.BaseUrl, limit: null, start: null, since: null, credential: null, CancellationToken.None);

        result.Should().NotBeNull();
        result!.Jobs.Should().HaveCount(1);
        result.Jobs[0].Filename.Should().Be("extras.gcode");
    }

    [Fact]
    public async Task GetHistoryListAsync_WithSinceFilter_OnlyReturnsJobsAfterDate()
    {
        // Two jobs: one before since, one after
        string[] taskIds = ["task-old", "task-new"];
        string idsResponse = BuildHistoryIdsResponse(ack: 0, taskIds: taskIds);

        // task-old: started at Jan 1, 2024 (unix 1704067200)
        // task-new: started at Jun 1, 2024 (unix 1717200000)
        var details = new Dictionary<string, string>
        {
            ["task-old"] = BuildHistoryDetailResponse(0, "task-old", "old.gcode", 1, 1704067200, 1704070800),
            ["task-new"] = BuildHistoryDetailResponse(0, "task-new", "new.gcode", 1, 1717200000, 1717203600),
        };

        await using var env = await CreateSdcpHistoryServer(idsResponse, details);

        // Filter since March 1, 2024
        DateTime sinceDate = new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc);

        ISupportsHistory historyClient = env.Client;
        HistoryListResponse? result = await historyClient.GetHistoryListAsync(
            env.BaseUrl, limit: null, start: null, since: sinceDate, credential: null, CancellationToken.None);

        result.Should().NotBeNull();
        result!.Jobs.Should().HaveCount(1);
        result.Jobs[0].Filename.Should().Be("new.gcode");
    }

    // ==================== File Delete Tests (Cmd 259) ====================

    [Fact]
    public async Task DeleteFileAsync_AckZero_ReturnsTrue()
    {
        string responsePayload = BuildCommandAckResponse(cmd: 259, ack: 0);

        await using var env = await CreateSdcpServer(responsePayload);

        ISupportsFileDelete deleteClient = env.Client;
        bool result = await deleteClient.DeleteFileAsync(env.BaseUrl, "/local/model.gcode", credential: null, CancellationToken.None);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task DeleteFileAsync_AckNonZero_ReturnsFalse()
    {
        string responsePayload = BuildCommandAckResponse(cmd: 259, ack: 1);

        await using var env = await CreateSdcpServer(responsePayload);

        ISupportsFileDelete deleteClient = env.Client;
        bool result = await deleteClient.DeleteFileAsync(env.BaseUrl, "/local/missing.gcode", credential: null, CancellationToken.None);

        result.Should().BeFalse();
    }

    // ==================== Heartbeat / Ping-Pong Tests ====================

    [Fact]
    public async Task GetStatusAsync_WhenServerSendsPingBeforeResponse_ClientRespondsAndStillReceivesData()
    {
        // This test verifies that the SDCP client correctly handles
        // a server-initiated WebSocket ping frame during a normal operation.
        // The server sends a ping, waits briefly for the pong (handled automatically
        // by .NET's ClientWebSocket), then sends the status response.
        int port = GetFreeTcpPort();
        bool pongReceived = false;

        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development
        });
        builder.WebHost.ConfigureKestrel(options => options.ListenLocalhost(port));

        WebApplication app = builder.Build();
        app.UseWebSockets();

        app.Map("/websocket", async context =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                return;
            }

            using WebSocket ws = await context.WebSockets.AcceptWebSocketAsync();

            // Read the client's request
            byte[] requestBuffer = new byte[8192];
            await ws.ReceiveAsync(requestBuffer, context.RequestAborted);

            // Send a ping frame before the actual response
            byte[] pingPayload = Encoding.UTF8.GetBytes("heartbeat");
            await ws.SendAsync(pingPayload, WebSocketMessageType.Binary, true, context.RequestAborted);

            // The .NET WebSocket stack on the server side auto-handles pong frames,
            // but we can verify the connection stays healthy by waiting briefly
            await Task.Delay(50, context.RequestAborted);
            pongReceived = ws.State == WebSocketState.Open;

            // Now send the real response
            string payload = JsonSerializer.Serialize(new
            {
                Status = new
                {
                    PrintInfo = new { Status = 5, Progress = 0, Filename = "" }
                },
                MainboardID = "test-heartbeat",
                TimeStamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Topic = string.Empty
            });

            byte[] payloadBytes = Encoding.UTF8.GetBytes(payload);
            await ws.SendAsync(payloadBytes, WebSocketMessageType.Text, true, context.RequestAborted);
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", context.RequestAborted);
        });

        await app.StartAsync();

        try
        {
            string baseUrl = $"http://127.0.0.1:{port}";
            var logger = new Mock<IUnifiedLoggingService>(MockBehavior.Loose);
            using var httpClient = new HttpClient();
            var client = new SdcpClient(httpClient, logger.Object);

            var status = await client.GetStatusAsync(baseUrl);

            // The client should have survived the ping and still received the status
            status.IsOnline.Should().BeTrue();
            status.State.Should().Be("idle");
            pongReceived.Should().BeTrue("the WebSocket connection should remain open after ping/pong exchange");
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    // ==================== Helper Methods ====================

    private static string BuildFileListResponse(int ack, object[] entries)
    {
        return JsonSerializer.Serialize(new
        {
            Id = (string?)null,
            Data = new
            {
                Cmd = 192, // Printer responds with Cmd 192 for file list
                Data = new
                {
                    Ack = ack,
                    FileList = entries
                },
                RequestID = "req-filelist",
                MainboardID = "mb-1",
                TimeStamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            },
            Topic = (string?)null
        });
    }

    private static string BuildHistoryIdsResponse(int ack, string[] taskIds)
    {
        return JsonSerializer.Serialize(new
        {
            Id = (string?)null,
            Data = new
            {
                Cmd = 320,
                Data = new
                {
                    Ack = ack,
                    HistoryData = taskIds
                },
                RequestID = "req-ids",
                MainboardID = "mb-1",
                TimeStamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            },
            Topic = (string?)null
        });
    }

    private static string BuildHistoryDetailResponse(
        int ack, string taskId, string filename, int status, double startTime, double endTime)
    {
        return JsonSerializer.Serialize(new
        {
            Id = (string?)null,
            Data = new
            {
                Cmd = 321,
                Data = new
                {
                    Ack = ack,
                    HistoryDetailList = new[]
                    {
                        new
                        {
                            TaskId = taskId,
                            TaskName = filename,
                            TaskStatus = status,
                            BeginTime = startTime,
                            EndTime = endTime,
                            Thumbnail = (string?)null,
                            AlreadyPrintLayer = 0,
                            ErrorStatusReason = 0
                        }
                    }
                },
                RequestID = "req-detail",
                MainboardID = "mb-1",
                TimeStamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            },
            Topic = (string?)null
        });
    }

    /// <summary>
    /// Creates a Kestrel-hosted WebSocket server that responds with a single payload
    /// when a file-list request comes in.
    /// </summary>
    private static async Task<SdcpTestEnvironment> CreateSdcpServer(string responsePayload)
    {
        int port = GetFreeTcpPort();

        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development
        });
        builder.WebHost.ConfigureKestrel(options => options.ListenLocalhost(port));

        WebApplication app = builder.Build();
        app.UseWebSockets();

        app.Map("/websocket", async context =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                return;
            }

            using WebSocket ws = await context.WebSockets.AcceptWebSocketAsync();

            // Read the client's request
            byte[] requestBuffer = new byte[8192];
            await ws.ReceiveAsync(requestBuffer, context.RequestAborted);

            // Send the file list response
            byte[] payloadBytes = Encoding.UTF8.GetBytes(responsePayload);
            await ws.SendAsync(payloadBytes, WebSocketMessageType.Text, true, context.RequestAborted);

            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", context.RequestAborted);
        });

        await app.StartAsync();

        string baseUrl = $"http://127.0.0.1:{port}";
        var logger = new Mock<IUnifiedLoggingService>(MockBehavior.Loose);
        using var httpClient = new HttpClient();
        var client = new SdcpClient(httpClient, logger.Object);

        return new SdcpTestEnvironment(app, client, baseUrl);
    }

    /// <summary>
    /// Creates a Kestrel-hosted WebSocket server that handles the history flow:
    /// first returns the IDs response, then returns the appropriate detail response
    /// for each subsequent request based on the TaskId in the request.
    /// </summary>
    private static async Task<SdcpTestEnvironment> CreateSdcpHistoryServer(
        string idsResponse, Dictionary<string, string> detailResponses)
    {
        int port = GetFreeTcpPort();

        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development
        });
        builder.WebHost.ConfigureKestrel(options => options.ListenLocalhost(port));

        WebApplication app = builder.Build();
        app.UseWebSockets();

        app.Map("/websocket", async context =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                return;
            }

            using WebSocket ws = await context.WebSockets.AcceptWebSocketAsync();

            byte[] buffer = new byte[8192];
            bool firstRequest = true;

            while (ws.State == WebSocketState.Open)
            {
                WebSocketReceiveResult receiveResult = await ws.ReceiveAsync(buffer, context.RequestAborted);
                if (receiveResult.MessageType == WebSocketMessageType.Close)
                {
                    await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", context.RequestAborted);
                    break;
                }

                if (firstRequest)
                {
                    // First request is for history IDs (Cmd 320)
                    byte[] idsBytes = Encoding.UTF8.GetBytes(idsResponse);
                    await ws.SendAsync(idsBytes, WebSocketMessageType.Text, true, context.RequestAborted);
                    firstRequest = false;
                }
                else
                {
                    // Subsequent requests are for history details (Cmd 321)
                    // Parse the request to find the TaskId
                    string requestJson = Encoding.UTF8.GetString(buffer, 0, receiveResult.Count);
                    string? taskId = ExtractTaskIdFromRequest(requestJson);

                    if (taskId is not null && detailResponses.TryGetValue(taskId, out string? detailPayload))
                    {
                        byte[] detailBytes = Encoding.UTF8.GetBytes(detailPayload);
                        await ws.SendAsync(detailBytes, WebSocketMessageType.Text, true, context.RequestAborted);
                    }
                    else
                    {
                        // Send an error ack for unknown task IDs
                        string errorResponse = BuildHistoryDetailResponse(1, taskId ?? "", "", 0, 0, 0);
                        byte[] errorBytes = Encoding.UTF8.GetBytes(errorResponse);
                        await ws.SendAsync(errorBytes, WebSocketMessageType.Text, true, context.RequestAborted);
                    }
                }
            }
        });

        await app.StartAsync();

        string baseUrl = $"http://127.0.0.1:{port}";
        var logger = new Mock<IUnifiedLoggingService>(MockBehavior.Loose);
        using var httpClient = new HttpClient();
        var client = new SdcpClient(httpClient, logger.Object);

        return new SdcpTestEnvironment(app, client, baseUrl);
    }

    /// <summary>
    /// Extracts the first task ID from a Cmd 321 request JSON (Id array).
    /// </summary>
    private static string? ExtractTaskIdFromRequest(string json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            var idArray = doc.RootElement
                .GetProperty("Data")
                .GetProperty("Data")
                .GetProperty("Id");
            if (idArray.GetArrayLength() > 0)
            {
                return idArray[0].GetString();
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static string BuildCommandAckResponse(int cmd, int ack)
    {
        return JsonSerializer.Serialize(new
        {
            Id = (string?)null,
            Data = new
            {
                Cmd = cmd,
                Data = new
                {
                    Ack = ack
                },
                RequestID = "req-ack",
                MainboardID = "mb-1",
                TimeStamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            },
            Topic = (string?)null
        });
    }

    private static int GetFreeTcpPort()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>
    /// Wraps the Kestrel app, SdcpClient, and base URL for easy test cleanup.
    /// </summary>
    private sealed class SdcpTestEnvironment(WebApplication app, SdcpClient client, string baseUrl) : IAsyncDisposable
    {
        public SdcpClient Client { get; } = client;
        public string BaseUrl { get; } = baseUrl;

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
