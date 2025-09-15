using System.Text.RegularExpressions;

namespace Farm.Web.Api.Services.SlicerServices.Progress;

public partial class PrusaProgressParser : IProgressParser
{
    // Examples parsed:
    // "Progress: 45%"
    // "Layer 10/100" => percentage = 10/100
    private static readonly Regex PercentRegex = MyRegex();
    private static readonly Regex LayerRegex = new(@"(?i)layer\s+(?<idx>\d+)\s*/\s*(?<total>\d+)", RegexOptions.Compiled);

    private static readonly (int Start, int End, string Message)[] _phases =
    [
        (0, 20, "Initializing slicer"),
        (20, 45, "Loading model"),
        (45, 70, "Generating toolpaths"),
        (70, 90, "Calculating time & writes"),
        (90, 100, "Finalizing G-code")
    ];

    private int _phaseIdx;

    public SlicerProgress? ParseLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        var lower = line.ToLowerInvariant();

        if (lower.Contains('%'))
        {
            var digits = string.Concat(line.Where(char.IsDigit));
            if (!string.IsNullOrEmpty(digits) && int.TryParse(digits, out var p))
            {
                var clamped = Math.Max(0, Math.Min(100, p));
                return new SlicerProgress(clamped, line);
            }
        }

        if (lower.Contains("loading") || lower.Contains("load"))
        {
            _phaseIdx = Math.Max(_phaseIdx, 1);
        }

        if (lower.Contains("analyzing") || lower.Contains("toolpath") || lower.Contains("toolpaths"))
        {
            _phaseIdx = Math.Max(_phaseIdx, 2);
        }

        if (lower.Contains("writing") || lower.Contains("writing g-code") || lower.Contains("exporting"))
        {
            _phaseIdx = Math.Max(_phaseIdx, 3);
        }

        if (lower.Contains("done") || lower.Contains("finished"))
        {
            _phaseIdx = Math.Max(_phaseIdx, 4);
        }

        var phase = _phases[Math.Min(_phaseIdx, _phases.Length - 1)];
        var progress = phase.Start + (phase.End - phase.Start) / 2;
        return new SlicerProgress(progress, line);
    }

    public ProgressUpdate? Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        var m = PercentRegex.Match(line);
        if (m.Success && int.TryParse(m.Groups["pct"].Value, out var pct))
        {
            var pctClamped = Math.Max(0, Math.Min(100, pct));
            return new ProgressUpdate(pctClamped, line, SlicerProgressState.InProgress);
        }

        m = LayerRegex.Match(line);
        if (m.Success && int.TryParse(m.Groups["idx"].Value, out var idx) && int.TryParse(m.Groups["total"].Value, out var total) && total > 0)
        {
            var p = (double)idx / total * 100.0;
            var pctVal = Math.Max(0.0, Math.Min(100.0, p));
            return new ProgressUpdate(Math.Round(pctVal, 2), line, SlicerProgressState.InProgress);
        }

        // Completed detection (Prusa may print 'Exported gcode' or similar)
        if (line.Contains("exported", StringComparison.OrdinalIgnoreCase) && line.Contains("gcode", StringComparison.OrdinalIgnoreCase))
        {
            return new ProgressUpdate(100.0, line, SlicerProgressState.Completed);
        }

        // Error detection
        if (line.Contains("error", StringComparison.OrdinalIgnoreCase) || line.Contains("failed", StringComparison.OrdinalIgnoreCase))
        {
            return new ProgressUpdate(0.0, line, SlicerProgressState.Failed);
        }

        return null;
    }

    [GeneratedRegex(@"(?i)progress[:\s]+(?<pct>\d{1,3})%", RegexOptions.Compiled, "en-US")]
    private static partial Regex MyRegex();
}
