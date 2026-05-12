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
        _client = await _factory.CreateAuthenticatedClientAsync();
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    private async Task<(Guid PrinterId, Guid CameraId)> SeedPrinterAndCameraAsync()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var uniqueSuffix = Guid.NewGuid().ToString("N")[..8];
        var manufacturer = new Manufacturer { Id = Guid.NewGuid(), Name = $"Test Mfr {uniqueSuffix}" };
        db.Manufacturers.Add(manufacturer);
        await db.SaveChangesAsync();

        var model = new PrinterModel { Id = Guid.NewGuid(), Name = $"Test Model {uniqueSuffix}", ManufacturerId = manufacturer.Id };
        db.PrinterModels.Add(model);
        await db.SaveChangesAsync();

        var printer = new Printer
        {
            Id = Guid.NewGuid(),
            Name = $"printer-{Guid.NewGuid():N}",
            ServerUrl = $"http://printer-{Guid.NewGuid():N}.local",
            ManufacturerId = manufacturer.Id,
            ModelId = model.Id,
        };
        db.Printers.Add(printer);
        await db.SaveChangesAsync();

        var camera = new Camera
        {
            Id = Guid.NewGuid(),
            Name = "Test Camera",
            PrinterId = printer.Id,
            IsEnabled = true,
        };
        db.Cameras.Add(camera);
        await db.SaveChangesAsync();

        return (printer.Id, camera.Id);
    }

    private async Task<CameraSnapshot> SeedSnapshotAsync(
        Guid? printerId = null,
        Guid? cameraId = null,
        Guid? printJobId = null,
        string eventType = "PrintStarted",
        string? filePath = null)
    {
        if (printerId is null || cameraId is null)
        {
            var (pid, cid) = await SeedPrinterAndCameraAsync();
            printerId ??= pid;
            cameraId ??= cid;
        }

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        if (printJobId.HasValue)
        {
            bool jobExists = await db.Set<PrintJob>().AnyAsync(j => j.Id == printJobId.Value);
            if (!jobExists)
            {
                db.Set<PrintJob>().Add(new PrintJob
                {
                    Id = printJobId.Value,
                    Name = "Test Job",
                    Status = PrintJobStatus.Queued,
                });
                await db.SaveChangesAsync();
            }
        }

        var snapshot = new CameraSnapshot
        {
            Id = Guid.NewGuid(),
            PrinterId = printerId.Value,
            CameraId = cameraId.Value,
            PrintJobId = printJobId,
            EventType = eventType,
            FilePath = filePath ?? $"{printerId}/{Guid.NewGuid():N}/snapshot.jpg",
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
        var (printerId, cameraId) = await SeedPrinterAndCameraAsync();
        await SeedSnapshotAsync(printerId: printerId, cameraId: cameraId, printJobId: printJobId, eventType: "PrintStarted");
        await SeedSnapshotAsync(printerId: printerId, cameraId: cameraId, printJobId: printJobId, eventType: "PrintCompleted");
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
        var (printerId, cameraId) = await SeedPrinterAndCameraAsync();
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        db.Set<PrintJob>().Add(new PrintJob { Id = printJobId, Name = "Test Job", Status = PrintJobStatus.Queued });
        await db.SaveChangesAsync();

        var first = new CameraSnapshot
        {
            Id = Guid.NewGuid(), PrinterId = printerId, CameraId = cameraId,
            PrintJobId = printJobId, EventType = "PrintStarted",
            FilePath = $"{printerId}/c.jpg", CapturedAt = DateTime.UtcNow.AddMinutes(-5),
        };
        var second = new CameraSnapshot
        {
            Id = Guid.NewGuid(), PrinterId = printerId, CameraId = cameraId,
            PrintJobId = printJobId, EventType = "PrintCompleted",
            FilePath = $"{printerId}/d.jpg", CapturedAt = DateTime.UtcNow,
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
        var (printerId, cameraId) = await SeedPrinterAndCameraAsync();
        await SeedSnapshotAsync(printerId: printerId, cameraId: cameraId);
        await SeedSnapshotAsync(printerId: printerId, cameraId: cameraId);
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
        var (printerId, cameraId) = await SeedPrinterAndCameraAsync();
        for (int i = 0; i < 5; i++)
        {
            await SeedSnapshotAsync(printerId: printerId, cameraId: cameraId);
        }

        HttpResponseMessage response = await _client!.GetAsync($"/api/snapshots/by-printer/{printerId}?limit=2");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        List<CameraSnapshotDto>? result = await response.Content.ReadFromJsonAsync<List<CameraSnapshotDto>>(_jsonOptions);
        result!.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetByPrinter_RespectsOffsetQueryParameter()
    {
        var (printerId, cameraId) = await SeedPrinterAndCameraAsync();
        for (int i = 0; i < 3; i++)
        {
            await SeedSnapshotAsync(printerId: printerId, cameraId: cameraId);
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
        var (printerId, cameraId) = await SeedPrinterAndCameraAsync();
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var storagePath = scope.ServiceProvider.GetRequiredService<Farm.Infrastructure.Services.StorageManagement.IStoragePathService>();

        string snapshotRoot = storagePath.GetSnapshotStorageDirectory();
        string relativePath = Path.Combine($"{printerId}", "snapshot.jpg");
        string fullPath = Path.Combine(snapshotRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllBytesAsync(fullPath, [0xFF, 0xD8, 0xFF, 0xE0]); // JPEG magic bytes

        var snapshot = new CameraSnapshot
        {
            Id = Guid.NewGuid(),
            PrinterId = printerId,
            CameraId = cameraId,
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
        var (printerId, cameraId) = await SeedPrinterAndCameraAsync();
        await using AsyncServiceScope setupScope = _factory.Services.CreateAsyncScope();
        var db = setupScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var storagePath = setupScope.ServiceProvider.GetRequiredService<Farm.Infrastructure.Services.StorageManagement.IStoragePathService>();

        string snapshotRoot = storagePath.GetSnapshotStorageDirectory();
        string relativePath = Path.Combine($"{printerId}", "snapshot.jpg");
        string fullPath = Path.Combine(snapshotRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllBytesAsync(fullPath, [0xFF, 0xD8]);

        var snapshot = new CameraSnapshot
        {
            Id = Guid.NewGuid(),
            PrinterId = printerId,
            CameraId = cameraId,
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
