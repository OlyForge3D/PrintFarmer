using Farm.Infrastructure.Domain.Webhooks;
using Farm.Infrastructure.Repositories.Webhooks;
using Farm.Infrastructure.Services.Webhooks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Manage webhook subscriptions for receiving event notifications via HTTP POST
/// </summary>
[ApiController]
[Route("api/webhooks")]
[Tags("Webhooks")]
[Authorize(Roles = "farm_admin")]
public class WebhooksController(
    IWebhookRepository webhookRepository,
    IWebhookService webhookService,
    ILogger<WebhooksController> logger) : ControllerBase
{
    private readonly IWebhookRepository _webhookRepository = webhookRepository;
    private readonly IWebhookService _webhookService = webhookService;
    private readonly ILogger<WebhooksController> _logger = logger;

    /// <summary>
    /// List all webhook subscriptions
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(List<WebhookSubscriptionDto>), 200)]
    public async Task<IActionResult> GetAllAsync(CancellationToken ct)
    {
        var webhooks = await _webhookRepository.GetAllAsync(ct);
        return Ok(webhooks.Select(ToDto).ToList());
    }

    /// <summary>
    /// Get a single webhook subscription by ID
    /// </summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(WebhookSubscriptionDto), 200)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> GetByIdAsync(Guid id, CancellationToken ct)
    {
        var webhook = await _webhookRepository.GetByIdAsync(id, ct);
        if (webhook is null)
        {
            return NotFound(new { message = "Webhook not found" });
        }

        return Ok(ToDto(webhook));
    }

    /// <summary>
    /// Create a new webhook subscription
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(WebhookSubscriptionDto), 201)]
    [ProducesResponseType(400)]
    public async Task<IActionResult> CreateAsync([FromBody] CreateWebhookDto dto, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(dto.Name))
        {
            return BadRequest(new { message = "Name is required" });
        }

        if (string.IsNullOrWhiteSpace(dto.Url) || !Uri.TryCreate(dto.Url, UriKind.Absolute, out _))
        {
            return BadRequest(new { message = "A valid absolute URL is required" });
        }

        var webhook = new WebhookSubscription
        {
            Name = dto.Name.Trim(),
            Url = dto.Url.Trim(),
            Secret = dto.Secret?.Trim(),
            EventTypes = string.IsNullOrWhiteSpace(dto.EventTypes) ? "*" : dto.EventTypes.Trim(),
            IsActive = dto.IsActive ?? true,
            MaxConsecutiveFailures = dto.MaxConsecutiveFailures ?? 10
        };

        await _webhookRepository.AddAsync(webhook, ct);

        _logger.LogInformation("Webhook subscription created: {Id} → {Url}", webhook.Id, webhook.Url);
        return StatusCode(201, ToDto(webhook));
    }

    /// <summary>
    /// Update an existing webhook subscription
    /// </summary>
    [HttpPut("{id:guid}")]
    [ProducesResponseType(typeof(WebhookSubscriptionDto), 200)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> UpdateAsync(Guid id, [FromBody] UpdateWebhookDto dto, CancellationToken ct)
    {
        var webhook = await _webhookRepository.GetByIdAsync(id, ct);
        if (webhook is null)
        {
            return NotFound(new { message = "Webhook not found" });
        }

        if (dto.Name is not null)
        {
            webhook.Name = dto.Name.Trim();
        }

        if (dto.Url is not null)
        {
            if (!Uri.TryCreate(dto.Url, UriKind.Absolute, out _))
            {
                return BadRequest(new { message = "A valid absolute URL is required" });
            }

            webhook.Url = dto.Url.Trim();
        }

        if (dto.Secret is not null)
        {
            webhook.Secret = dto.Secret.Trim();
        }

        if (dto.EventTypes is not null)
        {
            webhook.EventTypes = dto.EventTypes.Trim();
        }

        if (dto.IsActive.HasValue)
        {
            webhook.IsActive = dto.IsActive.Value;
        }

        if (dto.MaxConsecutiveFailures.HasValue)
        {
            webhook.MaxConsecutiveFailures = dto.MaxConsecutiveFailures.Value;
        }

        // Reset failure counter if re-enabled
        if (dto.IsActive == true)
        {
            webhook.ConsecutiveFailures = 0;
        }

        await _webhookRepository.UpdateAsync(webhook, ct);
        return Ok(ToDto(webhook));
    }

    /// <summary>
    /// Delete a webhook subscription and all delivery logs
    /// </summary>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(204)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> DeleteAsync(Guid id, CancellationToken ct)
    {
        var webhook = await _webhookRepository.GetByIdAsync(id, ct);
        if (webhook is null)
        {
            return NotFound(new { message = "Webhook not found" });
        }

        await _webhookRepository.DeleteAsync(webhook, ct);

        _logger.LogInformation("Webhook subscription deleted: {Id}", id);
        return NoContent();
    }

    /// <summary>
    /// Send a test event to a webhook subscription
    /// </summary>
    [HttpPost("{id:guid}/test")]
    [ProducesResponseType(200)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> TestAsync(Guid id, CancellationToken ct)
    {
        var webhook = await _webhookRepository.GetByIdAsync(id, ct);
        if (webhook is null)
        {
            return NotFound(new { message = "Webhook not found" });
        }

        _webhookService.Enqueue("webhook.test", new
        {
            webhookId = webhook.Id,
            message = "This is a test webhook delivery from PrintFarmer",
            timestamp = DateTime.UtcNow
        });

        return Ok(new { message = "Test event enqueued" });
    }

    /// <summary>
    /// Get recent delivery logs for a webhook subscription
    /// </summary>
    [HttpGet("{id:guid}/deliveries")]
    [ProducesResponseType(typeof(List<WebhookDeliveryDto>), 200)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> GetDeliveriesAsync(
        Guid id,
        [FromQuery] int limit = 50,
        CancellationToken ct = default)
    {
        var exists = await _webhookRepository.ExistsAsync(id, ct);
        if (!exists)
        {
            return NotFound(new { message = "Webhook not found" });
        }

        var logs = await _webhookRepository.GetDeliveryLogsAsync(id, limit, ct);

        var result = logs.Select(d => new WebhookDeliveryDto
        {
            Id = d.Id,
            EventType = d.EventType,
            StatusCode = d.StatusCode,
            Success = d.Success,
            ErrorMessage = d.ErrorMessage,
            Attempt = d.Attempt,
            DurationMs = d.DurationMs,
            CreatedAt = d.CreatedAt
        }).ToList();

        return Ok(result);
    }

    /// <summary>
    /// List all supported webhook event types
    /// </summary>
    [HttpGet("event-types")]
    [Authorize]
    [ProducesResponseType(typeof(List<string>), 200)]
    public IActionResult GetEventTypes()
    {
        return Ok(WebhookEventTypes.All);
    }

    private static WebhookSubscriptionDto ToDto(WebhookSubscription w) => new()
    {
        Id = w.Id,
        Name = w.Name,
        Url = w.Url,
        HasSecret = !string.IsNullOrEmpty(w.Secret),
        EventTypes = w.EventTypes,
        IsActive = w.IsActive,
        ConsecutiveFailures = w.ConsecutiveFailures,
        MaxConsecutiveFailures = w.MaxConsecutiveFailures,
        CreatedAt = w.CreatedAt,
        LastDeliveryAt = w.LastDeliveryAt,
        LastSuccessAt = w.LastSuccessAt
    };
}

public record CreateWebhookDto
{
    public string Name { get; init; } = string.Empty;

    public string Url { get; init; } = string.Empty;

    public string? Secret { get; init; }

    public string? EventTypes { get; init; }

    public bool? IsActive { get; init; }

    public int? MaxConsecutiveFailures { get; init; }
}

public record UpdateWebhookDto
{
    public string? Name { get; init; }

    public string? Url { get; init; }

    public string? Secret { get; init; }

    public string? EventTypes { get; init; }

    public bool? IsActive { get; init; }

    public int? MaxConsecutiveFailures { get; init; }
}

public record WebhookSubscriptionDto
{
    public Guid Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public string Url { get; init; } = string.Empty;

    public bool HasSecret { get; init; }

    public string EventTypes { get; init; } = string.Empty;

    public bool IsActive { get; init; }

    public int ConsecutiveFailures { get; init; }

    public int MaxConsecutiveFailures { get; init; }

    public DateTime CreatedAt { get; init; }

    public DateTime? LastDeliveryAt { get; init; }

    public DateTime? LastSuccessAt { get; init; }
}

public record WebhookDeliveryDto
{
    public Guid Id { get; init; }

    public string EventType { get; init; } = string.Empty;

    public int? StatusCode { get; init; }

    public bool Success { get; init; }

    public string? ErrorMessage { get; init; }

    public int Attempt { get; init; }

    public long? DurationMs { get; init; }

    public DateTime CreatedAt { get; init; }
}

/// <summary>
/// All supported webhook event types
/// </summary>
public static class WebhookEventTypes
{
    // Job events
    public const string JobQueued = "job.queued";
    public const string JobStarted = "job.started";
    public const string JobCompleted = "job.completed";
    public const string JobFailed = "job.failed";
    public const string JobPaused = "job.paused";
    public const string JobResumed = "job.resumed";
    public const string JobCancelled = "job.cancelled";

    // Printer events
    public const string PrinterOnline = "printer.online";
    public const string PrinterOffline = "printer.offline";
    public const string PrinterStatusChanged = "printer.status_changed";

    // Maintenance events
    public const string MaintenanceDue = "maintenance.due";
    public const string MaintenanceCompleted = "maintenance.completed";

    // Discovery events
    public const string DiscoveryPrinterFound = "discovery.printer_found";
    public const string DiscoveryCompleted = "discovery.completed";

    // System events
    public const string WebhookTest = "webhook.test";

    public static readonly string[] All =
    [
        JobQueued, JobStarted, JobCompleted, JobFailed, JobPaused, JobResumed, JobCancelled,
        PrinterOnline, PrinterOffline, PrinterStatusChanged,
        MaintenanceDue, MaintenanceCompleted,
        DiscoveryPrinterFound, DiscoveryCompleted,
        WebhookTest
    ];
}
