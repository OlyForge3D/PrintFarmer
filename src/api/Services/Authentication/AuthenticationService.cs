using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Farm.Web.Api.Data;
using Farm.Web.Api.Domain;
using Farm.Web.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace Farm.Web.Api.Services.Authentication;

public class AuthenticationService : IAuthenticationService
{
    private readonly AppDbContext _context;
    private readonly IPasswordHashingService _passwordHashing;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AuthenticationService> _logger;

    public AuthenticationService(
        AppDbContext context,
        IPasswordHashingService passwordHashing,
        IConfiguration configuration,
        ILogger<AuthenticationService> logger)
    {
        _context = context;
        _passwordHashing = passwordHashing;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<AuthenticationResult> AuthenticateAsync(string username, string password)
    {
        try
        {
            var user = await GetUserByUsernameAsync(username);
            if (user == null)
            {
                _logger.LogWarning("Authentication failed for username: {Username} - user not found", username);
                return new AuthenticationResult(false, Error: "Invalid username or password");
            }

            if (!user.IsActive)
            {
                _logger.LogWarning("Authentication failed for username: {Username} - user is inactive", username);
                return new AuthenticationResult(false, Error: "User account is disabled");
            }

            if (!_passwordHashing.VerifyPassword(password, user.PasswordHash))
            {
                _logger.LogWarning("Authentication failed for username: {Username} - invalid password", username);
                return new AuthenticationResult(false, Error: "Invalid username or password");
            }

            // Update last login time
            user.LastLogin = DateTime.UtcNow;
            user.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            var token = await GenerateJwtTokenAsync(user);
            var userDto = await GetUserWithRolesAndPermissionsAsync(user.Id);

            _logger.LogInformation("User {Username} authenticated successfully", username);

            return new AuthenticationResult(
                true,
                token,
                DateTime.UtcNow.AddDays(7), // Token expires in 7 days
                userDto
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during authentication for username: {Username}", username);
            return new AuthenticationResult(false, Error: "Authentication service error");
        }
    }

    public async Task<AuthenticationResult> RegisterAsync(RegisterRequest request)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(request);
            // If a user with the same username AND email already exists and the password matches,
            // treat registration as idempotent and return a valid authentication result.
            var existingExact = await _context.Users.FirstOrDefaultAsync(u => u.Username == request.Username && u.Email == request.Email);
            if (existingExact != null && _passwordHashing.VerifyPassword(request.Password, existingExact.PasswordHash))
            {
                var tokenExisting = await GenerateJwtTokenAsync(existingExact);
                var userDtoExisting = await GetUserWithRolesAndPermissionsAsync(existingExact.Id);
                return new AuthenticationResult(true, tokenExisting, DateTime.UtcNow.AddDays(7), userDtoExisting);
            }

            // Check if username already exists
            if (await _context.Users.AnyAsync(u => u.Username == request.Username))
            {
                return new AuthenticationResult(false, Error: "Username is already taken");
            }

            // Check if email already exists
            if (await _context.Users.AnyAsync(u => u.Email == request.Email))
            {
                return new AuthenticationResult(false, Error: "Email is already registered");
            }

            var user = new User
            {
                Id = Guid.NewGuid(),
                Username = request.Username,
                Email = request.Email,
                PasswordHash = _passwordHashing.HashPassword(request.Password),
                FirstName = request.FirstName,
                LastName = request.LastName,
                IsActive = true,
                EmailConfirmed = false, // TODO: Implement email confirmation
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            _context.Users.Add(user);

            // Assign default "farm_user" role
            var defaultRole = await _context.Roles
                .FirstOrDefaultAsync(r => r.Name == "farm_user" && r.IsSystemRole);

            if (defaultRole != null)
            {
                var userRole = new UserRole
                {
                    Id = Guid.NewGuid(),
                    UserId = user.Id,
                    RoleId = defaultRole.Id,
                    AssignedAt = DateTime.UtcNow,
                    IsActive = true
                };

                _context.UserRoles.Add(userRole);
            }

            await _context.SaveChangesAsync();

            var token = await GenerateJwtTokenAsync(user);
            var userDto = await GetUserWithRolesAndPermissionsAsync(user.Id);

            _logger.LogInformation("User {Username} registered successfully", request.Username);

            return new AuthenticationResult(
                true,
                token,
                DateTime.UtcNow.AddDays(7),
                userDto
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during registration for username: {Username}", request.Username);
            return new AuthenticationResult(false, Error: "Registration service error");
        }
    }

    public async Task<string> GenerateJwtTokenAsync(User user)
    {
        ArgumentNullException.ThrowIfNull(user);
        var rawKey = _configuration["Jwt:Key"];
        if (string.IsNullOrWhiteSpace(rawKey) || rawKey.Length < 32)
        {
            _logger.LogError("JWT key is missing or too short. Minimum 32 characters recommended.");
            throw new InvalidOperationException("Secure JWT key not configured");
        }
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(rawKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var roles = await GetUserRolesAsync(user.Id);
        var permissions = await GetUserPermissionsAsync(user.Id);

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.Username),
            new(ClaimTypes.Email, user.Email),
            new("given_name", user.FirstName ?? ""),
            new("family_name", user.LastName ?? "")
        };

        // Add role claims
        claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role, role)));

        // Add permission claims
        claims.AddRange(permissions.Select(perm => new Claim("permission", $"{perm.Resource}:{perm.Action}")));

        var token = new JwtSecurityToken(
            issuer: _configuration["Jwt:Issuer"],
            audience: _configuration["Jwt:Audience"],
            claims: claims,
            expires: DateTime.UtcNow.AddDays(7),
            signingCredentials: creds
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public Task<bool> ValidateTokenAsync(string token)
    {
        try
        {
            var tokenHandler = new JwtSecurityTokenHandler();
            var rawKey = _configuration["Jwt:Key"];
            if (string.IsNullOrWhiteSpace(rawKey))
            {
                return Task.FromResult(false);
            }

            var key = Encoding.UTF8.GetBytes(rawKey);

            var validationParameters = new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(key),
                ValidateIssuer = true,
                ValidIssuer = _configuration["Jwt:Issuer"],
                ValidateAudience = true,
                ValidAudience = _configuration["Jwt:Audience"],
                ValidateLifetime = true,
                ClockSkew = TimeSpan.Zero
            };

            tokenHandler.ValidateToken(token, validationParameters, out _);
            return Task.FromResult(true);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    public Task<ClaimsPrincipal?> GetPrincipalFromTokenAsync(string token)
    {
        try
        {
            var tokenHandler = new JwtSecurityTokenHandler();
            var rawKey = _configuration["Jwt:Key"];
            if (string.IsNullOrWhiteSpace(rawKey))
            {
                return Task.FromResult<ClaimsPrincipal?>(null);
            }

            var key = Encoding.UTF8.GetBytes(rawKey);

            var validationParameters = new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(key),
                ValidateIssuer = true,
                ValidIssuer = _configuration["Jwt:Issuer"],
                ValidateAudience = true,
                ValidAudience = _configuration["Jwt:Audience"],
                ValidateLifetime = false, // We'll handle lifetime validation elsewhere
                ClockSkew = TimeSpan.Zero
            };

            var principal = tokenHandler.ValidateToken(token, validationParameters, out _);
            return Task.FromResult<ClaimsPrincipal?>(principal);
        }
        catch
        {
            return Task.FromResult<ClaimsPrincipal?>(null);
        }
    }

    public async Task<UserDto?> GetUserWithRolesAndPermissionsAsync(Guid userId)
    {
        var user = await _context.Users
            .Include(u => u.UserRoles)
                .ThenInclude(ur => ur.Role)
                    .ThenInclude(r => r.RolePermissions)
                        .ThenInclude(rp => rp.Resource)
            .Include(u => u.UserRoles)
                .ThenInclude(ur => ur.Role)
                    .ThenInclude(r => r.RolePermissions)
                        .ThenInclude(rp => rp.Action)
            .FirstOrDefaultAsync(u => u.Id == userId);

        if (user == null)
        {
            return null;
        }

        var roles = user.UserRoles
            .Where(ur => ur.IsActive && (ur.ExpiresAt == null || ur.ExpiresAt > DateTime.UtcNow))
            .Select(ur => ur.Role.Name)
            .ToArray();

        var permissions = user.UserRoles
            .Where(ur => ur.IsActive && (ur.ExpiresAt == null || ur.ExpiresAt > DateTime.UtcNow))
            .SelectMany(ur => ur.Role.RolePermissions)
            .Where(rp => rp.Granted)
            .Select(rp => $"{rp.Resource.Name}:{rp.Action.Name}")
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
            permissions
        );
    }

    public async Task<bool> HasPermissionAsync(Guid userId, string resource, string action)
    {
        return await _context.UserRoles
            .Where(ur => ur.UserId == userId && ur.IsActive &&
                        (ur.ExpiresAt == null || ur.ExpiresAt > DateTime.UtcNow))
            .SelectMany(ur => ur.Role.RolePermissions)
            .AnyAsync(rp => rp.Granted &&
                           rp.Resource.Name == resource &&
                           rp.Action.Name == action);
    }

    public async Task<User?> GetUserByUsernameAsync(string username)
    {
        return await _context.Users.FirstOrDefaultAsync(u => u.Username == username);
    }

    public async Task<User?> GetUserByEmailAsync(string email)
    {
        return await _context.Users.FirstOrDefaultAsync(u => u.Email == email);
    }

    public async Task<bool> ChangePasswordAsync(Guid userId, string currentPassword, string newPassword)
    {
        var user = await _context.Users.FindAsync(userId);
        if (user == null)
        {
            return false;
        }

        if (!_passwordHashing.VerifyPassword(currentPassword, user.PasswordHash))
        {
            return false;
        }

        user.PasswordHash = _passwordHashing.HashPassword(newPassword);
        user.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        return true;
    }

    // TODO: Implement email confirmation and password reset functionality
    public Task<bool> SendEmailConfirmationAsync(User user) => Task.FromResult(true);
    public Task<bool> ConfirmEmailAsync(string token) => Task.FromResult(true);
    public Task<bool> SendPasswordResetAsync(string email) => Task.FromResult(true);
    public Task<bool> ResetPasswordAsync(string token, string newPassword) => Task.FromResult(true);

    private async Task<List<string>> GetUserRolesAsync(Guid userId)
    {
        return await _context.UserRoles
            .Where(ur => ur.UserId == userId && ur.IsActive &&
                        (ur.ExpiresAt == null || ur.ExpiresAt > DateTime.UtcNow))
            .Select(ur => ur.Role.Name)
            .ToListAsync();
    }

    private async Task<List<(string Resource, string Action)>> GetUserPermissionsAsync(Guid userId)
    {
        return await _context.UserRoles
            .Where(ur => ur.UserId == userId && ur.IsActive &&
                        (ur.ExpiresAt == null || ur.ExpiresAt > DateTime.UtcNow))
            .SelectMany(ur => ur.Role.RolePermissions)
            .Where(rp => rp.Granted)
            .Select(rp => new ValueTuple<string, string>(rp.Resource.Name, rp.Action.Name))
            .Distinct()
            .ToListAsync();
    }
}
