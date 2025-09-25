using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Web.Api.Services.Authentication;
using Farm.Web.Shared;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Farm.Infrastructure;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Provides endpoints for initial application setup and configuration.
/// Used during first-run to create initial admin user and configure the system.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class SetupController(
    AppDbContext db,
    IAuthenticationService authService,
    IPasswordHashingService passwordHashingService,
    IUnifiedLoggingService logger) : ControllerBase
{
    private readonly AppDbContext _db = db;
    private readonly IAuthenticationService _authService = authService;
    private readonly IPasswordHashingService _passwordHashingService = passwordHashingService;
    private readonly IUnifiedLoggingService _logger = logger;

    /// <summary>
    /// Checks if the application needs initial setup.
    /// Returns true if no admin users exist in the system.
    /// </summary>
    [HttpGet("status")]
    public async Task<ActionResult<object>> GetSetupStatusAsync(CancellationToken ct)
    {
        // Check if there are any admin users
        bool hasAdminUsers = await _db.Users
            .AnyAsync(u => u.UserRoles.Any(ur => ur.Role.Name == "farm_admin" && ur.IsActive), ct);

        return Ok(new { needsSetup = !hasAdminUsers });
    }

    /// <summary>
    /// Creates the initial admin user and completes first-run setup.
    /// This endpoint is only available when no admin users exist.
    /// </summary>
    [HttpPost("initial-admin")]
    public async Task<ActionResult<AuthenticationResult>> CreateInitialAdminAsync(
        [FromBody] CreateInitialAdminRequest request,
        CancellationToken ct)
    {
        if (request == null)
        {
            return BadRequest(new AuthenticationResult(false, Error: "Request body required"));
        }
        // Validate that setup is actually needed
        bool hasAdminUsers = await _db.Users
            .AnyAsync(u => u.UserRoles.Any(ur => ur.Role.Name == "farm_admin" && ur.IsActive), ct);

        if (hasAdminUsers)
        {
            // If an admin already exists, check idempotency first for same credentials
            if (!string.IsNullOrWhiteSpace(request.Username) &&
                !string.IsNullOrWhiteSpace(request.Email) &&
                !string.IsNullOrWhiteSpace(request.Password))
            {
                User? existingAdmin = await _db.Users
                    .Include(u => u.UserRoles).ThenInclude(ur => ur.Role)
                    .FirstOrDefaultAsync(u =>
                        u.Username == request.Username && u.Email == request.Email &&
                        u.UserRoles.Any(ur => ur.Role.Name == "farm_admin" && ur.IsActive), ct);

                if (existingAdmin != null && _passwordHashingService.VerifyPassword(request.Password, existingAdmin.PasswordHash))
                {
                    string tokenExisting = await _authService.GenerateJwtTokenAsync(existingAdmin);
                    UserDto? userDtoExisting = await _authService.GetUserWithRolesAndPermissionsAsync(existingAdmin.Id);

                    return Ok(new AuthenticationResult(
                        Success: true,
                        Token: tokenExisting,
                        ExpiresAt: DateTime.UtcNow.AddDays(7),
                        User: userDtoExisting
                    ));
                }
            }

            // Then validate request and provide precise duplicate feedback
            if (string.IsNullOrWhiteSpace(request.Username) ||
                string.IsNullOrWhiteSpace(request.Email) ||
                string.IsNullOrWhiteSpace(request.Password))
            {
                return BadRequest(new AuthenticationResult(false, Error: "Username, email, and password are required"));
            }

            User? duplicateUser = await _db.Users
                .AsNoTracking()
                .FirstOrDefaultAsync(u => u.Username == request.Username || u.Email == request.Email, ct);

            if (duplicateUser != null)
            {
                return BadRequest(new AuthenticationResult(false, Error: "Username or email is already taken"));
            }

            return BadRequest(new AuthenticationResult(false, Error: "Setup has already been completed. Admin users exist in the system."));
        }

        // Validate required fields
        if (string.IsNullOrWhiteSpace(request.Username) ||
            string.IsNullOrWhiteSpace(request.Email) ||
            string.IsNullOrWhiteSpace(request.Password))
        {
            return BadRequest(new AuthenticationResult(false, Error: "Username, email, and password are required"));
        }

        // Load password policy (default values if none present)
        PasswordPolicy? policy = await _db.PasswordPolicies.OrderBy(p => p.Id).FirstOrDefaultAsync(ct);
        int minLength = policy?.MinLength ?? 8;
        if (request.Password.Length < minLength)
        {
            // Return field-level problem style error for better client UX
            return BadRequest(new AuthenticationResult(false, Error: $"Password must be at least {minLength} characters long"));
        }
        // Optional complexity checks
        if (policy != null)
        {
            if (policy.RequireUppercase && !request.Password.Any(char.IsUpper))
            {
                return BadRequest(new AuthenticationResult(false, Error: "Password must contain at least one uppercase letter"));
            }
            if (policy.RequireLowercase && !request.Password.Any(char.IsLower))
            {
                return BadRequest(new AuthenticationResult(false, Error: "Password must contain at least one lowercase letter"));
            }
            if (policy.RequireDigit && !request.Password.Any(char.IsDigit))
            {
                return BadRequest(new AuthenticationResult(false, Error: "Password must contain at least one digit"));
            }
            if (policy.RequireSymbol && request.Password.All(c => char.IsLetterOrDigit(c)))
            {
                return BadRequest(new AuthenticationResult(false, Error: "Password must contain at least one symbol"));
            }
        }

        // Check if username or email already exists
        bool existingUser = await _db.Users
            .AnyAsync(u => u.Username == request.Username || u.Email == request.Email, ct);

        if (existingUser)
        {
            return BadRequest(new AuthenticationResult(false, Error: "Username or email is already taken"));
        }

        // Get admin role
        Role? adminRole = await _db.Roles.FirstOrDefaultAsync(r => r.Name == "farm_admin", ct);
        if (adminRole == null)
        {
            return StatusCode(500, new AuthenticationResult(false, Error: "Admin role not found in database. Database may not be properly initialized."));
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
            EmailConfirmed = true, // Auto-confirm email for initial admin
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _db.Users.Add(adminUser);

        // Assign admin role
        _db.UserRoles.Add(new UserRole
        {
            Id = Guid.NewGuid(),
            UserId = adminUser.Id,
            RoleId = adminRole.Id,
            AssignedAt = DateTime.UtcNow,
            IsActive = true
        });

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation($"Initial admin user created: {adminUser.Username} ({adminUser.Email})");

        // Generate JWT token for immediate login
        string token = await _authService.GenerateJwtTokenAsync(adminUser);
        UserDto? userDto = await _authService.GetUserWithRolesAndPermissionsAsync(adminUser.Id);

        return Ok(new AuthenticationResult(
            Success: true,
            Token: token,
            ExpiresAt: DateTime.UtcNow.AddDays(7),
            User: userDto
        ));
    }

    /// <summary>
    /// Gets available configuration options for setup.
    /// </summary>
    [HttpGet("config-options")]
    public ActionResult<object> GetConfigurationOptions()
    {
        // Use dictionaries to preserve exact key casing as expected by tests
        Dictionary<string, object> result = new()
        {
            ["DatabaseProviders"] = new[] { "SQLite", "SQL Server", "PostgreSQL", "MySQL" },
            ["DefaultNetworkRanges"] = new[] { "192.168.1.0/24", "192.168.0.0/24", "10.0.0.0/24" },
            ["RecommendedPorts"] = new Dictionary<string, int>
            {
                ["Moonraker"] = 7125,
                ["PrusaLink"] = 8080,
                ["SDCP"] = 3000
            }
        };

        return Ok(result);
    }
}

public class CreateInitialAdminRequest
{
    public string Username { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
}
