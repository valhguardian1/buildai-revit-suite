using System.Collections.Generic;
using BuildAI.Core.Models;

namespace Plugin2.VolumeEstimator.Estimation
{
    /// <summary>
    /// Output of a Volume Estimator pass: the aggregated material rows that go to
    /// BuildAI (specification §5.3), plus the project id used for the URL and diagnostics
    /// about the federated de-duplication (which models were read, and whether
    /// any model's discipline could not be determined).
    /// </summary>
    public sealed class EstimationResult
    {
        public string ProjectId { get; set; } = "";
        public string ProjectName { get; set; } = "";

        /// <summary>Total elements visited across host + links.</summary>
        public int ElementsScanned { get; set; }

        /// <summary>Elements whose materials were actually counted (post-ownership filter).</summary>
        public int ElementsCounted { get; set; }

        /// <summary>Human-readable "ModelTitle -> AR/ST/MEP/?" notes for the UI/log.</summary>
        public List<string> SourceNotes { get; set; } = new List<string>();

        /// <summary>True if at least one model's discipline was undetermined
        /// (its rows are counted as-is, so double-counting can't be guaranteed
        /// away for it — surfaced as a warning).</summary>
        public bool HasUndeterminedSource { get; set; }

        public List<RevitMaterialItem> Rows { get; set; } = new List<RevitMaterialItem>();

        /// <summary>BuildAI Viewer publication state for the same recalculation.</summary>
        public string PublicationStatus { get; set; } = "pending";
        public BuildAI.Core.Issues.ApsPushpinContext AccView { get; set; }
        public string PublicationUrl { get; set; } = "";
        public string PublicationError { get; set; } = "";
        public long? PublicationId { get; set; }
        public long? PublishedRevitModelId { get; set; }
        public string PublishedFileName { get; set; } = "";
    }
}
