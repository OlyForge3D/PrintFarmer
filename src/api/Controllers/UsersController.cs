using System.Security.Claims;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Telemetry;
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
[Route("api/users")]
[Authorize(Roles = "farm_admin")]
public class UsersController(
    Farm.Web.Api.Services.Users.IUsersService usersService,
    IAuthenticationService authService,
    ITokenRevocationService tokenRevocationService,
    AppDbContext dbContext,
    IUnifiedLoggingService logger) : ControllerBase
{
    private readonly Farm.Web.Api.Services.Users.IUsersService _users = usersService;
    private readonly IAuthenticationService _authService = authService;
    private readonly ITokenRevocationService _tokenRevocation = tokenRevocationService;
    private readonly AppDbContext _db = dbContext;
    private readonly IUnifiedLoggingService _logger = logger;

    /// <summary>
    /// Gets all users in the system.
    /// </summary>
    /// <param name="ct">Cancellation token</param>
    /// <returns>List of all users with their roles and basic information</returns>
    [HttpGet]
    public async Task<ActionResult<IEnumerable<UserDto>>> GetUsersAsync(CancellationToken ct)
    {
        IReadOnlyList<UserDto> users = await _users.GetUsersAsync(ct);
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
        UserDto? user = await _authService.GetUserWithRolesAndPermissionsAsync(id);
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
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Username) ||
            string.IsNullOrWhiteSpace(request.Email) ||
            string.IsNullOrWhiteSpace(request.Password))
        {
            return BadRequest("Username, email, and password are required");
        }

        // Check if username or email already exists
        try
        {
            UserDto createdUser = await _users.CreateUserAsync(request, ct);
            string? currentUserId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            _logger.LogInformation($"User {currentUserId} created new user {createdUser.Id} ({createdUser.Username})");
            return CreatedAtRoute("GetUserById", new { id = createdUser.Id }, createdUser);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
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
        ArgumentNullException.ThrowIfNull(request);

        UserDto? updated = await _users.UpdateUserAsync(id, request, ct);
        if (updated is null)
        {
            return NotFound();
        }
        string? currentUserId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        _logger.LogInformation($"User {currentUserId} updated user {id}");
        return Ok(updated);
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
        string? currentUserId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (currentUserId != null && Guid.Parse(currentUserId) == id)
        {
            return BadRequest("Cannot delete your own account");
        }

        bool deleted = await _users.DeleteUserAsync(id, ct);
        if (!deleted)
        {
            return NotFound();
        }
        _logger.LogInformation($"User {currentUserId} deleted user {id}");
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
        IReadOnlyList<RoleDto> roles = await _users.GetRolesAsync(ct);
        return Ok(roles);
    }

    /// <summary>
    /// Lightweight availability check for username and/or email prior to user creation.
    /// Any parameter omitted will not be checked (returns null for that field).
    /// </summary>
    /// <param name="username">Prospective username</param>
    /// <param name="email">Prospective email</param>
    /// <param name="ct">Cancellation token</param>
    [HttpGet("availability")]
    [AllowAnonymous] // Allows pre-registration checks (still low-risk data)
    [ProducesResponseType(typeof(UserAvailabilityDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<UserAvailabilityDto>> CheckAvailabilityAsync(
        [FromQuery] string? username,
        [FromQuery] string? email,
        CancellationToken ct)
    {
        UserAvailabilityDto availability = await _users.CheckAvailabilityAsync(username, email, ct);
        return Ok(availability);
    }

    /// <summary>
    /// Revokes all active sessions for a user, forcing logout from all devices.
    /// </summary>
    /// <param name="userId">The ID of the user whose sessions should be revoked</param>
    /// <param name="request">The revocation request containing the reason</param>
    /// <param name="ct">Cancellation token</param>
    [HttpPost("{userId:guid}/revoke-sessions")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RevokeSessionsResult>> RevokeAllSessionsAsync(
        Guid userId,
        [FromBody] RevokeSessionsRequest request,
        CancellationToken ct)
    {
        // Get admin user ID from claims
        string? adminUserIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(adminUserIdClaim) || !Guid.TryParse(adminUserIdClaim, out Guid adminUserId))
        {
            _logger.LogWarning("Admin user ID not found in claims");
            return BadRequest(new { error = "Unable to identify admin user" });
        }

        // Prevent admin from revoking their own sessions
        if (userId == adminUserId)
        {
            _logger.LogWarning($"Admin {adminUserId} attempted to revoke their own sessions");
            return BadRequest(new { error = "Admins cannot revoke their own sessions" });
        }

        // Verify user exists
        bool userExists = await _db.Users.AnyAsync(u => u.Id == userId, ct);
        if (!userExists)
        {
            return NotFound(new { error = $"User {userId} not found" });
        }

        // Revoke all tokens
        string ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        int revokedCount = await _tokenRevocation.RevokeAllUserTokensAsync(
            userId,
            adminUserId,
            request.Reason ?? "Revoked by administrator",
            ipAddress);

        _logger.LogInformation($"Admin {adminUserId} revoked {revokedCount} sessions for user {userId}");

        return Ok(new RevokeSessionsResult
        {
            UserId = userId,
            RevokedCount = revokedCount,
            RevokedAt = DateTime.UtcNow
        });
    }

    /// <summary>
    /// Gets the revocation history for a user.
    /// </summary>
    /// <param name="userId">The ID of the user</param>
    /// <param name="ct">Cancellation token</param>
    [HttpGet("{userId:guid}/revoked-tokens")]
    [ProducesResponseType(typeof(IEnumerable<RevokedTokenDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IEnumerable<RevokedTokenDto>>> GetRevokedTokensAsync(
        Guid userId,
        CancellationToken ct)
    {
        // Verify user exists
        bool userExists = await _db.Users.AnyAsync(u => u.Id == userId, ct);
        if (!userExists)
        {
            return NotFound(new { error = $"User {userId} not found" });
        }

        List<RevokedToken> revokedTokens = await _tokenRevocation.GetUserRevokedTokensAsync(userId);

        IEnumerable<RevokedTokenDto> dtos = revokedTokens.Select(rt => new RevokedTokenDto
        {
            Id = rt.Id,
            RevokedAt = rt.RevokedAt,
            Reason = rt.Reason,
            ExpiresAt = rt.ExpiresAt,
            IpAddress = rt.IpAddress,
            RevokedByUserId = rt.RevokedByUserId
        });

        return Ok(dtos);
    }
}

/// <summary>
/// Request model for revoking user sessions.
/// </summary>
public record RevokeSessionsRequest
{
    /// <summary>
    /// The reason for revoking sessions.
    /// </summary>
    public string? Reason { get; init; }
}

/// <summary>
/// Result of revoking user sessions.
/// </summary>
public record RevokeSessionsResult
{
    /// <summary>
    /// The ID of the user whose sessions were revoked.
    /// </summary>
    public Guid UserId { get; init; }

    /// <summary>
    /// The number of sessions revoked.
    /// </summary>
    public int RevokedCount { get; init; }

    /// <summary>
    /// When the revocation occurred.
    /// </summary>
    public DateTime RevokedAt { get; init; }
}

/// <summary>
/// DTO for revoked token information.
/// </summary>
public record RevokedTokenDto
{
    /// <summary>
    /// The unique identifier for the revocation record.
    /// </summary>
    public Guid Id { get; init; }

    /// <summary>
    /// When the token was revoked.
    /// </summary>
    public DateTime RevokedAt { get; init; }

    /// <summary>
    /// The reason for revocation.
    /// </summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>
    /// When the token expires (original expiration).
    /// </summary>
    public DateTime ExpiresAt { get; init; }

    /// <summary>
    /// The IP address from which revocation was initiated.
    /// </summary>
    public string? IpAddress { get; init; }

    /// <summary>
    /// The ID of the admin who revoked the token.
    /// </summary>
    public Guid? RevokedByUserId { get; init; }
}
