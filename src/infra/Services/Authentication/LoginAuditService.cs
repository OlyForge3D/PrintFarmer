using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.Authentication;

/// <summary>
/// Persists login attempts to <see cref="LoginAuditEntry"/>. Fire-and-forget safe:
/// callers should not let an audit failure propagate to the user.
/// </summary>
public class LoginAuditService(AppDbContext db, ILogger<LoginAuditService> logger) : ILoginAuditService
{
    private readonly AppDbContext _db = db;
    private readonly ILogger<LoginAuditService> _logger = logger;

    /// <inheritdoc/>
    public async Task RecordAsync(
        string? username,
        bool success,
        string ipAddress,
        string? userAgent,
        string? failureReason,
        CancellationToken cancellationToken = default)
    {
        try
        {
            LoginAuditEntry entry = new()
            {
                Id = Guid.NewGuid(),
                Timestamp = DateTimeOffset.UtcNow,
                Username = username is { Length: > 256 } ? username[..256] : username,
                Success = success,
                IpAddress = ipAddress.Length > 64 ? ipAddress[..64] : ipAddress,
                UserAgent = userAgent is { Length: > 512 } ? userAgent[..512] : userAgent,
                FailureReason = failureReason,
            };

            _db.LoginAuditEntries.Add(entry);
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            // Audit failures must never surface to the caller — log and swallow
            _logger.LogError(ex, "Failed to write login audit entry for {IpAddress}", ipAddress);
        }
    }
}
