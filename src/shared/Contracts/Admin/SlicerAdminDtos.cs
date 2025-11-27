using System.Collections.Generic;

namespace Farm.Web.Shared.Contracts.Admin
{
    public class DryRunRequest
    {
        public string? Template { get; set; }
        public SlicerEngineType Engine { get; set; } = Farm.Web.Shared.SlicerEngineType.OrcaSlicer;
    }

    public class DryRunResult
    {
        public bool IsValid { get; set; }
        private readonly List<string> _issues = new();
        private readonly List<string> _warnings = new();
        public IReadOnlyList<string> Issues => _issues;
        public IReadOnlyList<string> Warnings => _warnings;
        public string? Rendered { get; set; }
        public Dictionary<string, string> SamplePlaceholders { get; set; } = new();

        public void AddIssue(string issue)
        {
            if (!string.IsNullOrEmpty(issue))
            {
                _issues.Add(issue);
            }
        }

        public void AddWarning(string warning)
        {
            if (!string.IsNullOrEmpty(warning))
            {
                _warnings.Add(warning);
            }
        }
    }
}
