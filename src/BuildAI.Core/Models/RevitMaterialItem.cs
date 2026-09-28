using Newtonsoft.Json;

namespace BuildAI.Core.Models
{
    /// <summary>
    /// One row of the material/volume array sent to the elements endpoint.
    /// The backend accepts "any JSON" in the body (per the client), with the
    /// model id (= ProjectId) in the URL. We use a typed, stable shape that
    /// mirrors specification §5.3 so BuildAI-side parsing is predictable.
    /// </summary>
    public sealed class RevitMaterialItem
    {
        [JsonProperty("category")]      public string Category { get; set; }
        [JsonProperty("material_name")] public string MaterialName { get; set; }
        [JsonProperty("material_class")]public string MaterialClass { get; set; }
        [JsonProperty("level")]         public string Level { get; set; }
        [JsonProperty("volume_m3")]     public double VolumeM3 { get; set; }
        [JsonProperty("area_m2")]       public double? AreaM2 { get; set; }
        [JsonProperty("count")]         public int Count { get; set; }
        [JsonProperty("section")]       public string Section { get; set; }
    }
}
