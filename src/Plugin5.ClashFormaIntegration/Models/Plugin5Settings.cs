using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace Plugin5.ClashFormaIntegration.Models
{
    public sealed class Plugin5Settings
    {
        public double ClearanceMm { get; set; } = 50;
        public int SectionScale { get; set; } = 50;
        public bool CreateSections { get; set; } = true;
        public bool CreateSheets { get; set; } = false;
        public string SectionNameTemplate { get; set; } = "Clash_{id}_{level}";
        public string SheetNumberPrefix { get; set; } = "CL-";
        public string TitleBlockTypeId { get; set; } = "";
        public string PreferredAssigneeRole { get; set; } = "";

        /// <summary>
        /// Category filter for each clash source group, as BuiltInCategory integer ids.
        /// <para>
        /// ClashEngine treats an EMPTY list as "no filter" and admits every model
        /// category in the document, which is why earlier runs compared architecture,
        /// structure and analytical volumes against MEP and produced unusable result
        /// counts. These lists are therefore seeded from the LOD 350 MEP catalogue
        /// rather than left empty.
        /// </para>
        /// </summary>
        public List<int> GroupACategories { get; set; } = new List<int>();
        public List<int> GroupBCategories { get; set; } = new List<int>();
        public List<int> GroupCCategories { get; set; } = new List<int>();

        /// <summary>
        /// Catalogue version the category lists were seeded from. Zero means the
        /// settings predate the catalogue and must be re-seeded on load.
        /// </summary>
        public int ClashCategoryCatalogueVersion { get; set; } = 0;

        /// <summary>
        /// Seeds any group whose category list is empty, and re-seeds every group when
        /// the settings were written before the current catalogue version.
        /// <para>
        /// A user who deliberately cleared a group would be overridden here, but an
        /// empty list does not mean "no categories" to the engine - it means "all
        /// categories", which nobody chooses on purpose. Restoring the MEP defaults is
        /// the safer reading.
        /// </para>
        /// </summary>
        public bool ApplyClashCategoryDefaults()
        {
            var stale = ClashCategoryCatalogueVersion < MepClashCategories.CatalogueVersion;
            var changed = false;

            if (stale || GroupACategories == null || GroupACategories.Count == 0)
            {
                GroupACategories = MepClashCategories.DefaultCategoryIds();
                changed = true;
            }
            if (stale || GroupBCategories == null || GroupBCategories.Count == 0)
            {
                GroupBCategories = MepClashCategories.DefaultCategoryIds();
                changed = true;
            }
            if (stale || GroupCCategories == null || GroupCCategories.Count == 0)
            {
                GroupCCategories = MepClashCategories.DefaultCategoryIds();
                changed = true;
            }

            if (changed) ClashCategoryCatalogueVersion = MepClashCategories.CatalogueVersion;
            return changed;
        }

        public string BuildAiBaseUrl { get; set; } = "https://app.buildai.me";
        public string BuildAiProjectId { get; set; } = "";

        // Legacy field kept only so old config files can still be read.
        // New builds store the token in Windows Credential Manager.
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string BuildAiApiKey { get; set; } = null;

        public bool UseDirectAps { get; set; } = false;
        public string ApsClientId { get; set; } = "";
        public string ApsClientSecret { get; set; } = "";
        public string ApsBucketKey { get; set; } = "";
        public const string DefaultAiEndpoint = "https://openrouter.ai/api/v1/chat/completions";
        public const string DefaultAiModel = "gpt-4o-mini";
        public const string DefaultIssueSubtypeId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";

        public string AiEndpoint { get; set; } = DefaultAiEndpoint;
        [JsonIgnore]
        public string AiApiKey { get; set; } = Environment.GetEnvironmentVariable("BUILDAI_CLASH_AI_API_KEY") ?? "";
        public string AiModel { get; set; } = DefaultAiModel;

        private static string PathName => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BuildAI",
            "plugin5-settings.json");

        public static Plugin5Settings Load()
        {
            try
            {
                if (!File.Exists(PathName))
                {
                    var fresh = new Plugin5Settings();
                    fresh.ApplyClashCategoryDefaults();
                    return fresh;
                }
                var loaded = JsonConvert.DeserializeObject<Plugin5Settings>(File.ReadAllText(PathName)) ?? new Plugin5Settings();
                loaded.AiEndpoint = DefaultAiEndpoint; loaded.AiModel = DefaultAiModel; loaded.AiApiKey = Environment.GetEnvironmentVariable("BUILDAI_CLASH_AI_API_KEY") ?? "";
                // Migrate installations whose category lists were saved empty, i.e.
                // unfiltered, before the MEP catalogue existed.
                if (loaded.ApplyClashCategoryDefaults())
                {
                    try { loaded.Save(); } catch { /* defaults still apply in memory */ }
                }
                return loaded;
            }
            catch
            {
                var fallback = new Plugin5Settings();
                fallback.ApplyClashCategoryDefaults();
                return fallback;
            }
        }

        public void Save()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PathName));
            BuildAiApiKey = null; AiEndpoint = DefaultAiEndpoint; AiModel = DefaultAiModel; AiApiKey = Environment.GetEnvironmentVariable("BUILDAI_CLASH_AI_API_KEY") ?? "";
            File.WriteAllText(PathName, JsonConvert.SerializeObject(this, Formatting.Indented));
        }
    }
}
