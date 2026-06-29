using System.Text.Json.Serialization;

namespace AmiiTag.AmiiboData.JSON.Amiibo
{
    internal class AmiiboJsonBase : JsonHelpers<AmiiboJsonBase>
    {
        [JsonPropertyName("amiibo_series")]
        public Dictionary<string, string>? AmiiboSeries { get; init; }

        [JsonPropertyName("amiibos")]
        public Dictionary<string, AmiiboJsonFigure>? AmiiboFigures { get; set; }

        [JsonPropertyName("characters")]
        public Dictionary<string, string>? Characters { get; set; }

        [JsonPropertyName("game_series")]
        public Dictionary<string, string>? GameSeries { get; set; }

        [JsonPropertyName("types")]
        public Dictionary<string, string>? Types { get; set; }

        public AmiiboJsonBase()
        {
            AmiiboSeries = new Dictionary<string, string>();
            AmiiboFigures = new Dictionary<string, AmiiboJsonFigure>();
            Characters = new Dictionary<string, string>();
            GameSeries = new Dictionary<string, string>();
            Types = new Dictionary<string, string>();
        }
    }
}
