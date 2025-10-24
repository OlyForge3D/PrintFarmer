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
    IUnifiedLoggingService logger,
    Farm.Web.Api.Services.Email.IEmailService emailService,
    Farm.Web.Api.Services.RateLimiting.IRateLimitService rateLimitService,
    IAccountLockoutService accountLockoutService,
    IAuthAuditService authAuditService) : IAuthenticationService
{
    private const string PasswordResetPath = "/reset-password";
    private const string EmailConfirmationPath = "/confirm-email";

    private readonly IUsersRepository _usersRepository = usersRepository;
    private readonly IPasswordHashingService _passwordHashing = passwordHashing;
    private readonly IConfiguration _configuration = configuration;
    private readonly IUnifiedLoggingService _logger = logger;
    private readonly Farm.Web.Api.Services.Email.IEmailService _emailService = emailService;
    private readonly Farm.Web.Api.Services.RateLimiting.IRateLimitService _rateLimitService = rateLimitService;
    private readonly IAccountLockoutService _accountLockoutService = accountLockoutService;
    private readonly IAuthAuditService _authAuditService = authAuditService;

    public async Task<AuthenticationResult> AuthenticateAsync(string username, string password)
    {
        try
        {
            User? user = await _usersRepository.GetByUsernameAsync(username);
            if (user == null)
            {
                // Record failed attempt even for non-existent users (prevent user enumeration)
                await _accountLockoutService.RecordFailedLoginByUsernameAsync(username, "unknown", "User not found");
                Console.WriteLine($"[AuthenticationService] Calling LogLoginFailedAsync (User not found) for username={username}");
                await _authAuditService.LogLoginFailedAsync(username, "User not found", "unknown", null);
                Console.WriteLine($"[AuthenticationService] Completed LogLoginFailedAsync (User not found) for username={username}");
                _logger.LogWarning($"Authentication failed for username: {username} - user not found", null, null);
                return new AuthenticationResult(false, Error: "Invalid username or password");
            }

            // Check if account is locked out
            if (await _accountLockoutService.IsLockedOutAsync(user.Id))
            {
                DateTime? lockoutEnd = await _accountLockoutService.GetLockoutEndAsync(user.Id);
                Console.WriteLine($"[AuthenticationService] Calling LogLoginFailedAsync (Account locked) for username={username}");
                await _authAuditService.LogLoginFailedAsync(username, $"Account locked until {lockoutEnd}", "unknown", null);
                Console.WriteLine($"[AuthenticationService] Completed LogLoginFailedAsync (Account locked) for username={username}");
                _logger.LogWarning($"Authentication failed for username: {username} - account locked until {lockoutEnd}", null, null);
                return new AuthenticationResult(false, Error: $"Account is temporarily locked. Please try again later.");
            }

            if (!user.IsActive)
            {
                Console.WriteLine($"[AuthenticationService] Calling LogLoginFailedAsync (User disabled) for username={username}");
                await _authAuditService.LogLoginFailedAsync(username, "User account is disabled", "unknown", null);
                Console.WriteLine($"[AuthenticationService] Completed LogLoginFailedAsync (User disabled) for username={username}");
                _logger.LogWarning($"Authentication failed for username: {username} - user is inactive", null, null);
                return new AuthenticationResult(false, Error: "User account is disabled");
            }

            if (!_passwordHashing.VerifyPassword(password, user.PasswordHash))
            {
                // Record failed login attempt (may trigger lockout)
                await _accountLockoutService.RecordFailedLoginAsync(user.Id, username, "unknown", "Invalid password");
                Console.WriteLine($"[AuthenticationService] Calling LogLoginFailedAsync (Invalid password) for username={username}");
                await _authAuditService.LogLoginFailedAsync(username, "Invalid password", "unknown", null);
                Console.WriteLine($"[AuthenticationService] Completed LogLoginFailedAsync (Invalid password) for username={username}");
                _logger.LogWarning($"Authentication failed for username: {username} - invalid password", null, null);
                return new AuthenticationResult(false, Error: "Invalid username or password");
            }

            // Successful authentication - reset failed login counter
            await _accountLockoutService.ResetFailedLoginCountAsync(user.Id);

            user.LastLogin = DateTime.UtcNow;
            user.UpdatedAt = DateTime.UtcNow;
            await _usersRepository.SaveChangesAsync();

            // Audit log successful login
            Console.WriteLine($"[AuthenticationService] Calling LogLoginAsync for UserId={user.Id}");
            await _authAuditService.LogLoginAsync(user.Id, "unknown", null);
            Console.WriteLine($"[AuthenticationService] Completed LogLoginAsync for UserId={user.Id}");

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

            // Audit log successful registration
            await _authAuditService.LogRegisterAsync(user.Id, "unknown", null);

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
        // Diagnostic logging to help tests: print a short preview of the stored hash and verification result
        try
        {
            var preview = user.PasswordHash != null && user.PasswordHash.Length > 10 ? user.PasswordHash.Substring(0, 10) : user.PasswordHash;
            Console.WriteLine($"[AuthenticationService] ChangePassword: UserId={userId} StoredHashPreview={preview}");
        }
        catch { }

        if (string.IsNullOrEmpty(user.PasswordHash))
        {
            Console.WriteLine($"[AuthenticationService] ChangePassword: Stored hash is null/empty for UserId={userId}");
            return false;
        }

        var currentMatches = _passwordHashing.VerifyPassword(currentPassword, user.PasswordHash);
        Console.WriteLine($"[AuthenticationService] ChangePassword: VerifyPassword result={currentMatches} for UserId={userId}");
        if (!currentMatches)
        {
            return false;
        }
        string newHash = _passwordHashing.HashPassword(newPassword);
        bool success = await _usersRepository.UpdatePasswordAsync(userId, currentPassword, newHash);

        if (success)
        {
            // Audit log password change
            await _authAuditService.LogPasswordChangeAsync(userId, "unknown", null);
        }

        return success;
    }

    public Task<bool> SendEmailConfirmationAsync(User user) => SendEmailConfirmationInternalAsync(user);

    public Task<bool> ConfirmEmailAsync(string token) => ConfirmEmailInternalAsync(token);

    private async Task<bool> SendEmailConfirmationInternalAsync(User user)
    {
        try
        {
            // Check rate limiting
            var rateLimit = await _rateLimitService.CheckEmailConfirmationLimitAsync(user.Email);
            if (!rateLimit.IsAllowed)
            {
                _logger.LogWarning($"Email confirmation rate limit exceeded for {user.Email}", null, new
                {
                    UserId = user.Id,
                    Email = user.Email,
                    RemainingAttempts = rateLimit.RemainingAttempts
                });
                return false;
            }

            // Record attempt
            await _rateLimitService.RecordEmailConfirmationAttemptAsync(user.Email);

            // Generate secure random token
            string token = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))
                .Replace("+", "-")
                .Replace("/", "_")
                .Replace("=", "");

            // Update user with confirmation token
            user.EmailConfirmationToken = token;
            user.UpdatedAt = DateTime.UtcNow;
            await _usersRepository.SaveChangesAsync();

            // Build confirmation link
            string baseUrl = _configuration["Email:BaseUrl"] ?? "http://localhost:3000";
            string confirmationLink = $"{baseUrl.TrimEnd('/')}{EmailConfirmationPath}?token={Uri.EscapeDataString(token)}";

            bool emailSent = false;
            try
            {
                emailSent = await _emailService.SendEmailConfirmationAsync(user.Email, confirmationLink);
            }
            catch (Exception exSend)
            {
                _logger.LogWarning(exSend, "Email confirmation send failed - falling back to log only", null, null);
            }

            _logger.LogInformation($"Email confirmation sent to {user.Email}. EmailSent={emailSent}", null, new
            {
                UserId = user.Id,
                Email = user.Email,
                ConfirmationLink = confirmationLink,
                ExpirationHours = 24
            });

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to send email confirmation for user {user.Id}", null, null);
            return false;
        }
    }

    private async Task<bool> ConfirmEmailInternalAsync(string token)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                return false;
            }

            User? user = await _usersRepository.GetByEmailConfirmationTokenAsync(token);
            if (user == null)
            {
                _logger.LogWarning($"Email confirmation attempted with invalid token");
                return false;
            }

            if (user.EmailConfirmed)
            {
                _logger.LogInformation($"Email already confirmed for user {user.Username}");
                return true; // Already confirmed, consider this success
            }

            // Confirm the email
            user.EmailConfirmed = true;
            user.EmailConfirmationToken = null; // Clear the token
            user.UpdatedAt = DateTime.UtcNow;
            await _usersRepository.SaveChangesAsync();

            _logger.LogInformation($"Email confirmed for user {user.Username}", null, new
            {
                UserId = user.Id,
                Email = user.Email
            });

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Email confirmation failed", null, null);
            return false;
        }
    }

    public async Task<bool> InitiatePasswordResetAsync(string email, string? ipAddress)
    {
        try
        {
            // Check rate limiting first
            var rateLimit = await _rateLimitService.CheckPasswordResetLimitAsync(email);
            if (!rateLimit.IsAllowed)
            {
                _logger.LogWarning($"Password reset rate limit exceeded for {email}", null, new
                {
                    Email = email,
                    RemainingAttempts = rateLimit.RemainingAttempts,
                    RetryAfter = rateLimit.RetryAfter
                });
                // Still return true to prevent information leakage
                return true;
            }

            User? user = await _usersRepository.GetByEmailAsync(email);
            if (user == null)
            {
                // Don't reveal that the email doesn't exist (security best practice)
                _logger.LogWarning($"Password reset requested for non-existent email: {email}");
                // Record attempt even for non-existent emails to prevent enumeration via rate limiting
                await _rateLimitService.RecordPasswordResetAttemptAsync(email);
                // Audit log the attempt (even for non-existent email)
                await _authAuditService.LogPasswordResetInitiatedAsync(email, ipAddress, null);
                return true; // Return true to prevent email enumeration
            }

            // Record the attempt
            await _rateLimitService.RecordPasswordResetAttemptAsync(email);

            // Generate secure random token
            string token = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))
                .Replace("+", "-")
                .Replace("/", "_")
                .Replace("=", "");

            // Create password reset token entity
            var resetToken = new PasswordResetToken
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                Token = token,
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddHours(1), // 1 hour expiration
                IsUsed = false
            };

            await _usersRepository.CreatePasswordResetTokenAsync(resetToken);
            await _usersRepository.SaveChangesAsync();

            // Build reset link
            string baseUrl = _configuration["Email:BaseUrl"] ?? "http://localhost:3000";
            string resetLink = $"{baseUrl.TrimEnd('/')}{PasswordResetPath}?token={Uri.EscapeDataString(token)}&email={Uri.EscapeDataString(user.Email)}";

            bool emailSent = false;
            try
            {
                emailSent = await _emailService.SendPasswordResetAsync(user.Email, resetLink);
            }
            catch (Exception exSend)
            {
                _logger.LogWarning(exSend, "Password reset email send failed - falling back to log only", null, null);
            }

            _logger.LogInformation($"Password reset token generated for user {user.Username}. EmailSent={emailSent}", null, new
            {
                UserId = user.Id,
                Email = user.Email,
                ResetLink = resetLink,
                ExpirationMinutes = 60
            });

            // Audit log password reset initiation
            await _authAuditService.LogPasswordResetInitiatedAsync(user.Email, ipAddress, null);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error initiating password reset for email: {email}");
            return false;
        }
    }

    public async Task<bool> ResetPasswordAsync(string token, string email, string newPassword, string? ipAddress)
    {
        try
        {
            // Find user by email
            User? user = await _usersRepository.GetByEmailAsync(email);
            if (user == null)
            {
                _logger.LogWarning($"Password reset attempted with invalid email: {email}");
                return false;
            }

            // Find and validate token
            var resetToken = await _usersRepository.GetPasswordResetTokenAsync(token);
            if (resetToken == null || resetToken.UserId != user.Id)
            {
                _logger.LogWarning($"Invalid password reset token for user: {user.Username}");
                return false;
            }

            // Check if token is expired
            if (resetToken.ExpiresAt < DateTime.UtcNow)
            {
                _logger.LogWarning($"Expired password reset token for user: {user.Username}");
                return false;
            }

            // Check if token has already been used
            if (resetToken.IsUsed)
            {
                _logger.LogWarning($"Already used password reset token for user: {user.Username}");
                return false;
            }

            // Hash new password
            string newHash = _passwordHashing.HashPassword(newPassword);

            // Update user password
            user.PasswordHash = newHash;
            user.UpdatedAt = DateTime.UtcNow;

            // Mark token as used
            resetToken.IsUsed = true;
            resetToken.UsedAt = DateTime.UtcNow;
            resetToken.UsedByIp = ipAddress;

            await _usersRepository.SaveChangesAsync();

            // Audit log successful password reset
            await _authAuditService.LogPasswordResetAsync(user.Id, ipAddress, null);

            _logger.LogInformation($"Password successfully reset for user: {user.Username}");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error resetting password");
            return false;
        }
    }

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
