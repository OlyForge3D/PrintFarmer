using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using Farm.Slicer.Module.Contracts;
using Farm.Slicer.Module.Data;
using Farm.Slicer.Module.Domain;
using Farm.Slicer.Module.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Farm.Slicer.Module.Api.Controllers.Admin;

/// <summary>
/// Administrative endpoints for slicer template validation and diagnostics.
/// </summary>
[ApiController]
[Route("api/admin/slicer")]
public partial class SlicerAdminController(SlicerDbContext db) : ControllerBase
{
    private readonly SlicerDbContext _db = db;
    private static readonly ReadOnlyDictionary<string, string> SamplePlaceholders = new(new Dictionary<string, string>
    {
        ["{filename}"] = "test_model",
        ["{extension}"] = ".gcode",
        ["{printer_name}"] = "Prusa MK4",
        ["{date}"] = DateTime.UtcNow.ToString("yyyy-MM-dd"),
        ["{time}"] = DateTime.UtcNow.ToString("HH-mm-ss"),
        ["{material}"] = "PLA",
        ["{layer_height}"] = "0.2mm",
    });

    /// <summary>
    /// Validates a slicer output filename template without actually slicing.
    /// </summary>
    /// <param name="request">The dry run request.</param>
    /// <returns>Validation result with rendered output and any issues.</returns>
    [HttpPost("dry-run")]
    public IActionResult DryRun([FromBody] DryRunRequest request)
    {
        var result = new DryRunResult
        {
            SamplePlaceholders = new Dictionary<string, string>(SamplePlaceholders),
        };

        if (string.IsNullOrWhiteSpace(request.Template))
        {
            result.AddIssue("Template is required.");
            return Ok(result);
        }

        // Validate no path traversal
        if (request.Template.Contains("..") || request.Template.Contains('/') || request.Template.Contains('\\'))
        {
            result.AddIssue("Template must not contain path separators or '..'.");
        }

        // Check for unknown placeholders
        var knownKeys = SamplePlaceholders.Keys.ToHashSet();
        foreach (Match match in PlaceholderPattern().Matches(request.Template))
        {
            if (!knownKeys.Contains(match.Value))
            {
                result.AddWarning($"Unknown placeholder: {match.Value}");
            }
        }

        // Render
        string rendered = request.Template;
        foreach (var (key, value) in SamplePlaceholders)
        {
            rendered = rendered.Replace(key, value, StringComparison.OrdinalIgnoreCase);
        }

        result.Rendered = rendered;
        result.IsValid = result.Issues.Count == 0;

        return Ok(result);
    }

    /// <summary>
    /// Gets global slicer settings.
    /// </summary>
    [HttpGet("settings")]
    [Authorize(Roles = "farm_admin")]
    public async Task<IActionResult> GetSettingsAsync(CancellationToken ct)
    {
        SlicerSettings? settings = await _db.SlicerSettings.FirstOrDefaultAsync(s => s.Id == 1, ct);
        if (settings is null)
        {
            settings = new SlicerSettings { Id = 1 };
            _db.SlicerSettings.Add(settings);
            await _db.SaveChangesAsync(ct);
        }

        return Ok(new SlicerSettingsDto
        {
            Enabled = settings.Enabled,
            JitterPercent = settings.JitterPercent,
            PerEngineJson = settings.PerEngineJson,
            UpdatedAt = settings.UpdatedAt,
        });
    }

    /// <summary>
    /// Updates global slicer settings.
    /// </summary>
    /// <param name="request">Updated settings values.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPut("settings")]
    [Authorize(Roles = "farm_admin")]
    public async Task<IActionResult> UpdateSettingsAsync([FromBody] UpdateSlicerSettingsRequest request, CancellationToken ct)
    {
        SlicerSettings? settings = await _db.SlicerSettings.FirstOrDefaultAsync(s => s.Id == 1, ct);
        if (settings is null)
        {
            settings = new SlicerSettings { Id = 1 };
            _db.SlicerSettings.Add(settings);
        }

        settings.Enabled = request.Enabled;
        settings.JitterPercent = request.JitterPercent;
        settings.PerEngineJson = request.PerEngineJson;
        settings.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);

        return Ok(new SlicerSettingsDto
        {
            Enabled = settings.Enabled,
            JitterPercent = settings.JitterPercent,
            PerEngineJson = settings.PerEngineJson,
            UpdatedAt = settings.UpdatedAt,
        });
    }

    [GeneratedRegex(@"\{[a-z_]+\}")]
    private static partial Regex PlaceholderPattern();
}
