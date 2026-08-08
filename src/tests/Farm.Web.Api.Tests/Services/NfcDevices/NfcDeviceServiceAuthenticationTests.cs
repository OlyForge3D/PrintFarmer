using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Services.NfcDevices;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Farm.Web.Api.Tests.Services.NfcDevices;

/// <summary>
/// Regression coverage for issue #1252: NFC ingestion endpoints must not treat a
/// caller-supplied PrinterId GUID as a credential. These tests exercise
/// <see cref="NfcDeviceService"/> directly against the four "How to verify" scenarios
/// from the issue.
/// </summary>
public class NfcDeviceServiceAuthenticationTests
{
    private static AppDbContext CreateDbContext()
    {
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"NfcDeviceServiceAuthenticationTests_{Guid.NewGuid()}")
            .Options;
        return new AppDbContext(options);
    }

    private static NfcDeviceService CreateService(AppDbContext db) =>
        new(db, NullLogger<NfcDeviceService>.Instance);

    // ─── Scenario 1: scan with no device credential ─────────────────────────

    [Fact]
    public async Task ScanEventAsync_WithoutDeviceCredential_IsUnauthorizedAndWritesNoRow()
    {
        await using AppDbContext db = CreateDbContext();
        NfcDeviceService service = CreateService(db);
        Guid printerId = Guid.NewGuid();

        NfcScanEventDto dto = new()
        {
            PrinterId = printerId.ToString(),
            SpoolId = 42,
            TagUid = "AA:BB:CC:DD",
            TagFormat = "nfc"
        };

        (NfcScanHistoryDto? result, bool unauthorized) = await service.ProcessScanEventAsync(dto, presentedToken: null, CancellationToken.None);

        unauthorized.Should().BeTrue();
        result.Should().BeNull();
        db.NfcScanEvents.Should().BeEmpty();
    }

    [Fact]
    public async Task ScanEventAsync_WithBogusToken_IsUnauthorizedAndWritesNoRow()
    {
        await using AppDbContext db = CreateDbContext();
        NfcDeviceService service = CreateService(db);
        Guid printerId = Guid.NewGuid();

        NfcDeviceApprovalResultDto approval = await ApproveNewDeviceAsync(db, service, printerId);

        NfcScanEventDto dto = new()
        {
            PrinterId = printerId.ToString(),
            SpoolId = 7,
            TagFormat = "nfc"
        };

        (NfcScanHistoryDto? result, bool unauthorized) = await service.ProcessScanEventAsync(
            dto, presentedToken: "not-the-real-token", CancellationToken.None);

        unauthorized.Should().BeTrue();
        result.Should().BeNull();
        db.NfcScanEvents.Should().BeEmpty();
        approval.DeviceToken.Should().NotBeNullOrWhiteSpace();
    }

    // ─── Scenario 2: heartbeat for unknown printer creates a pending device ─

    [Fact]
    public async Task HeartbeatAsync_ForUnknownPrinter_CreatesPendingUnapprovedDevice()
    {
        await using AppDbContext db = CreateDbContext();
        NfcDeviceService service = CreateService(db);
        Guid printerId = Guid.NewGuid();

        NfcDeviceHeartbeatDto dto = new()
        {
            PrinterId = printerId.ToString(),
            Ip = "10.0.0.5",
            FirmwareVersion = "1.0.0"
        };

        (NfcDeviceDto? device, bool unauthorized) = await service.ProcessHeartbeatAsync(dto, presentedToken: null, CancellationToken.None);

        unauthorized.Should().BeFalse();
        device.Should().NotBeNull();
        device!.IsApproved.Should().BeFalse();
        device.PrinterId.Should().Be(printerId);

        Farm.Infrastructure.Domain.NfcDevice stored = await db.NfcDevices.SingleAsync(d => d.PrinterId == printerId);
        stored.IsApproved.Should().BeFalse();
        stored.DeviceTokenHash.Should().BeNull();
    }

    [Fact]
    public async Task HeartbeatAsync_WithoutToken_DoesNotMutateAnApprovedDevice()
    {
        await using AppDbContext db = CreateDbContext();
        NfcDeviceService service = CreateService(db);
        Guid printerId = Guid.NewGuid();

        await ApproveNewDeviceAsync(db, service, printerId);
        Farm.Infrastructure.Domain.NfcDevice before = await db.NfcDevices.AsNoTracking().SingleAsync(d => d.PrinterId == printerId);

        NfcDeviceHeartbeatDto attackerHeartbeat = new()
        {
            PrinterId = printerId.ToString(),
            Ip = "203.0.113.9",
            FirmwareVersion = "attacker-firmware"
        };

        (NfcDeviceDto? device, bool unauthorized) = await service.ProcessHeartbeatAsync(
            attackerHeartbeat, presentedToken: null, CancellationToken.None);

        unauthorized.Should().BeTrue();
        device.Should().BeNull();

        Farm.Infrastructure.Domain.NfcDevice after = await db.NfcDevices.AsNoTracking().SingleAsync(d => d.PrinterId == printerId);
        after.IpAddress.Should().Be(before.IpAddress);
        after.FirmwareVersion.Should().Be(before.FirmwareVersion);
        after.LastHeartbeat.Should().Be(before.LastHeartbeat);
    }

    // ─── Scenario 3: scan from an unapproved device is rejected ─────────────

    [Fact]
    public async Task ScanEventAsync_FromUnapprovedPendingDevice_IsRejected()
    {
        await using AppDbContext db = CreateDbContext();
        NfcDeviceService service = CreateService(db);
        Guid printerId = Guid.NewGuid();

        NfcDeviceHeartbeatDto heartbeat = new() { PrinterId = printerId.ToString(), Ip = "10.0.0.6" };
        (NfcDeviceDto? pendingDevice, _) = await service.ProcessHeartbeatAsync(heartbeat, presentedToken: null, CancellationToken.None);
        pendingDevice!.IsApproved.Should().BeFalse();

        NfcScanEventDto scan = new()
        {
            PrinterId = printerId.ToString(),
            SpoolId = 3,
            TagFormat = "nfc"
        };

        (NfcScanHistoryDto? result, bool unauthorized) = await service.ProcessScanEventAsync(scan, presentedToken: null, CancellationToken.None);

        unauthorized.Should().BeTrue();
        result.Should().BeNull();
        db.NfcScanEvents.Should().BeEmpty();
    }

    // ─── Scenario 4: approved device with a valid token succeeds end to end ─

    [Fact]
    public async Task ScanEventAsync_FromApprovedDeviceWithValidToken_Succeeds()
    {
        await using AppDbContext db = CreateDbContext();
        NfcDeviceService service = CreateService(db);
        Guid printerId = Guid.NewGuid();

        NfcDeviceApprovalResultDto approval = await ApproveNewDeviceAsync(db, service, printerId);

        NfcScanEventDto scan = new()
        {
            PrinterId = printerId.ToString(),
            SpoolId = 99,
            TagUid = "11:22:33:44",
            TagFormat = "nfc"
        };

        (NfcScanHistoryDto? result, bool unauthorized) = await service.ProcessScanEventAsync(
            scan, approval.DeviceToken, CancellationToken.None);

        unauthorized.Should().BeFalse();
        result.Should().NotBeNull();
        result!.SpoolId.Should().Be(99);
        db.NfcScanEvents.Should().ContainSingle(e => e.SpoolId == 99);
    }

    [Fact]
    public async Task HeartbeatAsync_FromApprovedDeviceWithValidToken_UpdatesDevice()
    {
        await using AppDbContext db = CreateDbContext();
        NfcDeviceService service = CreateService(db);
        Guid printerId = Guid.NewGuid();

        NfcDeviceApprovalResultDto approval = await ApproveNewDeviceAsync(db, service, printerId);

        NfcDeviceHeartbeatDto heartbeat = new()
        {
            PrinterId = printerId.ToString(),
            Ip = "10.0.0.7",
            WifiRssi = -55
        };

        (NfcDeviceDto? device, bool unauthorized) = await service.ProcessHeartbeatAsync(
            heartbeat, approval.DeviceToken, CancellationToken.None);

        unauthorized.Should().BeFalse();
        device.Should().NotBeNull();
        device!.IpAddress.Should().Be("10.0.0.7");
        device.WifiRssi.Should().Be(-55);
    }

    /// <summary>
    /// Announces a device via heartbeat (creating it pending), then approves it,
    /// returning the issued approval result (including the raw device token).
    /// </summary>
    private static async Task<NfcDeviceApprovalResultDto> ApproveNewDeviceAsync(
        AppDbContext db, NfcDeviceService service, Guid printerId)
    {
        NfcDeviceHeartbeatDto heartbeat = new() { PrinterId = printerId.ToString(), Ip = "10.0.0.1" };
        (NfcDeviceDto? pending, _) = await service.ProcessHeartbeatAsync(heartbeat, presentedToken: null, CancellationToken.None);
        pending.Should().NotBeNull();

        NfcDeviceApprovalResultDto? approval = await service.ApproveAsync(pending!.Id, CancellationToken.None);
        approval.Should().NotBeNull();
        return approval!;
    }
}
