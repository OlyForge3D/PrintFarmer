using System.Text.RegularExpressions;
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

        string template = request.Template ?? string.Empty;
        SlicerEngineType engine = request.Engine;

        // Find placeholders
        Regex rx = MyRegex();
        MatchCollection matches = rx.Matches(template);
        HashSet<string> placeholders = new(StringComparer.OrdinalIgnoreCase);
        foreach (System.Text.RegularExpressions.Match m in matches)
        {
            if (m.Success && m.Groups.Count > 1)
            {
                placeholders.Add(m.Groups[1].Value);
            }
        }

        DryRunResult result = new();

        // Known placeholders we support
        string[] known = new[] { "input", "output", "config", "profile" };

        // Prepare sample replacements
        Dictionary<string, string> samples = new(StringComparer.OrdinalIgnoreCase)
        {
            ["input"] = "/tmp/model.stl",
            ["output"] = "/tmp/output.gcode",
            ["config"] = "/tmp/config.ini",
            ["profile"] = "default"
        };

        foreach (string ph in placeholders)
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
        string rendered = rx.Replace(template, m =>
        {
            string key = m.Groups[1].Value;
            return samples.TryGetValue(key, out string? val) ? val : m.Value;
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
