using System.Collections.Generic;

namespace BuildAI.Core.AI
{
    public sealed class AiRecommendation
    {
        public string ResultKey { get; set; } = "";
        public bool ShouldCreateIssue { get; set; }
        public bool IsRealIssue { get; set; }
        public string Assessment { get; set; } = "manual_review";
        public string Severity { get; set; } = "medium";
        public double Confidence { get; set; }
        public string Reason { get; set; } = "";
        public string Comment { get; set; } = "";
        public string ResponsibleDiscipline { get; set; } = "Coordination";
        public string RecommendedAction { get; set; } = "Manual review";
    }

    public sealed class AiAnalysisResult
    {
        public string Summary { get; set; } = "";
        public List<AiRecommendation> Issues { get; set; } = new List<AiRecommendation>();
        public bool FromCache { get; set; }
        public string InputHash { get; set; } = "";
    }
}
