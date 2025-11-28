using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Farm.Web.Api.Services.Authentication;

/// <summary>
/// Service for managing account lockouts after failed login attempts
/// </summary>
public class AccountLockoutService : IAccountLockoutService
{
    private readonly AppDbContext _context;
    private readonly IConfiguration _configuration;
    private readonly IAuthAuditService _authAuditService;
    private readonly int _maxFailedAttempts;
    private readonly int _lockoutDurationMinutes;

    public AccountLockoutService(AppDbContext context, IConfiguration configuration, IAuthAuditService authAuditService)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _authAuditService = authAuditService ?? throw new ArgumentNullException(nameof(authAuditService));

        // Read lockout settings from configuration with sensible defaults
        _maxFailedAttempts = int.Parse(_configuration["AccountLockout:MaxFailedAttempts"] ?? "5");
        _lockoutDurationMinutes = int.Parse(_configuration["AccountLockout:LockoutDurationMinutes"] ?? "15");
    }

    public async Task<bool> IsLockedOutAsync(Guid userId)
    {
        User? user = await _context.Users.FindAsync(userId);
        if (user == null)
        {
            return false;
        }

        // Check if lockout period has ended
        if (user.LockoutEnd.HasValue && user.LockoutEnd.Value > DateTime.UtcNow)
        {
            return true;
        }

        // Clear expired lockout
        if (user.LockoutEnd.HasValue && user.LockoutEnd.Value <= DateTime.UtcNow)
        {
            user.LockoutEnd = null;
            user.FailedLoginAttempts = 0;

            // Audit log account unlock (automatic expiration)
            await _authAuditService.LogAccountUnlockedAsync(user.Id, "Lockout period expired", null);

            _ = await _context.SaveChangesAsync();
        }

        return false;
    }

    public async Task RecordFailedLoginAsync(Guid userId, string identifier, string? ipAddress, string? failureReason = null)
    {
        User? user = await _context.Users.FindAsync(userId);
        if (user == null)
        {
            // Record attempt for non-existent user (for auditing)
            FailedLoginAttempt attempt = new FailedLoginAttempt
            {
                Identifier = identifier,
                IpAddress = ipAddress,
                AttemptedAt = DateTime.UtcNow,
                FailureReason = failureReason ?? "User not found"
            };
            _ = _context.FailedLoginAttempts.Add(attempt);
            _ = await _context.SaveChangesAsync();
            return;
        }

        // Increment failed attempts
        user.FailedLoginAttempts++;
        user.LastFailedLogin = DateTime.UtcNow;

        // Record the failed attempt in audit log
        FailedLoginAttempt failedAttempt = new FailedLoginAttempt
        {
            Identifier = identifier,
            IpAddress = ipAddress,
            AttemptedAt = DateTime.UtcNow,
            FailureReason = failureReason ?? "Invalid password"
        };
        _ = _context.FailedLoginAttempts.Add(failedAttempt);

        // Check if we should lock the account
        if (user.FailedLoginAttempts >= _maxFailedAttempts)
        {
            user.LockoutEnd = DateTime.UtcNow.AddMinutes(_lockoutDurationMinutes);

            // Audit log account lockout
            await _authAuditService.LogAccountLockedAsync(
                user.Id,
                user.FailedLoginAttempts,
                TimeSpan.FromMinutes(_lockoutDurationMinutes),
                ipAddress);
        }

        _ = await _context.SaveChangesAsync();
    }

    public async Task RecordFailedLoginByUsernameAsync(string username, string? ipAddress, string? failureReason = null)
    {
        User? user = await _context.Users.FirstOrDefaultAsync(u => u.Username == username);

        if (user != null)
        {
            await RecordFailedLoginAsync(user.Id, username, ipAddress, failureReason);
        }
        else
        {
            // Record attempt for non-existent username (for auditing)
            FailedLoginAttempt attempt = new FailedLoginAttempt
            {
                Identifier = username,
                IpAddress = ipAddress,
                AttemptedAt = DateTime.UtcNow,
                FailureReason = failureReason ?? "User not found"
            };
            _ = _context.FailedLoginAttempts.Add(attempt);
            _ = await _context.SaveChangesAsync();
        }
    }

    public async Task ResetFailedLoginCountAsync(Guid userId)
    {
        User? user = await _context.Users.FindAsync(userId);
        if (user != null)
        {
            user.FailedLoginAttempts = 0;
            user.LockoutEnd = null;
            user.LastFailedLogin = null;
            _ = await _context.SaveChangesAsync();
        }
    }

    public async Task<int> GetFailedLoginCountAsync(Guid userId)
    {
        User? user = await _context.Users.FindAsync(userId);
        return user?.FailedLoginAttempts ?? 0;
    }

    public async Task<DateTime?> GetLockoutEndAsync(Guid userId)
    {
        User? user = await _context.Users.FindAsync(userId);
        return user?.LockoutEnd;
    }

    public async Task ManuallyLockAccountAsync(Guid userId, int lockoutDurationMinutes)
    {
        User? user = await _context.Users.FindAsync(userId);
        if (user != null)
        {
            user.LockoutEnd = DateTime.UtcNow.AddMinutes(lockoutDurationMinutes);
            user.FailedLoginAttempts = _maxFailedAttempts; // Mark as max to indicate manual lockout
            _ = await _context.SaveChangesAsync();
        }
    }

    public async Task UnlockAccountAsync(Guid userId)
    {
        User? user = await _context.Users.FindAsync(userId);
        if (user != null)
        {
            user.LockoutEnd = null;
            user.FailedLoginAttempts = 0;
            user.LastFailedLogin = null;
            _ = await _context.SaveChangesAsync();
        }
    }
}

public interface IAccountLockoutService
{
    /// <summary>
    /// Check if an account is currently locked out
    /// </summary>
    Task<bool> IsLockedOutAsync(Guid userId);

    /// <summary>
    /// Record a failed login attempt and potentially lock the account
    /// </summary>
    Task RecordFailedLoginAsync(Guid userId, string identifier, string? ipAddress, string? failureReason = null);

    /// <summary>
    /// Record a failed login attempt by username (when user doesn't exist)
    /// </summary>
    Task RecordFailedLoginByUsernameAsync(string username, string? ipAddress, string? failureReason = null);

    /// <summary>
    /// Reset failed login count (called after successful login)
    /// </summary>
    Task ResetFailedLoginCountAsync(Guid userId);

    /// <summary>
    /// Get current failed login count for a user
    /// </summary>
    Task<int> GetFailedLoginCountAsync(Guid userId);

    /// <summary>
    /// Get the lockout end time for a user
    /// </summary>
    Task<DateTime?> GetLockoutEndAsync(Guid userId);

    /// <summary>
    /// Manually lock an account (admin action)
    /// </summary>
    Task ManuallyLockAccountAsync(Guid userId, int lockoutDurationMinutes);

    /// <summary>
    /// Unlock an account (admin action)
    /// </summary>
    Task UnlockAccountAsync(Guid userId);
}
