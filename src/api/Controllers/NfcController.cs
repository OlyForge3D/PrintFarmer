using Farm.Infrastructure;
using Farm.Infrastructure.Services.NfcDevices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// NFC tag binding endpoints — maps tag UIDs to spools and printer/tray contexts.
/// </summary>
[ApiController]
[Route("api/nfc")]
[Tags("NFC")]
public class NfcController(INfcTagService nfcTagService) : ControllerBase
{
    /// <summary>
    /// Binds an NFC tag UID to a spool and optional printer/tray context.
    /// Creates a new binding or updates an existing one for the same tag UID.
    /// </summary>
    /// <remarks>
    /// Payload: { tagUid, spoolId, spoolName, printerId, trayId, readAt }
    /// </remarks>
    [Authorize]
    [HttpPost("link")]
    [ProducesResponseType(typeof(NfcTagBindingDto), 200)]
    [ProducesResponseType(400)]
    public async Task<ActionResult<NfcTagBindingDto>> LinkAsync(
        [FromBody] LinkNfcTagRequest request,
        CancellationToken ct)
    {
        var result = await nfcTagService.LinkTagAsync(request, ct);
        return Ok(result);
    }
}
