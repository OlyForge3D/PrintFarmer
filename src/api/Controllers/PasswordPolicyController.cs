using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Web.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Controllers;

[ApiController]
[Route("api/settings/security/password-policy")]
[Authorize(Roles = "farm_admin")]
public class PasswordPolicyController(Farm.Web.Api.Services.PasswordPolicy.IPasswordPolicyService svc) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PasswordPolicyDto>> GetAsync(CancellationToken ct)
    {
        var dto = await svc.GetAsync(ct);
        return Ok(dto);
    }

    [HttpPut]
    public async Task<ActionResult<PasswordPolicyDto>> UpdateAsync([FromBody] UpdatePasswordPolicyRequest request, CancellationToken ct)
    {
        if (request == null)
        {
            return BadRequest("Request body required");
        }
        // The service enforces validation and persists changes via repository
        var updated = await svc.UpdateAsync(request, ct);
        return Ok(updated);
    }
}
