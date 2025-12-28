using System.Security.Claims;
using Farm.Infrastructure.Contracts.Auth;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Services.Authentication;

public interface IAuthenticationService
{
    Task<AuthenticationResult> AuthenticateAsync(string username, string password);
    Task<AuthenticationResult> RegisterAsync(RegisterRequest request);
    Task<bool> ValidateTokenAsync(string token);
    Task<ClaimsPrincipal?> GetPrincipalFromTokenAsync(string token);
    Task<string> GenerateJwtTokenAsync(User user);
    Task<bool> SendEmailConfirmationAsync(User user);
    Task<bool> ConfirmEmailAsync(string token);
    Task<bool> InitiatePasswordResetAsync(string email, string? ipAddress);
    Task<bool> ResetPasswordAsync(string token, string email, string newPassword, string? ipAddress);
    Task<UserDto?> GetUserWithRolesAndPermissionsAsync(Guid userId);
    Task<bool> HasPermissionAsync(Guid userId, string resource, string action);
    Task<User?> GetUserByUsernameAsync(string username);
    Task<User?> GetUserByEmailAsync(string email);
    Task<bool> ChangePasswordAsync(Guid userId, string currentPassword, string newPassword);
}
