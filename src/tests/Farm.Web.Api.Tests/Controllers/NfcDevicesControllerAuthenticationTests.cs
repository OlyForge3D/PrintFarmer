using System.Net;
using System.Net.Http.Json;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Farm.Web.Api.Tests.Controllers;

/// <summary>
/// HTTP-boundary regression coverage for issue #1252. Exercises the actual controller
/// routes (model binding, <c>[Authorize]</c>/<c>[AllowAnonymous]</c> gates, and status-code
/// mapping) rather than the service layer directly, since the vulnerability and its fix
/// both live at that boundary.
/// </summary>
public class NfcDevicesControllerAuthenticationTests : IAsyncLifetime
{
    private readonly CustomWebApplicationFactory _factory;
    private HttpClient? _adminClient;
    private HttpClient? _nonAdminClient;

    public NfcDevicesControllerAuthenticationTests()
    {
        _factory = new CustomWebApplicationFactory(new Dictionary<string, string?>
        {
            ["Security:DevModeBypassAuth"] = "false",
        });
    }

    public async Task InitializeAsync()
    {
        _adminClient = await _factory.CreateAdminClientAsync();
        _nonAdminClient = await _factory.CreateAuthenticatedClientAsync(
            username: "nfc-devices-user",
            email: "nfc-devices-user@example.com");
    }

    public async Task DisposeAsync()
    {
        _adminClient?.Dispose();
        _nonAdminClient?.Dispose();
        await _factory.DisposeAsync();
    }

    // ─── /approve is a farm_admin-only, credential-issuing action ───────────

    [Fact]
    public async Task ApproveAsync_Unauthenticated_Returns401()
    {
        using HttpClient anonymousClient = _factory.CreateClient();
        Guid deviceId = await AnnounceUnknownPrinterAsync(anonymousClient);

        HttpResponseMessage response = await anonymousClient.PostAsync($"/api/nfc-devices/{deviceId}/approve", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ApproveAsync_AuthenticatedNonAdmin_Returns403()
    {
        using HttpClient anonymousClient = _factory.CreateClient();
        Guid deviceId = await AnnounceUnknownPrinterAsync(anonymousClient);

        HttpResponseMessage response = await _nonAdminClient!.PostAsync($"/api/nfc-devices/{deviceId}/approve", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "approving a device mints a durable credential and must be restricted to farm_admin, not any authenticated user");
    }

    [Fact]
    public async Task ApproveAsync_Admin_ReturnsOneTimeRawToken()
    {
        using HttpClient anonymousClient = _factory.CreateClient();
        Guid deviceId = await AnnounceUnknownPrinterAsync(anonymousClient);

        HttpResponseMessage response = await _adminClient!.PostAsync($"/api/nfc-devices/{deviceId}/approve", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        NfcDeviceApprovalResultDto? approval = await response.Content.ReadFromJsonAsync<NfcDeviceApprovalResultDto>();
        approval.Should().NotBeNull();
        approval!.DeviceId.Should().Be(deviceId);
        approval.DeviceToken.Should().NotBeNullOrWhiteSpace();
    }

    // ─── /heartbeat stays anonymous but is claim-only once approved ─────────

    [Fact]
    public async Task HeartbeatAsync_UnknownPrinter_Returns200AndCreatesPendingUnapprovedDevice()
    {
        using HttpClient anonymousClient = _factory.CreateClient();
        Guid printerId = await SeedPrinterAsync();

        HttpResponseMessage response = await anonymousClient.PostAsJsonAsync("/api/nfc-devices/heartbeat", new
        {
            printerId = printerId.ToString(),
            ip = "10.0.0.5"
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        NfcDeviceDto? device = await response.Content.ReadFromJsonAsync<NfcDeviceDto>();
        device.Should().NotBeNull();
        device!.IsApproved.Should().BeFalse();
    }

    [Fact]
    public async Task HeartbeatAsync_ForApprovedDeviceWithoutToken_Returns401()
    {
        using HttpClient anonymousClient = _factory.CreateClient();
        (Guid printerId, _) = await AnnounceAndApproveAsync(anonymousClient);

        HttpResponseMessage response = await anonymousClient.PostAsJsonAsync("/api/nfc-devices/heartbeat", new
        {
            printerId = printerId.ToString(),
            ip = "203.0.113.9",
            firmwareVersion = "attacker-firmware"
        });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task HeartbeatAsync_ForApprovedDeviceWithValidToken_Returns200()
    {
        using HttpClient anonymousClient = _factory.CreateClient();
        (Guid printerId, string token) = await AnnounceAndApproveAsync(anonymousClient);

        using HttpRequestMessage request = new(HttpMethod.Post, "/api/nfc-devices/heartbeat")
        {
            Content = JsonContent.Create(new { printerId = printerId.ToString(), ip = "10.0.0.9" })
        };
        request.Headers.Add(NfcDeviceAuthHeaders.DeviceToken, token);

        HttpResponseMessage response = await anonymousClient.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    // ─── /scan requires an approved device and a valid token ────────────────

    [Fact]
    public async Task ScanEventAsync_WithoutToken_Returns401()
    {
        using HttpClient anonymousClient = _factory.CreateClient();
        (Guid printerId, _) = await AnnounceAndApproveAsync(anonymousClient);

        HttpResponseMessage response = await anonymousClient.PostAsJsonAsync("/api/nfc-devices/scan", new
        {
            printerId = printerId.ToString(),
            spoolId = 1,
            tagFormat = "nfc"
        });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ScanEventAsync_WithBogusToken_Returns401()
    {
        using HttpClient anonymousClient = _factory.CreateClient();
        (Guid printerId, _) = await AnnounceAndApproveAsync(anonymousClient);

        using HttpRequestMessage request = new(HttpMethod.Post, "/api/nfc-devices/scan")
        {
            Content = JsonContent.Create(new { printerId = printerId.ToString(), spoolId = 2, tagFormat = "nfc" })
        };
        request.Headers.Add(NfcDeviceAuthHeaders.DeviceToken, "not-the-real-token");

        HttpResponseMessage response = await anonymousClient.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ScanEventAsync_FromUnapprovedPendingDevice_Returns401()
    {
        using HttpClient anonymousClient = _factory.CreateClient();
        Guid printerId = await SeedPrinterAsync();
        await AnnounceUnknownPrinterAsync(anonymousClient, printerId);

        HttpResponseMessage response = await anonymousClient.PostAsJsonAsync("/api/nfc-devices/scan", new
        {
            printerId = printerId.ToString(),
            spoolId = 3,
            tagFormat = "nfc"
        });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ScanEventAsync_FromApprovedDeviceWithValidToken_Returns200()
    {
        using HttpClient anonymousClient = _factory.CreateClient();
        (Guid printerId, string token) = await AnnounceAndApproveAsync(anonymousClient);

        using HttpRequestMessage request = new(HttpMethod.Post, "/api/nfc-devices/scan")
        {
            Content = JsonContent.Create(new { printerId = printerId.ToString(), spoolId = 42, tagFormat = "nfc" })
        };
        request.Headers.Add(NfcDeviceAuthHeaders.DeviceToken, token);

        HttpResponseMessage response = await anonymousClient.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        NfcScanHistoryDto? result = await response.Content.ReadFromJsonAsync<NfcScanHistoryDto>();
        result.Should().NotBeNull();
        result!.SpoolId.Should().Be(42);
    }

    /// <summary>
    /// Seeds a minimal real <see cref="Printer"/> row (reusing a shared manufacturer/model)
    /// so heartbeat/scan requests against its ID satisfy the NfcDevice.PrinterId FK — the
    /// production heartbeat endpoint accepts any syntactically valid GUID, but the schema
    /// still requires a real printer row to exist once a device is persisted against it.
    /// </summary>
    private async Task<Guid> SeedPrinterAsync()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        Manufacturer? manufacturer = await db.Manufacturers.FirstOrDefaultAsync();
        if (manufacturer is null)
        {
            manufacturer = new Manufacturer { Id = Guid.NewGuid(), Name = "Test Manufacturer" };
            db.Manufacturers.Add(manufacturer);
            await db.SaveChangesAsync();
        }

        PrinterModel? model = await db.PrinterModels.FirstOrDefaultAsync();
        if (model is null)
        {
            model = new PrinterModel { Id = Guid.NewGuid(), Name = "Test Model", ManufacturerId = manufacturer.Id };
            db.PrinterModels.Add(model);
            await db.SaveChangesAsync();
        }

        var printer = new Printer
        {
            Id = Guid.NewGuid(),
            Name = $"NFC Test Printer {Guid.NewGuid():N}",
            ServerUrl = $"http://nfc-test-{Guid.NewGuid():N}",
            BackendPort = 7125,
            Backend = (int)PrinterBackend.Moonraker,
            ManufacturerId = manufacturer.Id,
            ModelId = model.Id,
        };
        db.Printers.Add(printer);
        await db.SaveChangesAsync();

        return printer.Id;
    }

    /// <summary>
    /// Sends an anonymous heartbeat for a freshly seeded printer, which creates a pending
    /// (unapproved) device row, and returns that device's ID.
    /// </summary>
    private async Task<Guid> AnnounceUnknownPrinterAsync(HttpClient anonymousClient, Guid? printerId = null)
    {
        Guid pid = printerId ?? await SeedPrinterAsync();
        HttpResponseMessage response = await anonymousClient.PostAsJsonAsync("/api/nfc-devices/heartbeat", new
        {
            printerId = pid.ToString(),
            ip = "10.0.0.1"
        });
        string body = await response.Content.ReadAsStringAsync();
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new Xunit.Sdk.XunitException($"Expected 200 OK from heartbeat but got {(int)response.StatusCode} {response.StatusCode}: {body}");
        }

        NfcDeviceDto? device = System.Text.Json.JsonSerializer.Deserialize<NfcDeviceDto>(
            body, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        device.Should().NotBeNull();
        return device!.Id;
    }

    /// <summary>
    /// Seeds a printer, announces a device via anonymous heartbeat, approves it as
    /// farm_admin, and returns the printer ID plus the raw device token issued by the
    /// approval.
    /// </summary>
    private async Task<(Guid PrinterId, string Token)> AnnounceAndApproveAsync(HttpClient anonymousClient)
    {
        Guid printerId = await SeedPrinterAsync();
        Guid deviceId = await AnnounceUnknownPrinterAsync(anonymousClient, printerId);

        HttpResponseMessage approveResponse = await _adminClient!.PostAsync($"/api/nfc-devices/{deviceId}/approve", content: null);
        approveResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        NfcDeviceApprovalResultDto? approval = await approveResponse.Content.ReadFromJsonAsync<NfcDeviceApprovalResultDto>();
        approval.Should().NotBeNull();

        return (printerId, approval!.DeviceToken);
    }
}
