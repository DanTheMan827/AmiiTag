using AmiiTag.AmiiboData.JSON.GameInfo;
using DanTheMan827.Helpers;
using DanTheMan827.Helpers.Extensions;
using System.Security.Cryptography;
using System.Text.Json;

namespace AmiiTag.AmiiboData
{
    public class GameInfo
    {
        internal List<GameUsage> _Usage { get; set; }
        internal List<string> _Ids { get; set; }

        public IReadOnlyList<GameUsage> Usage => _Usage;
        public IReadOnlyList<string> Ids => _Ids;
        public string Name { get; internal set; }
        public SystemKind System { get; internal set; }

        internal string GetHash()
        {
            var json = JsonSerializer.Serialize(this);

            return HashHelpers.ComputeHash<MD5>(json).ToHexString();
        }

        internal GameInfo(GameInfoAmiiboJson json, SystemKind kind)
        {
            Name = json.Name;
            System = kind;
            _Usage = new();
            _Ids = new();

            _Usage.AddRange(json.Usages.Select(use => new GameUsage()
            {
                Description = use.Usage,
                Writable = use.Writable,
            }).OrderBy(a => a.Description));
            _Ids.AddRange(json.GameIds);
        }

        internal GameInfo()
        {
            _Usage = new();
            _Ids = new();
            Name = string.Empty;
            System = SystemKind.None;
        }
    }
}
