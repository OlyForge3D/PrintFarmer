using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Farm.Infrastructure.Authorization;
using Farm.Infrastructure.Services.Gcode.PrinterFiles;
using Farm.Infrastructure.Services.Printers;
using Farm.Modules.Gcode.Services.Gcode;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace Farm.Modules.Gcode.Controllers;

/// <summary>Opt-in content reads, separate from catalog enumeration and durable Copy Local.</summary>
[ApiController]
[Route("api/printer-files/locations")]
[Authorize(Policy = InteractiveSessionRequirement.PolicyName)]
[Authorize(Policy = "ModelRead")]
public sealed class PrinterFileContentController(PrinterFileContentService content) : ControllerBase
{
    /// <summary>Returns persisted metadata and enrichment status; never contacts the printer.</summary>
    [HttpGet("{id:guid}/preview")]
    [ProducesResponseType<PrinterFilePreviewDto>(StatusCodes.Status200OK)]
    public Task<IActionResult> PreviewAsync(Guid id, [FromQuery] long observationRevision, CancellationToken ct) =>
        ExecuteAsync(async actor => Ok(await content.PreviewAsync(actor, id, observationRevision, ct)));

    /// <summary>
    /// Serves only a locally stored, currently authorized image. An optional <paramref name="version"/>
    /// pins the exact artifact; a mismatch is reported as unavailable rather than serving other bytes.
    /// </summary>
    [HttpGet("{id:guid}/thumbnail")]
    public Task<IActionResult> ThumbnailAsync(
        Guid id, [FromQuery] long observationRevision, [FromQuery] Guid? version, CancellationToken ct) =>
        ExecuteAsync(async actor =>
        {
            PrinterFilePreviewContent image = await content.ThumbnailAsync(actor, id, observationRevision, version, ct);
            string hash = Convert.ToHexStringLower(SHA256.HashData(image.Image!));
            return File(image.Image!, image.ContentType!, lastModified: null, new EntityTagHeaderValue($"\"{hash}\""));
        });

    /// <summary>Downloads to the browser without importing or creating any queue operation.</summary>
    [HttpGet("{id:guid}/download")]
    [Authorize(Policy = "ModelWrite")]
    public Task<IActionResult> DownloadAsync(Guid id, [FromQuery] long observationRevision, CancellationToken ct) =>
        ExecuteAsync(async actor =>
        {
            await content.DownloadAsync(actor, id, observationRevision, name =>
            {
                ContentDispositionHeaderValue disposition = new("attachment");
                disposition.SetHttpFileName(name);
                Response.Headers.ContentDisposition = disposition.ToString();
                Response.ContentType = "application/octet-stream";

                // No Content-Length: postflight failures must abort, not look like a completed download.
                return Response.Body;
            }, ct);
            return new EmptyResult();
        });

    private async Task<IActionResult> ExecuteAsync(Func<Guid, Task<IActionResult>> action)
    {
        Response.Headers.CacheControl = "private, no-store";
        Response.Headers.XContentTypeOptions = "nosniff";
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out Guid actor) || actor == Guid.Empty)
        {
            return Forbid();
        }

        try
        {
            return await action(actor);
        }
        catch (PrinterFileCommandException error)
        {
            return Failure(error.StatusCode, error.Reason);
        }
        catch (PrinterFileCopyException error)
        {
            return Failure(error.Reason == "size_limit" ? 413 : 409, error.Reason);
        }
        catch (OperationCanceledException)
        {
            return Failure(409, "transfer_interrupted");
        }
        catch (Exception error) when (error is IOException or HttpRequestException or JsonException or
            KeyNotFoundException or InvalidOperationException)
        {
            // Never return printer URLs, credentials, response bodies or exception messages.
            return Failure(503, "content_unavailable");
        }
    }

    private IActionResult Failure(int statusCode, string code)
    {
        if (Response.HasStarted)
        {
            HttpContext.Abort();
            return new EmptyResult();
        }

        Response.Headers.Remove(HeaderNames.ContentDisposition);
        Response.ContentType = null;
        ProblemDetails problem = new() { Status = statusCode, Title = code };
        problem.Extensions["code"] = code;
        problem.Extensions["reasonCode"] = code;
        return StatusCode(statusCode, problem);
    }
}
