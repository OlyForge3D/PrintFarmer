using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Farm.Web.Api.Controllers.Admin;

/// <summary>
/// Admin-only endpoint for querying the login audit log.
/// Returns paginated, filterable login attempt records for security review.
/// </summary>
[ApiController]
[Route("api/admin/security")]
[Authorize(Roles = "farm_admin")]
[Tags("Admin - Security")]
public class SecurityAuditController(AppDbContext db) : ControllerBase
{
    private readonly AppDbContext _db = db;

    /// <summary>
    /// Query login attempts with optional filtering by date range, username, and outcome.
    /// Results are ordered newest-first. Default pageSize=50, max=200.
    /// </summary>
    [HttpGet("login-audit")]
    [ProducesResponseType(typeof(LoginAuditPageDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<LoginAuditPageDto>> GetLoginAuditAsync(
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromQuery] string? username,
        [FromQuery] bool? success,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);

        IQueryable<LoginAuditEntry> query = _db.LoginAuditEntries.AsNoTracking();

        if (from.HasValue)
        {
            query = query.Where(e => e.Timestamp >= from.Value);
        }

        if (to.HasValue)
        {
            query = query.Where(e => e.Timestamp <= to.Value);
        }

        if (!string.IsNullOrWhiteSpace(username))
        {
            query = query.Where(e => e.Username != null && e.Username.Contains(username));
        }

        if (success.HasValue)
        {
            query = query.Where(e => e.Success == success.Value);
        }

        int totalCount = await query.CountAsync(ct);

        List<LoginAuditItemDto> items = await query
            .OrderByDescending(e => e.Timestamp)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(e => new LoginAuditItemDto(
                e.Id,
                e.Timestamp,
                e.Username,
                e.Success,
                e.IpAddress,
                e.UserAgent,
                e.FailureReason))
            .ToListAsync(ct);

        return Ok(new LoginAuditPageDto
        {
            Items = items,
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize,
        });
    }
}

/// <summary>Single login audit entry returned to the admin UI.</summary>
/// <param name="Id">Unique entry ID.</param>
/// <param name="Timestamp">UTC timestamp of the attempt.</param>
/// <param name="Username">Username as submitted; null if not provided.</param>
/// <param name="Success">Whether the login succeeded.</param>
/// <param name="IpAddress">Client IP address.</param>
/// <param name="UserAgent">User-Agent string, if present.</param>
/// <param name="FailureReason">Normalized failure code; null on success.</param>
public record LoginAuditItemDto(
    Guid Id,
    DateTimeOffset Timestamp,
    string? Username,
    bool Success,
    string IpAddress,
    string? UserAgent,
    string? FailureReason);

/// <summary>Paginated response wrapper for login audit queries.</summary>
public class LoginAuditPageDto
{
    /// <summary>Current page of entries, ordered newest-first.</summary>
    public List<LoginAuditItemDto> Items { get; set; } = [];

    /// <summary>Total entries matching the filter (before pagination).</summary>
    public int TotalCount { get; set; }

    /// <summary>Current page number (1-based).</summary>
    public int Page { get; set; }

    /// <summary>Number of items per page.</summary>
    public int PageSize { get; set; }
}
