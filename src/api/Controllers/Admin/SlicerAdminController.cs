using Farm.Web.Shared;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Controllers.Admin;

[ApiController]
[Route("api/admin/slicer")]
public partial class SlicerAdminController : ControllerBase
{
    [HttpPost("dryrun")]
    public ActionResult<DryRunResult> DryRun([FromBody] DryRunRequest request)
    {
        if (request == null)
        {
            return BadRequest("Empty request");
        }

        var template = request.Template ?? string.Empty;
        var engine = request.Engine;

        // Find placeholders
        var rx = MyRegex();
        var matches = rx.Matches(template);
        var placeholders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Text.RegularExpressions.Match m in matches)
        {
            if (m.Success && m.Groups.Count > 1)
            {
                placeholders.Add(m.Groups[1].Value);
            }
        }

        var result = new DryRunResult();

        // Known placeholders we support
        var known = new[] { "input", "output", "config", "profile" };

        // Prepare sample replacements
        var samples = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["input"] = "/tmp/model.stl",
            ["output"] = "/tmp/output.gcode",
            ["config"] = "/tmp/config.ini",
            ["profile"] = "default"
        };

        foreach (var ph in placeholders)
        {
            if (!known.Contains(ph, StringComparer.OrdinalIgnoreCase))
            {
                result.Warnings.Add($"Unknown placeholder '{{{ph}}}' — it will remain unexpanded.");
            }
        }

        // Basic validation rules
        if (!placeholders.Contains("input", StringComparer.OrdinalIgnoreCase))
        {
            result.Issues.Add("Template should include an {input} placeholder pointing to the model path.");
        }
        if (!placeholders.Contains("output", StringComparer.OrdinalIgnoreCase))
        {
            result.Warnings.Add("Template does not include an {output} placeholder — default output name will be used.");
        }

        // Do a safe render using sample values
        var rendered = rx.Replace(template, m =>
        {
            var key = m.Groups[1].Value;
            return samples.TryGetValue(key, out var val) ? val : m.Value;
        });

        // Safety checks on rendered args
        if (rendered.Contains("..") || rendered.Contains("~"))
        {
            result.Warnings.Add("Rendered args contain path traversal sequences (.. or ~). Ensure templates are safe and admin-provided paths are trusted.");
        }

        result.IsValid = result.Issues.Count == 0;
        result.Rendered = rendered;
        result.SamplePlaceholders = samples;
        return Ok(result);
    }

    [System.Text.RegularExpressions.GeneratedRegex("\\{([a-zA-Z0-9_]+)\\}")]
    private static partial System.Text.RegularExpressions.Regex MyRegex();
}

public class DryRunRequest
{
    public string? Template { get; set; }
    public SlicerEngineType Engine { get; set; } = SlicerEngineType.OrcaSlicer;
}

public class DryRunResult
{
    public bool IsValid { get; set; }
    public List<string> Issues { get; } = new();
    public List<string> Warnings { get; } = new();
    public string? Rendered { get; set; }
    public Dictionary<string, string> SamplePlaceholders { get; set; } = new();
}
