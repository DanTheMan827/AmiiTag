using System.Text.Json.Serialization;

namespace AmiiTag.AmiiboData.JSON.GameInfo
{
    internal class GameInfoJsonBase : JsonHelpers<GameInfoJsonBase>
    {
        [JsonPropertyName("amiibos")]
        public Dictionary<string, GameInfoCollectionItemJson> Amiibos { get; set; } = new();
    }
}
