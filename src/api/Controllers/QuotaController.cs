using System.ComponentModel.DataAnnotations;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.PrintQuotas;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Manages print quotas and user balances for cost/count/weight limiting.
/// </summary>
[ApiController]
[Route("api/quotas")]
[Tags("Quotas")]
[Authorize]
public class QuotaController(IPrintQuotaService quotaService) : ControllerBase
{
    // ── Quota CRUD ──────────────────────────────────────────────────────

    /// <summary>Returns all quotas (admin view).</summary>
    [Authorize(Roles = "farm_admin")]
    [HttpGet]
    [ProducesResponseType(typeof(QuotaDto[]), 200)]
    public async Task<ActionResult<QuotaDto[]>> GetAllQuotasAsync(CancellationToken ct)
    {
        PrintQuota[] quotas = await quotaService.GetAllQuotasAsync(ct);
        return Ok(quotas.Select(MapToDto).ToArray());
    }

    /// <summary>Returns quotas for a specific user.</summary>
    [Authorize(Roles = "farm_admin")]
    [HttpGet("user/{userId:guid}")]
    [ProducesResponseType(typeof(QuotaDto[]), 200)]
    public async Task<ActionResult<QuotaDto[]>> GetQuotasForUserAsync(Guid userId, CancellationToken ct)
    {
        PrintQuota[] quotas = await quotaService.GetQuotasForUserAsync(userId, ct);
        return Ok(quotas.Select(MapToDto).ToArray());
    }

    /// <summary>Returns quotas for a named group.</summary>
    [Authorize(Roles = "farm_admin")]
    [HttpGet("group/{groupName}")]
    [ProducesResponseType(typeof(QuotaDto[]), 200)]
    public async Task<ActionResult<QuotaDto[]>> GetQuotasForGroupAsync(string groupName, CancellationToken ct)
    {
        PrintQuota[] quotas = await quotaService.GetQuotasForGroupAsync(groupName, ct);
        return Ok(quotas.Select(MapToDto).ToArray());
    }

    /// <summary>Gets a single quota by ID.</summary>
    [Authorize(Roles = "farm_admin")]
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(QuotaDto), 200)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<QuotaDto>> GetQuotaAsync(Guid id, CancellationToken ct)
    {
        PrintQuota? quota = await quotaService.GetQuotaByIdAsync(id, ct);
        return quota is null ? NotFound(new { message = "Quota not found" }) : Ok(MapToDto(quota));
    }

    /// <summary>Creates a new quota.</summary>
    [Authorize(Roles = "farm_admin")]
    [HttpPost]
    [ProducesResponseType(typeof(QuotaDto), 201)]
    [ProducesResponseType(400)]
    public async Task<ActionResult<QuotaDto>> CreateQuotaAsync(CreateQuotaRequest request, CancellationToken ct)
    {
        if (request.UserId is null && string.IsNullOrWhiteSpace(request.GroupName))
        {
            return BadRequest(new { message = "Either userId or groupName is required" });
        }

        if (request.UserId is not null && !string.IsNullOrWhiteSpace(request.GroupName))
        {
            return BadRequest(new { message = "Specify either userId or groupName, not both" });
        }

        PrintQuota quota = new()
        {
            UserId = request.UserId,
            GroupName = request.GroupName?.Trim(),
            QuotaType = request.QuotaType,
            LimitAmount = request.LimitAmount,
            PeriodType = request.PeriodType,
            IsActive = request.IsActive ?? true,
            Notes = request.Notes?.Trim()
        };

        PrintQuota created = await quotaService.CreateQuotaAsync(quota, ct);
        return StatusCode(201, MapToDto(created));
    }

    /// <summary>Updates an existing quota.</summary>
    [Authorize(Roles = "farm_admin")]
    [HttpPut("{id:guid}")]
    [ProducesResponseType(typeof(QuotaDto), 200)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<QuotaDto>> UpdateQuotaAsync(Guid id, UpdateQuotaRequest request, CancellationToken ct)
    {
        PrintQuota? updated = await quotaService.UpdateQuotaAsync(id, request.LimitAmount, request.PeriodType, request.IsActive, request.Notes, ct);
        return updated is null ? NotFound(new { message = "Quota not found" }) : Ok(MapToDto(updated));
    }

    /// <summary>Deletes a quota.</summary>
    [Authorize(Roles = "farm_admin")]
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(204)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> DeleteQuotaAsync(Guid id, CancellationToken ct)
    {
        bool deleted = await quotaService.DeleteQuotaAsync(id, ct);
        return deleted ? NoContent() : NotFound(new { message = "Quota not found" });
    }

    /// <summary>Checks if a user can submit a job (pre-dispatch check).</summary>
    [Authorize(Roles = "farm_admin")]
    [HttpPost("check")]
    [ProducesResponseType(typeof(QuotaCheckResult), 200)]
    public async Task<ActionResult<QuotaCheckResult>> CheckQuotaAsync(CheckQuotaRequest request, CancellationToken ct)
    {
        QuotaCheckResult result = await quotaService.CheckQuotaAsync(
            request.UserId, request.EstimatedCost, request.JobCount, request.EstimatedWeightGrams, ct);
        return Ok(result);
    }

    /// <summary>Resets all expired quotas (admin or background job).</summary>
    [Authorize(Roles = "farm_admin")]
    [HttpPost("reset-expired")]
    [ProducesResponseType(typeof(object), 200)]
    public async Task<IActionResult> ResetExpiredQuotasAsync(CancellationToken ct)
    {
        int count = await quotaService.ResetExpiredQuotasAsync(ct);
        return Ok(new { resetCount = count });
    }

    // ── Balance ─────────────────────────────────────────────────────────

    /// <summary>Gets a user's balance.</summary>
    [Authorize(Roles = "farm_admin")]
    [HttpGet("balance/{userId:guid}")]
    [ProducesResponseType(typeof(UserBalanceDto), 200)]
    public async Task<ActionResult<UserBalanceDto>> GetBalanceAsync(Guid userId, CancellationToken ct)
    {
        UserBalance balance = await quotaService.GetOrCreateBalanceAsync(userId, ct);
        return Ok(MapBalanceToDto(balance));
    }

    /// <summary>Credits a user's balance (admin).</summary>
    [Authorize(Roles = "farm_admin")]
    [HttpPost("balance/{userId:guid}/credit")]
    [ProducesResponseType(typeof(UserBalanceDto), 200)]
    [ProducesResponseType(400)]
    public async Task<ActionResult<UserBalanceDto>> CreditBalanceAsync(Guid userId, BalanceAdjustRequest request, CancellationToken ct)
    {
        if (request.Amount <= 0)
        {
            return BadRequest(new { message = "Amount must be positive" });
        }

        string performedBy = User.Identity?.Name ?? "admin";
        UserBalance balance = await quotaService.CreditBalanceAsync(userId, request.Amount, request.Description ?? "Manual credit", performedBy, ct);
        return Ok(MapBalanceToDto(balance));
    }

    /// <summary>Debits a user's balance (admin).</summary>
    [Authorize(Roles = "farm_admin")]
    [HttpPost("balance/{userId:guid}/debit")]
    [ProducesResponseType(typeof(UserBalanceDto), 200)]
    [ProducesResponseType(400)]
    public async Task<ActionResult<UserBalanceDto>> DebitBalanceAsync(Guid userId, BalanceAdjustRequest request, CancellationToken ct)
    {
        if (request.Amount <= 0)
        {
            return BadRequest(new { message = "Amount must be positive" });
        }

        string performedBy = User.Identity?.Name ?? "admin";
        try
        {
            UserBalance balance = await quotaService.DebitBalanceAsync(userId, request.Amount, request.Description ?? "Manual debit", performedBy, ct);
            return Ok(MapBalanceToDto(balance));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>Gets balance transaction history for a user.</summary>
    [Authorize(Roles = "farm_admin")]
    [HttpGet("balance/{userId:guid}/transactions")]
    [ProducesResponseType(typeof(BalanceTransactionDto[]), 200)]
    public async Task<ActionResult<BalanceTransactionDto[]>> GetTransactionsAsync(Guid userId, [FromQuery] int take = 50, CancellationToken ct = default)
    {
        BalanceTransaction[] txns = await quotaService.GetTransactionHistoryAsync(userId, take, ct);
        return Ok(txns.Select(MapTransactionToDto).ToArray());
    }

    // ── Group membership ────────────────────────────────────────────────

    /// <summary>Lists the quota groups a user belongs to.</summary>
    [Authorize(Roles = "farm_admin")]
    [HttpGet("user/{userId:guid}/groups")]
    [ProducesResponseType(typeof(string[]), 200)]
    public async Task<ActionResult<string[]>> GetUserGroupsAsync(Guid userId, CancellationToken ct)
    {
        string[] groups = await quotaService.GetUserGroupsAsync(userId, ct);
        return Ok(groups);
    }

    /// <summary>Adds a user to a quota group.</summary>
    [Authorize(Roles = "farm_admin")]
    [HttpPost("user/{userId:guid}/groups")]
    [ProducesResponseType(typeof(GroupMembershipDto), 201)]
    [ProducesResponseType(400)]
    public async Task<ActionResult<GroupMembershipDto>> AddUserToGroupAsync(Guid userId, AddUserToGroupRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.GroupName))
        {
            return BadRequest(new { message = "groupName is required" });
        }

        try
        {
            UserQuotaGroupMembership membership = await quotaService.AddUserToGroupAsync(userId, request.GroupName, ct);
            return StatusCode(201, MapMembershipToDto(membership));
        }
        catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase) == true
            || ex.InnerException?.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase) == true
            || ex.InnerException?.Message.Contains("IX_UserQuotaGroupMemberships", StringComparison.OrdinalIgnoreCase) == true)
        {
            return BadRequest(new { message = $"User is already a member of group '{request.GroupName}'" });
        }
    }

    /// <summary>Removes a user from a quota group.</summary>
    [Authorize(Roles = "farm_admin")]
    [HttpDelete("user/{userId:guid}/groups/{groupName}")]
    [ProducesResponseType(204)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> RemoveUserFromGroupAsync(Guid userId, string groupName, CancellationToken ct)
    {
        bool removed = await quotaService.RemoveUserFromGroupAsync(userId, groupName, ct);
        return removed ? NoContent() : NotFound(new { message = "Membership not found" });
    }

    /// <summary>Lists all members of a quota group.</summary>
    [Authorize(Roles = "farm_admin")]
    [HttpGet("group/{groupName}/members")]
    [ProducesResponseType(typeof(GroupMembershipDto[]), 200)]
    public async Task<ActionResult<GroupMembershipDto[]>> GetGroupMembersAsync(string groupName, CancellationToken ct)
    {
        UserQuotaGroupMembership[] members = await quotaService.GetGroupMembersAsync(groupName, ct);
        return Ok(members.Select(MapMembershipToDto).ToArray());
    }

    // ── Mapping ─────────────────────────────────────────────────────────
    private static QuotaDto MapToDto(PrintQuota q) => new()
    {
        Id = q.Id,
        UserId = q.UserId,
        UserName = q.User?.Username,
        GroupName = q.GroupName,
        QuotaType = q.QuotaType,
        LimitAmount = q.LimitAmount,
        UsedAmount = q.UsedAmount,
        RemainingAmount = Math.Max(0, q.LimitAmount - q.UsedAmount),
        PeriodType = q.PeriodType,
        PeriodStart = q.PeriodStart,
        ResetAt = q.ResetAt,
        IsActive = q.IsActive,
        Notes = q.Notes,
        CreatedAt = q.CreatedAt,
        UpdatedAt = q.UpdatedAt
    };

    private static UserBalanceDto MapBalanceToDto(UserBalance b) => new()
    {
        Id = b.Id,
        UserId = b.UserId,
        BalanceAmount = b.BalanceAmount,
        Currency = b.Currency,
        LastUpdated = b.LastUpdated
    };

    private static BalanceTransactionDto MapTransactionToDto(BalanceTransaction t) => new()
    {
        Id = t.Id,
        Amount = t.Amount,
        TransactionType = t.TransactionType,
        PrintJobId = t.PrintJobId,
        Description = t.Description,
        PerformedBy = t.PerformedBy,
        CreatedAt = t.CreatedAt
    };

    private static GroupMembershipDto MapMembershipToDto(UserQuotaGroupMembership m) => new()
    {
        Id = m.Id,
        UserId = m.UserId,
        UserName = m.User?.Username,
        GroupName = m.GroupName,
        CreatedAt = m.CreatedAt
    };
}

// ── Request / Response DTOs ─────────────────────────────────────────────
public sealed class CreateQuotaRequest
{
    public Guid? UserId { get; set; }

    public string? GroupName { get; set; }

    [Required] public QuotaType QuotaType { get; set; }

    [Required][Range(0.01, double.MaxValue)] public decimal LimitAmount { get; set; }

    [Required] public QuotaPeriodType PeriodType { get; set; }

    public bool? IsActive { get; set; }

    [MaxLength(500)] public string? Notes { get; set; }
}

public sealed class UpdateQuotaRequest
{
    [Range(0.01, double.MaxValue)] public decimal? LimitAmount { get; set; }

    public QuotaPeriodType? PeriodType { get; set; }

    public bool? IsActive { get; set; }

    [MaxLength(500)] public string? Notes { get; set; }
}

public sealed class CheckQuotaRequest
{
    [Required] public Guid UserId { get; set; }

    public decimal EstimatedCost { get; set; }

    public int JobCount { get; set; } = 1;

    public double EstimatedWeightGrams { get; set; }
}

public sealed class BalanceAdjustRequest
{
    [Required][Range(0.01, double.MaxValue)] public decimal Amount { get; set; }

    [MaxLength(500)] public string? Description { get; set; }
}

public sealed class QuotaDto
{
    public Guid Id { get; set; }

    public Guid? UserId { get; set; }

    public string? UserName { get; set; }

    public string? GroupName { get; set; }

    public QuotaType QuotaType { get; set; }

    public decimal LimitAmount { get; set; }

    public decimal UsedAmount { get; set; }

    public decimal RemainingAmount { get; set; }

    public QuotaPeriodType PeriodType { get; set; }

    public DateTime PeriodStart { get; set; }

    public DateTime? ResetAt { get; set; }

    public bool IsActive { get; set; }

    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}

public sealed class UserBalanceDto
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public decimal BalanceAmount { get; set; }

    public string Currency { get; set; } = "USD";

    public DateTime LastUpdated { get; set; }
}

public sealed class BalanceTransactionDto
{
    public Guid Id { get; set; }

    public decimal Amount { get; set; }

    public BalanceTransactionType TransactionType { get; set; }

    public Guid? PrintJobId { get; set; }

    public string? Description { get; set; }

    public string? PerformedBy { get; set; }

    public DateTime CreatedAt { get; set; }
}

public sealed class AddUserToGroupRequest
{
    [Required]
    [MaxLength(200)]
    public string GroupName { get; set; } = string.Empty;
}

public sealed class GroupMembershipDto
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public string? UserName { get; set; }

    public string GroupName { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }
}
