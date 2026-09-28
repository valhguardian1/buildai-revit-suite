using Newtonsoft.Json;

namespace BuildAI.Core.Models
{
    /// <summary>
    /// Payload for POST https://app.buildai.me/api/revit_work/
    ///
    /// Field names and shape are taken verbatim from the live backend contract
    /// (confirmed by the client and cross-checked against the legacy
    /// RevitPluginsOld.dll). This is a FLAT object — not the §3.4 meta-envelope
    /// from the original specification. If the backend later moves to the meta-envelope,
    /// wrap this type instead of changing call sites.
    /// </summary>
    public sealed class RevitWorkItem
    {
        [JsonProperty("ItemName")]            public string ItemName { get; set; }
        [JsonProperty("ItemUniqueId")]        public string ItemUniqueId { get; set; }
        [JsonProperty("ProjectId")]           public string ProjectId { get; set; }
        [JsonProperty("ItemCategory")]        public string ItemCategory { get; set; }
        [JsonProperty("ProjectTitle")]        public string ProjectTitle { get; set; }
        [JsonProperty("ProjectBuildingName")] public string ProjectBuildingName { get; set; }
        [JsonProperty("ProjectName")]         public string ProjectName { get; set; }
        [JsonProperty("ProjectNumber")]       public string ProjectNumber { get; set; }

        /// <summary>add_element | modify_element | delete_element</summary>
        [JsonProperty("Method")]              public string Method { get; set; }

        [JsonProperty("Level")]               public string Level { get; set; }

        /// <summary>
        /// Event timestamp in UTC, serialized as ISO-8601 (e.g. 2026-06-26T13:42:05Z).
        /// Confirmed by the client: this is the moment the event occurred, NOT an
        /// accumulated duration.
        /// </summary>
        [JsonProperty("Time")]                public string Time { get; set; }

        [JsonProperty("Username")]            public string Username { get; set; }
    }

    /// <summary>Canonical values for <see cref="RevitWorkItem.Method"/>.</summary>
    public static class WorkMethod
    {
        public const string Add    = "add_element";
        public const string Modify = "modify_element";
        public const string Delete = "delete_element";
    }
}
