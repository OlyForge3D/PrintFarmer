using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Farm.Web.Api.Tests.Controllers;

/// <summary>
/// Integration tests verifying that PUT /api/printers/{id} validates BuddyCameraIp
/// against injection payloads and correctly handles the clear (empty-string) case.
/// </summary>
[Trait("Category", "Integration")]
public class PrintersControllerBuddyCameraIpTests : IAsyncLifetime
{
    private readonly CustomWebApplicationFactory _factory;
    private HttpClient? _client;

    public PrintersControllerBuddyCameraIpTests()
    {
        _factory = new CustomWebApplicationFactory();
    }

    public async Task InitializeAsync()
    {
        _client = await _factory.CreateAdminClientAsync();
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        await _factory.DisposeAsync();
    }

    private async Task<Guid> SeedPrinterAsync()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        string suffix = Guid.NewGuid().ToString("N")[..8];

        var manufacturer = new Manufacturer { Id = Guid.NewGuid(), Name = $"Mfr-{suffix}" };
        db.Manufacturers.Add(manufacturer);
        await db.SaveChangesAsync();

        var model = new PrinterModel
        {
            Id = Guid.NewGuid(),
            Name = $"Model-{suffix}",
            ManufacturerId = manufacturer.Id
        };
        db.PrinterModels.Add(model);
        await db.SaveChangesAsync();

        var printer = new Printer
        {
            Id = Guid.NewGuid(),
            Name = $"printer-{suffix}",
            ServerUrl = $"http://printer-{suffix}.local",
            ManufacturerId = manufacturer.Id,
            ModelId = model.Id,
        };
        db.Printers.Add(printer);
        await db.SaveChangesAsync();

        return printer.Id;
    }

    [Fact]
    public async Task UpdatePrinter_BuddyCameraIp_WhenValidIp_ReturnsOk()
    {
        Guid id = await SeedPrinterAsync();
        var dto = new UpdatePrinterDto(BuddyCameraIp: "192.168.1.100");

        HttpResponseMessage response = await _client!.PutAsJsonAsync($"/api/printers/{id}", dto);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task UpdatePrinter_BuddyCameraIp_WhenValidHostname_ReturnsOk()
    {
        Guid id = await SeedPrinterAsync();
        var dto = new UpdatePrinterDto(BuddyCameraIp: "myprinter.local");

        HttpResponseMessage response = await _client!.PutAsJsonAsync($"/api/printers/{id}", dto);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task UpdatePrinter_BuddyCameraIp_WhenEmptyString_ReturnsOkAndClearsCamera()
    {
        Guid id = await SeedPrinterAsync();
        var dto = new UpdatePrinterDto(BuddyCameraIp: "");

        HttpResponseMessage response = await _client!.PutAsJsonAsync($"/api/printers/{id}", dto);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("http://192.168.1.100/snapshot")]
    [InlineData("rtsp://192.168.1.100/live")]
    [InlineData("file:///etc/passwd")]
    public async Task UpdatePrinter_BuddyCameraIp_WhenContainsScheme_ReturnsBadRequest(string injection)
    {
        Guid id = await SeedPrinterAsync();
        var dto = new UpdatePrinterDto(BuddyCameraIp: injection);

        HttpResponseMessage response = await _client!.PutAsJsonAsync($"/api/printers/{id}", dto);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("192.168.1.100/snapshot")]
    [InlineData("192.168.1.100\\share")]
    [InlineData("user@192.168.1.100")]
    [InlineData("192.168.1.100?query=1")]
    [InlineData("192.168.1.100#frag")]
    public async Task UpdatePrinter_BuddyCameraIp_WhenContainsInjectionChars_ReturnsBadRequest(string injection)
    {
        Guid id = await SeedPrinterAsync();
        var dto = new UpdatePrinterDto(BuddyCameraIp: injection);

        HttpResponseMessage response = await _client!.PutAsJsonAsync($"/api/printers/{id}", dto);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task UpdatePrinter_BuddyCameraIp_WhenBadRequest_ResponseBodyDoesNotEchoInput()
    {
        Guid id = await SeedPrinterAsync();
        string injection = "http://evil.example.com/payload";
        var dto = new UpdatePrinterDto(BuddyCameraIp: injection);

        HttpResponseMessage response = await _client!.PutAsJsonAsync($"/api/printers/{id}", dto);
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        body.Should().NotContain(injection, because: "raw user input must not be reflected in error responses");
    }

    [Fact]
    public async Task UpdatePrinter_ClearsBuddyCameraIp_WhenCameraHasSnapshots_Succeeds()
    {
        // Arrange: printer with a PrusaLink buddy camera + one snapshot row
        Guid printerId = await SeedPrinterAsync();

        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var camera = new Camera
            {
                Id = Guid.NewGuid(),
                PrinterId = printerId,
                Name = "Buddy Camera",
                Source = CameraSource.PrusaLink,
                StreamUrl = "rtsp://192.168.1.50:554/live/",
            };
            db.Cameras.Add(camera);
            await db.SaveChangesAsync();

            var snapshot = new CameraSnapshot
            {
                Id = Guid.NewGuid(),
                PrinterId = printerId,
                CameraId = camera.Id,
                EventType = "test",
                FilePath = "/snapshots/test.jpg",
                CapturedAt = DateTime.UtcNow,
            };
            db.CameraSnapshots.Add(snapshot);
            await db.SaveChangesAsync();

            // Also stamp BuddyCameraIp on the printer row so the service finds an existing camera
            var printer = await db.Printers.FindAsync(printerId);
            printer!.BuddyCameraIp = "192.168.1.50";
            await db.SaveChangesAsync();
        }

        // Act: clear BuddyCameraIp — this would throw FK violation before the fix
        var dto = new UpdatePrinterDto(BuddyCameraIp: "");
        HttpResponseMessage response = await _client!.PutAsJsonAsync($"/api/printers/{printerId}", dto);

        // Assert: succeeds and camera + snapshot rows are gone
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Cameras.Where(c => c.PrinterId == printerId).Should().BeEmpty(
                because: "the buddy camera must be removed when BuddyCameraIp is cleared");
            db.CameraSnapshots.Where(s => s.PrinterId == printerId).Should().BeEmpty(
                because: "snapshots must be pre-deleted to avoid FK violation on the Restrict constraint");
        }
    }

    [Fact]
    public async Task UpdatePrinter_BuddyCameraIp_WhenValidIp_CreatesCameraRowInDb()
    {
        // Arrange
        Guid printerId = await SeedPrinterAsync();
        const string ip = "192.168.10.55";
        var dto = new UpdatePrinterDto(BuddyCameraIp: ip);

        // Act
        HttpResponseMessage response = await _client!.PutAsJsonAsync($"/api/printers/{printerId}", dto);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // Assert: re-query in a fresh scope to avoid first-level cache hits
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        List<Camera> cameras = await db.Cameras
            .Where(c => c.PrinterId == printerId && c.Source == CameraSource.PrusaLink)
            .ToListAsync();

        cameras.Should().ContainSingle(because: "a single Buddy camera row must be created for the printer");
        Camera cam = cameras[0];
        cam.StreamUrl.Should().Be($"rtsp://{ip}:554/live/",
            because: "the RTSP stream URL must encode the supplied IP on the standard buddy port");
        cam.IsEnabled.Should().BeTrue(because: "newly created Buddy cameras must be enabled by default");

        Printer? printer = await db.Printers.FindAsync(printerId);
        printer!.BuddyCameraIp.Should().Be(ip,
            because: "BuddyCameraIp on the Printer row must be persisted after the update");
    }

    [Fact]
    public async Task UpdatePrinter_BuddyCameraIp_WhenCleared_RemovesCameraRowFromDb()
    {
        // Arrange: set an IP first so a camera row exists
        Guid printerId = await SeedPrinterAsync();
        await _client!.PutAsJsonAsync($"/api/printers/{printerId}", new UpdatePrinterDto(BuddyCameraIp: "10.0.0.1"));

        // Act: clear the IP
        HttpResponseMessage response = await _client!.PutAsJsonAsync($"/api/printers/{printerId}", new UpdatePrinterDto(BuddyCameraIp: ""));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // Assert: camera row must be gone and BuddyCameraIp must be null
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        List<Camera> cameras = await db.Cameras
            .Where(c => c.PrinterId == printerId && c.Source == CameraSource.PrusaLink)
            .ToListAsync();

        cameras.Should().BeEmpty(because: "clearing BuddyCameraIp must delete the Buddy camera row from the Cameras table");

        Printer? printer = await db.Printers.FindAsync(printerId);
        printer!.BuddyCameraIp.Should().BeNull(because: "BuddyCameraIp on the Printer row must be nulled after the clear");
    }

    [Fact]
    public async Task UpdatePrinter_BuddyCameraIp_DoesNotAffectOtherPrintersCameras()
    {
        // Arrange: two independent printers; give printer A an existing Buddy camera
        Guid printerAId = await SeedPrinterAsync();
        Guid printerBId = await SeedPrinterAsync();

        await _client!.PutAsJsonAsync($"/api/printers/{printerAId}", new UpdatePrinterDto(BuddyCameraIp: "172.16.0.1"));

        // Act: update printer B's BuddyCameraIp — must not touch printer A
        HttpResponseMessage response = await _client!.PutAsJsonAsync($"/api/printers/{printerBId}", new UpdatePrinterDto(BuddyCameraIp: "172.16.0.2"));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // Assert: printer A's Buddy camera is unaffected
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        List<Camera> camerasA = await db.Cameras
            .Where(c => c.PrinterId == printerAId && c.Source == CameraSource.PrusaLink)
            .ToListAsync();

        camerasA.Should().ContainSingle(because: "printer A's Buddy camera must not be touched when printer B is updated");
        camerasA[0].StreamUrl.Should().Be("rtsp://172.16.0.1:554/live/",
            because: "printer A's stream URL must remain unchanged after printer B is updated");
    }
}
