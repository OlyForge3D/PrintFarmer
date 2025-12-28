using Farm.Infrastructure;
using Farm.Infrastructure.Contracts.Auth;
using Farm.Infrastructure.Contracts.Setup;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Users;
using Farm.Infrastructure.Services.Authentication;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Services.Authentication;
using Farm.Web.Api.Services.Setup;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services;

public class SetupServiceTests
{
    private readonly Mock<IUsersRepository> _usersRepository;
    private readonly Mock<IAuthenticationService> _authService;
    private readonly Mock<IPasswordHashingService> _passwordHashingService;
    private readonly Mock<IUnifiedLoggingService> _logger;
    private readonly SetupService _service;

    public SetupServiceTests()
    {
        _usersRepository = new Mock<IUsersRepository>();
        _authService = new Mock<IAuthenticationService>();
        _passwordHashingService = new Mock<IPasswordHashingService>();
        _logger = new Mock<IUnifiedLoggingService>();

        _service = new SetupService(
            _usersRepository.Object,
            _authService.Object,
            _passwordHashingService.Object,
            _logger.Object);
    }

    #region NeedsSetupAsync Tests

    [Fact]
    public async Task NeedsSetupAsync_WhenNoAdminUsers_ReturnsTrue()
    {
        // Arrange
        _usersRepository.Setup(r => r.HasAdminUsersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        // Act
        var result = await _service.NeedsSetupAsync(CancellationToken.None);

        // Assert
        Assert.True(result);
        _usersRepository.Verify(r => r.HasAdminUsersAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task NeedsSetupAsync_WhenAdminUsersExist_ReturnsFalse()
    {
        // Arrange
        _usersRepository.Setup(r => r.HasAdminUsersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // Act
        var result = await _service.NeedsSetupAsync(CancellationToken.None);

        // Assert
        Assert.False(result);
    }

    #endregion

    #region CreateInitialAdminAsync - Null/Invalid Request Tests

    [Fact]
    public async Task CreateInitialAdminAsync_WithNullRequest_ReturnsFalseWithError()
    {
        // Act
        var result = await _service.CreateInitialAdminAsync(null!, CancellationToken.None);

        // Assert
        Assert.False(result.Success);
        Assert.Contains("Request body required", result.Error ?? "");
    }

    [Fact]
    public async Task CreateInitialAdminAsync_WithEmptyUsername_ReturnsFalseWithError()
    {
        // Arrange
        var request = new CreateInitialAdminRequest
        {
            Username = "",
            Email = "admin@example.com",
            Password = "ValidPassword123!",
            FirstName = "Admin",
            LastName = "User"
        };

        _usersRepository.Setup(r => r.HasAdminUsersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        // Act
        var result = await _service.CreateInitialAdminAsync(request, CancellationToken.None);

        // Assert
        Assert.False(result.Success);
        Assert.Contains("required", result.Error ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateInitialAdminAsync_WithEmptyEmail_ReturnsFalseWithError()
    {
        // Arrange
        var request = new CreateInitialAdminRequest
        {
            Username = "admin",
            Email = "",
            Password = "ValidPassword123!",
            FirstName = "Admin",
            LastName = "User"
        };

        _usersRepository.Setup(r => r.HasAdminUsersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        // Act
        var result = await _service.CreateInitialAdminAsync(request, CancellationToken.None);

        // Assert
        Assert.False(result.Success);
    }

    [Fact]
    public async Task CreateInitialAdminAsync_WithEmptyPassword_ReturnsFalseWithError()
    {
        // Arrange
        var request = new CreateInitialAdminRequest
        {
            Username = "admin",
            Email = "admin@example.com",
            Password = "",
            FirstName = "Admin",
            LastName = "User"
        };

        _usersRepository.Setup(r => r.HasAdminUsersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        // Act
        var result = await _service.CreateInitialAdminAsync(request, CancellationToken.None);

        // Assert
        Assert.False(result.Success);
    }

    #endregion

    #region CreateInitialAdminAsync - Setup Already Completed Tests

    [Fact]
    public async Task CreateInitialAdminAsync_WhenAdminAlreadyExists_ReturnsFalseWithError()
    {
        // Arrange
        var request = new CreateInitialAdminRequest
        {
            Username = "admin",
            Email = "admin@example.com",
            Password = "ValidPassword123!",
            FirstName = "Admin",
            LastName = "User"
        };

        _usersRepository.Setup(r => r.HasAdminUsersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _usersRepository.Setup(r => r.GetAdminByUsernameAndEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((User?)null);
        _usersRepository.Setup(r => r.AnyUserByUsernameOrEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        // Act
        var result = await _service.CreateInitialAdminAsync(request, CancellationToken.None);

        // Assert
        Assert.False(result.Success);
        Assert.Contains("Setup has already been completed", result.Error ?? "");
    }

    [Fact]
    public async Task CreateInitialAdminAsync_WhenAdminExistsButUsernameOrEmailTaken_ReturnsFalseWithError()
    {
        // Arrange
        var request = new CreateInitialAdminRequest
        {
            Username = "admin",
            Email = "admin@example.com",
            Password = "DifferentPassword123!",
            FirstName = "Admin",
            LastName = "User"
        };

        _usersRepository.Setup(r => r.HasAdminUsersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _usersRepository.Setup(r => r.AnyUserByUsernameOrEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // Act
        var result = await _service.CreateInitialAdminAsync(request, CancellationToken.None);

        // Assert
        Assert.False(result.Success);
        Assert.Contains("already taken", result.Error ?? "");
    }

    [Fact]
    public async Task CreateInitialAdminAsync_WhenExistingAdminWithSameCredentials_ReturnsSuccessWithToken()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var request = new CreateInitialAdminRequest
        {
            Username = "admin",
            Email = "admin@example.com",
            Password = "ValidPassword123!",
            FirstName = "Admin",
            LastName = "User"
        };

        var existingUser = new User
        {
            Id = userId,
            Username = "admin",
            Email = "admin@example.com",
            PasswordHash = "hashed_password",
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };

        var userDto = new UserDto
        {
            Id = userId,
            Username = "admin",
            Email = "admin@example.com",
            FirstName = "Admin",
            LastName = "User"
        };

        _usersRepository.Setup(r => r.HasAdminUsersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _usersRepository.Setup(r => r.GetAdminByUsernameAndEmailAsync("admin", "admin@example.com", It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingUser);
        _passwordHashingService.Setup(p => p.VerifyPassword("ValidPassword123!", "hashed_password"))
            .Returns(true);
        _authService.Setup(a => a.GenerateJwtTokenAsync(existingUser))
            .ReturnsAsync("test_token");
        _authService.Setup(a => a.GetUserWithRolesAndPermissionsAsync(userId))
            .ReturnsAsync(userDto);

        // Act
        var result = await _service.CreateInitialAdminAsync(request, CancellationToken.None);

        // Assert
        Assert.True(result.Success);
        Assert.Equal("test_token", result.Token);
        Assert.NotNull(result.User);
    }

    #endregion

    #region CreateInitialAdminAsync - Password Policy Validation Tests

    [Fact]
    public async Task CreateInitialAdminAsync_WithPasswordBelowMinimumLength_ReturnsFalseWithError()
    {
        // Arrange
        var request = new CreateInitialAdminRequest
        {
            Username = "admin",
            Email = "admin@example.com",
            Password = "Short1!",
            FirstName = "Admin",
            LastName = "User"
        };

        var policy = new PasswordPolicyEntity { MinLength = 12, RequireUppercase = false, RequireLowercase = false, RequireDigit = false, RequireSymbol = false };

        _usersRepository.Setup(r => r.HasAdminUsersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _usersRepository.Setup(r => r.GetPasswordPolicyAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(policy);

        // Act
        var result = await _service.CreateInitialAdminAsync(request, CancellationToken.None);

        // Assert
        Assert.False(result.Success);
        Assert.Contains("12 characters", result.Error ?? "");
    }

    [Fact]
    public async Task CreateInitialAdminAsync_WithNoPasswordPolicy_UsesDefaultMinimumLength()
    {
        // Arrange
        var request = new CreateInitialAdminRequest
        {
            Username = "admin",
            Email = "admin@example.com",
            Password = "Short",
            FirstName = "Admin",
            LastName = "User"
        };

        _usersRepository.Setup(r => r.HasAdminUsersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _usersRepository.Setup(r => r.GetPasswordPolicyAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((PasswordPolicyEntity?)null);

        // Act
        var result = await _service.CreateInitialAdminAsync(request, CancellationToken.None);

        // Assert
        Assert.False(result.Success);
        Assert.Contains("8 characters", result.Error ?? "");
    }

    [Fact]
    public async Task CreateInitialAdminAsync_WithRequiredUppercaseMissing_ReturnsFalseWithError()
    {
        // Arrange
        var request = new CreateInitialAdminRequest
        {
            Username = "admin",
            Email = "admin@example.com",
            Password = "password123!",
            FirstName = "Admin",
            LastName = "User"
        };

        var policy = new PasswordPolicyEntity
        {
            MinLength = 8,
            RequireUppercase = true,
            RequireLowercase = false,
            RequireDigit = false,
            RequireSymbol = false
        };

        _usersRepository.Setup(r => r.HasAdminUsersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _usersRepository.Setup(r => r.GetPasswordPolicyAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(policy);

        // Act
        var result = await _service.CreateInitialAdminAsync(request, CancellationToken.None);

        // Assert
        Assert.False(result.Success);
        Assert.Contains("uppercase", result.Error ?? "");
    }

    [Fact]
    public async Task CreateInitialAdminAsync_WithRequiredLowercaseMissing_ReturnsFalseWithError()
    {
        // Arrange
        var request = new CreateInitialAdminRequest
        {
            Username = "admin",
            Email = "admin@example.com",
            Password = "PASSWORD123!",
            FirstName = "Admin",
            LastName = "User"
        };

        var policy = new PasswordPolicyEntity
        {
            MinLength = 8,
            RequireUppercase = false,
            RequireLowercase = true,
            RequireDigit = false,
            RequireSymbol = false
        };

        _usersRepository.Setup(r => r.HasAdminUsersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _usersRepository.Setup(r => r.GetPasswordPolicyAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(policy);

        // Act
        var result = await _service.CreateInitialAdminAsync(request, CancellationToken.None);

        // Assert
        Assert.False(result.Success);
        Assert.Contains("lowercase", result.Error ?? "");
    }

    [Fact]
    public async Task CreateInitialAdminAsync_WithRequiredDigitMissing_ReturnsFalseWithError()
    {
        // Arrange
        var request = new CreateInitialAdminRequest
        {
            Username = "admin",
            Email = "admin@example.com",
            Password = "Password!",
            FirstName = "Admin",
            LastName = "User"
        };

        var policy = new PasswordPolicyEntity
        {
            MinLength = 8,
            RequireUppercase = false,
            RequireLowercase = false,
            RequireDigit = true,
            RequireSymbol = false
        };

        _usersRepository.Setup(r => r.HasAdminUsersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _usersRepository.Setup(r => r.GetPasswordPolicyAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(policy);

        // Act
        var result = await _service.CreateInitialAdminAsync(request, CancellationToken.None);

        // Assert
        Assert.False(result.Success);
        Assert.Contains("digit", result.Error ?? "");
    }

    [Fact]
    public async Task CreateInitialAdminAsync_WithRequiredSymbolMissing_ReturnsFalseWithError()
    {
        // Arrange
        var request = new CreateInitialAdminRequest
        {
            Username = "admin",
            Email = "admin@example.com",
            Password = "Password123",
            FirstName = "Admin",
            LastName = "User"
        };

        var policy = new PasswordPolicyEntity
        {
            MinLength = 8,
            RequireUppercase = false,
            RequireLowercase = false,
            RequireDigit = false,
            RequireSymbol = true
        };

        _usersRepository.Setup(r => r.HasAdminUsersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _usersRepository.Setup(r => r.GetPasswordPolicyAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(policy);

        // Act
        var result = await _service.CreateInitialAdminAsync(request, CancellationToken.None);

        // Assert
        Assert.False(result.Success);
        Assert.Contains("symbol", result.Error ?? "");
    }

    #endregion

    #region CreateInitialAdminAsync - Duplicate User Tests

    [Fact]
    public async Task CreateInitialAdminAsync_WithDuplicateUsername_ReturnsFalseWithError()
    {
        // Arrange
        var request = new CreateInitialAdminRequest
        {
            Username = "existinguser",
            Email = "newadmin@example.com",
            Password = "ValidPassword123!",
            FirstName = "Admin",
            LastName = "User"
        };

        _usersRepository.Setup(r => r.HasAdminUsersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _usersRepository.Setup(r => r.GetPasswordPolicyAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((PasswordPolicyEntity?)null);
        _usersRepository.Setup(r => r.AnyUserByUsernameOrEmailAsync("existinguser", "newadmin@example.com", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // Act
        var result = await _service.CreateInitialAdminAsync(request, CancellationToken.None);

        // Assert
        Assert.False(result.Success);
        Assert.Contains("already taken", result.Error ?? "");
    }

    [Fact]
    public async Task CreateInitialAdminAsync_WithDuplicateEmail_ReturnsFalseWithError()
    {
        // Arrange
        var request = new CreateInitialAdminRequest
        {
            Username = "newadmin",
            Email = "existing@example.com",
            Password = "ValidPassword123!",
            FirstName = "Admin",
            LastName = "User"
        };

        _usersRepository.Setup(r => r.HasAdminUsersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _usersRepository.Setup(r => r.GetPasswordPolicyAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((PasswordPolicyEntity?)null);
        _usersRepository.Setup(r => r.AnyUserByUsernameOrEmailAsync("newadmin", "existing@example.com", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // Act
        var result = await _service.CreateInitialAdminAsync(request, CancellationToken.None);

        // Assert
        Assert.False(result.Success);
        Assert.Contains("already taken", result.Error ?? "");
    }

    #endregion

    #region CreateInitialAdminAsync - Successful Admin Creation Tests

    [Fact]
    public async Task CreateInitialAdminAsync_WithValidRequest_CreatesAdminAndReturnsToken()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var request = new CreateInitialAdminRequest
        {
            Username = "admin",
            Email = "admin@example.com",
            Password = "ValidPassword123!",
            FirstName = "Admin",
            LastName = "User"
        };

        var adminRole = new Role { Id = Guid.NewGuid(), Name = "farm_admin" };
        var userDto = new UserDto
        {
            Id = userId,
            Username = "admin",
            Email = "admin@example.com",
            FirstName = "Admin",
            LastName = "User"
        };

        _usersRepository.Setup(r => r.HasAdminUsersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _usersRepository.Setup(r => r.GetPasswordPolicyAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((PasswordPolicyEntity?)null);
        _usersRepository.Setup(r => r.AnyUserByUsernameOrEmailAsync("admin", "admin@example.com", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _usersRepository.Setup(r => r.GetRoleByNameAsync("farm_admin", It.IsAny<CancellationToken>()))
            .ReturnsAsync(adminRole);
        _passwordHashingService.Setup(p => p.HashPassword("ValidPassword123!"))
            .Returns("hashed_password");
        _usersRepository.Setup(r => r.AddUserWithRoleAsync(It.IsAny<User>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _authService.Setup(a => a.GenerateJwtTokenAsync(It.IsAny<User>()))
            .ReturnsAsync("test_token_123");
        _authService.Setup(a => a.GetUserWithRolesAndPermissionsAsync(It.IsAny<Guid>()))
            .ReturnsAsync(userDto);

        // Act
        var result = await _service.CreateInitialAdminAsync(request, CancellationToken.None);

        // Assert
        Assert.True(result.Success);
        Assert.Equal("test_token_123", result.Token);
        Assert.NotNull(result.User);
        Assert.Equal("admin", result.User.Username);
        Assert.Equal("admin@example.com", result.User.Email);
        Assert.NotNull(result.ExpiresAt);

        _usersRepository.Verify(
            r => r.AddUserWithRoleAsync(It.IsAny<User>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Once);
        _logger.Verify(
            l => l.LogInformation(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<object?>()),
            Times.Once);
    }

    [Fact]
    public async Task CreateInitialAdminAsync_WhenRoleNotFound_ReturnsFalseWithError()
    {
        // Arrange
        var request = new CreateInitialAdminRequest
        {
            Username = "admin",
            Email = "admin@example.com",
            Password = "ValidPassword123!",
            FirstName = "Admin",
            LastName = "User"
        };

        _usersRepository.Setup(r => r.HasAdminUsersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _usersRepository.Setup(r => r.GetPasswordPolicyAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((PasswordPolicyEntity?)null);
        _usersRepository.Setup(r => r.AnyUserByUsernameOrEmailAsync("admin", "admin@example.com", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _usersRepository.Setup(r => r.GetRoleByNameAsync("farm_admin", It.IsAny<CancellationToken>()))
            .ReturnsAsync((Role?)null);

        // Act
        var result = await _service.CreateInitialAdminAsync(request, CancellationToken.None);

        // Assert
        Assert.False(result.Success);
        Assert.Contains("Admin role not found", result.Error ?? "");
    }

    #endregion

    #region GetConfigurationOptions Tests

    [Fact]
    public void GetConfigurationOptions_ReturnsValidConfiguration()
    {
        // Act
        var options = _service.GetConfigurationOptions();

        // Assert
        Assert.NotNull(options);
        Assert.NotNull(options.DatabaseProviders);
        Assert.NotEmpty(options.DatabaseProviders);
        Assert.Contains("SQLite", options.DatabaseProviders);
        Assert.Contains("SQL Server", options.DatabaseProviders);
        Assert.Contains("PostgreSQL", options.DatabaseProviders);
        Assert.Contains("MySQL", options.DatabaseProviders);
    }

    [Fact]
    public void GetConfigurationOptions_IncludesDefaultNetworkRanges()
    {
        // Act
        var options = _service.GetConfigurationOptions();

        // Assert
        Assert.NotNull(options.DefaultNetworkRanges);
        Assert.NotEmpty(options.DefaultNetworkRanges);
        Assert.Contains("192.168.1.0/24", options.DefaultNetworkRanges);
    }

    [Fact]
    public void GetConfigurationOptions_IncludesRecommendedPorts()
    {
        // Act
        var options = _service.GetConfigurationOptions();

        // Assert
        Assert.NotNull(options.RecommendedPorts);
        Assert.NotEmpty(options.RecommendedPorts);
        Assert.Contains("Moonraker", options.RecommendedPorts.Keys);
        Assert.Contains("PrusaLink", options.RecommendedPorts.Keys);
        Assert.Equal(7125, options.RecommendedPorts["Moonraker"]);
        Assert.Equal(8080, options.RecommendedPorts["PrusaLink"]);
    }

    #endregion
}
