using DanTheMan827.Helpers;
using DanTheMan827.Helpers.Extensions;
using System.Security.Cryptography;
using System.Text.Json;

namespace AmiiTag.AmiiboData
{
    public class GameUsage
    {
        public string Description { get; internal set; } = "";

        public bool Writable { get; internal set; } = false;

        internal GameUsage() { }

        internal string GetHash()
        {
            var json = JsonSerializer.Serialize(this);

            return HashHelpers.ComputeHash<MD5>(json).ToHexString();
        }
    }
}
