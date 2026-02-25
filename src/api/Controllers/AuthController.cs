using System.Security.Claims;
using Farm.Infrastructure;
using Farm.Infrastructure.Contracts.Auth;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ChangePasswordRequest = Farm.Infrastructure.Contracts.Auth.ChangePasswordRequest;
using ConfirmEmailRequest = Farm.Infrastructure.ConfirmEmailRequest;
using ForgotPasswordRequest = Farm.Infrastructure.Contracts.Auth.ForgotPasswordRequest;
using LoginRequest = Farm.Infrastructure.Contracts.Auth.LoginRequest;
using RegisterRequest = Farm.Infrastructure.Contracts.Auth.RegisterRequest;
using ResetPasswordRequest = Farm.Infrastructure.Contracts.Auth.ResetPasswordRequest;
using UserDto = Farm.Infrastructure.Contracts.Auth.UserDto;
using Microsoft.Extensions.Logging;

namespace Farm.Web.Api.Controllers;

[ApiController]
[Route("api/auth")]
[Tags("Authentication")]
public class AuthController(IAuthenticationService authService, ILogger<AuthController> logger) : ControllerBase
{
    private readonly IAuthenticationService _authService = authService;
    private readonly ILogger<AuthController> _logger = logger;

    [HttpPost("login")]
    public async Task<ActionResult<AuthenticationResult>> LoginAsync([FromBody] LoginRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.UsernameOrEmail) || string.IsNullOrWhiteSpace(request.Password))
        {
            return BadRequest(new AuthenticationResult(false, Error: "Username/Email and password are required"));
        }

        AuthenticationResult result = await _authService.AuthenticateAsync(request.UsernameOrEmail, request.Password);

        return result.Success ? Ok(result) : Unauthorized(result);
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

        // Map the new DTO to the old service DTO
        RegisterRequest serviceRequest = new RegisterRequest
        {
            Username = request.Username,
            Email = request.Email,
            Password = request.Password,
            ConfirmPassword = request.Password,  // Assume they match for self-registration
            FirstName = request.FirstName,
            LastName = request.LastName
        };

        AuthenticationResult result = await _authService.RegisterAsync(serviceRequest);

        // If registration succeeded but user is not active, inform user that admin approval is required
        if (result.Success && result.User is { IsActive: false })
        {
            // Never return a JWT for unapproved users
            return Ok(new AuthenticationResult(
                Success: true,
                Token: null,
                ExpiresAt: null,
                User: result.User,
                Error: "Registration successful. Your account requires admin approval before you can log in."));
        }

        return result.Success ? Ok(result) : BadRequest(result);
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

        UserDto? serviceUser = await _authService.GetUserWithRolesAndPermissionsAsync(userId);
        if (serviceUser == null)
        {
            return NotFound();
        }

        // Map from service UserDto to contracts UserDto
        UserDto user = new UserDto
        {
            Id = serviceUser.Id,
            Username = serviceUser.Username,
            Email = serviceUser.Email,
            FirstName = serviceUser.FirstName,
            LastName = serviceUser.LastName,
            IsActive = serviceUser.IsActive,
            EmailConfirmed = serviceUser.EmailConfirmed,
            LastLogin = serviceUser.LastLogin,
            CreatedAt = serviceUser.CreatedAt,
            Roles = serviceUser.Roles?.ToList() ?? new List<string>(),
            Permissions = serviceUser.Permissions?.ToList() ?? new List<string>()
        };

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

    [HttpPost("forgot-password")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(ForgotPasswordResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<ForgotPasswordResponse>> ForgotPasswordAsync([FromBody] ForgotPasswordRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Email))
        {
            return BadRequest(new ForgotPasswordResponse
            {
                Success = false,
                Message = "Email is required"
            });
        }

        try
        {
            _ = await _authService.InitiatePasswordResetAsync(request.Email, HttpContext.Connection.RemoteIpAddress?.ToString());

            // Always return success message to prevent email enumeration
            return Ok(new ForgotPasswordResponse
            {
                Success = true,
                Message = "If an account with that email exists, a password reset link has been sent"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing forgot password request");

            // Return generic success message even on error to prevent email enumeration
            return Ok(new ForgotPasswordResponse
            {
                Success = true,
                Message = "If an account with that email exists, a password reset link has been sent"
            });
        }
    }

    [HttpPost("reset-password")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(ResetPasswordResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ResetPasswordResponse), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ResetPasswordResponse>> ResetPasswordAsync([FromBody] ResetPasswordRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Token) ||
            string.IsNullOrWhiteSpace(request.Email) ||
            string.IsNullOrWhiteSpace(request.NewPassword))
        {
            return BadRequest(new ResetPasswordResponse
            {
                Success = false,
                Message = "Token, email, and new password are required"
            });
        }

        if (request.NewPassword.Length < 6)
        {
            return BadRequest(new ResetPasswordResponse
            {
                Success = false,
                Message = "Password must be at least 6 characters long"
            });
        }

        try
        {
            bool success = await _authService.ResetPasswordAsync(
                request.Token,
                request.Email,
                request.NewPassword,
                HttpContext.Connection.RemoteIpAddress?.ToString());

            return success
                ? Ok(new ResetPasswordResponse
                {
                    Success = true,
                    Message = "Password has been reset successfully"
                })
                : BadRequest(new ResetPasswordResponse
                {
                    Success = false,
                    Message = "Invalid or expired password reset token"
                });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error resetting password");
            return BadRequest(new ResetPasswordResponse
            {
                Success = false,
                Message = "An error occurred while resetting your password"
            });
        }
    }

    [HttpPost("confirm-email")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(ConfirmEmailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ConfirmEmailResponse), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ConfirmEmailResponse>> ConfirmEmailAsync([FromBody] ConfirmEmailRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Token))
        {
            return BadRequest(new ConfirmEmailResponse(false, "Confirmation token is required"));
        }

        try
        {
            bool success = await _authService.ConfirmEmailAsync(request.Token);

            return success
                ? Ok(new ConfirmEmailResponse(true, "Email address confirmed successfully. You can now log in."))
                : BadRequest(new ConfirmEmailResponse(false, "Invalid or expired email confirmation token"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error confirming email");
            return BadRequest(new ConfirmEmailResponse(false, "An error occurred while confirming your email address"));
        }
    }

    [HttpPost("resend-confirmation")]
    [Authorize]
    [ProducesResponseType(typeof(ResendConfirmationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ResendConfirmationResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ResendConfirmationResponse>> ResendEmailConfirmationAsync()
    {
        try
        {
            // Get user ID from authenticated claims
            Claim? userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier);
            if (userIdClaim == null || !Guid.TryParse(userIdClaim.Value, out Guid userId))
            {
                return BadRequest(new ResendConfirmationResponse(false, "Invalid user authentication"));
            }

            UserDto? userDto = await _authService.GetUserWithRolesAndPermissionsAsync(userId);
            if (userDto == null)
            {
                return NotFound();
            }

            if (userDto.EmailConfirmed)
            {
                return Ok(new ResendConfirmationResponse(true, "Email address is already confirmed"));
            }

            // Get the actual User entity to pass to SendEmailConfirmationAsync
            User? user = await _authService.GetUserByEmailAsync(userDto.Email);
            if (user == null)
            {
                return NotFound();
            }

            bool success = await _authService.SendEmailConfirmationAsync(user);

            return success
                ? Ok(new ResendConfirmationResponse(true, "Confirmation email has been sent"))
                : BadRequest(new ResendConfirmationResponse(false, "Failed to send confirmation email. Please try again later."));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error resending email confirmation");
            return BadRequest(new ResendConfirmationResponse(false, "An error occurred while sending the confirmation email"));
        }
    }
}

public record ConfirmEmailResponse(bool Success, string Message);

public record ResendConfirmationResponse(bool Success, string Message);
