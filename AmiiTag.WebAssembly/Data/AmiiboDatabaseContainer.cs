using AmiiTag.AmiiboData;
using AmiiTag.Core;
using AmiiTag.Core.Interfaces;
using DanTheMan827.Helpers;

namespace AmiiTag.WebAssembly.Data
{
    public class AmiiboDatabaseContainer : IDatabaseContainer
    {
        private static string[] UpdateUrls { get; set; } = new string[]
        {
            "https://dantheman827.github.io/AmiiboData",
            "https://dantheman827.github.io/UnofficialAmiiboDB"
        };

        private Dictionary<string, Dictionary<string, string>> Hashes { get; set; } = new();


        public AmiiboDatabase Database { get; private set; }

        public AmiiboDatabaseContainer(AmiiboDatabase database)
        {
            this.Database = database;
        }

        public async Task<bool> CheckUpdate(CancellationToken cancellationToken = default)
        {
            foreach (var url in UpdateUrls)
            {
                if (await CheckUpdate(url, cancellationToken) == true)
                {
                    return true;
                }
            }

            return false;
        }

        public async Task ClearData(CancellationToken cancellationToken = default) => Database.ClearData();

        public async Task LoadData(CancellationToken cancellationToken = default) => await UpdateData(null, cancellationToken);

        public async Task UpdateData(Func<ProgressInfo, Task>? progress = null, CancellationToken cancellationToken = default)
        {
            if (await CheckUpdate() == false)
            {
                return;
            }

            await ClearData(cancellationToken);

            foreach (var url in UpdateUrls.Select((url, index) => new KeyValuePair<int, string>(index, url)))
            {
                progress?.Invoke(new ProgressInfo(url.Key, UpdateUrls.Length, url.Value));

                try
                {
                    var hashes = await Web.DownloadJsonAsync<Dictionary<string, string>>(url.Value, cancellationToken);

                    if (hashes != null)
                    {
                        if (Hashes.ContainsKey(url.Value))
                        {
                            Hashes.Remove(url.Value);
                        }

                        Hashes.Add(url.Value, hashes);
                    }
                }
                catch (Exception ex)
                { }

                try
                {
                    await Database.ImportData(url.Value);
                }
                catch (Exception ex)
                { }
            }

            progress?.Invoke(new ProgressInfo(UpdateUrls.Length, UpdateUrls.Length, "Done"));
        }

        private async Task<Dictionary<string, string>?> LoadHashes(string url, CancellationToken cancellationToken) => await Web.DownloadJsonAsync<Dictionary<string, string>>(url, cancellationToken);

        private async Task<bool> CheckUpdate(string url, CancellationToken cancellationToken = default)
        {
            if (!Hashes.ContainsKey(url))
            {
                return true;
            }

            try
            {
                var hashes = await Web.DownloadJsonAsync<Dictionary<string, string>>(url, cancellationToken);

                return !CompareDictionaries(hashes, Hashes[url]);
            }
            catch (Exception ex)
            { }

            return true;
        }

        private static bool CompareDictionaries<TKey, TValue>(Dictionary<TKey, TValue> dict1, Dictionary<TKey, TValue> dict2)
        {
            // Use LINQ SequenceEqual to compare the key-value pairs
            return dict1.OrderBy(kvp => kvp.Key)
                        .SequenceEqual(dict2.OrderBy(kvp => kvp.Key));
        }
    }
}
