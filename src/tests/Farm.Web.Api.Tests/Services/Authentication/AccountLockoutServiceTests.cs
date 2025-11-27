using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Web.Api.Services.Authentication;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services.Authentication;

public class AccountLockoutServiceTests : IDisposable
{
    private readonly AppDbContext _context;
    private readonly AccountLockoutService _service;
    private readonly IConfiguration _configuration;
    private readonly Mock<IAuthAuditService> _mockAuthAuditService;

    public AccountLockoutServiceTests()
    {
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"AccountLockoutTest_{Guid.NewGuid()}")
            .Options;
        _context = new AppDbContext(options);

        // Create configuration with test settings
        Dictionary<string, string?> configData = new Dictionary<string, string?>
        {
            ["AccountLockout:MaxFailedAttempts"] = "5",
            ["AccountLockout:LockoutDurationMinutes"] = "15",
            ["AccountLockout:AttemptWindowMinutes"] = "15"
        };
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        // Create mock audit service
        _mockAuthAuditService = new Mock<IAuthAuditService>();

        _service = new AccountLockoutService(_context, _configuration, _mockAuthAuditService.Object);
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
        Guid userId = Guid.NewGuid();
        User user = new User
        {
            Id = userId,
            Username = "testuser",
            Email = "test@test.com",
            PasswordHash = "hash",
            FailedLoginAttempts = 0,
            LockoutEnd = null
        };
        _ = _context.Users.Add(user);
        _ = await _context.SaveChangesAsync();

        // Act
        bool result = await _service.IsLockedOutAsync(userId);

        // Assert
        _ = result.Should().BeFalse();
    }

    [Fact]
    public async Task IsLockedOutAsync_ReturnsTrue_WhenUserLockedAndNotExpired()
    {
        // Arrange
        Guid userId = Guid.NewGuid();
        User user = new User
        {
            Id = userId,
            Username = "testuser",
            Email = "test@test.com",
            PasswordHash = "hash",
            FailedLoginAttempts = 5,
            LockoutEnd = DateTime.UtcNow.AddMinutes(10) // Locked for 10 more minutes
        };
        _ = _context.Users.Add(user);
        _ = await _context.SaveChangesAsync();

        // Act
        bool result = await _service.IsLockedOutAsync(userId);

        // Assert
        _ = result.Should().BeTrue();
    }

    [Fact]
    public async Task IsLockedOutAsync_ReturnsFalse_WhenLockoutExpired()
    {
        // Arrange
        Guid userId = Guid.NewGuid();
        User user = new User
        {
            Id = userId,
            Username = "testuser",
            Email = "test@test.com",
            PasswordHash = "hash",
            FailedLoginAttempts = 5,
            LockoutEnd = DateTime.UtcNow.AddMinutes(-5) // Expired 5 minutes ago
        };
        _ = _context.Users.Add(user);
        _ = await _context.SaveChangesAsync();

        // Act
        bool result = await _service.IsLockedOutAsync(userId);

        // Assert
        _ = result.Should().BeFalse();
    }

    [Fact]
    public async Task RecordFailedLoginAsync_IncrementsCounter()
    {
        // Arrange
        Guid userId = Guid.NewGuid();
        User user = new User
        {
            Id = userId,
            Username = "testuser",
            Email = "test@test.com",
            PasswordHash = "hash",
            FailedLoginAttempts = 2
        };
        _ = _context.Users.Add(user);
        _ = await _context.SaveChangesAsync();

        // Act
        await _service.RecordFailedLoginAsync(userId, "testuser", "192.168.1.1", "Invalid password");

        // Assert
        User? updatedUser = await _context.Users.FindAsync(userId);
        _ = updatedUser.Should().NotBeNull();
        _ = updatedUser!.FailedLoginAttempts.Should().Be(3);
    }

    [Fact]
    public async Task RecordFailedLoginAsync_LocksAccount_WhenThresholdExceeded()
    {
        // Arrange
        Guid userId = Guid.NewGuid();
        User user = new User
        {
            Id = userId,
            Username = "testuser",
            Email = "test@test.com",
            PasswordHash = "hash",
            FailedLoginAttempts = 4 // One away from threshold of 5
        };
        _ = _context.Users.Add(user);
        _ = await _context.SaveChangesAsync();

        // Act
        await _service.RecordFailedLoginAsync(userId, "testuser", "192.168.1.1", "Invalid password");

        // Assert
        User? updatedUser = await _context.Users.FindAsync(userId);
        _ = updatedUser.Should().NotBeNull();
        _ = updatedUser!.FailedLoginAttempts.Should().Be(5);
        _ = updatedUser.LockoutEnd.Should().NotBeNull();
        _ = updatedUser.LockoutEnd.Should().BeAfter(DateTime.UtcNow);
    }

    [Fact]
    public async Task RecordFailedLoginAsync_CreatesAuditEntry()
    {
        // Arrange
        Guid userId = Guid.NewGuid();
        User user = new User
        {
            Id = userId,
            Username = "testuser",
            Email = "test@test.com",
            PasswordHash = "hash",
            FailedLoginAttempts = 0
        };
        _ = _context.Users.Add(user);
        _ = await _context.SaveChangesAsync();

        // Act
        await _service.RecordFailedLoginAsync(userId, "testuser", "192.168.1.1", "Invalid password");

        // Assert
        List<FailedLoginAttempt> auditEntries = await _context.FailedLoginAttempts
            .Where(f => f.Identifier == "testuser")
            .ToListAsync();
        _ = auditEntries.Should().HaveCount(1);
        _ = auditEntries[0].IpAddress.Should().Be("192.168.1.1");
        _ = auditEntries[0].FailureReason.Should().Be("Invalid password");
    }

    [Fact]
    public async Task RecordFailedLoginByUsernameAsync_CreatesAuditEntry_ForNonExistentUser()
    {
        // Act
        await _service.RecordFailedLoginByUsernameAsync("nonexistent", "192.168.1.1", "User not found");

        // Assert
        List<FailedLoginAttempt> auditEntries = await _context.FailedLoginAttempts
            .Where(f => f.Identifier == "nonexistent")
            .ToListAsync();
        _ = auditEntries.Should().HaveCount(1);
        _ = auditEntries[0].FailureReason.Should().Be("User not found");
    }

    [Fact]
    public async Task ResetFailedLoginCountAsync_ClearsCounter_AndLockout()
    {
        // Arrange
        Guid userId = Guid.NewGuid();
        User user = new User
        {
            Id = userId,
            Username = "testuser",
            Email = "test@test.com",
            PasswordHash = "hash",
            FailedLoginAttempts = 5,
            LockoutEnd = DateTime.UtcNow.AddMinutes(10),
            LastFailedLogin = DateTime.UtcNow
        };
        _ = _context.Users.Add(user);
        _ = await _context.SaveChangesAsync();

        // Act
        await _service.ResetFailedLoginCountAsync(userId);

        // Assert
        User? updatedUser = await _context.Users.FindAsync(userId);
        _ = updatedUser.Should().NotBeNull();
        _ = updatedUser!.FailedLoginAttempts.Should().Be(0);
        _ = updatedUser.LockoutEnd.Should().BeNull();
        _ = updatedUser.LastFailedLogin.Should().BeNull();
    }

    [Fact]
    public async Task GetFailedLoginCountAsync_ReturnsCorrectCount()
    {
        // Arrange
        Guid userId = Guid.NewGuid();
        User user = new User
        {
            Id = userId,
            Username = "testuser",
            Email = "test@test.com",
            PasswordHash = "hash",
            FailedLoginAttempts = 3
        };
        _ = _context.Users.Add(user);
        _ = await _context.SaveChangesAsync();

        // Act
        int count = await _service.GetFailedLoginCountAsync(userId);

        // Assert
        _ = count.Should().Be(3);
    }

    [Fact]
    public async Task GetLockoutEndAsync_ReturnsCorrectDateTime()
    {
        // Arrange
        DateTime lockoutEnd = DateTime.UtcNow.AddMinutes(15);
        Guid userId = Guid.NewGuid();
        User user = new User
        {
            Id = userId,
            Username = "testuser",
            Email = "test@test.com",
            PasswordHash = "hash",
            LockoutEnd = lockoutEnd
        };
        _ = _context.Users.Add(user);
        _ = await _context.SaveChangesAsync();

        // Act
        DateTime? result = await _service.GetLockoutEndAsync(userId);

        // Assert
        _ = result.Should().NotBeNull();
        _ = result.Should().BeCloseTo(lockoutEnd, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task ManuallyLockAccountAsync_LocksAccount()
    {
        // Arrange
        Guid userId = Guid.NewGuid();
        User user = new User
        {
            Id = userId,
            Username = "testuser",
            Email = "test@test.com",
            PasswordHash = "hash",
            FailedLoginAttempts = 0
        };
        _ = _context.Users.Add(user);
        _ = await _context.SaveChangesAsync();

        // Act
        await _service.ManuallyLockAccountAsync(userId, 30);

        // Assert
        User? updatedUser = await _context.Users.FindAsync(userId);
        _ = updatedUser.Should().NotBeNull();
        _ = updatedUser!.LockoutEnd.Should().NotBeNull();
        _ = updatedUser.LockoutEnd.Should().BeAfter(DateTime.UtcNow.AddMinutes(29));
    }

    [Fact]
    public async Task UnlockAccountAsync_ClearsLockout()
    {
        // Arrange
        Guid userId = Guid.NewGuid();
        User user = new User
        {
            Id = userId,
            Username = "testuser",
            Email = "test@test.com",
            PasswordHash = "hash",
            FailedLoginAttempts = 5,
            LockoutEnd = DateTime.UtcNow.AddMinutes(15)
        };
        _ = _context.Users.Add(user);
        _ = await _context.SaveChangesAsync();

        // Act
        await _service.UnlockAccountAsync(userId);

        // Assert
        User? updatedUser = await _context.Users.FindAsync(userId);
        _ = updatedUser.Should().NotBeNull();
        _ = updatedUser!.FailedLoginAttempts.Should().Be(0);
        _ = updatedUser.LockoutEnd.Should().BeNull();
    }
}
