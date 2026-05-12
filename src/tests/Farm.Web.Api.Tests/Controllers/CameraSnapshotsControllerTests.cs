using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Web.Api.Tests.TestInfrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Farm.Web.Api.Tests.Controllers;

/// <summary>
/// Integration tests for <see cref="CameraSnapshotsController"/>.
/// </summary>
[Trait("Category", "Integration")]
[Collection(IntegrationTestCollection.Name)]
public class CameraSnapshotsControllerTests : IAsyncLifetime
{
    private readonly CustomWebApplicationFactory _factory;
    private HttpClient? _client;
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public CameraSnapshotsControllerTests()
    {
        _factory = new CustomWebApplicationFactory();
    }

    public async Task InitializeAsync()
    {
        await _factory.ResetDatabaseAsync();
        _client = _factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    private async Task<CameraSnapshot> SeedSnapshotAsync(
        Guid? printerId = null,
        Guid? printJobId = null,
        string eventType = "PrintStarted",
        string? filePath = null)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var snapshot = new CameraSnapshot
        {
            Id = Guid.NewGuid(),
            PrinterId = printerId ?? Guid.NewGuid(),
            CameraId = Guid.NewGuid(),
            PrintJobId = printJobId,
            EventType = eventType,
            FilePath = filePath ?? $"printer-id/{Guid.NewGuid()}/snapshot.jpg",
            CapturedAt = DateTime.UtcNow,
            FileSizeBytes = 1024,
        };

        db.CameraSnapshots.Add(snapshot);
        await db.SaveChangesAsync();
        return snapshot;
    }

    #region GET /api/snapshots/by-job/{printJobId}

    [Fact]
    public async Task GetByPrintJob_WithMatchingSnapshots_ReturnsOkWithSnapshots()
    {
        var printJobId = Guid.NewGuid();
        await SeedSnapshotAsync(printJobId: printJobId, eventType: "PrintStarted");
        await SeedSnapshotAsync(printJobId: printJobId, eventType: "PrintCompleted");
        await SeedSnapshotAsync(); // different job — should not appear

        HttpResponseMessage response = await _client!.GetAsync($"/api/snapshots/by-job/{printJobId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        List<CameraSnapshotDto>? result = await response.Content.ReadFromJsonAsync<List<CameraSnapshotDto>>(_jsonOptions);
        result.Should().NotBeNull();
        result!.Should().HaveCount(2);
        result.Should().AllSatisfy(s => s.PrintJobId.Should().Be(printJobId));
    }

    [Fact]
    public async Task GetByPrintJob_WithNoMatchingSnapshots_ReturnsEmptyList()
    {
        HttpResponseMessage response = await _client!.GetAsync($"/api/snapshots/by-job/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        List<CameraSnapshotDto>? result = await response.Content.ReadFromJsonAsync<List<CameraSnapshotDto>>(_jsonOptions);
        result.Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public async Task GetByPrintJob_SnapshotDtoDoesNotExposeFilePath()
    {
        var printJobId = Guid.NewGuid();
        await SeedSnapshotAsync(printJobId: printJobId);

        HttpResponseMessage response = await _client!.GetAsync($"/api/snapshots/by-job/{printJobId}");
        string json = await response.Content.ReadAsStringAsync();

        json.Should().NotContain("filePath", "FilePath must not be serialized to protect storage layout");
    }

    [Fact]
    public async Task GetByPrintJob_SnapshotsOrderedByAscendingCapturedAt()
    {
        var printJobId = Guid.NewGuid();
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var first = new CameraSnapshot
        {
            Id = Guid.NewGuid(), PrinterId = Guid.NewGuid(), CameraId = Guid.NewGuid(),
            PrintJobId = printJobId, EventType = "PrintStarted",
            FilePath = "a/b/c.jpg", CapturedAt = DateTime.UtcNow.AddMinutes(-5),
        };
        var second = new CameraSnapshot
        {
            Id = Guid.NewGuid(), PrinterId = Guid.NewGuid(), CameraId = Guid.NewGuid(),
            PrintJobId = printJobId, EventType = "PrintCompleted",
            FilePath = "a/b/d.jpg", CapturedAt = DateTime.UtcNow,
        };
        db.CameraSnapshots.AddRange(second, first); // inserted out of order intentionally
        await db.SaveChangesAsync();

        HttpResponseMessage response = await _client!.GetAsync($"/api/snapshots/by-job/{printJobId}");
        List<CameraSnapshotDto>? result = await response.Content.ReadFromJsonAsync<List<CameraSnapshotDto>>(_jsonOptions);

        result!.Should().HaveCount(2);
        result[0].CapturedAt.Should().BeBefore(result[1].CapturedAt);
    }

    #endregion

    #region GET /api/snapshots/by-printer/{printerId}

    [Fact]
    public async Task GetByPrinter_WithMatchingSnapshots_ReturnsOkWithSnapshots()
    {
        var printerId = Guid.NewGuid();
        await SeedSnapshotAsync(printerId: printerId);
        await SeedSnapshotAsync(printerId: printerId);
        await SeedSnapshotAsync(); // different printer

        HttpResponseMessage response = await _client!.GetAsync($"/api/snapshots/by-printer/{printerId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        List<CameraSnapshotDto>? result = await response.Content.ReadFromJsonAsync<List<CameraSnapshotDto>>(_jsonOptions);
        result.Should().NotBeNull();
        result!.Should().HaveCount(2);
        result.Should().AllSatisfy(s => s.PrinterId.Should().Be(printerId));
    }

    [Fact]
    public async Task GetByPrinter_RespectsLimitQueryParameter()
    {
        var printerId = Guid.NewGuid();
        for (int i = 0; i < 5; i++)
        {
            await SeedSnapshotAsync(printerId: printerId);
        }

        HttpResponseMessage response = await _client!.GetAsync($"/api/snapshots/by-printer/{printerId}?limit=2");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        List<CameraSnapshotDto>? result = await response.Content.ReadFromJsonAsync<List<CameraSnapshotDto>>(_jsonOptions);
        result!.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetByPrinter_RespectsOffsetQueryParameter()
    {
        var printerId = Guid.NewGuid();
        for (int i = 0; i < 3; i++)
        {
            await SeedSnapshotAsync(printerId: printerId);
        }

        HttpResponseMessage response = await _client!.GetAsync($"/api/snapshots/by-printer/{printerId}?limit=10&offset=2");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        List<CameraSnapshotDto>? result = await response.Content.ReadFromJsonAsync<List<CameraSnapshotDto>>(_jsonOptions);
        result!.Should().HaveCount(1);
    }

    #endregion

    #region GET /api/snapshots/{snapshotId}/image

    [Fact]
    public async Task GetImage_WhenSnapshotNotFound_ReturnsNotFound()
    {
        HttpResponseMessage response = await _client!.GetAsync($"/api/snapshots/{Guid.NewGuid()}/image");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetImage_WhenFileDoesNotExistOnDisk_ReturnsNotFound()
    {
        var snapshot = await SeedSnapshotAsync(filePath: "missing/file.jpg");

        HttpResponseMessage response = await _client!.GetAsync($"/api/snapshots/{snapshot.Id}/image");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetImage_WhenFileExists_ReturnsJpegContent()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var storagePath = scope.ServiceProvider.GetRequiredService<Farm.Infrastructure.Services.StorageManagement.IStoragePathService>();

        string snapshotRoot = storagePath.GetSnapshotStorageDirectory();
        string relativePath = Path.Combine("test-printer", "snapshot.jpg");
        string fullPath = Path.Combine(snapshotRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllBytesAsync(fullPath, [0xFF, 0xD8, 0xFF, 0xE0]); // JPEG magic bytes

        var snapshot = new CameraSnapshot
        {
            Id = Guid.NewGuid(),
            PrinterId = Guid.NewGuid(),
            CameraId = Guid.NewGuid(),
            EventType = "PrintStarted",
            FilePath = relativePath,
            CapturedAt = DateTime.UtcNow,
        };
        db.CameraSnapshots.Add(snapshot);
        await db.SaveChangesAsync();

        HttpResponseMessage response = await _client!.GetAsync($"/api/snapshots/{snapshot.Id}/image");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("image/jpeg");
    }

    // --- Future tests (require Lambert's path traversal fix) ---
    // Once GetImageAsync validates that the resolved path stays within the snapshot root,
    // add a test that seeds a CameraSnapshot with FilePath = "../../etc/passwd" and verifies
    // the endpoint returns 400 Bad Request rather than serving the traversed file.

    #endregion

    #region DELETE /api/snapshots/{snapshotId}

    [Fact]
    public async Task Delete_WhenSnapshotNotFound_ReturnsNotFound()
    {
        HttpResponseMessage response = await _client!.DeleteAsync($"/api/snapshots/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Delete_WhenSnapshotExists_ReturnsNoContentAndRemovesRecord()
    {
        var snapshot = await SeedSnapshotAsync(filePath: "printer/no-file.jpg");

        HttpResponseMessage response = await _client!.DeleteAsync($"/api/snapshots/{snapshot.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        bool exists = await db.CameraSnapshots.AnyAsync(s => s.Id == snapshot.Id);
        exists.Should().BeFalse();
    }

    [Fact]
    public async Task Delete_WhenFileExistsOnDisk_DeletesFile()
    {
        await using AsyncServiceScope setupScope = _factory.Services.CreateAsyncScope();
        var db = setupScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var storagePath = setupScope.ServiceProvider.GetRequiredService<Farm.Infrastructure.Services.StorageManagement.IStoragePathService>();

        string snapshotRoot = storagePath.GetSnapshotStorageDirectory();
        string relativePath = Path.Combine("to-delete", "snapshot.jpg");
        string fullPath = Path.Combine(snapshotRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllBytesAsync(fullPath, [0xFF, 0xD8]);

        var snapshot = new CameraSnapshot
        {
            Id = Guid.NewGuid(),
            PrinterId = Guid.NewGuid(),
            CameraId = Guid.NewGuid(),
            EventType = "PrintStarted",
            FilePath = relativePath,
            CapturedAt = DateTime.UtcNow,
        };
        db.CameraSnapshots.Add(snapshot);
        await db.SaveChangesAsync();

        await _client!.DeleteAsync($"/api/snapshots/{snapshot.Id}");

        File.Exists(fullPath).Should().BeFalse();
    }

    // --- Future tests (require Lambert's path traversal fix) ---
    // Once DeleteAsync validates that the resolved path stays within the snapshot root,
    // add a test that seeds a CameraSnapshot with FilePath = "../../etc/passwd" and verifies
    // the endpoint returns 400 Bad Request rather than attempting deletion.

    #endregion
}
