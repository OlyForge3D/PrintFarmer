using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Web.Api.Services.Artifacts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ArtifactsController : ControllerBase
{
    private readonly IArtifactsService _service;

    public ArtifactsController(IArtifactsService service)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
    }

    /// <summary>
    /// Upload a new artifact (multipart/form-data). Fields: jobId (Guid), kind (string), workerId (Guid optional), file (IFormFile)
    /// </summary>
    [HttpPost]
    [RequestSizeLimit(110_000_000)] // Slightly above default max to match settings guard
    public async Task<IActionResult> UploadAsync([FromForm] Guid jobId, [FromForm] string kind, [FromForm] Guid? workerId, [FromForm] IFormFile file, CancellationToken ct)
    {
        if (file == null) return BadRequest("file is required");
        try
        {
            var artifact = await _service.UploadAsync(file, jobId, workerId, kind, ct);
            return Ok(new
            {
                artifact.Id,
                artifact.JobId,
                artifact.WorkerId,
                artifact.Kind,
                artifact.FileName,
                artifact.SizeBytes,
                artifact.Sha256,
                artifact.CreatedAt,
                url = $"/artifacts/{artifact.RelativePath}" // Consumer can GET this static path if served
            });
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("{id:guid}")] 
    public async Task<IActionResult> GetAsync(Guid id, CancellationToken ct)
    {
        var a = await _service.GetAsync(id, ct);
        return a == null ? NotFound() : Ok(a);
    }

    [HttpGet("job/{jobId:guid}")] 
    public async Task<IActionResult> ListByJobAsync(Guid jobId, CancellationToken ct)
    {
        var list = await _service.ListByJobAsync(jobId, ct);
        return Ok(list.Select(a => new { a.Id, a.Kind, a.FileName, a.SizeBytes, a.CreatedAt }));
    }
}
