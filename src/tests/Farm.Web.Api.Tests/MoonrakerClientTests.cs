using System.Net;
using System.Text;
using System.Text.Json;
using Farm.Web.Api.Services;
using Farm.Web.Api.Services.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;

namespace Farm.Web.Api.Tests;

public class MoonrakerClientTests
{
    private static (IMoonrakerClient client, Mock<HttpMessageHandler> handler, List<HttpRequestMessage> recorded) CreateClient(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var recorded = new List<HttpRequestMessage>();
        var handler = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage req, CancellationToken _) =>
            {
                recorded.Add(req);
                return responder(req);
            });

        var http = new HttpClient(handler.Object);
        var client = new MoonrakerClient(http, NullLogger<MoonrakerClient>.Instance) as IMoonrakerClient;
        return (client, handler, recorded);
    }

    private static HttpResponseMessage Json(object obj, HttpStatusCode code = HttpStatusCode.OK)
    {
        var json = JsonSerializer.Serialize(obj);
        return new HttpResponseMessage(code)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private const string Base = "http://printer"; // will normalize to http://printer:7125

    [Fact]
    public async Task GetStatusAsync_ReturnsOnlineAndStateAsync()
    {
        var (client, _, _) = CreateClient(req =>
        {
            req.RequestUri!.AbsolutePath.Should().Be("/printer/info");
            return Json(new { result = new { state = "ready" } });
        });

        var status = await client.GetStatusAsync(Base);
        status.IsOnline.Should().BeTrue();
        status.State.Should().Be("ready");
    }

    [Fact]
    public async Task GetPrinterInfoAsync_ParsesWrappedInfoAsync()
    {
        var (client, _, _) = CreateClient(req =>
        {
            req.RequestUri!.AbsolutePath.Should().Be("/printer/info");
            return Json(new { result = new { hostname = "mkr-01", state = "ready" } });
        });

        var info = await client.GetPrinterInfoAsync(Base);
        info.Should().NotBeNull();
        info!.Hostname.Should().Be("mkr-01");
        info.State.Should().Be("ready");
    }

    [Fact]
    public async Task GetJobAsync_WhenPrinting_ReturnsProgressJobNameAndThumbAsync()
    {
        var (client, _, _) = CreateClient(req =>
        {
            if (req.RequestUri!.ToString().Contains("/printer/objects/query?print_stats&display_status&job_queue"))
            {
                return Json(new
                {
                    result = new
                    {
                        status = new
                        {
                            print_stats = new { state = "printing", filename = "benchy.gcode" },
                            display_status = new { progress = 0.42 }
                        },
                        job_queue = new
                        {
                            thumbnails = new[]
                            {
                                new { relative_path = "thumbs/benchy-120x120.png" }
                            }
                        }
                    }
                });
            }
            // Should not be called for metadata in this path
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var job = await client.GetJobAsync(Base);
        job.Should().NotBeNull();
        job!.PrintState.Should().Be("printing");
        job.Progress.Should().BeApproximately(42.0, 0.001);
        job.JobName.Should().Be("benchy.gcode");
        job.ThumbnailUrl.Should().NotBeNullOrWhiteSpace();
        job.ThumbnailUrl!.Should().Contain("/server/files/gcodes/");
        job.ThumbnailUrl.Should().Contain("thumbs%2Fbenchy-120x120.png");
    }

    [Fact]
    public async Task GetCompositeStatusAsync_AggregatesStateJobPositionTempsAndCameraAsync()
    {
        var (client, _, recorded) = CreateClient(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.EndsWith("/printer/info"))
            {
                return Json(new { result = new { state = "ready" } });
            }

            if (url.Contains("printer/objects/query?print_stats&display_status&job_queue"))
            {
                return Json(new { result = new { status = new { print_stats = new { state = "standby" } } } });
            }

            if (url.Contains("toolhead=position"))
            {
                return Json(new { result = new { status = new { toolhead = new { position = new[] { 10.0, 20.0, 5.0 } } } } });
            }

            if (url.Contains("extruder&heater_bed"))
            {
                return Json(new { result = new { status = new { extruder = new { temperature = 205.0, target = 210.0 }, heater_bed = new { temperature = 58.5, target = 60.0 } } } });
            }

            if (url.EndsWith("/server/webcams/list"))
            {
                return Json(new { result = new { webcams = new[] { new { enabled = true, stream_url = "/stream", snapshot_url = "/snap.jpg", uid = "cam1", name = "cam1" } } } });
            }
            // Snapshot not required for composite; GetCameraUrls parses listing only
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var cs = await client.GetCompositeStatusAsync(Base);
        cs.IsOnline.Should().BeTrue();
        cs.State.Should().Be("standby");
        cs.X.Should().Be(10.0);
        cs.Y.Should().Be(20.0);
        cs.Z.Should().Be(5.0);
        cs.HotendTemp.Should().Be(205.0);
        cs.BedTemp.Should().BeApproximately(58.5, 0.001);
        cs.HotendTarget.Should().Be(210.0);
        cs.BedTarget.Should().Be(60.0);
        cs.CameraStreamUrl.Should().NotBeNullOrWhiteSpace();
        cs.CameraSnapshotUrl.Should().NotBeNullOrWhiteSpace();
        cs.CameraStreamUrl!.Should().EndWith("/stream");
        cs.CameraSnapshotUrl!.Should().EndWith("/snap.jpg");

        recorded.Should().NotBeEmpty();
    }

    [Fact]
    public async Task GetCameraSnapshotAsync_FetchesSnapshotBytesAsync()
    {
        var (client, _, _) = CreateClient(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.EndsWith("/server/webcams/list"))
            {
                return Json(new { result = new { webcams = new[] { new { enabled = true, snapshot_url = "/snap.jpg", uid = "cam1" } } } });
            }

            if (url.EndsWith("/snap.jpg"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var bytes = await client.GetCameraSnapshotAsync(Base);
        bytes.Should().NotBeNull();
        bytes!.Length.Should().Be(3);
    }

    [Fact]
    public async Task GetCompositeStatusAsync_NormalizesLoopbackCameraHostsToBaseAsync()
    {
        var (client, _, _) = CreateClient(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.EndsWith("/printer/info"))
            {
                return Json(new { result = new { state = "ready" } });
            }

            if (url.EndsWith("/server/webcams/list"))
            {
                // Absolute loopback URLs should be rewritten to base host:port
                return Json(new
                {
                    result = new
                    {
                        webcams = new[]
                        {
                            new
                            {
                                enabled = true,
                                stream_url = "http://127.0.0.1:8080/stream.mjpg",
                                snapshot_url = "http://localhost:8080/snap.jpg"
                            }
                        }
                    }
                });
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var cs = await client.GetCompositeStatusAsync(Base);
        cs.IsOnline.Should().BeTrue();
        cs.CameraStreamUrl.Should().NotBeNullOrWhiteSpace();
        cs.CameraSnapshotUrl.Should().NotBeNullOrWhiteSpace();

        // Base is http://printer -> normalized to base host with scheme; explicit port may or may not be present
        cs.CameraStreamUrl!.Should().StartWith("http://printer");
        cs.CameraStreamUrl.Should().EndWith("/stream.mjpg");
        cs.CameraSnapshotUrl!.Should().StartWith("http://printer");
        cs.CameraSnapshotUrl.Should().EndWith("/snap.jpg");
    }

    [Theory]
    [InlineData("G28", nameof(IMoonrakerClient.SendHomeAsync))]
    [InlineData("G28 X Y", nameof(IMoonrakerClient.HomeXYAsync))]
    [InlineData("G28 Z", nameof(IMoonrakerClient.HomeZAsync))]
    [InlineData("PAUSE", nameof(IMoonrakerClient.PauseAsync))]
    [InlineData("RESUME", nameof(IMoonrakerClient.ResumeAsync))]
    [InlineData("M112", nameof(IMoonrakerClient.EmergencyStopAsync))]
    public async Task GcodeCommandEndpoints_SendExpectedScriptAsync(string expectedScript, string methodName)
    {
        string? body = null;
        var (client, _, _) = CreateClient(req =>
        {
            req.Method.Should().Be(HttpMethod.Post);
            req.RequestUri!.AbsolutePath.Should().Be("/printer/gcode/script");
            body = req.Content!.ReadAsStringAsync().Result;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        // Disambiguate overloads: explicitly select (string baseUrl, CancellationToken ct) signature
        var mi = typeof(IMoonrakerClient).GetMethod(methodName, [typeof(string), typeof(CancellationToken)]);
        mi.Should().NotBeNull();
        var task = (Task<bool>)mi!.Invoke(client, [Base, CancellationToken.None])!;
        var ok = await task;
        ok.Should().BeTrue();
        body.Should().Contain(expectedScript);
    }

    [Fact]
    public async Task SetTempsAsync_PostsM104AndM140Async()
    {
        string? body = null;
        var (client, _, _) = CreateClient(req =>
        {
            body = req.Content!.ReadAsStringAsync().Result;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var ok = await client.SetTempsAsync(Base, hotend: 210, bed: 60);
        ok.Should().BeTrue();
        body.Should().Contain("M104 S210");
        body.Should().Contain("M140 S60");
    }

    [Fact]
    public async Task MoveAsync_UsesRelativeModeAndResetsAbsoluteAsync()
    {
        string? body = null;
        var (client, _, _) = CreateClient(req =>
        {
            body = req.Content!.ReadAsStringAsync().Result;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var ok = await client.MoveAsync(Base, x: 1.5, y: -2.25, f: 1200);
        ok.Should().BeTrue();
        body.Should().Contain("G91 G0");
        body.Should().Contain("X1.5 Y-2.25 F1200");
        body.Should().Contain("G90");
    }

    [Fact]
    public async Task MoveToAsync_UsesAbsoluteMoveAsync()
    {
        string? body = null;
        var (client, _, _) = CreateClient(req =>
        {
            body = req.Content!.ReadAsStringAsync().Result;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var ok = await client.MoveToAsync(Base, x: 100, z: 0.2, f: 3000);
        ok.Should().BeTrue();
        body.Should().Contain("G90 G0 X100 Z0.2 F3000");
    }

    [Fact]
    public async Task StartPrintAsync_PostsFilenameAsync()
    {
        string? body = null;
        var (client, _, _) = CreateClient(req =>
        {
            req.RequestUri!.AbsolutePath.Should().Be("/printer/print/start");
            body = req.Content!.ReadAsStringAsync().Result;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var ok = await client.StartPrintAsync(Base, "benchy.gcode");
        ok.Should().BeTrue();
        body.Should().Contain("benchy.gcode");
    }

    [Fact]
    public async Task GetFileListAsync_ReturnsGcodeNamesAsync()
    {
        var (client, _, _) = CreateClient(req =>
        {
            req.RequestUri!.AbsolutePath.Should().Be("/server/files/list");
            req.RequestUri!.Query.Should().Contain("root=gcodes");
            return Json(new
            {
                result = new[]
                {
                    new { path = "a.gcode" },
                    new { path = "b.txt" },
                    new { path = "sub/c.gcode" }
                }
            });
        });

        var list = await client.GetFileListAsync(Base);
        list.Should().BeEquivalentTo(["a.gcode", "sub/c.gcode"]);
    }

    [Fact]
    public async Task GetFileRootsAsync_ReturnsRootsAsync()
    {
        var (client, _, _) = CreateClient(req =>
        {
            req.RequestUri!.AbsolutePath.Should().Be("/server/files/roots");
            return Json(new { result = new[] { new { name = "gcodes", path = "/gcodes", permissions = "rw" } } });
        });

        var roots = await client.GetFileRootsAsync(Base);
        roots.Should().HaveCount(1);
        roots[0].Name.Should().Be("gcodes");
        roots[0].Path.Should().Be("/gcodes");
    }

    [Fact]
    public async Task GetDirectoryAsync_ReturnsDirectoryInfoAsync()
    {
        var (client, _, _) = CreateClient(req =>
        {
            req.RequestUri!.AbsolutePath.Should().Be("/server/files/directory");
            req.RequestUri!.Query.Should().Contain("path=gcodes");
            return Json(new { result = new { path = "gcodes", dirs = Array.Empty<object>(), files = Array.Empty<object>(), size = 0, modified = 0 } });
        });

        var dir = await client.GetDirectoryAsync(Base, "gcodes");
        dir.Should().NotBeNull();
        dir!.Path.Should().Be("gcodes");
    }

    [Fact]
    public async Task CreateDirectoryAsync_PostsAndParsesResponseAsync()
    {
        string? body = null;
        var (client, _, _) = CreateClient(req =>
        {
            req.Method.Should().Be(HttpMethod.Post);
            req.RequestUri!.AbsolutePath.Should().Be("/server/files/directory");
            body = req.Content!.ReadAsStringAsync().Result;
            return Json(new { result = new { item = new { path = "gcodes/new", modified = 0, size = 0, permissions = "rw" }, action = "create_dir" } });
        });

        var res = await client.CreateDirectoryAsync(Base, "gcodes/new");
        res.Should().NotBeNull();
        res!.Item.Path.Should().Be("gcodes/new");
        body.Should().Contain("gcodes/new");
    }

    [Fact]
    public async Task DeleteFileOrDirectoryAsync_CallsDeleteWithForceFlagAsync()
    {
        var (client, _, _) = CreateClient(req =>
        {
            req.Method.Should().Be(HttpMethod.Delete);
            req.RequestUri!.AbsolutePath.Should().Be("/server/files/directory");
            req.RequestUri!.Query.Should().Contain("path=gcodes%2Fold");
            req.RequestUri!.Query.Should().Contain("force=true");
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var ok = await client.DeleteFileOrDirectoryAsync(Base, "gcodes/old", true);
        ok.Should().BeTrue();
    }

    [Fact]
    public async Task MoveAndCopyFileAsync_PostsSourceAndDestAsync()
    {
        string? moveBody = null;
        string? copyBody = null;
        var (client, _, _) = CreateClient(req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/server/files/move"))
            {
                moveBody = req.Content!.ReadAsStringAsync().Result;
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
            if (req.RequestUri!.AbsolutePath.EndsWith("/server/files/copy"))
            {
                copyBody = req.Content!.ReadAsStringAsync().Result;
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        (await client.MoveFileAsync(Base, "src.gcode", "dst.gcode")).Should().BeTrue();
        (await client.CopyFileAsync(Base, "a.gcode", "b.gcode")).Should().BeTrue();
        moveBody.Should().Contain("src.gcode");
        moveBody.Should().Contain("dst.gcode");
        copyBody.Should().Contain("a.gcode");
        copyBody.Should().Contain("b.gcode");
    }

    [Fact]
    public async Task GetFileMetadata_StartMetadataScan_AndGetThumbnailAsync()
    {
        var (client, _, _) = CreateClient(req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/server/files/metadata"))
            {
                req.RequestUri!.Query.Should().Contain("filename=benchy.gcode");
                return Json(new { result = new { size = 1000, thumbnails = new[] { new { width = 120, height = 120, size = 1234, relative_path = "thumbs/benchy.png" } } } });
            }
            if (req.RequestUri!.AbsolutePath.EndsWith("/server/files/metascan"))
            {
                req.Method.Should().Be(HttpMethod.Post);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
            if (req.RequestUri!.AbsolutePath.StartsWith("/server/files/thumbs/"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([9, 8]) };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var meta = await client.GetFileMetadataAsync(Base, "benchy.gcode");
        meta.Should().NotBeNull();
        meta!.Size.Should().Be(1000);
        (await client.StartMetadataScanAsync(Base, "benchy.gcode")).Should().BeTrue();
        var thumb = await client.GetFileThumbnailAsync(Base, "benchy.gcode");
        thumb.Should().NotBeNull();
        thumb!.Length.Should().Be(2);
    }

    [Fact]
    public async Task DownloadAndDeleteAndStreamFile_WorkAsync()
    {
        var (client, _, _) = CreateClient(req =>
        {
            if (req.Method == HttpMethod.Delete)
            {
                req.RequestUri!.AbsolutePath.Should().StartWith("/server/files/gcodes/");
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
            if (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.StartsWith("/server/files/gcodes/"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes("G1 X1")) };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var bytes = await client.DownloadFileAsync(Base, "sub/benchy.gcode");
        bytes.Should().NotBeNull();
        bytes!.Length.Should().BeGreaterThan(0);
        (await client.DeleteFileAsync(Base, "sub/benchy.gcode")).Should().BeTrue();
        var stream = await client.GetFileStreamAsync(Base, "sub/benchy.gcode");
        stream.Should().NotBeNull();
    }

    [Fact]
    public async Task GetDetailedFileListAsync_ReturnsExtendedEntriesAsync()
    {
        var (client, _, _) = CreateClient(req =>
        {
            req.RequestUri!.AbsolutePath.Should().Be("/server/files/list");
            req.RequestUri!.Query.Should().Contain("extended=true");
            return Json(new { result = new[] { new { path = "a.gcode", size = 1, modified = 0 } } });
        });

        var items = await client.GetDetailedFileListAsync(Base, root: "gcodes", path: "/");
        items.Should().HaveCount(1);
        items[0].Path.Should().Be("a.gcode");
    }

    [Fact]
    public async Task HistoryEndpoints_WorkAsync()
    {
        var (client, _, _) = CreateClient(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("/server/history/list"))
            {
                var json = "{\"result\":{\"count\":1,\"jobs\":[{\"job_id\":\"abc\",\"filename\":\"benchy.gcode\",\"start_time\":1.0,\"total_duration\":2.0,\"print_duration\":2.0,\"filament_used\":10.0,\"status\":\"completed\"}]}}";
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            }
            if (url.Contains("/server/history/job") && req.Method == HttpMethod.Get)
            {
                var json = "{\"result\":{\"job_id\":\"abc\",\"filename\":\"benchy.gcode\",\"start_time\":1.0,\"total_duration\":2.0,\"print_duration\":2.0,\"filament_used\":10.0,\"status\":\"completed\"}}";
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            }
            if (url.Contains("/server/history/job") && req.Method == HttpMethod.Delete)
            {
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            if (url.Contains("/server/history/totals"))
            {
                var json = "{\"result\":{\"job_totals\":{\"total_jobs\":5,\"total_time\":100,\"total_print_time\":90,\"total_filament_used\":123,\"longest_job\":30,\"longest_print\":25}}}";
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            }
            if (url.Contains("/server/history/reset_totals"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var list = await client.GetHistoryListAsync(Base, limit: 10);
        list.Should().NotBeNull();
        list!.Count.Should().Be(1);
        var job = await client.GetHistoryJobAsync(Base, "abc");
        job.Should().NotBeNull();
        (await client.DeleteHistoryJobAsync(Base, "abc")).Should().BeTrue();
        var totals = await client.GetHistoryTotalsAsync(Base);
        totals.Should().NotBeNull();
        (await client.ResetHistoryTotalsAsync(Base)).Should().BeTrue();
    }

    [Fact]
    public async Task SpoolmanEndpoints_WorkAsync()
    {
        var (client, _, _) = CreateClient(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("/server/spoolman/status"))
            {
                return Json(new { result = new { spoolman_connected = true, spool_id = 7 } });
            }

            if (url.Contains("/server/spoolman/spool_id") && req.Method == HttpMethod.Get)
            {
                return Json(new { result = new { spool_id = 7 } });
            }

            if (url.Contains("/server/spoolman/spool_id") && req.Method == HttpMethod.Post)
            {
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            if (url.Contains("/server/spoolman/proxy"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true}") };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var status = await client.GetSpoolmanStatusAsync(Base);
        status.Should().NotBeNull();
        status!.SpoolmanConnected.Should().BeTrue();
        (await client.GetSpoolmanActiveSpoolAsync(Base)).Should().Be(7);
        (await client.SetSpoolmanActiveSpoolAsync(Base, 9)).Should().BeTrue();
        var proxy = await client.SpoolmanProxyRequestAsync(Base, "GET", "/api/v1/spool");
        proxy.Should().Contain("ok");
    }
}
