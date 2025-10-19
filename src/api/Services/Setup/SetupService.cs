using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Users;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Services.Authentication;
using Farm.Web.Shared;
using Farm.Web.Shared.Contracts.Setup;

namespace Farm.Web.Api.Services.Setup;

/// <summary>
/// Service for handling initial application setup and configuration.
/// </summary>
public class SetupService : ISetupService
{
    private readonly IUsersRepository _usersRepository;
    private readonly IAuthenticationService _authService;
    private readonly IPasswordHashingService _passwordHashingService;
    private readonly IUnifiedLoggingService _logger;

    public SetupService(
        IUsersRepository usersRepository,
        IAuthenticationService authService,
        IPasswordHashingService passwordHashingService,
        IUnifiedLoggingService logger)
    {
        _usersRepository = usersRepository ?? throw new ArgumentNullException(nameof(usersRepository));
        _authService = authService ?? throw new ArgumentNullException(nameof(authService));
        _passwordHashingService = passwordHashingService ?? throw new ArgumentNullException(nameof(passwordHashingService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<bool> NeedsSetupAsync(CancellationToken ct)
    {
        bool hasAdminUsers = await _usersRepository.HasAdminUsersAsync(ct);
        return !hasAdminUsers;
    }

    public async Task<AuthenticationResult> CreateInitialAdminAsync(CreateInitialAdminRequest request, CancellationToken ct)
    {
        if (request == null)
        {
            return new AuthenticationResult(false, Error: "Request body required");
        }

        // Check if setup is actually needed
        bool hasAdminUsers = await _usersRepository.HasAdminUsersAsync(ct);

        if (hasAdminUsers)
        {
            // Check idempotency for same credentials
            if (!string.IsNullOrWhiteSpace(request.Username) &&
                !string.IsNullOrWhiteSpace(request.Email) &&
                !string.IsNullOrWhiteSpace(request.Password))
            {
                User? existingAdmin = await _usersRepository.GetAdminByUsernameAndEmailAsync(
                    request.Username, request.Email, ct);

                if (existingAdmin != null && _passwordHashingService.VerifyPassword(request.Password, existingAdmin.PasswordHash))
                {
                    string tokenExisting = await _authService.GenerateJwtTokenAsync(existingAdmin);
                    UserDto? userDtoExisting = await _authService.GetUserWithRolesAndPermissionsAsync(existingAdmin.Id);

                    return new AuthenticationResult(
                        Success: true,
                        Token: tokenExisting,
                        ExpiresAt: DateTime.UtcNow.AddDays(7),
                        User: userDtoExisting
                    );
                }
            }

            // Validate request
            if (string.IsNullOrWhiteSpace(request.Username) ||
                string.IsNullOrWhiteSpace(request.Email) ||
                string.IsNullOrWhiteSpace(request.Password))
            {
                return new AuthenticationResult(false, Error: "Username, email, and password are required");
            }

            bool duplicateUser = await _usersRepository.AnyUserByUsernameOrEmailAsync(
                request.Username, request.Email, ct);

            if (duplicateUser)
            {
                return new AuthenticationResult(false, Error: "Username or email is already taken");
            }

            return new AuthenticationResult(false, Error: "Setup has already been completed. Admin users exist in the system.");
        }

        // Validate required fields
        if (string.IsNullOrWhiteSpace(request.Username) ||
            string.IsNullOrWhiteSpace(request.Email) ||
            string.IsNullOrWhiteSpace(request.Password))
        {
            return new AuthenticationResult(false, Error: "Username, email, and password are required");
        }

        // Load password policy
        PasswordPolicyEntity? policy = await _usersRepository.GetPasswordPolicyAsync(ct);
        int minLength = policy?.MinLength ?? 8;

        if (request.Password.Length < minLength)
        {
            return new AuthenticationResult(false, Error: $"Password must be at least {minLength} characters long");
        }

        // Optional complexity checks
        if (policy != null)
        {
            if (policy.RequireUppercase && !request.Password.Any(char.IsUpper))
            {
                return new AuthenticationResult(false, Error: "Password must contain at least one uppercase letter");
            }

            if (policy.RequireLowercase && !request.Password.Any(char.IsLower))
            {
                return new AuthenticationResult(false, Error: "Password must contain at least one lowercase letter");
            }

            if (policy.RequireDigit && !request.Password.Any(char.IsDigit))
            {
                return new AuthenticationResult(false, Error: "Password must contain at least one digit");
            }

            if (policy.RequireSymbol && request.Password.All(c => char.IsLetterOrDigit(c)))
            {
                return new AuthenticationResult(false, Error: "Password must contain at least one symbol");
            }
        }

        // Check if username or email already exists
        bool existingUser = await _usersRepository.AnyUserByUsernameOrEmailAsync(
            request.Username, request.Email, ct);

        if (existingUser)
        {
            return new AuthenticationResult(false, Error: "Username or email is already taken");
        }

        // Get admin role
        Role? adminRole = await _usersRepository.GetRoleByNameAsync("farm_admin", ct);
        if (adminRole == null)
        {
            return new AuthenticationResult(false, Error: "Admin role not found in database. Database may not be properly initialized.");
        }

        // Create the admin user
        User adminUser = new()
        {
            Id = Guid.NewGuid(),
            Username = request.Username,
            Email = request.Email,
            FirstName = request.FirstName,
            LastName = request.LastName,
            PasswordHash = _passwordHashingService.HashPassword(request.Password),
            IsActive = true,
            EmailConfirmed = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        // Add user with admin role (repository handles SaveChanges)
        await _usersRepository.AddUserWithRoleAsync(adminUser, adminRole.Id, ct);

        _logger.LogInformation($"Initial admin user created: {adminUser.Username} ({adminUser.Email})");

        // Generate JWT token for immediate login
        string token = await _authService.GenerateJwtTokenAsync(adminUser);
        UserDto? userDto = await _authService.GetUserWithRolesAndPermissionsAsync(adminUser.Id);

        return new AuthenticationResult(
            Success: true,
            Token: token,
            ExpiresAt: DateTime.UtcNow.AddDays(7),
            User: userDto
        );
    }

    public SetupConfigurationOptions GetConfigurationOptions()
    {
        return new SetupConfigurationOptions(
            DatabaseProviders: new[] { "SQLite", "SQL Server", "PostgreSQL", "MySQL" },
            DefaultNetworkRanges: new[] { "192.168.1.0/24", "192.168.0.0/24", "10.0.0.0/24" },
            RecommendedPorts: new Dictionary<string, int>
            {
                ["Moonraker"] = 7125,
                ["PrusaLink"] = 8080,
                ["SDCP"] = 3000
            }
        );
    }
}
