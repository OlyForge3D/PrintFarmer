using System.Security.Claims;
using Farm.Web.Api.Domain;
using Farm.Web.Shared;

namespace Farm.Web.Api.Services.Authentication;

public interface IAuthenticationService
{
    Task<AuthenticationResult> AuthenticateAsync(string username, string password);
    Task<AuthenticationResult> RegisterAsync(RegisterRequest request);
    Task<bool> ValidateTokenAsync(string token);
    Task<ClaimsPrincipal?> GetPrincipalFromTokenAsync(string token);
    Task<string> GenerateJwtTokenAsync(User user);
    Task<bool> SendEmailConfirmationAsync(User user);
    Task<bool> ConfirmEmailAsync(string token);
    Task<bool> SendPasswordResetAsync(string email);
    Task<bool> ResetPasswordAsync(string token, string newPassword);
    Task<UserDto?> GetUserWithRolesAndPermissionsAsync(Guid userId);
    Task<bool> HasPermissionAsync(Guid userId, string resource, string action);
    Task<User?> GetUserByUsernameAsync(string username);
    Task<User?> GetUserByEmailAsync(string email);
    Task<bool> ChangePasswordAsync(Guid userId, string currentPassword, string newPassword);
}