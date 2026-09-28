using Newtonsoft.Json;

namespace Plugin2.VolumeEstimator.Publishing
{
    public sealed class RevitPublishResponse
    {
        [JsonProperty("id")] public long Id { get; set; }
        [JsonProperty("revit_model_id")] public long RevitModelId { get; set; }
        [JsonProperty("revit_model_uid")] public string RevitModelUid { get; set; } = "";
        [JsonProperty("file_name")] public string FileName { get; set; } = "";
        [JsonProperty("url")] public string Url { get; set; } = "";
    }
}
