using System.Text.Json.Serialization;

namespace AmiiTag.AmiiboData.JSON.GameInfo
{
    public class GameInfoUsageJson
    {
        [JsonPropertyName("Usage")]
        public string Usage { get; set; } = "";

        [JsonPropertyName("write")]
        public bool Writable { get; set; } = false;
    }
}
