using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BuildAI.Core.Issues
{
    public sealed class RevitIssueElementDto
    {
        [JsonProperty("category")] public string Category { get; set; } = "";
        [JsonProperty("model_uid")] public string ModelUid { get; set; } = "";
        [JsonProperty("discipline")] public string Discipline { get; set; } = "";
        [JsonProperty("element_id")] public int ElementId { get; set; }
        [JsonProperty("element_unique_id")] public string ElementUniqueId { get; set; } = "";
        [JsonProperty("link_instance_uid")] public string LinkInstanceUid { get; set; }
    }

    public class ExistingRevitIssueDto
    {
        [JsonProperty("level")] public string Level { get; set; } = "";
        [JsonProperty("due_date")] public string DueDate { get; set; } = "";
        [JsonProperty("issue_id")] public string IssueId { get; set; } = "";
        /// <summary>Autodesk's human-facing issue number, shown in the result tables
        /// so a row identifies the Issue without opening it. Nullable because records
        /// written before the backend returned the field carry no value.</summary>
        [JsonProperty("display_id")] public int? DisplayId { get; set; }
        [JsonProperty("owner_id")] public string OwnerId { get; set; } = "";
        [JsonProperty("issue_url")] public string IssueUrl { get; set; } = "";
        [JsonProperty("created_at")] public DateTime? CreatedAt { get; set; }
        [JsonProperty("issue_type")] public string IssueType { get; set; } = "";
        [JsonProperty("result_key")] public string ResultKey { get; set; } = "";
        [JsonProperty("assignee_id")] public string AssigneeId { get; set; } = "";
        [JsonProperty("issue_title")] public string IssueTitle { get; set; } = "";
        [JsonProperty("result_data")] public Dictionary<string, object> ResultData { get; set; } = new Dictionary<string, object>();
        [JsonProperty("issue_status")] public string IssueStatus { get; set; } = "";
        [JsonProperty("assignee_role")] public string AssigneeRole { get; set; } = "";
        [JsonProperty("primary_element")] public RevitIssueElementDto PrimaryElement { get; set; }
        [JsonProperty("issue_subtype_id")] public string IssueSubtypeId { get; set; } = "";
        [JsonProperty("secondary_element")] public RevitIssueElementDto SecondaryElement { get; set; }
    }

    public sealed class ApsProjectResponse
    {
        [JsonProperty("aps_id")] public string ApsId { get; set; } = "";
        [JsonProperty("aps_project_id")] public string ApsProjectId { get; set; } = "";
        [JsonProperty("project_id")] public string ExplicitProjectId { get; set; } = "";
        [JsonProperty("container_id")] public string ContainerId { get; set; } = "";
        [JsonProperty("hub_id")] public string HubId { get; set; } = "";
        [JsonProperty("region")] public string Region { get; set; } = "";
        [JsonProperty("urns")] public List<string> Urns { get; set; } = new List<string>();
        [JsonIgnore] public string ProjectId => !string.IsNullOrWhiteSpace(ExplicitProjectId)
            ? ExplicitProjectId
            : (string.IsNullOrWhiteSpace(ApsProjectId) ? ApsId : ApsProjectId);
    }

    public sealed class ApsTokenResponse
    {
        private readonly SemaphoreSlim _refreshGate = new SemaphoreSlim(1, 1);
        private Func<CancellationToken, IProgress<string>, Task<ApsTokenResponse>> _refreshCallback;
        private string _lastRejectedTokenWithoutRotation;

        [JsonProperty("access_token")] public string AccessToken { get; set; } = "";
        [JsonProperty("token_type")] public string TokenType { get; set; } = "Bearer";
        [JsonProperty("expires_in")] public int ExpiresIn { get; set; }
        [JsonProperty("refresh_token")] public string RefreshToken { get; set; } = "";
        [JsonProperty("issue_subtype_id")] public string IssueSubtypeId { get; set; } = "";
        [JsonProperty("owner_id")] public string OwnerId { get; set; } = "";
        [JsonProperty("assignee_id")] public string AssigneeId { get; set; } = "";

        [JsonIgnore] public DateTime ReceivedAtUtc { get; private set; }
        [JsonIgnore] public DateTime ExpiresAtUtc { get; private set; }

        internal void ConfigureRefresh(
            Func<CancellationToken, IProgress<string>, Task<ApsTokenResponse>> refreshCallback,
            DateTime receivedAtUtc)
        {
            _refreshCallback = refreshCallback;
            SetLifetime(receivedAtUtc);
        }

        public bool ExpiresSoon(DateTime utcNow, TimeSpan safetyWindow)
        {
            return ExpiresIn > 0 && ExpiresAtUtc != default(DateTime) && utcNow.Add(safetyWindow) >= ExpiresAtUtc;
        }

        public async Task<bool> EnsureFreshAsync(
            CancellationToken ct,
            IProgress<string> diagnostics = null,
            bool force = false,
            string rejectedAccessToken = null)
        {
            if (_refreshCallback == null) return false;
            if (force && !string.IsNullOrWhiteSpace(rejectedAccessToken) &&
                string.Equals(_lastRejectedTokenWithoutRotation, rejectedAccessToken, StringComparison.Ordinal))
            {
                diagnostics?.Report("APS TOKEN REFRESH SKIPPED\nThis rejected token was already returned unchanged by BuildAI. Continuing regional discovery without another refresh request.");
                return false;
            }
            var safetyWindow = TimeSpan.FromSeconds(120);
            if (!force && !ExpiresSoon(DateTime.UtcNow, safetyWindow)) return false;

            await _refreshGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (force && !string.IsNullOrWhiteSpace(rejectedAccessToken) &&
                    !string.Equals(AccessToken, rejectedAccessToken, StringComparison.Ordinal))
                    return true;
                if (!force && !ExpiresSoon(DateTime.UtcNow, safetyWindow)) return false;

                diagnostics?.Report("APS TOKEN REFRESH START\nReason: " +
                                    (force ? "Autodesk rejected the current token with 401 AUTH-006." :
                                     "Token expires in 120 seconds or less."));
                var replacement = await _refreshCallback(ct, diagnostics).ConfigureAwait(false);
                if (replacement == null || string.IsNullOrWhiteSpace(replacement.AccessToken))
                    throw new InvalidOperationException("BuildAI returned an empty APS access token during refresh.");
                if (force && !string.IsNullOrWhiteSpace(rejectedAccessToken) &&
                    string.Equals(replacement.AccessToken, rejectedAccessToken, StringComparison.Ordinal))
                {
                    _lastRejectedTokenWithoutRotation = rejectedAccessToken;
                    diagnostics?.Report(
                        "APS TOKEN REFRESH DID NOT ROTATE TOKEN\n" +
                        "BuildAI returned the same access token. The current regional route will be recorded as rejected, " +
                        "but the remaining Autodesk routes will still be checked.");
                    return false;
                }

                AccessToken = replacement.AccessToken;
                _lastRejectedTokenWithoutRotation = null;
                TokenType = string.IsNullOrWhiteSpace(replacement.TokenType) ? "Bearer" : replacement.TokenType;
                ExpiresIn = replacement.ExpiresIn;
                RefreshToken = replacement.RefreshToken;
                IssueSubtypeId = replacement.IssueSubtypeId;
                OwnerId = replacement.OwnerId;
                AssigneeId = replacement.AssigneeId;
                SetLifetime(DateTime.UtcNow);
                diagnostics?.Report("APS TOKEN REFRESH SUCCESS\nexpires_in: " + ExpiresIn +
                                    "\nExpires at UTC: " + ExpiresAtUtc.ToString("O"));
                return true;
            }
            finally
            {
                _refreshGate.Release();
            }
        }

        private void SetLifetime(DateTime receivedAtUtc)
        {
            ReceivedAtUtc = receivedAtUtc;
            ExpiresAtUtc = ExpiresIn > 0 ? receivedAtUtc.AddSeconds(ExpiresIn) : default(DateTime);
        }
    }



    public sealed class ApsCloudModelIdentity
    {
        public string HubId { get; set; } = "";
        public string ProjectId { get; set; } = "";
        public string ProjectGuid { get; set; } = "";
        public string ModelGuid { get; set; } = "";
        public string DocumentTitle { get; set; } = "";
        // Shared-coordinate offset of the host internal origin, in Revit internal units (feet).
        public double SharedOffsetX { get; set; }
        public double SharedOffsetY { get; set; }
        public double SharedOffsetZ { get; set; }
        // Rotation from Revit internal coordinates to shared coordinates, radians.
        public double SharedAngleRadians { get; set; }
        public bool UseExistingPublication { get; set; }
        public bool RequireExactModelMatch { get; set; }
        // Frozen Document.GetCloudModelUrn(), never a title-derived identifier.
        public string CloudModelUrn { get; set; } = "";
        public bool IsUsable => !string.IsNullOrWhiteSpace(ProjectId) &&
                                (!string.IsNullOrWhiteSpace(ModelGuid) || !string.IsNullOrWhiteSpace(DocumentTitle));
    }

    public sealed class ApsModelItemContext
    {
        public string ItemId { get; set; } = "";
        public string ItemName { get; set; } = "";
        public string VersionId { get; set; } = "";
        public int VersionNumber { get; set; }
        public DateTime? VersionCreatedAt { get; set; }
        public string DerivativeUrn { get; set; } = "";
        public string DerivativeManifestUrl { get; set; } = "";
        public string DerivativeRegion { get; set; } = "";
        public string DerivativeScopes { get; set; } = "";
    }

    public sealed class ApsPushpinContext
    {
        // Data Management ITEM/lineage URN, for example:
        // urn:adsk.wipprod:dm.lineage:...
        // Do not put a Model Derivative URN or fs.file version URN here.
        public string DocumentUrn { get; set; } = "";
        // Data Management project id (usually starts with b.).
        public string ProjectId { get; set; } = "";
        // Exact Data Management version URN used to build the Model Properties index.
        public string VersionUrn { get; set; } = "";
        public string DerivativeUrn { get; set; } = "";
        // Authoritative Model Derivative routing returned by Data Management
        // for this exact version. Preserve the regional path and scopes query.
        public string DerivativeManifestUrl { get; set; } = "";
        public string DerivativeRegion { get; set; } = "";
        public string DerivativeScopes { get; set; } = "";
        // GUID of the manifest geometry node. This is the value exposed as
        // loadedDocument.data.guid by APS Viewer and belongs in
        // linkedDocuments[].details.viewable.guid.
        public string ViewableGeometryGuid { get; set; } = "";
        // Manifest geometry.viewableID (or the matching child view GUID).
        public string ViewableId { get; set; } = "";
        // GUID of the manifest child resource with role=graphics. The Model
        // Derivative /metadata endpoint exposes this GUID and it is used only
        // for object/property lookup; it is not a pushpin viewable identity.
        public string ModelPropertiesGuid { get; set; } = "";
        public string ViewableName { get; set; } = "3D View";
        public int FileVersion { get; set; } = 1;
        public bool Is3D { get; set; } = true;
        // Exact number of metres represented by one Viewer internal unit.
        // Revit SVF/SVF2 geometry uses international feet, so this is 0.3048.
        public double ViewerUnitScaleToMeters { get; set; }
        // Translation and 3x3 linear part of the published AECModelData
        // refPointTransformation, expressed in Viewer internal units. The
        // coefficients below use normal row/column notation even though the
        // source array is serialized in Three.js column-major order.
        public double ViewerGlobalOffsetX { get; set; }
        public double ViewerGlobalOffsetY { get; set; }
        public double ViewerGlobalOffsetZ { get; set; }
        public double ViewerTransformM00 { get; set; }
        public double ViewerTransformM01 { get; set; }
        public double ViewerTransformM02 { get; set; }
        public double ViewerTransformM10 { get; set; }
        public double ViewerTransformM11 { get; set; }
        public double ViewerTransformM12 { get; set; }
        public double ViewerTransformM20 { get; set; }
        public double ViewerTransformM21 { get; set; }
        public double ViewerTransformM22 { get; set; }
        public string ViewerTransformSource { get; set; } = "";
        public string Status { get; set; } = "";
        public string ValidationError { get; set; } = "";
        public bool HasDocumentUrn => !string.IsNullOrWhiteSpace(DocumentUrn);
        public double ViewerTransformDeterminant =>
            ViewerTransformM00 * (ViewerTransformM11 * ViewerTransformM22 - ViewerTransformM12 * ViewerTransformM21) -
            ViewerTransformM01 * (ViewerTransformM10 * ViewerTransformM22 - ViewerTransformM12 * ViewerTransformM20) +
            ViewerTransformM02 * (ViewerTransformM10 * ViewerTransformM21 - ViewerTransformM11 * ViewerTransformM20);
        public bool HasViewerCoordinateTransform =>
            ViewerUnitScaleToMeters > 0.0 &&
            !double.IsNaN(ViewerUnitScaleToMeters) &&
            !double.IsInfinity(ViewerUnitScaleToMeters) &&
            !(double.IsNaN(ViewerGlobalOffsetX) || double.IsNaN(ViewerGlobalOffsetY) || double.IsNaN(ViewerGlobalOffsetZ) ||
              double.IsInfinity(ViewerGlobalOffsetX) || double.IsInfinity(ViewerGlobalOffsetY) || double.IsInfinity(ViewerGlobalOffsetZ)) &&
            IsFinite(ViewerTransformM00) && IsFinite(ViewerTransformM01) && IsFinite(ViewerTransformM02) &&
            IsFinite(ViewerTransformM10) && IsFinite(ViewerTransformM11) && IsFinite(ViewerTransformM12) &&
            IsFinite(ViewerTransformM20) && IsFinite(ViewerTransformM21) && IsFinite(ViewerTransformM22) &&
            Math.Abs(ViewerTransformDeterminant) > 0.000000000001;
        public bool HasViewableIdentity =>
            !string.IsNullOrWhiteSpace(ViewableGeometryGuid) &&
            !string.IsNullOrWhiteSpace(ViewableId) &&
            !string.IsNullOrWhiteSpace(ModelPropertiesGuid);
        public bool IsUsable => HasDocumentUrn && HasViewableIdentity && HasViewerCoordinateTransform && FileVersion > 0;
        private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }


    public sealed class ApsObjectBounds
    {
        // Model Properties index ids are retained for identity correlation.
        // Retained only for optional Construction Index diagnostics. Active
        // Issue workflows obtain final identity and coordinates from Viewer.
        public int ObjectId { get; set; }
        public int LmvId { get; set; }
        public int Svf2Id { get; set; }
        public string ExternalId { get; set; } = "";
        public double MinX { get; set; }
        public double MinY { get; set; }
        public double MinZ { get; set; }
        public double MaxX { get; set; }
        public double MaxY { get; set; }
        public double MaxZ { get; set; }
        public double CenterX => (MinX + MaxX) / 2.0;
        public double CenterY => (MinY + MaxY) / 2.0;
        public double CenterZ => (MinZ + MaxZ) / 2.0;
        public bool IsValid =>
            !(double.IsNaN(MinX) || double.IsNaN(MinY) || double.IsNaN(MinZ) ||
              double.IsNaN(MaxX) || double.IsNaN(MaxY) || double.IsNaN(MaxZ) ||
              double.IsInfinity(MinX) || double.IsInfinity(MinY) || double.IsInfinity(MinZ) ||
              double.IsInfinity(MaxX) || double.IsInfinity(MaxY) || double.IsInfinity(MaxZ)) &&
            MaxX >= MinX && MaxY >= MinY && MaxZ >= MinZ;
        public bool HasViewerObjectIdentity => LmvId > 0 && !string.IsNullOrWhiteSpace(ExternalId);
        public bool HasValidatedPushpinIdentity =>
            LmvId > 0 && Svf2Id > 0 && !string.IsNullOrWhiteSpace(ExternalId);
    }

    public sealed class ApsPushpinPosition
    {
        [JsonProperty("x")] public double X { get; set; }
        [JsonProperty("y")] public double Y { get; set; }
        [JsonProperty("z")] public double Z { get; set; }
    }

    public sealed class ApsPushpinViewable
    {
        [JsonProperty("guid")] public string Guid { get; set; } = "";
        [JsonProperty("viewableId")] public string ViewableId { get; set; } = "";
        [JsonProperty("name")] public string Name { get; set; } = "3D View";
        [JsonProperty("is3D")] public bool Is3D { get; set; } = true;
    }

    public sealed class ApsPushpinDetails
    {
        [JsonProperty("viewable")] public ApsPushpinViewable Viewable { get; set; }
        [JsonProperty("position")] public ApsPushpinPosition Position { get; set; }
        [JsonProperty("objectId", NullValueHandling = NullValueHandling.Ignore)] public int? ObjectId { get; set; }
        [JsonProperty("externalId", NullValueHandling = NullValueHandling.Ignore)] public string ExternalId { get; set; }
        [JsonProperty("viewerState", NullValueHandling = NullValueHandling.Ignore)] public JObject ViewerState { get; set; }
    }

    public sealed class ApsLinkedDocument
    {
        [JsonProperty("type")] public string Type { get; set; } = "TwoDVectorPushpin";
        [JsonProperty("urn")] public string Urn { get; set; } = "";
        [JsonProperty("createdBy")] public string CreatedBy { get; set; } = "";
        [JsonProperty("createdAt")] public string CreatedAt { get; set; } = "";
        [JsonProperty("createdAtVersion")] public int CreatedAtVersion { get; set; } = 1;
        [JsonProperty("details")] public ApsPushpinDetails Details { get; set; }
    }


    public sealed class ApsAssigneeResolution
    {
        public string ProjectId { get; set; }
        public string AssignedTo { get; set; }
        public string AssignedToType { get; set; }
        public string DisplayName { get; set; } = "";
        public string PreferredRole { get; set; } = "";
        public string ResolutionReason { get; set; } = "";
        public bool IsResolved => !string.IsNullOrWhiteSpace(AssignedTo) && !string.IsNullOrWhiteSpace(AssignedToType);
    }

    public sealed class ApsCreateIssueRequest
    {
        [JsonProperty("title")] public string Title { get; set; } = "";
        [JsonProperty("description")] public string Description { get; set; } = "";
        [JsonProperty("status")] public string Status { get; set; } = "open";
        [JsonProperty("issueSubtypeId")] public string IssueSubtypeId { get; set; } = "";
        [JsonProperty("assignedTo", NullValueHandling = NullValueHandling.Ignore)] public string AssignedTo { get; set; }
        [JsonProperty("assignedToType", NullValueHandling = NullValueHandling.Ignore)] public string AssignedToType { get; set; }
        [JsonProperty("dueDate")] public string DueDate { get; set; } = "";
        [JsonProperty("rootCauseId", NullValueHandling = NullValueHandling.Ignore)] public string RootCauseId { get; set; }
        [JsonProperty("locationDetails", NullValueHandling = NullValueHandling.Ignore)] public string LocationDetails { get; set; }
        [JsonProperty("linkedDocuments", NullValueHandling = NullValueHandling.Ignore)] public List<ApsLinkedDocument> LinkedDocuments { get; set; }
        [JsonProperty("published", NullValueHandling = NullValueHandling.Ignore)] public bool? Published { get; set; }
    }

    public sealed class ApsCreateIssueResponse
    {
        [JsonIgnore] public int HttpStatusCode { get; set; }
        [JsonProperty("id")] public string Id { get; set; } = "";
        [JsonProperty("title")] public string Title { get; set; } = "";
        [JsonProperty("status")] public string Status { get; set; } = "";
        // Autodesk returns displayId on creation, so a row can show its number
        // immediately instead of only after the table is reloaded from BuildAI.
        [JsonProperty("displayId")] public int? DisplayId { get; set; }
        [JsonProperty("published")] public bool Published { get; set; }
        [JsonProperty("permittedActions")] public string[] PermittedActions { get; set; } = Array.Empty<string>();
    }

    public sealed class SavedIssueDto : ExistingRevitIssueDto
    {
        [JsonProperty("ai_analysis")] public AiIssueAnalysisDto AiAnalysis { get; set; }
    }

    public sealed class AiIssueAnalysisDto
    {
        [JsonProperty("is_real_issue")] public bool IsRealIssue { get; set; }
        [JsonProperty("assessment")] public string Assessment { get; set; } = "";
        [JsonProperty("severity")] public string Severity { get; set; } = "";
        [JsonProperty("comment")] public string Comment { get; set; } = "";
        [JsonProperty("reason")] public string Reason { get; set; } = "";
    }

    public sealed class SaveIssueBatchRequest
    {
        [JsonProperty("revit_model_id", NullValueHandling = NullValueHandling.Ignore)] public int? RevitModelId { get; set; }
        [JsonProperty("revit_model_uid")] public string RevitModelUid { get; set; } = "";
        [JsonProperty("source")] public string Source { get; set; } = "";
        [JsonProperty("issues")] public List<SavedIssueDto> Issues { get; set; } = new List<SavedIssueDto>();
    }

    public sealed class SaveIssueBatchItemResult
    {
        [JsonProperty("result_key")] public string ResultKey { get; set; } = "";
        [JsonProperty("issue_id")] public string IssueId { get; set; } = "";
        [JsonProperty("status")] public string Status { get; set; } = "";
        [JsonProperty("error")] public string Error { get; set; } = "";
    }

    public sealed class SaveIssueBatchResponse
    {
        [JsonProperty("saved")] public int Saved { get; set; }
        [JsonProperty("updated")] public int Updated { get; set; }
        [JsonProperty("failed")] public int Failed { get; set; }
        [JsonProperty("items")] public List<SaveIssueBatchItemResult> Items { get; set; } = new List<SaveIssueBatchItemResult>();
    }

    public sealed class IssueCreationProgress
    {
        public int Current { get; set; }
        public int Total { get; set; }
        public int Succeeded { get; set; }
        public int Failed { get; set; }
        public string Message { get; set; } = "";
        public string Error { get; set; } = "";
        public bool IsCompleted { get; set; }
    }
}
