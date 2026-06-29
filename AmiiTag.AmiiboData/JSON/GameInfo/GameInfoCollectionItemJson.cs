using System.Text.Json.Serialization;

namespace AmiiTag.AmiiboData.JSON.GameInfo
{
    internal class GameInfoCollectionItemJson
    {
        [JsonPropertyName("games3DS")]
        public List<GameInfoAmiiboJson> Games3ds { get; set; } = new();

        [JsonPropertyName("gamesSwitch")]
        public List<GameInfoAmiiboJson> GamesSwitch { get; set; } = new();

        [JsonPropertyName("gamesWiiU")]
        public List<GameInfoAmiiboJson> GamesWiiU { get; set; } = new();
    }
}
