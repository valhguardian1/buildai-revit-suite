using System;

namespace Plugin4.LinkComparatorAI.Revit
{
    public sealed class PublishViewPreparationResult
    {
        public string CoordinationViewName { get; set; } = "BuildAI Coordination";
        public string ArStViewName { get; set; } = "BuildAI AR-ST";
        public string HubId { get; set; }
        public string ProjectId { get; set; }
        public string ProjectGuid { get; set; }
        public string ModelGuid { get; set; }
        public string DocumentTitle { get; set; }
        public string HostModelUid { get; set; }
        public double SharedOffsetX { get; set; }
        public double SharedOffsetY { get; set; }
        public double SharedOffsetZ { get; set; }
        public double SharedAngleRadians { get; set; }
        public bool ViewsCreated { get; set; }
        public bool DocumentWasModifiedBeforePreparation { get; set; }
        public bool DocumentIsModifiedAfterPreparation { get; set; }
        public bool RequiresSynchronization => ViewsCreated || DocumentWasModifiedBeforePreparation || DocumentIsModifiedAfterPreparation;
        public bool IsCloudModel => !string.IsNullOrWhiteSpace(ProjectId);
        public string Summary => "Views prepared: " + CoordinationViewName + ", " + ArStViewName +
            "\nRevit cloud hub id: " + (string.IsNullOrWhiteSpace(HubId) ? "not available" : HubId) +
            "\nRevit cloud project id: " + (string.IsNullOrWhiteSpace(ProjectId) ? "not available" : ProjectId) +
            "\nRevit project GUID: " + (string.IsNullOrWhiteSpace(ProjectGuid) ? "not available" : ProjectGuid) +
            "\nRevit model GUID: " + (string.IsNullOrWhiteSpace(ModelGuid) ? "not available" : ModelGuid) +
            "\nFrozen Revit model UID: " + (string.IsNullOrWhiteSpace(HostModelUid) ? "not available" : HostModelUid) +
            "\nRevit shared-transform metadata (diagnostic only; pushpin positions come from the Viewer probe, not from this matrix): offset ft X=" + SharedOffsetX.ToString("0.###") + "; Y=" + SharedOffsetY.ToString("0.###") + "; Z=" + SharedOffsetZ.ToString("0.###") + "; angle rad=" + SharedAngleRadians.ToString("0.######") +
            "\nPublication mode: " + (RequiresSynchronization ? "Safe (sync + publish)" : "Fast (reuse latest publication)");
    }
}
