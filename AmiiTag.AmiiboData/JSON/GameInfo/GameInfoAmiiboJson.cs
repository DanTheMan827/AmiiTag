using System.Text.Json.Serialization;

namespace AmiiTag.AmiiboData.JSON.GameInfo
{
    internal class GameInfoAmiiboJson
    {
        [JsonPropertyName("amiiboUsage")]
        public List<GameInfoUsageJson> Usages { get; set; } = new();

        [JsonPropertyName("gameID")]
        public List<string> GameIds { get; set; } = new();

        [JsonPropertyName("gameName")]
        public string Name { get; set; } = "";
    }
}
