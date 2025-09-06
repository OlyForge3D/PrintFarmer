using System.Security.Claims;
using Farm.Web.Api.Data;
using Farm.Web.Api.Domain;
using Farm.Web.Api.Services.Authentication;
using Farm.Web.Shared;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Provides endpoints for initial application setup and configuration.
/// Used during first-run to create initial admin user and configure the system.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class SetupController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IAuthenticationService _authService;
    private readonly IPasswordHashingService _passwordHashingService;
    private readonly ILogger<SetupController> _logger;

    public SetupController(
        AppDbContext db,
        IAuthenticationService authService,
        IPasswordHashingService passwordHashingService,
        ILogger<SetupController> logger)
    {
        _db = db;
        _authService = authService;
        _passwordHashingService = passwordHashingService;
        _logger = logger;
    }

    /// <summary>
    /// Checks if the application needs initial setup.
    /// Returns true if no admin users exist in the system.
    /// </summary>
    [HttpGet("status")]
    public async Task<ActionResult<object>> GetSetupStatusAsync(CancellationToken ct)
    {
        // Check if there are any admin users
        var hasAdminUsers = await _db.Users
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
        // Validate that setup is actually needed
        var hasAdminUsers = await _db.Users
            .AnyAsync(u => u.UserRoles.Any(ur => ur.Role.Name == "farm_admin" && ur.IsActive), ct);

        if (hasAdminUsers)
        {
            return BadRequest("Setup has already been completed. Admin users exist in the system.");
        }

        // Validate required fields
        if (string.IsNullOrWhiteSpace(request.Username) ||
            string.IsNullOrWhiteSpace(request.Email) ||
            string.IsNullOrWhiteSpace(request.Password))
        {
            return BadRequest("Username, email, and password are required");
        }

        // Validate password strength
        if (request.Password.Length < 8)
        {
            return BadRequest("Password must be at least 8 characters long for admin accounts");
        }

        // Check if username or email already exists
        var existingUser = await _db.Users
            .AnyAsync(u => u.Username == request.Username || u.Email == request.Email, ct);

        if (existingUser)
        {
            return BadRequest("Username or email already exists");
        }

        // Get admin role
        var adminRole = await _db.Roles.FirstOrDefaultAsync(r => r.Name == "farm_admin", ct);
        if (adminRole == null)
        {
            return StatusCode(500, "Admin role not found in database. Database may not be properly initialized.");
        }

        // Create the admin user
        var adminUser = new User
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

        _logger.LogInformation("Initial admin user created: {Username} ({Email})", 
            adminUser.Username, adminUser.Email);

        // Generate JWT token for immediate login
        var token = await _authService.GenerateJwtTokenAsync(adminUser);
        var userDto = await _authService.GetUserWithRolesAndPermissionsAsync(adminUser.Id);

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
        return Ok(new
        {
            DatabaseProviders = new[] { "SQLite", "SQL Server", "PostgreSQL", "MySQL" },
            DefaultNetworkRanges = new[] { "192.168.1.0/24", "192.168.0.0/24", "10.0.0.0/24" },
            RecommendedPorts = new { Moonraker = 7125, PrusaLink = 8080, SDCP = 3000 }
        });
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