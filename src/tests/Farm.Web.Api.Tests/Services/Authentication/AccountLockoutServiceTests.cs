using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Web.Api.Services.Authentication;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Farm.Web.Api.Tests.Services.Authentication;

public class AccountLockoutServiceTests : IDisposable
{
    private readonly AppDbContext _context;
    private readonly AccountLockoutService _service;
    private readonly IConfiguration _configuration;

    public AccountLockoutServiceTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"AccountLockoutTest_{Guid.NewGuid()}")
            .Options;
        _context = new AppDbContext(options);

        // Create configuration with test settings
        var configData = new Dictionary<string, string?>
        {
            ["AccountLockout:MaxFailedAttempts"] = "5",
            ["AccountLockout:LockoutDurationMinutes"] = "15",
            ["AccountLockout:AttemptWindowMinutes"] = "15"
        };
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        _service = new AccountLockoutService(_context, _configuration);
    }

    public void Dispose()
    {
        _context.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task IsLockedOutAsync_ReturnsFalse_WhenUserNotLocked()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var user = new User
        {
            Id = userId,
            Username = "testuser",
            Email = "test@test.com",
            PasswordHash = "hash",
            FailedLoginAttempts = 0,
            LockoutEnd = null
        };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        // Act
        bool result = await _service.IsLockedOutAsync(userId);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task IsLockedOutAsync_ReturnsTrue_WhenUserLockedAndNotExpired()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var user = new User
        {
            Id = userId,
            Username = "testuser",
            Email = "test@test.com",
            PasswordHash = "hash",
            FailedLoginAttempts = 5,
            LockoutEnd = DateTime.UtcNow.AddMinutes(10) // Locked for 10 more minutes
        };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        // Act
        bool result = await _service.IsLockedOutAsync(userId);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public async Task IsLockedOutAsync_ReturnsFalse_WhenLockoutExpired()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var user = new User
        {
            Id = userId,
            Username = "testuser",
            Email = "test@test.com",
            PasswordHash = "hash",
            FailedLoginAttempts = 5,
            LockoutEnd = DateTime.UtcNow.AddMinutes(-5) // Expired 5 minutes ago
        };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        // Act
        bool result = await _service.IsLockedOutAsync(userId);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task RecordFailedLoginAsync_IncrementsCounter()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var user = new User
        {
            Id = userId,
            Username = "testuser",
            Email = "test@test.com",
            PasswordHash = "hash",
            FailedLoginAttempts = 2
        };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        // Act
        await _service.RecordFailedLoginAsync(userId, "testuser", "192.168.1.1", "Invalid password");

        // Assert
        var updatedUser = await _context.Users.FindAsync(userId);
        updatedUser.Should().NotBeNull();
        updatedUser!.FailedLoginAttempts.Should().Be(3);
    }

    [Fact]
    public async Task RecordFailedLoginAsync_LocksAccount_WhenThresholdExceeded()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var user = new User
        {
            Id = userId,
            Username = "testuser",
            Email = "test@test.com",
            PasswordHash = "hash",
            FailedLoginAttempts = 4 // One away from threshold of 5
        };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        // Act
        await _service.RecordFailedLoginAsync(userId, "testuser", "192.168.1.1", "Invalid password");

        // Assert
        var updatedUser = await _context.Users.FindAsync(userId);
        updatedUser.Should().NotBeNull();
        updatedUser!.FailedLoginAttempts.Should().Be(5);
        updatedUser.LockoutEnd.Should().NotBeNull();
        updatedUser.LockoutEnd.Should().BeAfter(DateTime.UtcNow);
    }

    [Fact]
    public async Task RecordFailedLoginAsync_CreatesAuditEntry()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var user = new User
        {
            Id = userId,
            Username = "testuser",
            Email = "test@test.com",
            PasswordHash = "hash",
            FailedLoginAttempts = 0
        };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        // Act
        await _service.RecordFailedLoginAsync(userId, "testuser", "192.168.1.1", "Invalid password");

        // Assert
        var auditEntries = await _context.FailedLoginAttempts
            .Where(f => f.Identifier == "testuser")
            .ToListAsync();
        auditEntries.Should().HaveCount(1);
        auditEntries[0].IpAddress.Should().Be("192.168.1.1");
        auditEntries[0].FailureReason.Should().Be("Invalid password");
    }

    [Fact]
    public async Task RecordFailedLoginByUsernameAsync_CreatesAuditEntry_ForNonExistentUser()
    {
        // Act
        await _service.RecordFailedLoginByUsernameAsync("nonexistent", "192.168.1.1", "User not found");

        // Assert
        var auditEntries = await _context.FailedLoginAttempts
            .Where(f => f.Identifier == "nonexistent")
            .ToListAsync();
        auditEntries.Should().HaveCount(1);
        auditEntries[0].FailureReason.Should().Be("User not found");
    }

    [Fact]
    public async Task ResetFailedLoginCountAsync_ClearsCounter_AndLockout()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var user = new User
        {
            Id = userId,
            Username = "testuser",
            Email = "test@test.com",
            PasswordHash = "hash",
            FailedLoginAttempts = 5,
            LockoutEnd = DateTime.UtcNow.AddMinutes(10),
            LastFailedLogin = DateTime.UtcNow
        };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        // Act
        await _service.ResetFailedLoginCountAsync(userId);

        // Assert
        var updatedUser = await _context.Users.FindAsync(userId);
        updatedUser.Should().NotBeNull();
        updatedUser!.FailedLoginAttempts.Should().Be(0);
        updatedUser.LockoutEnd.Should().BeNull();
        updatedUser.LastFailedLogin.Should().BeNull();
    }

    [Fact]
    public async Task GetFailedLoginCountAsync_ReturnsCorrectCount()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var user = new User
        {
            Id = userId,
            Username = "testuser",
            Email = "test@test.com",
            PasswordHash = "hash",
            FailedLoginAttempts = 3
        };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        // Act
        int count = await _service.GetFailedLoginCountAsync(userId);

        // Assert
        count.Should().Be(3);
    }

    [Fact]
    public async Task GetLockoutEndAsync_ReturnsCorrectDateTime()
    {
        // Arrange
        var lockoutEnd = DateTime.UtcNow.AddMinutes(15);
        var userId = Guid.NewGuid();
        var user = new User
        {
            Id = userId,
            Username = "testuser",
            Email = "test@test.com",
            PasswordHash = "hash",
            LockoutEnd = lockoutEnd
        };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        // Act
        DateTime? result = await _service.GetLockoutEndAsync(userId);

        // Assert
        result.Should().NotBeNull();
        result.Should().BeCloseTo(lockoutEnd, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task ManuallyLockAccountAsync_LocksAccount()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var user = new User
        {
            Id = userId,
            Username = "testuser",
            Email = "test@test.com",
            PasswordHash = "hash",
            FailedLoginAttempts = 0
        };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        // Act
        await _service.ManuallyLockAccountAsync(userId, 30);

        // Assert
        var updatedUser = await _context.Users.FindAsync(userId);
        updatedUser.Should().NotBeNull();
        updatedUser!.LockoutEnd.Should().NotBeNull();
        updatedUser.LockoutEnd.Should().BeAfter(DateTime.UtcNow.AddMinutes(29));
    }

    [Fact]
    public async Task UnlockAccountAsync_ClearsLockout()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var user = new User
        {
            Id = userId,
            Username = "testuser",
            Email = "test@test.com",
            PasswordHash = "hash",
            FailedLoginAttempts = 5,
            LockoutEnd = DateTime.UtcNow.AddMinutes(15)
        };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        // Act
        await _service.UnlockAccountAsync(userId);

        // Assert
        var updatedUser = await _context.Users.FindAsync(userId);
        updatedUser.Should().NotBeNull();
        updatedUser!.FailedLoginAttempts.Should().Be(0);
        updatedUser.LockoutEnd.Should().BeNull();
    }
}
