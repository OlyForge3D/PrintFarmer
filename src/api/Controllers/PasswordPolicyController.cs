using Farm.Web.Api.Data;
using Farm.Web.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Farm.Web.Api.Controllers;

[ApiController]
[Route("api/settings/security/password-policy")]
[Authorize(Roles = "farm_admin")]
public class PasswordPolicyController(AppDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PasswordPolicyDto>> GetAsync(CancellationToken ct)
    {
        var entity = await db.PasswordPolicies.OrderBy(p => p.Id).FirstOrDefaultAsync(ct);
        if (entity == null)
        {
            return Ok(new PasswordPolicyDto());
        }
        return Ok(new PasswordPolicyDto
        {
            MinLength = entity.MinLength,
            RequireUppercase = entity.RequireUppercase,
            RequireLowercase = entity.RequireLowercase,
            RequireDigit = entity.RequireDigit,
            RequireSymbol = entity.RequireSymbol
        });
    }

    [HttpPut]
    public async Task<ActionResult<PasswordPolicyDto>> UpdateAsync([FromBody] UpdatePasswordPolicyRequest request, CancellationToken ct)
    {
        if (request == null)
        {
            return BadRequest("Request body required");
        }
        var entity = await db.PasswordPolicies.OrderBy(p => p.Id).FirstOrDefaultAsync(ct);
        if (entity == null)
        {
            entity = new Domain.PasswordPolicy();
            db.PasswordPolicies.Add(entity);
        }

        if (request.MinLength.HasValue)
        {
            if (request.MinLength.Value < 6 || request.MinLength.Value > 256)
            {
                return BadRequest("MinLength must be between 6 and 256");
            }
            entity.MinLength = request.MinLength.Value;
        }
        if (request.RequireUppercase.HasValue)
        {
            entity.RequireUppercase = request.RequireUppercase.Value;
        }
        if (request.RequireLowercase.HasValue)
        {
            entity.RequireLowercase = request.RequireLowercase.Value;
        }
        if (request.RequireDigit.HasValue)
        {
            entity.RequireDigit = request.RequireDigit.Value;
        }
        if (request.RequireSymbol.HasValue)
        {
            entity.RequireSymbol = request.RequireSymbol.Value;
        }
        entity.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return await GetAsync(ct);
    }
}
