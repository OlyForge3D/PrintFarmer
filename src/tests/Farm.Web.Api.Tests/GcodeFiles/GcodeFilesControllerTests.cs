using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Xunit;

namespace Farm.Web.Api.Tests.GcodeFiles;

public class GcodeFilesControllerTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _client;
    private readonly string _overrideRoot;

    public GcodeFilesControllerTests(CustomWebApplicationFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        // Establish isolated temp root for gcode library and tell API via env var before host starts
        _overrideRoot = Path.Combine(Path.GetTempPath(), "pfarm_gcode_test_" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("GCODE_LIBRARY_ROOT", _overrideRoot);
        _client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
    }

    [Fact]
    public async Task List_EmptyRoot_ReturnsEmptyStructure()
    {
        var resp = await _client.GetAsync("/api/gcode-files");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await resp.Content.ReadFromJsonAsync<GcodeListResponse>();
        json.Should().NotBeNull();
        json!.Files.Should().BeEmpty();
        json.TotalFiles.Should().Be(0);
        json.TotalSize.Should().Be(0);
        json.Page.Should().Be(1);
        json.PageSize.Should().Be(100); // default
        json.TotalPages.Should().Be(0);
        json.TotalItems.Should().Be(0);
    }

    [Fact]
    public async Task Pagination_WorksWithPageSize_AndMetadata()
    {
        // Arrange: create 120 dummy files under isolated override root
        var libRoot = EnsureLibRoot();
        for (int i = 0; i < 120; i++)
        {
            await File.WriteAllTextAsync(Path.Combine(libRoot, $"file{i:000}.gcode"), ";gcode test\n");
        }

        // Act page 1
        var page1 = await _client.GetFromJsonAsync<GcodeListResponse>("/api/gcode-files?page=1&pageSize=50");
        page1.Should().NotBeNull();
        page1!.Files.Count.Should().Be(50);
        page1.TotalFiles.Should().Be(120);
        page1.Page.Should().Be(1);
        page1.PageSize.Should().Be(50);
        page1.TotalItems.Should().Be(120);
        page1.TotalPages.Should().Be(3);

        // Act page 3
        var page3 = await _client.GetFromJsonAsync<GcodeListResponse>("/api/gcode-files?page=3&pageSize=50");
        page3.Should().NotBeNull();
        page3!.Files.Count.Should().Be(20);
        page3.TotalFiles.Should().Be(120);
        page3.Page.Should().Be(3);
        page3.TotalPages.Should().Be(3);

        // Act page beyond range -> empty page
        var page4 = await _client.GetFromJsonAsync<GcodeListResponse>("/api/gcode-files?page=4&pageSize=50");
        page4.Should().NotBeNull();
        page4!.Files.Count.Should().Be(0);
        page4.TotalFiles.Should().Be(120);
        page4.Page.Should().Be(4); // still echoes requested page
        page4.TotalPages.Should().Be(3);
    }

    [Fact]
    public async Task Sorting_BySizeAscending_ThenDescending_Works()
    {
        var libRoot = EnsureLibRoot();
        var files = new (string name, int size)[] { ("c.gcode", 300), ("a.gcode", 100), ("b.gcode", 200) };
        foreach (var f in files)
        {
            await File.WriteAllBytesAsync(Path.Combine(libRoot, f.name), new byte[f.size]);
        }

        var asc = await _client.GetFromJsonAsync<GcodeListResponse>("/api/gcode-files?sortBy=size&sortOrder=asc&pageSize=10");
        asc!.Files.Where(f => !f.IsDirectory).Select(f => f.Name).Should().ContainInOrder("a.gcode", "b.gcode", "c.gcode");

        var desc = await _client.GetFromJsonAsync<GcodeListResponse>("/api/gcode-files?sortBy=size&sortOrder=desc&pageSize=10");
        desc!.Files.Where(f => !f.IsDirectory).Select(f => f.Name).Should().ContainInOrder("c.gcode", "b.gcode", "a.gcode");
    }

    [Fact]
    public async Task Delete_RemovesSpecifiedFiles()
    {
        var libRoot = EnsureLibRoot();
        await File.WriteAllTextAsync(Path.Combine(libRoot, "keep.gcode"), ";1\n");
        await File.WriteAllTextAsync(Path.Combine(libRoot, "remove.gcode"), ";2\n");

        var before = await _client.GetFromJsonAsync<GcodeListResponse>("/api/gcode-files");
        before!.TotalFiles.Should().Be(2);

        var deleteRequest = new HttpRequestMessage(HttpMethod.Delete, "/api/gcode-files")
        {
            Content = JsonContent.Create(new { filePaths = new[] { "/remove.gcode" } })
        };
        var deleteResp = await _client.SendAsync(deleteRequest);
        deleteResp.StatusCode.Should().Be(HttpStatusCode.OK);

        var after = await _client.GetFromJsonAsync<GcodeListResponse>("/api/gcode-files");
        after!.TotalFiles.Should().Be(1);
        after.Files.Should().ContainSingle(f => f.Name == "keep.gcode");
    }

    [Fact]
    public async Task Download_ReturnsFileBytes()
    {
        var libRoot = EnsureLibRoot();
        var content = ";hello world\n";
        await File.WriteAllTextAsync(Path.Combine(libRoot, "download-me.gcode"), content);

        var resp = await _client.GetAsync("/api/gcode-files/download?path=/download-me.gcode");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var bytes = await resp.Content.ReadAsByteArrayAsync();
        bytes.Length.Should().Be(content.Length);

        var missing = await _client.GetAsync("/api/gcode-files/download?path=/does-not-exist.gcode");
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Sorting_ByNameAscending_ThenDescending_Works()
    {
        var libRoot = EnsureLibRoot();
        await File.WriteAllTextAsync(Path.Combine(libRoot, "Zulu.gcode"), "z\n");
        await File.WriteAllTextAsync(Path.Combine(libRoot, "alpha.gcode"), "a\n");
        await File.WriteAllTextAsync(Path.Combine(libRoot, "Bravo.gcode"), "b\n");

        var asc = await _client.GetFromJsonAsync<GcodeListResponse>("/api/gcode-files?sortBy=name&sortOrder=asc&pageSize=10");
        asc!.Files.Where(f => !f.IsDirectory).Select(f => f.Name).Should().ContainInOrder("alpha.gcode", "Bravo.gcode", "Zulu.gcode");

        var desc = await _client.GetFromJsonAsync<GcodeListResponse>("/api/gcode-files?sortBy=name&sortOrder=desc&pageSize=10");
        desc!.Files.Where(f => !f.IsDirectory).Select(f => f.Name).Should().ContainInOrder("Zulu.gcode", "Bravo.gcode", "alpha.gcode");
    }

    [Fact]
    public async Task Sorting_ByDateAscending_ThenDescending_Works()
    {
        var libRoot = EnsureLibRoot();
        var older = Path.Combine(libRoot, "older.gcode");
        var middle = Path.Combine(libRoot, "middle.gcode");
        var newer = Path.Combine(libRoot, "newer.gcode");

        await File.WriteAllTextAsync(older, "o\n");
        await Task.Delay(15); // ensure distinct timestamps
        await File.WriteAllTextAsync(middle, "m\n");
        await Task.Delay(15);
        await File.WriteAllTextAsync(newer, "n\n");

        var asc = await _client.GetFromJsonAsync<GcodeListResponse>("/api/gcode-files?sortBy=date&sortOrder=asc&pageSize=10");
        asc!.Files.Where(f => !f.IsDirectory).Select(f => f.Name).Should().ContainInOrder("older.gcode", "middle.gcode", "newer.gcode");

        var desc = await _client.GetFromJsonAsync<GcodeListResponse>("/api/gcode-files?sortBy=date&sortOrder=desc&pageSize=10");
        desc!.Files.Where(f => !f.IsDirectory).Select(f => f.Name).Should().ContainInOrder("newer.gcode", "middle.gcode", "older.gcode");
    }

    [Fact]
    public async Task Search_Filtering_ReturnsOnlyMatches()
    {
        var libRoot = EnsureLibRoot();
        await File.WriteAllTextAsync(Path.Combine(libRoot, "rocket_part.gcode"), "r\n");
        await File.WriteAllTextAsync(Path.Combine(libRoot, "ROCKET_NOSE.gcode"), "rn\n");
        await File.WriteAllTextAsync(Path.Combine(libRoot, "plane.gcode"), "p\n");

        var resp = await _client.GetFromJsonAsync<GcodeListResponse>("/api/gcode-files?search=rocket&pageSize=25");
        resp!.TotalFiles.Should().Be(2);
        resp.Files.Select(f => f.Name).Should().BeEquivalentTo(new[] { "rocket_part.gcode", "ROCKET_NOSE.gcode" });
    }

    [Fact]
    public async Task PageSize_IsClampedToMaximum()
    {
        var libRoot = EnsureLibRoot();
        for (int i = 0; i < 600; i++)
        {
            await File.WriteAllTextAsync(Path.Combine(libRoot, $"f{i:000}.gcode"), ";test\n");
        }
        var resp = await _client.GetFromJsonAsync<GcodeListResponse>("/api/gcode-files?page=1&pageSize=9999");
        resp!.PageSize.Should().Be(500);
        resp.Files.Count.Should().Be(500); // first page limited
        resp.TotalFiles.Should().Be(600);
    }

    [Fact]
    public async Task Delete_DirectoryIsRejected()
    {
        var libRoot = EnsureLibRoot();
        var subDir = Path.Combine(libRoot, "subfolder");
        Directory.CreateDirectory(subDir);
        await File.WriteAllTextAsync(Path.Combine(subDir, "inside.gcode"), ";t\n");

        var deleteRequest = new HttpRequestMessage(HttpMethod.Delete, "/api/gcode-files")
        {
            Content = JsonContent.Create(new { filePaths = new[] { "/subfolder" } })
        };
        var resp = await _client.SendAsync(deleteRequest);
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Download_HeadAndConditional_Get304()
    {
        var libRoot = EnsureLibRoot();
        var path = Path.Combine(libRoot, "cache-test.gcode");
        await File.WriteAllTextAsync(path, ";cached\n");

        // Initial GET
        var first = await _client.GetAsync("/api/gcode-files/download?path=/cache-test.gcode");
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        first.Headers.ETag.Should().NotBeNull();
        var etag = first.Headers.ETag!.Tag;
        first.Content.Headers.LastModified.Should().NotBeNull();
        var lastMod = first.Content.Headers.LastModified;

        // HEAD request (no body expected)
        var head = new HttpRequestMessage(HttpMethod.Head, "/api/gcode-files/download?path=/cache-test.gcode");
        var headResp = await _client.SendAsync(head);
        headResp.StatusCode.Should().Be(HttpStatusCode.OK);
    // HEAD should now report the actual file size while omitting the body
    headResp.Content.Headers.ContentLength.Should().Be(new FileInfo(path).Length);

        // Conditional GET with If-None-Match
        var cond = new HttpRequestMessage(HttpMethod.Get, "/api/gcode-files/download?path=/cache-test.gcode");
        cond.Headers.TryAddWithoutValidation("If-None-Match", etag);
        var condResp = await _client.SendAsync(cond);
        condResp.StatusCode.Should().Be(HttpStatusCode.NotModified);

        // Conditional GET with If-Modified-Since
        var cond2 = new HttpRequestMessage(HttpMethod.Get, "/api/gcode-files/download?path=/cache-test.gcode");
        cond2.Headers.IfModifiedSince = lastMod;
        var condResp2 = await _client.SendAsync(cond2);
        condResp2.StatusCode.Should().Be(HttpStatusCode.NotModified);
    }

    private string EnsureLibRoot()
    {
        var libRoot = Path.Combine(_overrideRoot, "gcode-library");
        Directory.CreateDirectory(libRoot);
        return libRoot;
    }
    private sealed record GcodeListResponse(
        IReadOnlyList<GcodeFileDto> Files,
        int TotalFiles,
        long TotalSize,
        int Page,
        int PageSize,
        int TotalPages,
        int TotalItems);
    private sealed record GcodeFileDto(
        string Path,
        string Name,
        long Size,
        DateTime ModifiedAt,
        bool IsDirectory,
        Guid? HarvestOperationId);
}
