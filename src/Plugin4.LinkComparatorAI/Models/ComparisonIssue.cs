using System;
using BuildAI.Core.Localization;

namespace Plugin4.LinkComparatorAI.Models
{
    public enum IssueSeverity { Critical, Important, Minor }
    public enum ComparatorCheckType { Wall, Floor, Column, Beam, Opening, Grid, RoomHeight }

    public sealed class ComparisonIssue
    {
        public string IssueId { get; set; } = Guid.NewGuid().ToString("N");
        public string ResultKey { get; set; } = "";
        public string ArchitecturalModelUid { get; set; } = "";
        public string StructuralModelUid { get; set; } = "";
        public string ArchitecturalSourceName { get; set; } = "";
        public string StructuralSourceName { get; set; } = "";
        public BuildAI.Core.Issues.ApsAssigneeResolution ManualAssignee { get; set; }
        public bool IsSelectedForIssueCreation { get; set; } = true;
        public string ApsIssueId { get; set; } = "";
        public string ApsIssueUrl { get; set; } = "";
        public string ApsIssueStatus { get; set; } = "";
        public bool HasApsIssue => !string.IsNullOrWhiteSpace(ApsIssueId) ||
                                   !string.IsNullOrWhiteSpace(ApsIssueUrl);
        /// <summary>Issue number for display. The left-to-right marks keep "#238"
        /// from being reordered when it sits next to Hebrew text.</summary>
        public string ApsIssueNumber => !HasApsIssue ? Loc.T("P5_ColIssueNumber")
            : "Already created" + (ApsIssueDisplayId.HasValue ? " #" + ApsIssueDisplayId.Value : "");
        public string AiAssessment { get; set; } = "Not analyzed";
        public string AiSeverity { get; set; } = "";
        public string AiComment { get; set; } = "";
        public string AiReason { get; set; } = "";
        public double AiConfidence { get; set; }
        public string AiRecommendedAction { get; set; } = "";
        public bool AiIsRealIssue { get; set; } = true;
        public ComparatorCheckType CheckType { get; set; }
        public IssueSeverity Severity { get; set; } = IssueSeverity.Important;
        public string Level { get; set; } = "";
        public string Category { get; set; } = "";
        /// <summary>Locale-independent category of each side, filled from CategoryNaming.
        /// Kept per side because the Issue title names both, and a single shared value
        /// silently claimed the structural element had the architectural category.</summary>
        public string ArchitecturalCategory { get; set; } = "";
        public string StructuralCategory { get; set; } = "";
        public int? ApsIssueDisplayId { get; set; }
        public string Title { get; set; } = "";
        public string Description { get; set; } = "";
        public string Recommendation { get; set; } = "";
        public double DeltaMm { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }
        public string ArchitecturalLinkUniqueId { get; set; } = "";
        public string ArchitecturalElementUniqueId { get; set; } = "";
        public string StructuralLinkUniqueId { get; set; } = "";
        public string StructuralElementUniqueId { get; set; } = "";
        public int ArchitecturalElementId { get; set; }
        public int StructuralElementId { get; set; }
        public string AiExplanation { get; set; } = "";
        public bool HasArchitecturalElement => !string.IsNullOrWhiteSpace(ArchitecturalElementUniqueId);
        public bool HasStructuralElement => !string.IsNullOrWhiteSpace(StructuralElementUniqueId);

        public int CategorySortOrder => CheckType == ComparatorCheckType.Floor ? 0 : CheckType == ComparatorCheckType.Column ? 1 : CheckType == ComparatorCheckType.Beam ? 2 : CheckType == ComparatorCheckType.Wall ? 3 : 100;
        public int ResultTypeSortOrder
        {
            get
            {
                var value = (Title ?? "") + " " + (Description ?? "");
                if (value.IndexOf("Mismatch", StringComparison.OrdinalIgnoreCase) >= 0) return 0;
                if (value.IndexOf("No AR match", StringComparison.OrdinalIgnoreCase) >= 0) return 1;
                if (value.IndexOf("No ST match", StringComparison.OrdinalIgnoreCase) >= 0) return 2;
                return 100;
            }
        }
        public string NaturalSortTitle
        {
            get
            {
                var title = Title ?? "";
                var colon = title.IndexOf(':');
                return colon >= 0 && colon + 1 < title.Length ? title.Substring(colon + 1).Trim() : title.Trim();
            }
        }
        public string SeverityText => Severity == IssueSeverity.Critical ? Loc.T("P4_Critical") : Severity == IssueSeverity.Important ? Loc.T("P4_Important") : Loc.T("P4_Minor");
        public string SeverityRu => SeverityText;
        public string CheckTypeRu => CheckTypeText;
        public string CheckTypeText
        {
            get
            {
                switch (CheckType)
                {
                    case ComparatorCheckType.Wall: return Loc.IsRightToLeft ? "קירות" : "Walls";
                    case ComparatorCheckType.Floor: return Loc.IsRightToLeft ? "רצפות" : "Floors";
                    case ComparatorCheckType.Column: return Loc.IsRightToLeft ? "עמודים" : "Columns";
                    case ComparatorCheckType.Beam: return Loc.IsRightToLeft ? "קורות" : "Beams";
                    default: return Loc.IsRightToLeft ? "גבהי חדרים" : "Room heights";
                }
            }
        }
    }
}
