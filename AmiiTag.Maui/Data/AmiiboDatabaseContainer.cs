using System.Text.Json;
using AmiiTag.AmiiboData;
using AmiiTag.Core;
using AmiiTag.Core.Data;
using AmiiTag.Core.Interfaces;
using AmiiTag.Helpers;
using DanTheMan827.Helpers;
using DanTheMan827.ShaSync;

namespace AmiiTag.Maui.Data
{
    /// <summary>
    /// Keeps a local copy of the amiibo databases (synced with ShaSync) so the app works offline,
    /// like the Swift app's database update. Which databases, and in what order, comes from the published
    /// databases.json (<see cref="DatabaseSources"/>): fetched when checking for or running an update, and kept beside
    /// the databases for when it can't be; the built-in list is used until one has been fetched.
    /// </summary>
    public sealed class AmiiboDatabaseContainer : IDatabaseContainer
    {
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

        private readonly IAppPaths _paths;
        private IReadOnlyList<DatabaseSource> _sources;

        public AmiiboDatabaseContainer(IAppPaths paths)
        {
            _paths = paths;
            _sources = ReadCachedSources() ?? DatabaseSources.BuiltIn;
        }

        /// <summary>
        /// The databases in use, in load order.
        /// </summary>
        public IReadOnlyList<DatabaseSource> Sources => _sources;

        private string CachedListPath => Path.Combine(_paths.DatabaseBase, DatabaseSources.FileName);

        private IReadOnlyList<DatabaseSource>? ReadCachedSources()
        {
            try
            {
                return File.Exists(CachedListPath) ? DatabaseSources.Parse(File.ReadAllBytes(CachedListPath)) : null;
            }
            catch (IOException)
            {
                return null;
            }
        }

        /// <summary>
        /// Fetches databases.json and keeps it. When it can't be fetched (offline, or not a usable list), the list in use
        /// stays: the last one fetched, else the built-in one. True when the list changed.
        /// </summary>
        private async Task<bool> RefreshSourcesAsync(CancellationToken cancellationToken)
        {
            byte[] json;
            try
            {
                json = await Http.GetByteArrayAsync($"{DatabaseSources.ListUrl}?{Guid.NewGuid():N}", cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                System.Diagnostics.Debug.WriteLine($"Databases: couldn't fetch {DatabaseSources.ListUrl}: {ex.Message}");
                return false;
            }

            if (DatabaseSources.Parse(json) is not { } fetched)
            {
                System.Diagnostics.Debug.WriteLine($"Databases: {DatabaseSources.ListUrl} isn't a usable list; keeping the current one.");
                return false;
            }

            try
            {
                Directory.CreateDirectory(_paths.DatabaseBase);
                await File.WriteAllBytesAsync(CachedListPath, DatabaseSources.Serialize(fetched), cancellationToken);
            }
            catch (IOException ex)
            {
                System.Diagnostics.Debug.WriteLine($"Databases: couldn't save the list: {ex.Message}");
            }

            var changed = !fetched.SequenceEqual(_sources);
            _sources = fetched;
            return changed;
        }

        public AmiiboDatabase Database { get; } = new();

        public bool IsLoaded { get; private set; }

        public bool LoadsRemotely => false;

        public event EventHandler? DataChanged;

        /// <summary>
        /// The local folders of the databases in use (read again on every call, as the list can change).
        /// </summary>
        public IEnumerable<string> SourceFolders => _sources.Select(source => Path.Combine(_paths.DatabaseBase, source.FolderName));

        public string? GetRemoteImageUrl(string fileName) => _sources.Count > 0 ? $"{_sources[0].Url}/images/{fileName}" : null;

        public async Task LoadData(CancellationToken cancellationToken = default)
        {
            Database.ClearData();
            foreach (var source in _sources)
            {
                var folder = Path.Combine(_paths.DatabaseBase, source.FolderName);
                if (File.Exists(Path.Combine(folder, "database", "amiibo.json")))
                {
                    try
                    {
                        // Image URLs point at the website; MauiFigureImageProvider prefers the synced local copies.
                        await Database.ImportData(folder, new Uri($"{source.Url}/images/"), cancellationToken);
                    }
                    catch
                    {
                        // A broken copy is fixed by the next update.
                    }
                }
            }

            IsLoaded = Database.Figures.Count > 0;
            DataChanged?.Invoke(this, EventArgs.Empty);
        }

        public Task ClearData(CancellationToken cancellationToken = default)
        {
            Database.ClearData();
            IsLoaded = false;
            DataChanged?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }

        public async Task<bool> CheckUpdate(CancellationToken cancellationToken = default)
        {
            // A database added to (or removed from, or reordered in) the list is an update in itself.
            if (await RefreshSourcesAsync(cancellationToken))
            {
                return true;
            }

            foreach (var source in _sources)
            {
                var localManifest = Path.Combine(_paths.DatabaseBase, source.FolderName, "sha1sum.json");
                if (!File.Exists(localManifest))
                {
                    return true;
                }

                Dictionary<string, string>? local;
                await using (var stream = File.OpenRead(localManifest))
                {
                    local = await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(stream, cancellationToken: cancellationToken);
                }

                var remote = await Web.DownloadJsonAsync<Dictionary<string, string>>($"{source.Url}/sha1sum.json?{Guid.NewGuid():N}", cancellationToken);
                if (local == null || remote == null)
                {
                    return true;
                }

                if (local.Count != remote.Count || remote.Any(item => !local.TryGetValue(item.Key, out var hash) || !string.Equals(hash, item.Value, StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }

            // No new version: quietly retry any images that were missing from the server (or failed) last time.
            await RetryPendingFilesAsync(cancellationToken);
            return false;
        }

        /// <summary>
        /// The number of optional files (images) the last update couldn't download.
        /// </summary>
        public int UnavailableFileCount { get; private set; }

        private async Task RetryPendingFilesAsync(CancellationToken cancellationToken)
        {
            var downloaded = false;
            var stillMissing = 0;
            foreach (var source in _sources)
            {
                var folder = Path.Combine(_paths.DatabaseBase, source.FolderName);
                var pending = await Syncer.GetPendingKeysAsync(folder, cancellationToken: cancellationToken);
                if (pending.Count == 0)
                {
                    continue;
                }

                try
                {
                    var result = await new Syncer($"{source.Url}/sha1sum.json", folder).SyncAsync(cancellationToken);
                    downloaded |= result.Downloaded.Count > 0;
                    stillMissing += result.Failed.Count;
                }
                catch (Syncer.SyncException)
                {
                    // Try again on the next check.
                }
            }

            UnavailableFileCount = stillMissing;
            if (downloaded)
            {
                await LoadData(cancellationToken);
            }
        }

        public async Task UpdateData(Func<ProgressInfo, Task>? progress = null, CancellationToken cancellationToken = default)
        {
            await RefreshSourcesAsync(cancellationToken);
            var unavailable = 0;
            foreach (var source in _sources)
            {
                var folder = Path.Combine(_paths.DatabaseBase, source.FolderName);
                Directory.CreateDirectory(folder);

                // Downloaded API data can always be fetched again: keep it out of backups (it stays in Documents on iOS).
                FileHelpers.SetExcludedFromBackup(_paths.DatabaseBase);
                FileHelpers.SetExcludedFromBackup(folder);

                var syncer = new Syncer($"{source.Url}/sha1sum.json", folder);
                syncer.SyncProgress += (_, e) =>
                {
                    var info = e.SyncItem == null
                        ? new ProgressInfo(e.Total, e.Total, "Done")
                        : new ProgressInfo(e.Index + 1, e.Total, Path.GetFileName(e.SyncItem.Key));
                    _ = progress?.Invoke(info);
                };

                // Missing images are skipped (and retried on later checks); a missing JSON file throws SyncException.
                var result = await syncer.SyncAsync(cancellationToken);
                unavailable += result.Failed.Count;
            }

            UnavailableFileCount = unavailable;
            if (unavailable > 0 && progress != null)
            {
                await progress(new ProgressInfo(1, 1, $"{unavailable} image{(unavailable == 1 ? "" : "s")} unavailable; will retry later"));
            }

            await LoadData(cancellationToken);
        }
    }
}
