using AmiiTag.AmiiboData;
using AmiiTag.Core;
using AmiiTag.Core.Interfaces;

namespace AmiiTag.WebAssembly.Data
{
    /// <summary>
    /// Loads the amiibo databases straight from the website on startup. The browser's HTTP cache keeps
    /// repeat visits fast, and sha1sum.json hashes are used as cache busters by <see cref="AmiiboDatabase"/>.
    /// </summary>
    public class AmiiboDatabaseContainer : IDatabaseContainer
    {
        private static readonly string[] UpdateUrls =
        [
            "https://dantheman827.github.io/AmiiboData",
            "https://dantheman827.github.io/UnofficialAmiiboDB",
        ];

        public AmiiboDatabaseContainer(AmiiboDatabase database)
        {
            Database = database;
        }

        public AmiiboDatabase Database { get; }

        public bool IsLoaded { get; private set; }

        public bool LoadsRemotely => true;

        public event EventHandler? DataChanged;

        /// <summary>
        /// Data is always fetched fresh, so there's never a separate update to offer.
        /// </summary>
        public Task<bool> CheckUpdate(CancellationToken cancellationToken = default) => Task.FromResult(false);

        public Task ClearData(CancellationToken cancellationToken = default)
        {
            Database.ClearData();
            IsLoaded = false;
            DataChanged?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }

        public Task LoadData(CancellationToken cancellationToken = default) => UpdateData(null, cancellationToken);

        public async Task UpdateData(Func<ProgressInfo, Task>? progress = null, CancellationToken cancellationToken = default)
        {
            Database.ClearData();
            for (var i = 0; i < UpdateUrls.Length; i++)
            {
                if (progress != null)
                {
                    await progress(new ProgressInfo(i + 1, UpdateUrls.Length, UpdateUrls[i]));
                }

                try
                {
                    await Database.ImportData(UpdateUrls[i], cancellationToken: cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A database JSON file that can't be fetched is a hard failure. Images are loaded lazily by the
                    // browser and fall back to a placeholder when missing.
                    IsLoaded = Database.Figures.Count > 0;
                    DataChanged?.Invoke(this, EventArgs.Empty);
                    throw new InvalidOperationException($"Couldn't load the amiibo database from {UpdateUrls[i]}: {ex.Message}", ex);
                }
            }

            IsLoaded = Database.Figures.Count > 0;
            DataChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
