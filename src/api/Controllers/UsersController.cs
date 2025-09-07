using System.Security.Claims;
using Farm.Web.Api.Data;
using Farm.Web.Api.Domain;
using Farm.Web.Api.Services.Authentication;
using Farm.Web.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Provides endpoints for user management operations.
/// Only accessible by administrators.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = "farm_admin")]
public class UsersController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IAuthenticationService _authService;
    private readonly IPasswordHashingService _passwordHashingService;
    private readonly ILogger<UsersController> _logger;

    public UsersController(
        AppDbContext db, 
        IAuthenticationService authService,
        IPasswordHashingService passwordHashingService,
        ILogger<UsersController> logger)
    {
        _db = db;
        _authService = authService;
        _passwordHashingService = passwordHashingService;
        _logger = logger;
    }

    /// <summary>
    /// Gets all users in the system.
    /// </summary>
    /// <param name="ct">Cancellation token</param>
    /// <returns>List of all users with their roles and basic information</returns>
    [HttpGet]
    public async Task<ActionResult<IEnumerable<UserDto>>> GetUsersAsync(CancellationToken ct)
    {
        var users = await _db.Users
            .Include(u => u.UserRoles)
            .ThenInclude(ur => ur.Role)
            .AsNoTracking()
            .Select(u => new UserDto(
                u.Id,
                u.Username,
                u.Email,
                u.FirstName,
                u.LastName,
                u.IsActive,
                u.EmailConfirmed,
                u.LastLogin,
                u.CreatedAt,
                u.UserRoles.Where(ur => ur.IsActive).Select(ur => ur.Role.Name).ToArray(),
                new string[0] // Permissions would be calculated from roles
            ))
            .ToListAsync(ct);

        return Ok(users);
    }

    /// <summary>
    /// Gets a specific user by ID.
    /// </summary>
    /// <param name="id">User ID</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>User details with roles and permissions</returns>
    [HttpGet("{id:guid}", Name = "GetUserById")]
    public async Task<ActionResult<UserDto>> GetUserAsync(Guid id, CancellationToken ct)
    {
        var user = await _authService.GetUserWithRolesAndPermissionsAsync(id);
        if (user == null)
        {
            return NotFound();
        }

        return Ok(user);
    }

    /// <summary>
    /// Creates a new user.
    /// </summary>
    /// <param name="request">User creation request</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Created user details</returns>
    [HttpPost]
    public async Task<ActionResult<UserDto>> CreateUserAsync([FromBody] CreateUserRequest request, CancellationToken ct)
    {
        if (request == null) return BadRequest("Request body required");
        if (string.IsNullOrWhiteSpace(request.Username) || 
            string.IsNullOrWhiteSpace(request.Email) || 
            string.IsNullOrWhiteSpace(request.Password))
        {
            return BadRequest("Username, email, and password are required");
        }

        // Check if username or email already exists
        var existingUser = await _db.Users
            .AnyAsync(u => u.Username == request.Username || u.Email == request.Email, ct);

        if (existingUser)
        {
            return BadRequest("Username or email is already taken");
        }

        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = request.Username,
            Email = request.Email,
            FirstName = request.FirstName,
            LastName = request.LastName,
            PasswordHash = _passwordHashingService.HashPassword(request.Password),
            IsActive = true,
            EmailConfirmed = false,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _db.Users.Add(user);

        // Assign roles if provided
    if (request.RoleIds is { Length: > 0 })
        {
            foreach (var roleId in request.RoleIds)
            {
                var role = await _db.Roles.FindAsync(roleId);
                if (role != null)
                {
                    _db.UserRoles.Add(new UserRole
                    {
                        Id = Guid.NewGuid(),
                        UserId = user.Id,
                        RoleId = roleId,
                        AssignedAt = DateTime.UtcNow,
                        IsActive = true
                    });
                }
            }
        }

        await _db.SaveChangesAsync(ct);

        var currentUserId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        _logger.LogInformation("User {UserId} created new user {NewUserId} ({Username})", 
            currentUserId, user.Id, user.Username);

        // Return the created user with roles and permissions
    var createdUser = await _authService.GetUserWithRolesAndPermissionsAsync(user.Id);
    return CreatedAtRoute("GetUserById", new { id = user.Id }, createdUser);
    }

    /// <summary>
    /// Updates an existing user.
    /// </summary>
    /// <param name="id">User ID</param>
    /// <param name="request">User update request</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Updated user details</returns>
    [HttpPut("{id:guid}")]
    public async Task<ActionResult<UserDto>> UpdateUserAsync(Guid id, [FromBody] UpdateUserRequest request, CancellationToken ct)
    {
        if (request == null) return BadRequest("Request body required");
        var user = await _db.Users.FindAsync(id);
        if (user == null)
        {
            return NotFound();
        }

        // Update basic fields
        if (!string.IsNullOrWhiteSpace(request.FirstName))
        {
            user.FirstName = request.FirstName;
        }
        
        if (!string.IsNullOrWhiteSpace(request.LastName))
        {
            user.LastName = request.LastName;
        }

        if (request.IsActive.HasValue)
        {
            user.IsActive = request.IsActive.Value;
        }
        user.UpdatedAt = DateTime.UtcNow;

        // Update roles if provided
        if (request.RoleIds != null)
        {
            // Remove existing roles
            var existingRoles = await _db.UserRoles.Where(ur => ur.UserId == id).ToListAsync(ct);
            _db.UserRoles.RemoveRange(existingRoles);

            // Add new roles
            foreach (var roleId in request.RoleIds)
            {
                var role = await _db.Roles.FindAsync(roleId);
                if (role != null)
                {
                    _db.UserRoles.Add(new UserRole
                    {
                        Id = Guid.NewGuid(),
                        UserId = id,
                        RoleId = roleId,
                        AssignedAt = DateTime.UtcNow,
                        IsActive = true
                    });
                }
            }
        }

        await _db.SaveChangesAsync(ct);

        var currentUserId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        _logger.LogInformation("User {UserId} updated user {UpdatedUserId} ({Username})", 
            currentUserId, user.Id, user.Username);

        // Return the updated user with roles and permissions
        var updatedUser = await _authService.GetUserWithRolesAndPermissionsAsync(user.Id);
        return Ok(updatedUser);
    }

    /// <summary>
    /// Deletes a user.
    /// </summary>
    /// <param name="id">User ID</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Success response</returns>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> DeleteUserAsync(Guid id, CancellationToken ct)
    {
        var currentUserId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (currentUserId != null && Guid.Parse(currentUserId) == id)
        {
            return BadRequest("Cannot delete your own account");
        }

        var user = await _db.Users.FindAsync(id);
        if (user == null)
        {
            return NotFound();
        }

        // Remove user roles first
        var userRoles = await _db.UserRoles.Where(ur => ur.UserId == id).ToListAsync(ct);
        _db.UserRoles.RemoveRange(userRoles);

        // Remove the user
        _db.Users.Remove(user);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("User {UserId} deleted user {DeletedUserId} ({Username})", 
            currentUserId, user.Id, user.Username);

        return NoContent();
    }

    /// <summary>
    /// Gets all available roles in the system.
    /// </summary>
    /// <param name="ct">Cancellation token</param>
    /// <returns>List of all roles</returns>
    [HttpGet("roles")]
    public async Task<ActionResult<IEnumerable<RoleDto>>> GetRolesAsync(CancellationToken ct)
    {
        var roles = await _db.Roles
            .Include(r => r.RolePermissions)
            .ThenInclude(rp => rp.Resource)
            .Include(r => r.RolePermissions)
            .ThenInclude(rp => rp.Action)
            .AsNoTracking()
            .Select(r => new RoleDto(
                r.Id,
                r.Name,
                r.DisplayName,
                r.Description,
                r.IsSystemRole,
                r.IsActive,
                r.CreatedAt,
                r.RolePermissions.Select(rp => new RolePermissionDto(
                    rp.Id,
                    rp.RoleId,
                    rp.ResourceId,
                    rp.ActionId,
                    rp.Resource.Name,
                    rp.Action.Name,
                    rp.Granted
                )).ToArray()
            ))
            .ToListAsync(ct);

        return Ok(roles);
    }
}