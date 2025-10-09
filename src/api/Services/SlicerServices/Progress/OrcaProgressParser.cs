using System.Text.RegularExpressions;

namespace Farm.Web.Api.Services.SlicerServices.Progress;

public partial class OrcaProgressParser : IProgressParser
{
    // Examples parsed:
    // "[info] Exporting: 30%"
    // "Saving G-code: 100%"
    private static readonly Regex PercentRegex = MyRegex();
    private static readonly Regex ExportingRegex = new(@"(?i)exporting|saving|writing", RegexOptions.Compiled);

    public ProgressUpdate? Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        // If we find a percent token, prefer it
        Match m = PercentRegex.Match(line);
        if (m.Success && int.TryParse(m.Groups["pct"].Value, out int pct))
        {
            int pctClamped = Math.Max(0, Math.Min(100, pct));
            SlicerProgressState state = pctClamped >= 100 ? SlicerProgressState.Completed : SlicerProgressState.InProgress;
            return new ProgressUpdate(pctClamped, line, state);
        }

        // If the line mentions exporting/writing but has no percent, produce an indeterminate in-progress update (0% with message)
        if (ExportingRegex.IsMatch(line))
        {
            return new ProgressUpdate(0.0, line, SlicerProgressState.InProgress);
        }

        // Error detection
        if (line.Contains("error", StringComparison.OrdinalIgnoreCase) || line.Contains("failed", StringComparison.OrdinalIgnoreCase))
        {
            return new ProgressUpdate(0.0, line, SlicerProgressState.Failed);
        }

        return null;
    }

    [GeneratedRegex(@"(?i)(?<pct>\d{1,3})%", RegexOptions.Compiled, "en-US")]
    private static partial Regex MyRegex();
}
