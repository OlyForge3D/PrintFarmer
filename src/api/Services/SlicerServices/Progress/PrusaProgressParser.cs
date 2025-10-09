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

        if (line.Contains('%'))
        {
            string digits = string.Concat(line.Where(char.IsDigit));
            if (!string.IsNullOrEmpty(digits) && int.TryParse(digits, out int p))
            {
                int clamped = Math.Max(0, Math.Min(100, p));
                return new SlicerProgress(clamped, line);
            }
        }
        if (line.Contains("loading", StringComparison.OrdinalIgnoreCase) || line.Contains("load", StringComparison.OrdinalIgnoreCase))
        {
            _phaseIdx = Math.Max(_phaseIdx, 1);
        }
        if (line.Contains("analyzing", StringComparison.OrdinalIgnoreCase) || line.Contains("toolpath", StringComparison.OrdinalIgnoreCase) || line.Contains("toolpaths", StringComparison.OrdinalIgnoreCase))
        {
            _phaseIdx = Math.Max(_phaseIdx, 2);
        }
        if (line.Contains("writing", StringComparison.OrdinalIgnoreCase) || line.Contains("writing g-code", StringComparison.OrdinalIgnoreCase) || line.Contains("exporting", StringComparison.OrdinalIgnoreCase))
        {
            _phaseIdx = Math.Max(_phaseIdx, 3);
        }
        if (line.Contains("done", StringComparison.OrdinalIgnoreCase) || line.Contains("finished", StringComparison.OrdinalIgnoreCase))
        {
            _phaseIdx = Math.Max(_phaseIdx, 4);
        }

        (int Start, int End, string Message) = _phases[Math.Min(_phaseIdx, _phases.Length - 1)];
        int progress = Start + (End - Start) / 2;
        return new SlicerProgress(progress, line);
    }

    public ProgressUpdate? Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        Match m = PercentRegex.Match(line);
        if (m.Success && int.TryParse(m.Groups["pct"].Value, out int pct))
        {
            int pctClamped = Math.Max(0, Math.Min(100, pct));
            return new ProgressUpdate(pctClamped, line, SlicerProgressState.InProgress);
        }

        m = LayerRegex.Match(line);
        if (m.Success && int.TryParse(m.Groups["idx"].Value, out int idx) && int.TryParse(m.Groups["total"].Value, out int total) && total > 0)
        {
            double p = (double)idx / total * 100.0;
            double pctVal = Math.Max(0.0, Math.Min(100.0, p));
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
