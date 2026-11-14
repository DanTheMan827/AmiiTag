using System.Security.Cryptography;
using System.Text.Json;

namespace DanTheMan827.ShaSync
{
    /// <summary>
    /// Utility for synchronizing files based on a manifest and SHA-1 hash values.
    /// </summary>
    /// <remarks>
    /// Files are split into required and optional ones (see <see cref="IsRequired"/>; by default every .json file is
    /// required). If the manifest or a required file can't be downloaded or doesn't match its hash, the sync fails
    /// with a <see cref="SyncException"/> and the local manifest isn't updated, so the whole sync is retried later.
    /// Optional files (images) that fail are skipped, listed in <see cref="SyncResult.Failed"/> and recorded in
    /// <see cref="FailuresFileName"/>; <see cref="GetPendingKeysAsync"/> reports them (and any missing files) so they can be retried.
    /// </remarks>
    public class Syncer
    {
        /// <summary>
        /// The file, next to the manifest, listing optional files that failed to download.
        /// </summary>
        public const string FailuresFileName = "sha1sum.failed.json";

        // Event to report synchronization progress
        public event EventHandler<SyncProgressEventArgs>? SyncProgress;

        // EventArgs class for the SyncProgress event
        public class SyncProgressEventArgs : EventArgs
        {
            public ManifestItem? SyncItem { get; }
            public int Index { get; }
            public int Total { get; }

            public SyncProgressEventArgs(ManifestItem? syncItem, int index, int total)
            {
                SyncItem = syncItem;
                Index = index;
                Total = total;
            }
        }

        /// <summary>
        /// The outcome of a sync.
        /// </summary>
        public sealed class SyncResult
        {
            internal SyncResult(IReadOnlyList<ManifestItem> downloaded, IReadOnlyList<ManifestItem> failed)
            {
                Downloaded = downloaded;
                Failed = failed;
            }

            /// <summary>
            /// Files that were downloaded and verified.
            /// </summary>
            public IReadOnlyList<ManifestItem> Downloaded { get; }

            /// <summary>
            /// Optional files that couldn't be downloaded or didn't match their hash. They'll be retried next time.
            /// </summary>
            public IReadOnlyList<ManifestItem> Failed { get; }
        }

        /// <summary>
        /// Thrown when the manifest or a required file can't be synchronized.
        /// </summary>
        public class SyncException : Exception
        {
            public SyncException(string message, Exception? innerException = null) : base(message, innerException)
            {
            }
        }

        /// <summary>
        /// Gets the base URL for the synchronization.
        /// </summary>
        public Uri BaseUrl { get; private set; }

        /// <summary>
        /// Gets the URL of the manifest used for synchronization.
        /// </summary>
        public Uri ManifestUrl { get; private set; }

        /// <summary>
        /// Gets the local path where files will be synchronized.
        /// </summary>
        public string LocalPath { get; private set; }

        /// <summary>
        /// Decides which files must sync for the sync to succeed. Defaults to every .json file.
        /// </summary>
        public Func<ManifestItem, bool> IsRequired { get; set; } = IsJson;

        /// <summary>
        /// Represents a manifest item.
        /// </summary>
        public class ManifestItem
        {
            /// <summary>
            /// The destination file path.
            /// </summary>
            public string DestinationFile { get; internal set; } = string.Empty;

            /// <summary>
            /// The destination folder of the file.
            /// </summary>
            public string DestinationFolder => Path.GetDirectoryName(DestinationFile) ?? string.Empty;

            /// <summary>
            /// The source URL for the file.
            /// </summary>
            public string SourceUrl { get; internal set; } = string.Empty;

            /// <summary>
            /// The path as specified in the manifest.
            /// </summary>
            public string Key { get; internal set; } = string.Empty;

            /// <summary>
            /// The SHA-1 hash of the item.
            /// </summary>
            public string Hash { get; internal set; } = string.Empty;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="Syncer"/> class.
        /// </summary>
        /// <param name="manifestUrl">The URL of the manifest used for synchronization.</param>
        /// <param name="localPath">The local path where files will be synchronized.</param>
        public Syncer(string manifestUrl, string localPath)
        {
            this.BaseUrl = new Uri(manifestUrl.Substring(0, manifestUrl.LastIndexOf("/") + 1));
            this.ManifestUrl = new Uri(BaseUrl, manifestUrl.Substring(manifestUrl.LastIndexOf("/") + 1));
            this.LocalPath = Path.GetFullPath(localPath);
        }

        public static bool IsJson(ManifestItem item) => item.Key.EndsWith(".json", StringComparison.OrdinalIgnoreCase);

        private string LocalManifestPath => Path.Combine(LocalPath, ManifestUrl.Segments.Last());

        private string FailuresPath => Path.Combine(LocalPath, FailuresFileName);

        /// <summary>
        /// Computes the SHA-1 hash of a file.
        /// </summary>
        private static async Task<string> ComputeSHA1Async(string filePath, CancellationToken cancellationToken = default)
        {
            using (var sha1 = SHA1.Create())
            {
                using (var stream = File.OpenRead(filePath))
                {
                    byte[] hashBytes = await sha1.ComputeHashAsync(stream, cancellationToken);
                    return Convert.ToHexString(hashBytes).ToLowerInvariant();
                }
            }
        }

        private static string ComputeSHA1(byte[] data) => Convert.ToHexString(SHA1.HashData(data)).ToLowerInvariant();

        /// <summary>
        /// Gets a list of items to be synchronized based on a manifest.
        /// </summary>
        private async Task<ManifestItem[]> GetSyncItemsAsync(IEnumerable<ManifestItem> manifest, CancellationToken cancellationToken = default)
        {
            var syncItems = new List<ManifestItem>();

            foreach (var item in manifest)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!item.DestinationFile.StartsWith(LocalPath, StringComparison.Ordinal))
                {
                    throw new SyncException($"Manifest entry \"{item.Key}\" points outside of the local path.");
                }

                if (File.Exists(item.DestinationFile) && (await ComputeSHA1Async(item.DestinationFile, cancellationToken)) == item.Hash.ToLowerInvariant())
                {
                    continue;
                }

                syncItems.Add(item);
            }

            return syncItems.ToArray();
        }

        private ManifestItem[] ParseManifest(string manifestJson)
        {
            Dictionary<string, string>? entries;
            try
            {
                entries = JsonSerializer.Deserialize<Dictionary<string, string>>(manifestJson);
            }
            catch (JsonException ex)
            {
                throw new SyncException($"The manifest at {ManifestUrl} isn't valid JSON.", ex);
            }

            if (entries == null)
            {
                throw new SyncException($"The manifest at {ManifestUrl} is empty.");
            }

            return entries.Select(item => new ManifestItem()
            {
                Key = item.Key,
                Hash = item.Value,
                DestinationFile = Path.GetFullPath(Path.Combine(this.LocalPath, item.Key)),
                SourceUrl = new Uri(BaseUrl, item.Key).ToString()
            }).ToArray();
        }

        /// <summary>
        /// Synchronizes files based on the manifest and SHA-1 hash values.
        /// </summary>
        /// <param name="cancellationToken">A cancellation token.</param>
        /// <returns>What was downloaded, and which optional files failed.</returns>
        /// <exception cref="SyncException">The manifest or a required file couldn't be synchronized.</exception>
        public async Task<SyncResult> SyncAsync(CancellationToken cancellationToken = default)
        {
            using var hc = new HttpClient();
            Directory.CreateDirectory(LocalPath);

            string manifestJson;
            try
            {
                manifestJson = await hc.GetStringAsync(this.ManifestUrl, cancellationToken);
            }
            catch (HttpRequestException ex)
            {
                throw new SyncException($"Couldn't download {ManifestUrl}: {ex.Message}", ex);
            }

            var manifest = ParseManifest(manifestJson);
            var syncItems = await GetSyncItemsAsync(manifest, cancellationToken);
            var downloaded = new List<ManifestItem>();
            var failed = new List<ManifestItem>();

            for (var itemIdx = 0; itemIdx < syncItems.Length; itemIdx++)
            {
                var item = syncItems[itemIdx];
                SyncProgress?.Invoke(this, new SyncProgressEventArgs(item, itemIdx, syncItems.Length));

                var required = IsRequired(item);
                string? problem = null;
                Exception? error = null;
                byte[]? data = null;

                try
                {
                    using var response = await hc.GetAsync(item.SourceUrl, cancellationToken);
                    if (response.IsSuccessStatusCode)
                    {
                        data = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                        if (ComputeSHA1(data) != item.Hash.ToLowerInvariant())
                        {
                            problem = "its contents don't match the manifest hash";
                            data = null;
                        }
                    }
                    else
                    {
                        problem = $"the server returned {(int)response.StatusCode} {response.ReasonPhrase}";
                    }
                }
                catch (HttpRequestException ex)
                {
                    problem = ex.Message;
                    error = ex;
                }
                catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
                {
                    problem = "the request timed out";
                    error = ex;
                }

                if (data == null)
                {
                    if (required)
                    {
                        throw new SyncException($"Couldn't download {item.Key}: {problem}.", error);
                    }

                    failed.Add(item);
                    continue;
                }

                // Write to a temporary file first so an interrupted write never leaves a truncated file behind.
                Directory.CreateDirectory(item.DestinationFolder);
                var tempFile = item.DestinationFile + ".download";
                await File.WriteAllBytesAsync(tempFile, data, cancellationToken);
                File.Move(tempFile, item.DestinationFile, overwrite: true);
                downloaded.Add(item);
            }

            // Every required file is in place, so the manifest can be recorded. Optional failures are tracked separately
            // so later checks can retry them without a new manifest.
            await File.WriteAllTextAsync(LocalManifestPath, manifestJson, cancellationToken);
            if (failed.Count > 0)
            {
                await File.WriteAllTextAsync(FailuresPath, JsonSerializer.Serialize(failed.Select(f => f.Key).ToArray()), cancellationToken);
            }
            else if (File.Exists(FailuresPath))
            {
                File.Delete(FailuresPath);
            }

            SyncProgress?.Invoke(this, new SyncProgressEventArgs(null, syncItems.Length, syncItems.Length));
            return new SyncResult(downloaded, failed);
        }

        /// <summary>
        /// Lists files from the local manifest that are missing locally or failed last time, without touching the network.
        /// Hashing every file is avoided; failures are tracked in <see cref="FailuresFileName"/> instead.
        /// </summary>
        /// <param name="localPath">The synced folder.</param>
        /// <param name="manifestFileName">The manifest's file name.</param>
        public static async Task<IReadOnlyList<string>> GetPendingKeysAsync(string localPath, string manifestFileName = "sha1sum.json", CancellationToken cancellationToken = default)
        {
            var root = Path.GetFullPath(localPath);
            var manifestPath = Path.Combine(root, manifestFileName);
            if (!File.Exists(manifestPath))
            {
                return Array.Empty<string>();
            }

            var pending = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                await using var stream = File.OpenRead(manifestPath);
                var manifest = await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(stream, cancellationToken: cancellationToken);
                foreach (var key in manifest?.Keys ?? Enumerable.Empty<string>())
                {
                    if (!File.Exists(Path.Combine(root, key)))
                    {
                        pending.Add(key);
                    }
                }
            }
            catch (JsonException)
            {
                return Array.Empty<string>();
            }

            var failuresPath = Path.Combine(root, FailuresFileName);
            if (File.Exists(failuresPath))
            {
                try
                {
                    await using var stream = File.OpenRead(failuresPath);
                    var failures = await JsonSerializer.DeserializeAsync<string[]>(stream, cancellationToken: cancellationToken);
                    pending.UnionWith(failures ?? Array.Empty<string>());
                }
                catch (JsonException)
                {
                }
            }

            return pending.ToArray();
        }

        /// <summary>
        /// A convenience method to instantiate the class, and start a sync with the specificied parameters.
        /// </summary>
        /// <param name="manifestUrl">The URL of the manifest used for synchronization.</param>
        /// <param name="localPath">The local path where files will be synchronized.</param>
        /// <param name="syncProgress">A method that will be invoked when progress changes.</param>
        /// <param name="cancellationToken">A cancellation token.</param>
        public static Task<SyncResult> SyncAsync(string manifestUrl, string localPath, EventHandler<SyncProgressEventArgs>? syncProgress = null, CancellationToken cancellationToken = default)
        {
            var syncer = new Syncer(manifestUrl, localPath);

            if (syncProgress != null)
            {
                syncer.SyncProgress += syncProgress;
            }

            return syncer.SyncAsync(cancellationToken);
        }
    }
}
