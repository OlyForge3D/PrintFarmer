using System.Security.Claims;
using Farm.Infrastructure;
using Farm.Infrastructure.Contracts.Auth;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Users;
using Farm.Infrastructure.Services.Authentication;
using Farm.Infrastructure.Services.Email;
using Farm.Infrastructure.Services.RateLimiting;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Services.Authentication;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services.Authentication;

/// <summary>
/// Unit tests for AuthenticationService focusing on business logic with mocked dependencies.
/// Integration tests for full authentication flows exist in AuthAuditIntegrationTests.cs and PasswordResetIntegrationTests.cs.
/// </summary>
public class AuthenticationServiceTests
{
    private readonly Mock<IUsersRepository> _mockUsersRepository;
    private readonly Mock<Farm.Infrastructure.Services.Authentication.IPasswordHashingService> _mockPasswordHashing;
    private readonly Mock<IConfiguration> _mockConfiguration;
    private readonly Mock<IUnifiedLoggingService> _mockLogger;
    private readonly Mock<IEmailService> _mockEmailService;
    private readonly Mock<IRateLimitService> _mockRateLimitService;
    private readonly Mock<IAccountLockoutService> _mockAccountLockoutService;
    private readonly Mock<Farm.Infrastructure.Services.Authentication.IAuthAuditService> _mockAuthAuditService;
    private readonly AuthenticationService _service;

    public AuthenticationServiceTests()
    {
        _mockUsersRepository = new Mock<IUsersRepository>();
        _mockPasswordHashing = new Mock<Farm.Infrastructure.Services.Authentication.IPasswordHashingService>();
        _mockConfiguration = new Mock<IConfiguration>();
        _mockLogger = new Mock<IUnifiedLoggingService>();
        _mockEmailService = new Mock<IEmailService>();
        _mockRateLimitService = new Mock<IRateLimitService>();
        _mockAccountLockoutService = new Mock<IAccountLockoutService>();
        _mockAuthAuditService = new Mock<Farm.Infrastructure.Services.Authentication.IAuthAuditService>();

        // Setup configuration defaults for JWT
        _mockConfiguration.Setup(c => c["Jwt:Key"]).Returns("ThisIsASuperSecureKeyForTestingPurposesOnly12345678");
        _mockConfiguration.Setup(c => c["Jwt:Issuer"]).Returns("PrintFarmer");
        _mockConfiguration.Setup(c => c["Jwt:Audience"]).Returns("PrintFarmer");

        _service = new AuthenticationService(
            _mockUsersRepository.Object,
            _mockPasswordHashing.Object,
            _mockConfiguration.Object,
            _mockLogger.Object,
            _mockEmailService.Object,
            _mockRateLimitService.Object,
            _mockAccountLockoutService.Object,
            _mockAuthAuditService.Object
        );
    }

    #region AuthenticateAsync Tests

    [Fact]
    public async Task AuthenticateAsync_WithNonexistentUser_ReturnsFailure()
    {
        // Arrange
        _mockUsersRepository.Setup(r => r.GetByUsernameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((User?)null);
        _mockAccountLockoutService.Setup(s => s.RecordFailedLoginByUsernameAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .Returns(Task.CompletedTask);
        _mockAuthAuditService.Setup(s => s.LogLoginFailedAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        AuthenticationResult result = await _service.AuthenticateAsync("nonexistent", "password");

        // Assert
        result.Success.Should().BeFalse();
        result.Error.Should().Be("Invalid username or password");
    }

    [Fact]
    public async Task AuthenticateAsync_WithLockedAccount_ReturnsFailure()
    {
        // Arrange
        User user = CreateTestUser();
        _mockUsersRepository.Setup(r => r.GetByUsernameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(user);
        _mockAccountLockoutService.Setup(s => s.IsLockedOutAsync(It.IsAny<Guid>()))
            .ReturnsAsync(true);
        _mockAccountLockoutService.Setup(s => s.GetLockoutEndAsync(It.IsAny<Guid>()))
            .ReturnsAsync(DateTime.UtcNow.AddMinutes(15));
        _mockAuthAuditService.Setup(s => s.LogLoginFailedAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        AuthenticationResult result = await _service.AuthenticateAsync("testuser", "password");

        // Assert
        result.Success.Should().BeFalse();
        result.Error.Should().Contain("temporarily locked");
    }

    [Fact]
    public async Task AuthenticateAsync_WithInactiveUser_ReturnsFailure()
    {
        // Arrange
        User user = CreateTestUser();
        user.IsActive = false;
        _mockUsersRepository.Setup(r => r.GetByUsernameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(user);
        _mockAccountLockoutService.Setup(s => s.IsLockedOutAsync(It.IsAny<Guid>()))
            .ReturnsAsync(false);
        _mockAuthAuditService.Setup(s => s.LogLoginFailedAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        AuthenticationResult result = await _service.AuthenticateAsync("testuser", "password");

        // Assert
        result.Success.Should().BeFalse();
        result.Error.Should().Be("User account is disabled");
    }

    [Fact]
    public async Task AuthenticateAsync_WithInvalidPassword_ReturnsFailure()
    {
        // Arrange
        User user = CreateTestUser();
        _mockUsersRepository.Setup(r => r.GetByUsernameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(user);
        _mockAccountLockoutService.Setup(s => s.IsLockedOutAsync(It.IsAny<Guid>()))
            .ReturnsAsync(false);
        _mockPasswordHashing.Setup(p => p.VerifyPassword(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(false);
        _mockAccountLockoutService.Setup(s => s.RecordFailedLoginAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .Returns(Task.CompletedTask);
        _mockAuthAuditService.Setup(s => s.LogLoginFailedAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        AuthenticationResult result = await _service.AuthenticateAsync("testuser", "wrongpassword");

        // Assert
        result.Success.Should().BeFalse();
        result.Error.Should().Be("Invalid username or password");
    }

    [Fact]
    public async Task AuthenticateAsync_WithValidCredentials_ReturnsSuccess()
    {
        // Arrange
        User user = CreateTestUser();
        _mockUsersRepository.Setup(r => r.GetByUsernameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(user);
        _mockAccountLockoutService.Setup(s => s.IsLockedOutAsync(It.IsAny<Guid>()))
            .ReturnsAsync(false);
        _mockPasswordHashing.Setup(p => p.VerifyPassword(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(true);
        _mockAccountLockoutService.Setup(s => s.ResetFailedLoginCountAsync(It.IsAny<Guid>()))
            .Returns(Task.CompletedTask);
        _mockUsersRepository.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockAuthAuditService.Setup(s => s.LogLoginAsync(
                It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockUsersRepository.Setup(r => r.GetUserEntityAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(user);
        _mockUsersRepository.Setup(r => r.GetActiveRoleNamesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string> { "farm_user" });
        _mockUsersRepository.Setup(r => r.GetGrantedPermissionsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<(string Resource, string Action)> { ("printers", "read") });

        // Act
        AuthenticationResult result = await _service.AuthenticateAsync("testuser", "correctpassword");

        // Assert
        result.Success.Should().BeTrue();
        result.Token.Should().NotBeNullOrEmpty();
        result.User.Should().NotBeNull();
        result.User!.Username.Should().Be(user.Username);
    }

    #endregion

    #region Password Reset Tests

    [Fact]
    public async Task InitiatePasswordResetAsync_WithNonexistentEmail_ReturnsTrueAndLogs()
    {
        // Arrange
        _mockRateLimitService.Setup(s => s.CheckPasswordResetLimitAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RateLimitResult(true, 5));
        _mockUsersRepository.Setup(r => r.GetByEmailAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((User?)null);
        _mockRateLimitService.Setup(s => s.RecordPasswordResetAttemptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockAuthAuditService.Setup(s => s.LogPasswordResetInitiatedAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        bool result = await _service.InitiatePasswordResetAsync("missing@example.com", "127.0.0.1");

        // Assert
        result.Should().BeTrue();
        _mockRateLimitService.Verify(s => s.RecordPasswordResetAttemptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        _mockAuthAuditService.Verify(s => s.LogPasswordResetInitiatedAsync(
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task InitiatePasswordResetAsync_WhenRateLimited_ReturnsTrueWithoutEmailSend()
    {
        // Arrange
        _mockRateLimitService.Setup(s => s.CheckPasswordResetLimitAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RateLimitResult(false, 0, TimeSpan.FromMinutes(5)));

        // Act
        bool result = await _service.InitiatePasswordResetAsync("any@example.com", "127.0.0.1");

        // Assert
        result.Should().BeTrue();
        _mockRateLimitService.Verify(s => s.RecordPasswordResetAttemptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _mockAuthAuditService.Verify(s => s.LogPasswordResetInitiatedAsync(
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ResetPasswordAsync_WithInvalidEmail_ReturnsFalse()
    {
        // Arrange
        _mockUsersRepository.Setup(r => r.GetByEmailAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((User?)null);

        // Act
        bool result = await _service.ResetPasswordAsync("token", "missing@example.com", "newPass", "127.0.0.1");

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task ResetPasswordAsync_WithInvalidToken_ReturnsFalse()
    {
        // Arrange
        User user = CreateTestUser();
        _mockUsersRepository.Setup(r => r.GetByEmailAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(user);
        _mockUsersRepository.Setup(r => r.GetPasswordResetTokenAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PasswordResetToken?)null);

        // Act
        bool result = await _service.ResetPasswordAsync("badtoken", user.Email, "newPass", "127.0.0.1");

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task ResetPasswordAsync_WithExpiredToken_ReturnsFalse()
    {
        // Arrange
        User user = CreateTestUser();
        PasswordResetToken token = new()
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            Token = "expired",
            CreatedAt = DateTime.UtcNow.AddHours(-2),
            ExpiresAt = DateTime.UtcNow.AddHours(-1),
            IsUsed = false
        };

        _mockUsersRepository.Setup(r => r.GetByEmailAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(user);
        _mockUsersRepository.Setup(r => r.GetPasswordResetTokenAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(token);

        // Act
        bool result = await _service.ResetPasswordAsync(token.Token, user.Email, "newPass", "127.0.0.1");

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task ResetPasswordAsync_WithValidToken_ResetsPasswordAndLogs()
    {
        // Arrange
        User user = CreateTestUser();
        PasswordResetToken token = new()
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            Token = "validtoken",
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddHours(1),
            IsUsed = false
        };

        _mockUsersRepository.Setup(r => r.GetByEmailAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(user);
        _mockUsersRepository.Setup(r => r.GetPasswordResetTokenAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(token);
        _mockPasswordHashing.Setup(p => p.HashPassword(It.IsAny<string>()))
            .Returns("hashed-new");
        _mockUsersRepository.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockAuthAuditService.Setup(s => s.LogPasswordResetAsync(
                It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        bool result = await _service.ResetPasswordAsync(token.Token, user.Email, "newPass", "127.0.0.1");

        // Assert
        result.Should().BeTrue();
        user.PasswordHash.Should().Be("hashed-new");
        token.IsUsed.Should().BeTrue();
        _mockAuthAuditService.Verify(s => s.LogPasswordResetAsync(
            user.Id, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    #endregion

    #region Email Confirmation Tests

    [Fact]
    public async Task SendEmailConfirmationAsync_WhenRateLimited_ReturnsFalse()
    {
        // Arrange
        User user = CreateTestUser();
        _mockRateLimitService.Setup(s => s.CheckEmailConfirmationLimitAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RateLimitResult(false, 0));

        // Act
        bool result = await _service.SendEmailConfirmationAsync(user);

        // Assert
        result.Should().BeFalse();
        _mockEmailService.Verify(e => e.SendEmailConfirmationAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SendEmailConfirmationAsync_WithValidUser_SendsEmailAndGeneratesToken()
    {
        // Arrange
        User user = CreateTestUser();
        _mockRateLimitService.Setup(s => s.CheckEmailConfirmationLimitAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RateLimitResult(true, 3));
        _mockRateLimitService.Setup(s => s.RecordEmailConfirmationAttemptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockUsersRepository.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockEmailService.Setup(e => e.SendEmailConfirmationAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // Act
        bool result = await _service.SendEmailConfirmationAsync(user);

        // Assert
        result.Should().BeTrue();
        user.EmailConfirmationToken.Should().NotBeNullOrEmpty();
        _mockEmailService.Verify(e => e.SendEmailConfirmationAsync(user.Email, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ConfirmEmailAsync_WithInvalidToken_ReturnsFalse()
    {
        // Arrange
        _mockUsersRepository.Setup(r => r.GetByEmailConfirmationTokenAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((User?)null);

        // Act
        bool result = await _service.ConfirmEmailAsync("badtoken");

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task ConfirmEmailAsync_WithValidToken_ConfirmsEmail()
    {
        // Arrange
        User user = CreateTestUser();
        user.EmailConfirmed = false;
        user.EmailConfirmationToken = "token";

        _mockUsersRepository.Setup(r => r.GetByEmailConfirmationTokenAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(user);
        _mockUsersRepository.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        bool result = await _service.ConfirmEmailAsync("token");

        // Assert
        result.Should().BeTrue();
        user.EmailConfirmed.Should().BeTrue();
        user.EmailConfirmationToken.Should().BeNull();
    }

    #endregion

    #region Permission and User DTO Tests

    [Fact]
    public async Task HasPermissionAsync_WithMatchingPermission_ReturnsTrue()
    {
        // Arrange
        Guid userId = Guid.NewGuid();
        _mockUsersRepository.Setup(r => r.GetGrantedPermissionsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<(string Resource, string Action)> { ("printers", "read") });

        // Act
        bool result = await _service.HasPermissionAsync(userId, "printers", "read");

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public async Task GetUserWithRolesAndPermissionsAsync_WhenUserMissing_ReturnsNull()
    {
        // Arrange
        _mockUsersRepository.Setup(r => r.GetUserEntityAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((User?)null);

        // Act
        UserDto? result = await _service.GetUserWithRolesAndPermissionsAsync(Guid.NewGuid());

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetUserWithRolesAndPermissionsAsync_WhenUserExists_ReturnsDto()
    {
        // Arrange
        User user = CreateTestUser();
        _mockUsersRepository.Setup(r => r.GetUserEntityAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(user);
        _mockUsersRepository.Setup(r => r.GetActiveRoleNamesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string> { "farm_user" });
        _mockUsersRepository.Setup(r => r.GetGrantedPermissionsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<(string Resource, string Action)> { ("printers", "read") });

        // Act
        UserDto? result = await _service.GetUserWithRolesAndPermissionsAsync(user.Id);

        // Assert
        result.Should().NotBeNull();
        result!.Username.Should().Be(user.Username);
        result.Roles.Should().Contain("farm_user");
        result.Permissions.Should().Contain("printers:read");
    }

    #endregion

    #region RegisterAsync Tests

    [Fact]
    public async Task RegisterAsync_WithExistingUsername_ReturnsFailure()
    {
        // Arrange
        RegisterRequest request = new()
        {
            Username = "existinguser",
            Email = "new@example.com",
            Password = "Password123!",
            FirstName = "Test",
            LastName = "User"
        };

        _mockUsersRepository.Setup(r => r.GetByUsernameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((User?)null);
        _mockUsersRepository.Setup(r => r.UsernameExistsStrictAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // Act
        AuthenticationResult result = await _service.RegisterAsync(request);

        // Assert
        result.Success.Should().BeFalse();
        result.Error.Should().Be("Username is already taken");
    }

    [Fact]
    public async Task RegisterAsync_WithExistingEmail_ReturnsFailure()
    {
        // Arrange
        RegisterRequest request = new()
        {
            Username = "newuser",
            Email = "existing@example.com",
            Password = "Password123!",
            FirstName = "Test",
            LastName = "User"
        };

        _mockUsersRepository.Setup(r => r.GetByUsernameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((User?)null);
        _mockUsersRepository.Setup(r => r.UsernameExistsStrictAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _mockUsersRepository.Setup(r => r.EmailExistsStrictAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // Act
        AuthenticationResult result = await _service.RegisterAsync(request);

        // Assert
        result.Success.Should().BeFalse();
        result.Error.Should().Be("Email is already registered");
    }

    [Fact]
    public async Task RegisterAsync_WithValidData_CreatesUserAndReturnsSuccess()
    {
        // Arrange
        RegisterRequest request = new()
        {
            Username = "newuser",
            Email = "new@example.com",
            Password = "Password123!",
            FirstName = "Test",
            LastName = "User"
        };

        _mockUsersRepository.Setup(r => r.GetByUsernameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((User?)null);
        _mockUsersRepository.Setup(r => r.UsernameExistsStrictAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _mockUsersRepository.Setup(r => r.EmailExistsStrictAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _mockPasswordHashing.Setup(p => p.HashPassword(It.IsAny<string>()))
            .Returns("$hashed$password$");
        _mockUsersRepository.Setup(r => r.AddUserAsync(It.IsAny<User>(), It.IsAny<IEnumerable<Guid>?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockUsersRepository.Setup(r => r.GetRoleByNameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Role { Id = Guid.NewGuid(), Name = "farm_user" });
        _mockUsersRepository.Setup(r => r.UpdateUserRolesAsync(It.IsAny<Guid>(), It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockUsersRepository.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockAuthAuditService.Setup(s => s.LogRegisterAsync(
                It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockUsersRepository.Setup(r => r.GetActiveRoleNamesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string> { "farm_user" });
        _mockUsersRepository.Setup(r => r.GetGrantedPermissionsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<(string Resource, string Action)>());

        // Act
        AuthenticationResult result = await _service.RegisterAsync(request);

        // Assert
        result.Success.Should().BeTrue();
        result.Token.Should().NotBeNullOrEmpty();
    }

    #endregion

    #region GenerateJwtTokenAsync Tests

    [Fact]
    public async Task GenerateJwtTokenAsync_WithNullUser_ThrowsArgumentNullException()
    {
        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() => _service.GenerateJwtTokenAsync(null!));
    }

    [Fact]
    public async Task GenerateJwtTokenAsync_WithMissingJwtKey_ThrowsInvalidOperationException()
    {
        // Arrange
        _mockConfiguration.Setup(c => c["Jwt:Key"]).Returns(string.Empty);
        User user = CreateTestUser();

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.GenerateJwtTokenAsync(user));
    }

    [Fact]
    public async Task GenerateJwtTokenAsync_WithValidUser_GeneratesToken()
    {
        // Arrange
        User user = CreateTestUser();
        _mockUsersRepository.Setup(r => r.GetActiveRoleNamesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string> { "farm_admin", "farm_user" });
        _mockUsersRepository.Setup(r => r.GetGrantedPermissionsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<(string Resource, string Action)>
            {
                ("printers", "read"),
                ("printers", "write")
            });

        // Act
        string token = await _service.GenerateJwtTokenAsync(user);

        // Assert
        token.Should().NotBeNullOrEmpty();
        token.Split('.').Should().HaveCount(3); // JWT has 3 parts: header.payload.signature
    }

    #endregion

    #region ValidateTokenAsync Tests

    [Fact]
    public async Task ValidateTokenAsync_WithValidToken_ReturnsTrue()
    {
        // Arrange
        User user = CreateTestUser();
        _mockUsersRepository.Setup(r => r.GetActiveRoleNamesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string> { "farm_user" });
        _mockUsersRepository.Setup(r => r.GetGrantedPermissionsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<(string Resource, string Action)>());

        string token = await _service.GenerateJwtTokenAsync(user);

        // Act
        bool result = await _service.ValidateTokenAsync(token);

        // Assert
        result.Should().BeTrue();
    }

    #endregion

    #region GetPrincipalFromTokenAsync Tests

    [Fact]
    public async Task GetPrincipalFromTokenAsync_WithValidToken_ReturnsPrincipal()
    {
        // Arrange
        User user = CreateTestUser();
        _mockUsersRepository.Setup(r => r.GetActiveRoleNamesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string> { "farm_user" });
        _mockUsersRepository.Setup(r => r.GetGrantedPermissionsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<(string Resource, string Action)> { ("printers", "read") });

        string token = await _service.GenerateJwtTokenAsync(user);

        // Act
        ClaimsPrincipal? principal = await _service.GetPrincipalFromTokenAsync(token);

        // Assert
        principal.Should().NotBeNull();
        principal!.FindFirst(ClaimTypes.NameIdentifier)?.Value.Should().Be(user.Id.ToString());
        principal.FindFirst(ClaimTypes.Name)?.Value.Should().Be(user.Username);
        principal.FindFirst(ClaimTypes.Email)?.Value.Should().Be(user.Email);
    }

    #endregion

    #region ChangePasswordAsync Tests

    [Fact]
    public async Task ChangePasswordAsync_WithNonexistentUser_ReturnsFalse()
    {
        // Arrange
        Guid userId = Guid.NewGuid();
        _mockUsersRepository.Setup(r => r.GetUserEntityAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((User?)null);

        // Act
        bool result = await _service.ChangePasswordAsync(userId, "currentpass", "newpass");

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task ChangePasswordAsync_WithInvalidCurrentPassword_ReturnsFalse()
    {
        // Arrange
        User user = CreateTestUser();
        _mockUsersRepository.Setup(r => r.GetUserEntityAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(user);
        _mockPasswordHashing.Setup(p => p.VerifyPassword(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(false);

        // Act
        bool result = await _service.ChangePasswordAsync(user.Id, "wrongcurrent", "newpass");

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task ChangePasswordAsync_WithValidCurrentPassword_UpdatesPassword()
    {
        // Arrange
        User user = CreateTestUser();
        _mockUsersRepository.Setup(r => r.GetUserEntityAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(user);
        _mockPasswordHashing.Setup(p => p.VerifyPassword(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(true);
        _mockPasswordHashing.Setup(p => p.HashPassword(It.IsAny<string>()))
            .Returns("$new$hashed$password$");
        _mockUsersRepository.Setup(r => r.UpdatePasswordAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _mockAuthAuditService.Setup(s => s.LogPasswordChangeAsync(
                It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        bool result = await _service.ChangePasswordAsync(user.Id, "currentpass", "newpass");

        // Assert
        result.Should().BeTrue();
    }

    #endregion

    #region Helper Methods

    private static User CreateTestUser()
    {
        return new User
        {
            Id = Guid.NewGuid(),
            Username = "testuser",
            Email = "test@example.com",
            PasswordHash = "$hashed$password$",
            FirstName = "Test",
            LastName = "User",
            IsActive = true,
            EmailConfirmed = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
    }

    #endregion
}
