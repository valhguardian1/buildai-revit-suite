using System;
using System.IO;
using Newtonsoft.Json;

namespace BuildAI.Core.Configuration
{
    /// <summary>
    /// All endpoints are configurable so the same binaries work against prod,
    /// staging, or a local mock. Defaults are the live values recovered from the
    /// shipped legacy plugin.
    /// </summary>
    public sealed class BuildAiOptions
    {
        public string BaseUrl { get; set; } = "https://app.buildai.me";

        /// <summary>POST endpoint for element/work events (specification Plugin 1).</summary>
        public string WorkPath { get; set; } = "/api/revit_work/";

        /// <summary>
        /// POST endpoint for materials/volumes (specification Plugin 2). Confirmed by the
        /// client: "/api/revit_elems/{model_id}", where "{model_id}" is the
        /// ProjectId, substituted into the URL at send time. Still configurable
        /// so the legacy "/api/revit_materials" (or a mock) can be used if needed.
        /// </summary>
        public string MaterialsPath { get; set; } = "/api/revit_elems/{model_id}";

        /// <summary>
        /// Optional element parameter that explicitly names the owning section
        /// (AR/ST/MEP). When present on an element it WINS over the automatic
        /// category/structural rule — an escape hatch for hand-tagged models.
        /// Empty = rely on the automatic ownership rule (specification §5.2.1).
        /// </summary>
        public string SectionParameterName { get; set; } = "";

        /// <summary>
        /// Scenario A (federated model): also traverse loaded Revit links so a
        /// coordination model with AR/ST/MEP links is measured in one pass.
        /// Each material is counted once via the ownership rule (see
        /// DisciplineRules) so AR/ST duplicates do not double-count.
        /// </summary>
        public bool IncludeLinkedModels { get; set; } = true;

        /// <summary>
        /// Optional manual mapping to force a model's discipline when its file
        /// name is not self-describing. Key = case-insensitive substring of the
        /// model title; Value = "AR" | "ST" | "MEP". Checked before name keywords.
        /// </summary>
        public System.Collections.Generic.Dictionary<string, string> DisciplineOverrides { get; set; }
            = new System.Collections.Generic.Dictionary<string, string>();

        /// <summary>Auto-run Volume Estimator after Sync with Central (specification §5.2.2).</summary>
        public bool VolumeAutoCalcOnSync { get; set; } = false;

        /// <summary>UI culture: "en" or "he" (RTL). Empty = follow Revit/OS.</summary>
        public string Language { get; set; } = "";

        /// <summary>Idle threshold for active-time accounting (specification §4.2.1).</summary>
        public int IdleTimeoutMinutes { get; set; } = 5;

        /// <summary>Heartbeat period in seconds (specification §4.4).</summary>
        public int HeartbeatSeconds { get; set; } = 30;


        public string SessionsPath { get; set; } = "/api/revit/sessions";
        public string LinkChecksPath { get; set; } = "/api/revit/link-checks";
        public int SessionUpdateSeconds { get; set; } = 60;
        public int LinkCheckDebounceSeconds { get; set; } = 7;

        /// <summary>Enable delivery of structured plugin logs to BuildAI.</summary>
        public bool RemoteLoggingEnabled { get; set; } = true;

        /// <summary>POST endpoint accepting { entries: [...] }.</summary>
        public string LogsPath { get; set; } = "/api/revit-logs/batch";

        public int LogBatchSize { get; set; } = 50;
        public int LogFlushSeconds { get; set; } = 30;
        public int LogRetentionDays { get; set; } = 14;

        public string ResolveWorkUrl()      => Combine(BaseUrl, WorkPath);
        public string ResolveSessionsUrl()  => Combine(BaseUrl, SessionsPath);
        public string ResolveLinkChecksUrl()=> Combine(BaseUrl, LinkChecksPath);
        public string ResolveMaterialsUrl(string projectId)
            => Combine(BaseUrl, MaterialsPath.Replace("{model_id}", projectId ?? ""));

        private static string Combine(string baseUrl, string path)
            => baseUrl.TrimEnd('/') + "/" + path.TrimStart('/');

        // ---- persistence (%LOCALAPPDATA%\BuildAI\config.json) -------------
        public static string ConfigPath { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BuildAI", "config.json");

        public static BuildAiOptions Load()
        {
            try
            {
                if (File.Exists(ConfigPath))
                    return JsonConvert.DeserializeObject<BuildAiOptions>(File.ReadAllText(ConfigPath))
                           ?? new BuildAiOptions();
            }
            catch { /* fall through to defaults */ }
            return new BuildAiOptions();
        }

        public void Save()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath));
            File.WriteAllText(ConfigPath, JsonConvert.SerializeObject(this, Formatting.Indented));
        }
    }
}
