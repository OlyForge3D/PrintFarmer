using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Users;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Shared;
using Microsoft.IdentityModel.Tokens;

namespace Farm.Web.Api.Services.Authentication;

public class AuthenticationService(
    IUsersRepository usersRepository,
    IPasswordHashingService passwordHashing,
    IConfiguration configuration,
    IUnifiedLoggingService logger) : IAuthenticationService
{
    private readonly IUsersRepository _usersRepository = usersRepository;
    private readonly IPasswordHashingService _passwordHashing = passwordHashing;
    private readonly IConfiguration _configuration = configuration;
    private readonly IUnifiedLoggingService _logger = logger;

    public async Task<AuthenticationResult> AuthenticateAsync(string username, string password)
    {
        try
        {
            User? user = await _usersRepository.GetByUsernameAsync(username);
            if (user == null)
            {
                _logger.LogWarning($"Authentication failed for username: {username} - user not found", null, null);
                return new AuthenticationResult(false, Error: "Invalid username or password");
            }
            if (!user.IsActive)
            {
                _logger.LogWarning($"Authentication failed for username: {username} - user is inactive", null, null);
                return new AuthenticationResult(false, Error: "User account is disabled");
            }
            if (!_passwordHashing.VerifyPassword(password, user.PasswordHash))
            {
                _logger.LogWarning($"Authentication failed for username: {username} - invalid password", null, null);
                return new AuthenticationResult(false, Error: "Invalid username or password");
            }
            user.LastLogin = DateTime.UtcNow;
            user.UpdatedAt = DateTime.UtcNow;
            await _usersRepository.SaveChangesAsync();
            string token = await GenerateJwtTokenAsync(user);
            UserDto? userDto = await GetUserWithRolesAndPermissionsAsync(user.Id);
            _logger.LogInformation($"User {username} authenticated successfully", null, null);
            return new AuthenticationResult(true, token, DateTime.UtcNow.AddDays(7), userDto);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error during authentication for username: {username}", null, null);
            return new AuthenticationResult(false, Error: "Authentication service error");
        }
    }

    public async Task<AuthenticationResult> RegisterAsync(RegisterRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            User? existing = await _usersRepository.GetByUsernameAsync(request.Username);
            if (existing != null && existing.Email == request.Email && _passwordHashing.VerifyPassword(request.Password, existing.PasswordHash))
            {
                string tokenExisting = await GenerateJwtTokenAsync(existing);
                UserDto? dtoExisting = await GetUserWithRolesAndPermissionsAsync(existing.Id);
                return new AuthenticationResult(true, tokenExisting, DateTime.UtcNow.AddDays(7), dtoExisting);
            }
            if (await _usersRepository.UsernameExistsStrictAsync(request.Username))
            {
                return new AuthenticationResult(false, Error: "Username is already taken");
            }
            if (await _usersRepository.EmailExistsStrictAsync(request.Email))
            {
                return new AuthenticationResult(false, Error: "Email is already registered");
            }
            User user = new()
            {
                Id = Guid.NewGuid(),
                Username = request.Username,
                Email = request.Email,
                PasswordHash = _passwordHashing.HashPassword(request.Password),
                FirstName = request.FirstName,
                LastName = request.LastName,
                IsActive = false,
                EmailConfirmed = false,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            await _usersRepository.AddUserAsync(user, roleIds: null);
            Role? defaultRole = await _usersRepository.GetRoleByNameAsync("farm_user");
            if (defaultRole != null)
            {
                await _usersRepository.UpdateUserRolesAsync(user.Id, new[] { defaultRole.Id });
            }
            await _usersRepository.SaveChangesAsync();
            string token = await GenerateJwtTokenAsync(user);
            UserDto? dto = await GetUserWithRolesAndPermissionsAsync(user.Id);
            _logger.LogInformation($"User {request.Username} registered successfully", null, null);
            return new AuthenticationResult(true, token, DateTime.UtcNow.AddDays(7), dto);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error during registration for username: {request.Username}", null, null);
            return new AuthenticationResult(false, Error: "Registration service error");
        }
    }

    public async Task<string> GenerateJwtTokenAsync(User user)
    {
        ArgumentNullException.ThrowIfNull(user);
        string? rawKey = _configuration["Jwt:Key"];
        if (string.IsNullOrWhiteSpace(rawKey) || rawKey.Length < 32)
        {
            _logger.LogError("JWT key is missing or too short. Minimum 32 characters recommended.", null, null);
            throw new InvalidOperationException("Secure JWT key not configured");
        }
#pragma warning disable S6781
        SymmetricSecurityKey key = new(Encoding.UTF8.GetBytes(rawKey));
#pragma warning restore S6781
        SigningCredentials creds = new(key, SecurityAlgorithms.HmacSha256);
        List<string> roles = await _usersRepository.GetActiveRoleNamesAsync(user.Id);
        List<(string Resource, string Action)> permissions = await _usersRepository.GetGrantedPermissionsAsync(user.Id);
        List<Claim> claims = new()
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.Username),
            new(ClaimTypes.Email, user.Email),
            new("given_name", user.FirstName ?? string.Empty),
            new("family_name", user.LastName ?? string.Empty)
        };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        claims.AddRange(permissions.Select(p => new Claim("permission", $"{p.Resource}:{p.Action}")));
        JwtSecurityToken token = new(
            issuer: _configuration["Jwt:Issuer"],
            audience: _configuration["Jwt:Audience"],
            claims: claims,
            expires: DateTime.UtcNow.AddDays(7),
            signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public async Task<bool> ValidateTokenAsync(string token)
    {
        try
        {
            JwtSecurityTokenHandler handler = new();
            string? rawKey = _configuration["Jwt:Key"];
            if (string.IsNullOrWhiteSpace(rawKey))
            {
                return false;
            }
            byte[] keyBytes = Encoding.UTF8.GetBytes(rawKey);
#pragma warning disable S6781
            TokenValidationParameters parms = new()
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(keyBytes),
                ValidateIssuer = true,
                ValidIssuer = _configuration["Jwt:Issuer"],
                ValidateAudience = true,
                ValidAudience = _configuration["Jwt:Audience"],
                ValidateLifetime = true,
                ClockSkew = TimeSpan.Zero
            };
#pragma warning restore S6781
            _ = await handler.ValidateTokenAsync(token, parms);
            return true;
        }
        catch (SecurityTokenException)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }

    public async Task<ClaimsPrincipal?> GetPrincipalFromTokenAsync(string token)
    {
        try
        {
            JwtSecurityTokenHandler handler = new();
            string? rawKey = _configuration["Jwt:Key"];
            if (string.IsNullOrWhiteSpace(rawKey))
            {
                return null;
            }
            byte[] keyBytes = Encoding.UTF8.GetBytes(rawKey);
#pragma warning disable S6781
            TokenValidationParameters parms = new()
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(keyBytes),
                ValidateIssuer = true,
                ValidIssuer = _configuration["Jwt:Issuer"],
                ValidateAudience = true,
                ValidAudience = _configuration["Jwt:Audience"],
                ValidateLifetime = true,
                ClockSkew = TimeSpan.Zero
            };
#pragma warning restore S6781
            TokenValidationResult result = await handler.ValidateTokenAsync(token, parms);
            if (!result.IsValid || result.SecurityToken is not JwtSecurityToken jwt || jwt.ValidTo < DateTime.UtcNow)
            {
                return null;
            }
            return result.ClaimsIdentity != null ? new ClaimsPrincipal(result.ClaimsIdentity) : null;
        }
        catch (SecurityTokenException)
        {
            return null;
        }
        catch
        {
            return null;
        }
    }

    public Task<UserDto?> GetUserWithRolesAndPermissionsAsync(Guid userId) => BuildUserDtoAsync(userId);

    public async Task<bool> HasPermissionAsync(Guid userId, string resource, string action)
    {
        var permissions = await _usersRepository.GetGrantedPermissionsAsync(userId);
        return permissions.Any(p => p.Resource == resource && p.Action == action);
    }

    public Task<User?> GetUserByUsernameAsync(string username) => _usersRepository.GetByUsernameAsync(username);
    public Task<User?> GetUserByEmailAsync(string email) => _usersRepository.GetByEmailAsync(email);

    public async Task<bool> ChangePasswordAsync(Guid userId, string currentPassword, string newPassword)
    {
        User? user = await _usersRepository.GetUserEntityAsync(userId);
        if (user == null)
        {
            return false;
        }
        if (!_passwordHashing.VerifyPassword(currentPassword, user.PasswordHash))
        {
            return false;
        }
        string newHash = _passwordHashing.HashPassword(newPassword);
        return await _usersRepository.UpdatePasswordAsync(userId, currentPassword, newHash);
    }

    public Task<bool> SendEmailConfirmationAsync(User user) => Task.FromResult(true);
    public Task<bool> ConfirmEmailAsync(string token) => Task.FromResult(true);
    public Task<bool> SendPasswordResetAsync(string email) => Task.FromResult(true);
    public Task<bool> ResetPasswordAsync(string token, string newPassword) => Task.FromResult(true);

    private async Task<UserDto?> BuildUserDtoAsync(Guid userId)
    {
        User? user = await _usersRepository.GetUserEntityAsync(userId);
        if (user == null)
        {
            return null;
        }
        string[] roles = (await _usersRepository.GetActiveRoleNamesAsync(user.Id)).ToArray();
        string[] permissions = (await _usersRepository.GetGrantedPermissionsAsync(user.Id))
            .Select(p => $"{p.Resource}:{p.Action}")
            .Distinct()
            .ToArray();
        return new UserDto(
            user.Id,
            user.Username,
            user.Email,
            user.FirstName,
            user.LastName,
            user.IsActive,
            user.EmailConfirmed,
            user.LastLogin,
            user.CreatedAt,
            roles,
            permissions);
    }
}
