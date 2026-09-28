using System;
using System.Collections.Generic;
using System.Linq;

namespace Plugin4.LinkComparatorAI.Models
{
    public sealed class ComparisonReport
    {
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
        public string ArchitecturalModel { get; set; } = "";
        public string StructuralModel { get; set; } = "";
        public List<ComparisonIssue> Issues { get; set; } = new List<ComparisonIssue>();
        public List<string> Warnings { get; set; } = new List<string>();
        public List<string> Diagnostics { get; set; } = new List<string>();
        public string AiSummary { get; set; } = "";
        public int ArchitecturalElementsCollected { get; set; }
        public int StructuralElementsCollected { get; set; }
        public int ProxiesCreated { get; set; }
        public int ElementsSkipped { get; set; }
        public int PairsCompared { get; set; }
        public long ElapsedMilliseconds { get; set; }
        public int CriticalCount => Issues.Count(x => x.Severity == IssueSeverity.Critical);
        public int ImportantCount => Issues.Count(x => x.Severity == IssueSeverity.Important);
        public int MinorCount => Issues.Count(x => x.Severity == IssueSeverity.Minor);
        public string DiagnosticSummary => string.Join(Environment.NewLine, Diagnostics);
    }
}
