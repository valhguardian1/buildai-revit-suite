using System;
using System.Collections.Generic;
using System.IO;
using BuildAI.Core.Issues;

namespace Plugin5.ClashFormaIntegration.Models
{
    public enum ClashKind { Hard, Clearance }
    public enum ClashSeverity { Critical, High, Medium, Low }
    public enum ClashSourceKind { Host, Link }

    public sealed class ClashRunOptions
    {
        public string SourceAKey { get; set; } = "host";
        public string SourceBKey { get; set; } = "host";
        public string SourceCKey { get; set; } = "host";
        public bool CompareAB { get; set; } = true;
        public bool CompareAC { get; set; } = true;
        public bool CompareBC { get; set; } = true;
        public double MinimumIntersectionVolumeMm3 { get; set; } = 0.1;
        public bool BoundingBoxesOnly { get; set; } = false;
        public List<int> GroupACategories { get; set; } = new List<int>();
        public List<int> GroupBCategories { get; set; } = new List<int>();
        public List<int> GroupCCategories { get; set; } = new List<int>();
    }

    public sealed class ClashSourceOption
    {
        public string Key { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public ClashSourceKind Kind { get; set; }
        public int LinkInstanceId { get; set; }
        public override string ToString() => DisplayName;
    }

    public sealed class ClashItem : IIssueCreationRow
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string ResultKey { get; set; } = "";
        public string ModelUidA { get; set; } = "";
        public string ModelUidB { get; set; } = "";
        public string ElementAUniqueId { get; set; } = "";
        public string ElementBUniqueId { get; set; } = "";
        public string LinkInstanceAUniqueId { get; set; } = "";
        public string LinkInstanceBUniqueId { get; set; } = "";
        public BuildAI.Core.Issues.ApsAssigneeResolution ManualAssignee { get; set; }
        public bool IsSelectedForIssueCreation { get; set; } = true;
        public string ApsIssueId { get; set; } = "";
        public string ApsIssueUrl { get; set; } = "";
        public string ApsIssueStatus { get; set; } = "";
        public DateTime? ApsIssueCreatedAt { get; set; }
        public IssueCreationState CreationState { get; set; } = IssueCreationState.New;
        public string CreationStateText => CreationState == IssueCreationState.AlreadyCreated ? "Already created" :
            CreationState == IssueCreationState.PreviouslyCreatedNotDetected ? "Previously created; not detected in current run" :
            CreationState == IssueCreationState.CreationFailed ? "Failed" :
            CreationState == IssueCreationState.CreatedThisRun ? "Created this run" : "New";
        /// <summary>The human-facing Autodesk issue number (displayId). Shown in the
        /// results table so a row can be matched to an Issue without opening it.</summary>
        public int? ApsIssueDisplayId { get; set; }
        public bool HasApsIssue => !string.IsNullOrWhiteSpace(ApsIssueId) || !string.IsNullOrWhiteSpace(ApsIssueUrl) || CreationState == IssueCreationState.AlreadyCreated || CreationState == IssueCreationState.PreviouslyCreatedNotDetected || CreationState == IssueCreationState.CreatedThisRun;
        public bool HasApsIssueUrl => !string.IsNullOrWhiteSpace(ApsIssueUrl);
        /// <summary>Issue number for display. The left-to-right marks keep "#238"
        /// from being reordered when it sits next to Hebrew text.</summary>
        public string ApsIssueNumber => !ApsIssueDisplayId.HasValue
            ? BuildAI.Core.Localization.Loc.T("P5_ColIssueNumber")
            : (BuildAI.Core.Localization.Loc.IsRightToLeft
                ? "\u200E#" + ApsIssueDisplayId.Value + "\u200E"
                : "#" + ApsIssueDisplayId.Value);
        public string AiAssessment { get; set; } = "Not analyzed";
        public string AiSeverity { get; set; } = "";
        public string AiComment { get; set; } = "";
        public string AiReason { get; set; } = "";
        public double AiConfidence { get; set; }
        public string AiRecommendedAction { get; set; } = "";
        public bool AiIsRealIssue { get; set; } = true;
        public ClashKind Kind { get; set; }
        public ClashSeverity Severity { get; set; } = ClashSeverity.High;
        public int ElementAId { get; set; }
        public int ElementBId { get; set; }
        public int? LinkInstanceAId { get; set; }
        public int? LinkInstanceBId { get; set; }
        public string SourceA { get; set; } = "";
        public string SourceB { get; set; } = "";
        public string SourceC { get; set; } = "";
        public string DisplaySourceA => WithLinkContext(NormalizeSourceForDisplay(SourceA), LinkInstanceAId);
        public string DisplaySourceB => WithLinkContext(NormalizeSourceForDisplay(SourceB), LinkInstanceBId);
        public string ElementA { get; set; } = "";
        public string ElementB { get; set; } = "";
        public string CategoryA { get; set; } = "";
        public string CategoryB { get; set; } = "";
        public string CategoryPair => NormalizeGroupValue(CategoryA) + " — " + NormalizeGroupValue(CategoryB);
        public string Level { get; set; } = "";
        public string PrimaryLevel { get; set; } = "";
        public string SecondaryLevel { get; set; } = "";
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }
        public double DistanceMm { get; set; }
        public double IntersectionVolumeMm3 { get; set; }
        public string ResponsibleDiscipline { get; set; } = "";
        public string Recommendation { get; set; } = "";
        public string AiExplanation { get; set; } = "";
        public int? SectionViewId { get; set; }
        public int? SheetId { get; set; }
        public string FormaIssueId { get; set; } = "";

        private static string NormalizeSourceForDisplay(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "<blank>";
            var text = value.Trim();
            var rvt = text.IndexOf(".rvt", StringComparison.OrdinalIgnoreCase);
            if (rvt >= 0) text = text.Substring(0, rvt + 4);
            try
            {
                var file = Path.GetFileName(text);
                return string.IsNullOrWhiteSpace(file) ? text : file;
            }
            catch { return text; }
        }

        private static string NormalizeGroupValue(string value) => string.IsNullOrWhiteSpace(value) ? "<blank>" : value.Trim();
        private static string WithLinkContext(string value, int? linkInstanceId)
            => linkInstanceId.HasValue ? value + " [link " + linkInstanceId.Value + "]" : value;
    }

    public sealed class ClashReport
    {
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
        public string DocumentTitle { get; set; } = "";
        public string SourceA { get; set; } = "";
        public string SourceB { get; set; } = "";
        public string SourceC { get; set; } = "";
        public List<int> SelectedCategoryIds { get; set; } = new List<int>();
        public int CandidatePairs { get; set; }
        public int ExactChecks { get; set; }
        public int ElementsA { get; set; }
        public int ElementsB { get; set; }
        public int ElementsC { get; set; }
        public int ElementsWithoutBoundingBox { get; set; }
        public int ElementsWithoutSolid { get; set; }
        public int BooleanFailures { get; set; }
        public int CollectedBeforeFilterA { get; set; }
        public int CollectedBeforeFilterB { get; set; }
        public int CollectedBeforeFilterC { get; set; }
        public List<string> Warnings { get; set; } = new List<string>();
        public long DurationMs { get; set; }

        /// <summary>
        /// Per-stage cost. Without these every tuning decision - page size, cell
        /// size, whether the grid actually helped - stays guesswork.
        /// </summary>
        public long MsInCollect { get; set; }
        public long MsInSweep { get; set; }
        public long MsInExact { get; set; }
        public List<ClashItem> Items { get; set; } = new List<ClashItem>();
        public string AiSummary { get; set; } = "";
    }
}
