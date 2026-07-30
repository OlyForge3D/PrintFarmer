using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Authentication;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Farm.Web.Api.Tests.Services.Authentication;

public class LoginAuditServiceTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly LoginAuditService _service;

    public LoginAuditServiceTests()
    {
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"LoginAuditTest_{Guid.NewGuid()}")
            .Options;
        _db = new AppDbContext(options);
        _service = new LoginAuditService(_db, NullLogger<LoginAuditService>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task RecordAsync_SuccessfulLogin_PersistsEntryWithSuccessTrue()
    {
        await _service.RecordAsync(
            username: "alice",
            success: true,
            ipAddress: "10.0.0.1",
            userAgent: "Mozilla/5.0",
            failureReason: null);

        LoginAuditEntry? entry = await _db.LoginAuditEntries.SingleOrDefaultAsync();

        entry.Should().NotBeNull();
        entry!.Username.Should().Be("alice");
        entry.Success.Should().BeTrue();
        entry.IpAddress.Should().Be("10.0.0.1");
        entry.UserAgent.Should().Be("Mozilla/5.0");
        entry.FailureReason.Should().BeNull();
    }

    [Fact]
    public async Task RecordAsync_FailedLogin_PersistsEntryWithSuccessFalseAndReason()
    {
        await _service.RecordAsync(
            username: "bob",
            success: false,
            ipAddress: "192.168.1.50",
            userAgent: null,
            failureReason: "invalid_credentials");

        LoginAuditEntry? entry = await _db.LoginAuditEntries.SingleOrDefaultAsync();

        entry.Should().NotBeNull();
        entry!.Username.Should().Be("bob");
        entry.Success.Should().BeFalse();
        entry.FailureReason.Should().Be("invalid_credentials");
        entry.UserAgent.Should().BeNull();
    }

    [Fact]
    public async Task RecordAsync_NullUsername_PersistsNullableUsernameField()
    {
        await _service.RecordAsync(
            username: null,
            success: false,
            ipAddress: "1.2.3.4",
            userAgent: null,
            failureReason: "invalid_credentials");

        LoginAuditEntry? entry = await _db.LoginAuditEntries.SingleOrDefaultAsync();

        entry.Should().NotBeNull();
        entry!.Username.Should().BeNull();
    }

    [Fact]
    public async Task RecordAsync_LongUsername_TruncatesAt256Chars()
    {
        string longName = new string('x', 300);

        await _service.RecordAsync(
            username: longName,
            success: false,
            ipAddress: "1.2.3.4",
            userAgent: null,
            failureReason: "invalid_credentials");

        LoginAuditEntry? entry = await _db.LoginAuditEntries.SingleOrDefaultAsync();

        entry!.Username.Should().HaveLength(256);
    }

    [Fact]
    public async Task RecordAsync_LongIpAddress_TruncatesAt64Chars()
    {
        string longIp = new string('1', 100);

        await _service.RecordAsync(
            username: "user",
            success: true,
            ipAddress: longIp,
            userAgent: null,
            failureReason: null);

        LoginAuditEntry? entry = await _db.LoginAuditEntries.SingleOrDefaultAsync();

        entry!.IpAddress.Should().HaveLength(64);
    }

    [Fact]
    public async Task RecordAsync_LongUserAgent_TruncatesAt512Chars()
    {
        string longUa = new string('u', 600);

        await _service.RecordAsync(
            username: "user",
            success: true,
            ipAddress: "1.2.3.4",
            userAgent: longUa,
            failureReason: null);

        LoginAuditEntry? entry = await _db.LoginAuditEntries.SingleOrDefaultAsync();

        entry!.UserAgent.Should().HaveLength(512);
    }

    [Fact]
    public async Task RecordAsync_TimestampIsUtc()
    {
        DateTimeOffset before = DateTimeOffset.UtcNow.AddSeconds(-1);

        await _service.RecordAsync("user", true, "1.2.3.4", null, null);

        LoginAuditEntry? entry = await _db.LoginAuditEntries.SingleOrDefaultAsync();
        entry!.Timestamp.Should().BeAfter(before);
        entry.Timestamp.Offset.Should().Be(TimeSpan.Zero);
    }
}
