using System;

namespace BuildAI.Core.APS
{
    /// <summary>
    /// Which Autodesk Issues API family serves this project.
    /// <para>
    /// The version numbers are misleading and were previously read backwards.
    /// They belong to two independent product lines, not to one upgrade path:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <b>AccIssuesV1</b> - construction/issues/v1/projects/{projectId}.
    /// The CURRENT API for Autodesk Construction Cloud. Despite the "v1" it is
    /// not legacy and is not deprecated. Project id must NOT carry the "b." prefix.
    /// </description></item>
    /// <item><description>
    /// <b>Bim360IssuesV2</b> - issues/v2/containers/{containerId}.
    /// BIM 360 only. Autodesk explicitly blocks ACC projects on this route via
    /// blockACCProjectRequestsMiddleware, answering HTTP 404 ISSUES_SERVICE_NOT_FOUND.
    /// Container id is a bare GUID.
    /// </description></item>
    /// </list>
    /// </summary>
    public enum ApsIssueApiMode
    {
        Unknown = 0,

        /// <summary>Autodesk Construction Cloud Issues API (construction/issues/v1).</summary>
        AccIssuesV1 = 1,

        /// <summary>BIM 360 Issues API v2 (issues/v2/containers). Not valid for ACC projects.</summary>
        Bim360IssuesV2 = 2
    }

    public enum ApsPlatformKind
    {
        Unknown = 0,
        AutodeskConstructionCloud = 1,
        Bim360 = 2
    }

    /// <summary>
    /// Resolved Issues API context. Resolution is capability-based: the plugin
    /// probes the available Issues route instead of assuming that a project id
    /// is also a container id.
    /// </summary>
    public sealed class ApsIssueEnvironment
    {
        public ApsPlatformKind Platform { get; set; }
        public ApsIssueApiMode ApiMode { get; set; }
        public string ProjectId { get; set; } = "";
        public string ContainerId { get; set; } = "";
        public string Region { get; set; } = "";
        public string ItemId { get; set; } = "";
        public string VersionId { get; set; } = "";
        public string ModelUrn { get; set; } = "";
        public string SeedUrn { get; set; } = "";
        public string IssueSubtypeId { get; set; } = "";
        public bool SupportsCurrentUserEndpoint { get; set; }

        public bool IsUsable => ApiMode != ApsIssueApiMode.Unknown &&
                                (!string.IsNullOrWhiteSpace(ContainerId) || !string.IsNullOrWhiteSpace(ProjectId)) &&
                                !string.IsNullOrWhiteSpace(IssueSubtypeId);

        public string DisplayName
        {
            get
            {
                var platform = Platform == ApsPlatformKind.Bim360 ? "BIM 360" :
                    Platform == ApsPlatformKind.AutodeskConstructionCloud ? "Autodesk Construction Cloud" : "Autodesk platform";
                var api = ApiMode == ApsIssueApiMode.AccIssuesV1 ? "ACC Issues API (construction/issues/v1)" :
                    ApiMode == ApsIssueApiMode.Bim360IssuesV2 ? "BIM 360 Issues API v2" : "Unknown Issues API";
                return platform + " / " + api;
            }
        }

        /// <summary>
        /// Autodesk Issues endpoints take a bare GUID. Data Management project
        /// ids arrive prefixed with "b." and that prefix fails GUID validation
        /// with HTTP 400 on both API families, so it must be removed rather than
        /// tried as an alternative candidate.
        /// </summary>
        public static string StripDataManagementPrefix(string id)
        {
            var value = (id ?? "").Trim();
            return value.StartsWith("b.", StringComparison.OrdinalIgnoreCase) ? value.Substring(2) : value;
        }
    }
}
