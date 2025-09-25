using Farm.Infrastructure;
using System.Security.Claims;
using Farm.Web.Api.Services.Authentication;
using Farm.Web.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Tags("Authentication")]
public class AuthController(IAuthenticationService authService, IUnifiedLoggingService logger) : ControllerBase
{
    private readonly IAuthenticationService _authService = authService;
    private readonly IUnifiedLoggingService _logger = logger;

    [HttpPost("login")]
    public async Task<ActionResult<AuthenticationResult>> LoginAsync([FromBody] LoginRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
        {
            return BadRequest(new AuthenticationResult(false, Error: "Username and password are required"));
        }

        AuthenticationResult result = await _authService.AuthenticateAsync(request.Username, request.Password);

        if (result.Success)
        {
            return Ok(result);
        }

        return Unauthorized(result);
    }

    [HttpPost("register")]
    public async Task<ActionResult<AuthenticationResult>> RegisterAsync([FromBody] RegisterRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Username) ||
            string.IsNullOrWhiteSpace(request.Email) ||
            string.IsNullOrWhiteSpace(request.Password))
        {
            return BadRequest(new AuthenticationResult(false, Error: "Username, email, and password are required"));
        }

        // Basic password validation
        if (request.Password.Length < 6)
        {
            return BadRequest(new AuthenticationResult(false, Error: "Password must be at least 6 characters long"));
        }

        AuthenticationResult result = await _authService.RegisterAsync(request);

        if (result.Success)
        {
            return Ok(result);
        }

        return BadRequest(result);
    }

    [HttpPost("logout")]
    [Authorize]
    public Task<IActionResult> LogoutAsync()
    {
        // For JWT tokens, logout is typically handled client-side by removing the token
        // In the future, we could implement a token blacklist for enhanced security
        _logger.LogInformation($"User {User.FindFirstValue(ClaimTypes.NameIdentifier)} logged out");

        return Task.FromResult<IActionResult>(Ok(new { message = "Logged out successfully" }));
    }

    // Provide GET variant used by some tests to check unauthorized behavior
    [HttpGet("logout")]
    [Authorize]
    public Task<IActionResult> LogoutGetAsync()
    {
        _logger.LogInformation($"User {User.FindFirstValue(System.Security.Claims.ClaimTypes.NameIdentifier)} logged out (GET)");
        return Task.FromResult<IActionResult>(Ok(new { message = "Logged out successfully" }));
    }

    [HttpGet("me")]
    [Authorize]
    public async Task<ActionResult<UserDto>> GetCurrentUserAsync()
    {
        string? userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userIdClaim) || !Guid.TryParse(userIdClaim, out Guid userId))
        {
            return Unauthorized();
        }

        UserDto? user = await _authService.GetUserWithRolesAndPermissionsAsync(userId);
        if (user == null)
        {
            return NotFound();
        }

        return Ok(user);
    }

    [HttpPost("change-password")]
    [Authorize]
    public async Task<IActionResult> ChangePasswordAsync([FromBody] ChangePasswordRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        string? userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userIdClaim) || !Guid.TryParse(userIdClaim, out Guid userId))
        {
            return Unauthorized();
        }

        if (string.IsNullOrWhiteSpace(request.CurrentPassword) || string.IsNullOrWhiteSpace(request.NewPassword))
        {
            return BadRequest(new { error = "Current password and new password are required" });
        }

        if (request.NewPassword.Length < 6)
        {
            return BadRequest(new { error = "New password must be at least 6 characters long" });
        }

        bool success = await _authService.ChangePasswordAsync(userId, request.CurrentPassword, request.NewPassword);
        if (!success)
        {
            return BadRequest(new { error = "Current password is incorrect" });
        }

        return Ok(new { message = "Password changed successfully" });
    }

    // TODO: Implement these endpoints when email service is available
    /*
    [HttpPost("forgot-password")]
    public async Task<IActionResult> ForgotPassword([FromBody] ForgotPasswordRequest request)
    {
        // Implementation for password reset email
        return Ok(new { message = "Password reset email sent if account exists" });
    }

    [HttpPost("reset-password")]
    public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordRequest request)
    {
        // Implementation for password reset
        return Ok(new { message = "Password reset successfully" });
    }

    [HttpPost("confirm-email")]
    public async Task<IActionResult> ConfirmEmail([FromBody] ConfirmEmailRequest request)
    {
        // Implementation for email confirmation
        return Ok(new { message = "Email confirmed successfully" });
    }
    */
}
