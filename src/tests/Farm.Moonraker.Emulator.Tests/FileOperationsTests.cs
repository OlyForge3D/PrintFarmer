using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Farm.Moonraker.Emulator.Tests;

public sealed class FileOperationsTests : IClassFixture<ReadyPrinterFactory>
{
    private readonly ReadyPrinterFactory _factory;

    public FileOperationsTests(ReadyPrinterFactory factory) => _factory = factory;

    [Fact]
    public async Task Roots_ReturnsGcodesConfigAndLogs()
    {
        using HttpClient client = _factory.CreateClient();
        using HttpResponseMessage response = await client.GetAsync("/server/files/roots");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        string[] names = doc.RootElement.GetProperty("result").EnumerateArray()
            .Select(e => e.GetProperty("name").GetString()!).ToArray();
        names.Should().Contain(["gcodes", "config", "logs"]);
    }

    [Fact]
    public async Task List_DefaultsToGcodesRoot_ContainsSeededBenchy()
    {
        using HttpClient client = _factory.CreateClient();
        using HttpResponseMessage response = await client.GetAsync("/server/files/list");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("result").EnumerateArray()
            .Select(e => e.GetProperty("path").GetString())
            .Should().Contain("benchy.gcode");
    }

    [Fact]
    public async Task Metadata_ForSeededFile_ReturnsThumbnailsAndObjectInfo()
    {
        using HttpClient client = _factory.CreateClient();
        using HttpResponseMessage response = await client.GetAsync("/server/files/metadata?filename=benchy.gcode");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement result = doc.RootElement.GetProperty("result");
        result.GetProperty("thumbnails").GetArrayLength().Should().BeGreaterThan(0);
        result.GetProperty("object_info").GetArrayLength().Should().BeGreaterThan(0);
        result.GetProperty("slicer").GetString().Should().Be("OrcaSlicer");
    }

    [Fact]
    public async Task Metadata_ForUnknownFile_Returns404()
    {
        using HttpClient client = _factory.CreateClient();
        using HttpResponseMessage response = await client.GetAsync("/server/files/metadata?filename=does-not-exist.gcode");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Directory_ListsSeededFileAtRoot()
    {
        using HttpClient client = _factory.CreateClient();
        using HttpResponseMessage response = await client.GetAsync("/server/files/directory?path=&extended=true");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("result").GetProperty("files").EnumerateArray()
            .Select(f => f.GetProperty("path").GetString())
            .Should().Contain("benchy.gcode");
    }

    [Fact]
    public async Task CreateDirectory_NewEmptyDirectory_MutatesStateAndAppearsInParentListing()
    {
        using HttpClient client = _factory.CreateClient();
        using HttpResponseMessage create = await client.PostAsync(
            "/server/files/directory",
            TestRequests.Json("""{"path":"gcodes/http-new-dir"}"""));
        create.StatusCode.Should().Be(HttpStatusCode.OK);

        using JsonDocument createDoc = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        JsonElement item = createDoc.RootElement.GetProperty("result").GetProperty("item");
        item.GetProperty("path").GetString().Should().Be("http-new-dir");
        item.GetProperty("root").GetString().Should().Be("gcodes");
        createDoc.RootElement.GetProperty("result").GetProperty("action").GetString().Should().Be("create_dir");

        // The directory must be real state, not just an acknowledged no-op: it must show up when
        // listing its parent, and listing the new (empty) directory itself must succeed with no
        // files/dirs, rather than silently 404ing or fabricating contents.
        using HttpResponseMessage parentListing = await client.GetAsync("/server/files/directory?path=");
        using JsonDocument parentDoc = JsonDocument.Parse(await parentListing.Content.ReadAsStringAsync());
        parentDoc.RootElement.GetProperty("result").GetProperty("dirs").EnumerateArray()
            .Select(d => d.GetProperty("dirname").GetString())
            .Should().Contain("http-new-dir");

        using HttpResponseMessage childListing = await client.GetAsync("/server/files/directory?path=http-new-dir");
        childListing.StatusCode.Should().Be(HttpStatusCode.OK);
        using JsonDocument childDoc = JsonDocument.Parse(await childListing.Content.ReadAsStringAsync());
        JsonElement childResult = childDoc.RootElement.GetProperty("result");
        childResult.GetProperty("dirs").GetArrayLength().Should().Be(0);
        childResult.GetProperty("files").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task CreateDirectory_AlreadyExists_Returns400NotFabricatedSuccess()
    {
        using HttpClient client = _factory.CreateClient();
        using HttpResponseMessage first = await client.PostAsync(
            "/server/files/directory",
            TestRequests.Json("""{"path":"gcodes/http-dup-dir"}"""));
        first.StatusCode.Should().Be(HttpStatusCode.OK);

        using HttpResponseMessage second = await client.PostAsync(
            "/server/files/directory",
            TestRequests.Json("""{"path":"gcodes/http-dup-dir"}"""));
        second.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using JsonDocument doc = JsonDocument.Parse(await second.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("error").GetString().Should().Be("WebRequestError");
    }

    [Fact]
    public async Task CreateDirectory_ParentMissing_Returns404()
    {
        using HttpClient client = _factory.CreateClient();
        using HttpResponseMessage response = await client.PostAsync(
            "/server/files/directory",
            TestRequests.Json("""{"path":"gcodes/http-missing-parent/child"}"""));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DeleteDirectory_EmptyDirectory_RemovesItFromParentListing()
    {
        using HttpClient client = _factory.CreateClient();
        using HttpResponseMessage create = await client.PostAsync(
            "/server/files/directory",
            TestRequests.Json("""{"path":"gcodes/http-delete-empty"}"""));
        create.StatusCode.Should().Be(HttpStatusCode.OK);

        using HttpResponseMessage delete = await client.DeleteAsync("/server/files/directory?path=gcodes/http-delete-empty&force=false");
        delete.StatusCode.Should().Be(HttpStatusCode.OK);

        using JsonDocument deleteDoc = JsonDocument.Parse(await delete.Content.ReadAsStringAsync());
        deleteDoc.RootElement.GetProperty("result").GetProperty("action").GetString().Should().Be("delete_dir");

        using HttpResponseMessage parentListing = await client.GetAsync("/server/files/directory?path=");
        using JsonDocument parentDoc = JsonDocument.Parse(await parentListing.Content.ReadAsStringAsync());
        parentDoc.RootElement.GetProperty("result").GetProperty("dirs").EnumerateArray()
            .Select(d => d.GetProperty("dirname").GetString())
            .Should().NotContain("http-delete-empty");
    }

    [Fact]
    public async Task DeleteDirectory_Unknown_Returns404()
    {
        using HttpClient client = _factory.CreateClient();
        using HttpResponseMessage response = await client.DeleteAsync("/server/files/directory?path=gcodes/http-does-not-exist&force=false");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DeleteDirectory_NonEmptyWithoutForce_Returns400AndLeavesContentIntact()
    {
        using HttpClient client = _factory.CreateClient();
        using var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent("; nested\nG28\n"u8.ToArray());
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(fileContent, "file", "http-nonempty-dir/inner.gcode");
        form.Add(new StringContent("gcodes"), "root");
        using HttpResponseMessage upload = await client.PostAsync("/server/files/upload", form);
        upload.StatusCode.Should().Be(HttpStatusCode.OK);

        using HttpResponseMessage delete = await client.DeleteAsync("/server/files/directory?path=gcodes/http-nonempty-dir&force=false");
        delete.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using HttpResponseMessage stillThere = await client.GetAsync("/server/files/gcodes/http-nonempty-dir/inner.gcode");
        stillThere.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task DeleteDirectory_NonEmptyWithForce_RemovesDirectoryAndContents()
    {
        using HttpClient client = _factory.CreateClient();
        using var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent("; nested\nG28\n"u8.ToArray());
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(fileContent, "file", "http-force-delete-dir/inner.gcode");
        form.Add(new StringContent("gcodes"), "root");
        using HttpResponseMessage upload = await client.PostAsync("/server/files/upload", form);
        upload.StatusCode.Should().Be(HttpStatusCode.OK);

        using HttpResponseMessage delete = await client.DeleteAsync("/server/files/directory?path=gcodes/http-force-delete-dir&force=true");
        delete.StatusCode.Should().Be(HttpStatusCode.OK);

        using HttpResponseMessage gone = await client.GetAsync("/server/files/gcodes/http-force-delete-dir/inner.gcode");
        gone.StatusCode.Should().Be(HttpStatusCode.NotFound);

        using HttpResponseMessage parentListing = await client.GetAsync("/server/files/directory?path=");
        using JsonDocument parentDoc = JsonDocument.Parse(await parentListing.Content.ReadAsStringAsync());
        parentDoc.RootElement.GetProperty("result").GetProperty("dirs").EnumerateArray()
            .Select(d => d.GetProperty("dirname").GetString())
            .Should().NotContain("http-force-delete-dir");
    }

    [Fact]
    public async Task UploadThenDownloadThenDelete_RoundTripsFileContent()
    {
        using HttpClient client = _factory.CreateClient();
        byte[] payload = "; roundtrip test\nG28\n"u8.ToArray();

        using var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(payload);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(fileContent, "file", "roundtrip.gcode");
        form.Add(new StringContent("gcodes"), "root");

        using HttpResponseMessage upload = await client.PostAsync("/server/files/upload", form);
        upload.StatusCode.Should().Be(HttpStatusCode.OK);

        using HttpResponseMessage download = await client.GetAsync("/server/files/gcodes/roundtrip.gcode");
        download.StatusCode.Should().Be(HttpStatusCode.OK);
        (await download.Content.ReadAsByteArrayAsync()).Should().Equal(payload);

        using HttpResponseMessage delete = await client.DeleteAsync("/server/files/gcodes/roundtrip.gcode");
        delete.StatusCode.Should().Be(HttpStatusCode.OK);

        using HttpResponseMessage afterDelete = await client.GetAsync("/server/files/gcodes/roundtrip.gcode");
        afterDelete.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GcodeRootDownload_KnownThumbnailRelativePath_ServesDeterministicPngBytes()
    {
        // Real Moonraker's gcode-root download route is not thumbnail-specific: slicers write
        // thumbnails physically to disk under the gcodes root, so MoonrakerClient.GetJobAsync
        // resolves a print job's thumbnail as {baseUrl}/server/files/gcodes/{relative_path} using
        // exactly this route rather than the dedicated server/files/thumbs/{file} route. The
        // seeded benchy.gcode's metadata declares "thumbs/benchy-32x32.png" as a relative_path, so
        // that path must resolve here too, instead of 404ing.
        using HttpClient client = _factory.CreateClient();
        using HttpResponseMessage response = await client.GetAsync("/server/files/gcodes/thumbs/benchy-32x32.png");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("image/png");

        byte[] bytes = await response.Content.ReadAsByteArrayAsync();
        bytes.Should().NotBeEmpty();
        bytes[0].Should().Be(0x89);
        bytes[1].Should().Be(0x50);
        bytes[2].Should().Be(0x4E);
        bytes[3].Should().Be(0x47);
    }

    [Fact]
    public async Task GcodeRootDownload_UnknownPath_StillReturns404()
    {
        // Guards the flip side: only *known seeded* thumbnail relative_paths should resolve —
        // this must not become a broad fallback that serves PNG bytes for any missing path.
        using HttpClient client = _factory.CreateClient();
        using HttpResponseMessage response = await client.GetAsync("/server/files/gcodes/thumbs/does-not-exist.png");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Upload_WithPrintTrue_StartsPrintImmediately()
    {
        using HttpClient client = _factory.CreateClient();
        using HttpResponseMessage scenario = await client.PostAsync(
            "/__emulator/printer/scenario",
            TestRequests.Json("""{"scenario":"Paused"}"""));
        scenario.StatusCode.Should().Be(HttpStatusCode.OK);

        byte[] payload = "; print on upload\nG28\n"u8.ToArray();

        using var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(payload);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(fileContent, "file", "auto-print.gcode");
        form.Add(new StringContent("gcodes"), "root");
        form.Add(new StringContent("true"), "print");

        using HttpResponseMessage upload = await client.PostAsync("/server/files/upload", form);
        upload.StatusCode.Should().Be(HttpStatusCode.OK);

        using HttpResponseMessage query = await client.GetAsync("/printer/objects/query?print_stats");
        using JsonDocument doc = JsonDocument.Parse(await query.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("result").GetProperty("status").GetProperty("print_stats").GetProperty("filename")
            .GetString().Should().Be("auto-print.gcode");
    }

    [Fact]
    public async Task MoveThenCopy_RelocatesAndDuplicatesFile()
    {
        using HttpClient client = _factory.CreateClient();
        byte[] payload = "; move/copy test\n"u8.ToArray();

        using var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(payload);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(fileContent, "file", "movable.gcode");
        form.Add(new StringContent("gcodes"), "root");
        using HttpResponseMessage upload = await client.PostAsync("/server/files/upload", form);
        upload.StatusCode.Should().Be(HttpStatusCode.OK);

        using HttpResponseMessage move = await client.PostAsync(
            "/server/files/move",
            TestRequests.Json("""{"source":"gcodes/movable.gcode","dest":"gcodes/moved.gcode"}"""));
        move.StatusCode.Should().Be(HttpStatusCode.OK);

        using HttpResponseMessage copy = await client.PostAsync(
            "/server/files/copy",
            TestRequests.Json("""{"source":"gcodes/moved.gcode","dest":"gcodes/copied.gcode"}"""));
        copy.StatusCode.Should().Be(HttpStatusCode.OK);

        using HttpResponseMessage list = await client.GetAsync("/server/files/list");
        using JsonDocument doc = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        string[] paths = doc.RootElement.GetProperty("result").EnumerateArray()
            .Select(e => e.GetProperty("path").GetString()!).ToArray();
        paths.Should().Contain(["moved.gcode", "copied.gcode"]);
        paths.Should().NotContain("movable.gcode");
    }

    [Fact]
    public async Task Metascan_ForSeededFile_ReturnsMetadata()
    {
        using HttpClient client = _factory.CreateClient();
        using HttpResponseMessage response = await client.PostAsync(
            "/server/files/metascan",
            TestRequests.Json("""{"filename":"benchy.gcode"}"""));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("result").GetProperty("filename").GetString().Should().Be("benchy.gcode");
    }

    [Fact]
    public async Task Thumbnails_ForSeededFile_ReturnsRelativePaths()
    {
        using HttpClient client = _factory.CreateClient();
        using HttpResponseMessage response = await client.GetAsync("/server/files/thumbnails?filename=benchy.gcode");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("result").GetArrayLength().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task ThumbnailBytes_ReturnsImageContent()
    {
        using HttpClient client = _factory.CreateClient();
        using HttpResponseMessage response = await client.GetAsync("/server/files/thumbs/thumbs/benchy-32x32.png");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("image/png");

        byte[] bytes = await response.Content.ReadAsByteArrayAsync();
        bytes.Should().NotBeEmpty();

        // PNG signature magic bytes: 89 50 4E 47 0D 0A 1A 0A. Thumbnail routes must keep serving PNG
        // (unlike the JPEG/MJPEG camera routes), since real slicer thumbnails are PNG.
        bytes[0].Should().Be(0x89);
        bytes[1].Should().Be(0x50);
        bytes[2].Should().Be(0x4E);
        bytes[3].Should().Be(0x47);
    }

    [Fact]
    public async Task GetDirectory_ViaJsonRpcOverHttpFallback_ReturnsSameShapeAsRest()
    {
        using HttpClient client = _factory.CreateClient();
        using HttpResponseMessage response = await client.PostAsync(
            "/websocket",
            TestRequests.Json("""{"jsonrpc":"2.0","method":"server.files.get_directory","params":{"path":"gcodes","extended":true},"id":1}"""));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("id").GetInt32().Should().Be(1);
        doc.RootElement.GetProperty("result").GetProperty("files").EnumerateArray()
            .Select(f => f.GetProperty("filename").GetString())
            .Should().Contain("benchy.gcode");
    }
}
