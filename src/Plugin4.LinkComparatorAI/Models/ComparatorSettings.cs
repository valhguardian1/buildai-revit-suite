using System;
using System.IO;
using Newtonsoft.Json;
using BuildAI.Core.Logging;

namespace Plugin4.LinkComparatorAI.Models
{
    public sealed class ComparatorSettings
    {
        public const string HostSourceId = "__HOST__";
        public string ArchitecturalLinkUniqueId { get; set; } = "";
        public string StructuralLinkUniqueId { get; set; } = "";
        public double PositionToleranceMm { get; set; } = 10;
        public double ElevationToleranceMm { get; set; } = 5;
        public double RoomMinHeightMm { get; set; } = 2200;
        public double RoomMaxHeightMm { get; set; } = 2450;
        public string PreferredAssigneeRole { get; set; } = "";
        public const string DefaultAiEndpoint = "https://openrouter.ai/api/v1/chat/completions";
        public const string DefaultAiModel = "gpt-4o-mini";
        public const string DefaultIssueSubtypeId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";

        public string AiEndpoint { get; set; } = DefaultAiEndpoint;
        [JsonIgnore]
        public string AiApiKey { get; set; } = Environment.GetEnvironmentVariable("BUILDAI_ARST_AI_API_KEY") ?? "";
        public string AiModel { get; set; } = DefaultAiModel;
        public bool AiEnabled => !string.IsNullOrWhiteSpace(AiEndpoint) && !string.IsNullOrWhiteSpace(AiModel) && !string.IsNullOrWhiteSpace(AiApiKey);


        public static string PathName { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BuildAI", "link-comparator.json");

        public static ComparatorSettings Load()
        {
            try
            {
                if (File.Exists(PathName))
                {
                    var loaded = JsonConvert.DeserializeObject<ComparatorSettings>(File.ReadAllText(PathName)) ?? new ComparatorSettings();
                    loaded.AiEndpoint = DefaultAiEndpoint;
                    loaded.AiModel = DefaultAiModel;
                    loaded.AiApiKey = Environment.GetEnvironmentVariable("BUILDAI_ARST_AI_API_KEY") ?? "";
                    return loaded;
                }
            }
            catch (Exception ex)
            {
                PluginLog.Error("Failed to load Link Comparator settings. Defaults will be used.", ex);
            }
            return new ComparatorSettings();
        }

        public void Save()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PathName));
            AiEndpoint = DefaultAiEndpoint; AiModel = DefaultAiModel; AiApiKey = Environment.GetEnvironmentVariable("BUILDAI_ARST_AI_API_KEY") ?? "";
            File.WriteAllText(PathName, JsonConvert.SerializeObject(this, Formatting.Indented));
        }
    }
}
