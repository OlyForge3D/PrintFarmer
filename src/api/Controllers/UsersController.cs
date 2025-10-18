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
    IUnifiedLoggingService logger) : ControllerBase
{
    private readonly Farm.Web.Api.Services.Users.IUsersService _users = usersService;
    private readonly IAuthenticationService _authService = authService;
    private readonly IUnifiedLoggingService _logger = logger;

    /// <summary>
    /// Gets all users in the system.
    /// </summary>
    /// <param name="ct">Cancellation token</param>
    /// <returns>List of all users with their roles and basic information</returns>
    [HttpGet]
    public async Task<ActionResult<IEnumerable<UserDto>>> GetUsersAsync(CancellationToken ct)
    {
        var users = await _users.GetUsersAsync(ct);
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
            var createdUser = await _users.CreateUserAsync(request, ct);
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

        var updated = await _users.UpdateUserAsync(id, request, ct);
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

        var deleted = await _users.DeleteUserAsync(id, ct);
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
        var roles = await _users.GetRolesAsync(ct);
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
        var availability = await _users.CheckAvailabilityAsync(username, email, ct);
        return Ok(availability);
    }
}
