using System.Text.Json.Serialization;

namespace AmiiTag.AmiiboData.JSON.Amiibo
{
    internal class AmiiboJsonFigure
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("release")]
        public Dictionary<string, string>? Release { get; set; }

        [JsonPropertyName("visible")]
        public bool IsVisible { get; set; } = true;

        [JsonPropertyName("mapped_id")]
        public string? MappedId { get; set; }
    }
}
